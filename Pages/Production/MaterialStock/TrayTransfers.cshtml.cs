using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PlantStockManager.Authorization;
using PlantStockManager.Data;
using PlantStockManager.Models;
using PlantStockManager.Services;

namespace PlantStockManager.Pages.Production.MaterialStock
{
    // Tray Transfers (2026-10-10). Owner decisions:
    //   send / cancel     TrayStock.Transfer         from an Area the user may stock trays in
    //                                                (TrayAreaScope: Sowing Supervisor own Areas,
    //                                                Office Coordinator the Mother Plant Areas)
    //   receive / reject  TrayStock.ReceiveTransfer  in a destination Area the user is assigned
    //                                                to (Mother Plant Supervisor)
    //   Admin (full access) may do all four.
    // The trays leave the source when sent (in transit) and reach the destination on receipt;
    // a rejection or cancellation returns them to the source (TrayTransferRepository).
    public class TrayTransfersModel : PageModel
    {
        private readonly TrayTransferRepository _repo;
        private readonly TrayStockRepository _trayStockRepo;
        private readonly AreaRepository _areaRepo;
        private readonly PolyhouseRepository _polyhouseRepo;
        private readonly AreaAccessService _areaAccess;
        private readonly TrayAreaScope _scope;

        public TrayTransfersModel(TrayTransferRepository repo, TrayStockRepository trayStockRepo, AreaRepository areaRepo,
            PolyhouseRepository polyhouseRepo, AreaAccessService areaAccess)
        {
            _repo = repo;
            _trayStockRepo = trayStockRepo;
            _areaRepo = areaRepo;
            _polyhouseRepo = polyhouseRepo;
            _areaAccess = areaAccess;
            _scope = new TrayAreaScope(areaRepo, areaAccess);
        }

        [BindProperty] public TrayTransferRequest Input { get; set; } = new();
        [BindProperty] public int TransferId { get; set; }
        [BindProperty] public DateTime ReceivedDate { get; set; }
        [BindProperty] public string? Reason { get; set; }

        public bool CanSend { get; private set; }
        public bool CanReceive { get; private set; }
        public List<Area> SourceAreas { get; private set; } = new();
        public List<Area> DestinationAreas { get; private set; } = new();
        public List<Polyhouse> Polyhouses { get; private set; } = new();
        public List<Models.TrayStock> SourcePools { get; private set; } = new();
        public List<TrayTransfer> Transfers { get; private set; } = new();
        public IReadOnlyList<string> TraySizes => DirectSowingRules.CavityTypes;
        public DateTime Today => AppClock.Today;
        public string? Status { get; private set; }
        public bool SchemaReady { get; private set; } = true;

        public bool MayReceive(TrayTransfer t) => CanReceive && t.Status == TrayTransferRules.StatusPending
            && CanReceiveAt(t.DestinationAreaId);

        // Location-aware (TrayAreaScope.IsReceivableBy): the receive permission for the destination
        // Area's type, in an Area the user is assigned to. Re-checked by the repository on POST.
        private Dictionary<int, Area> _areasById = new();
        // Full-access users may always close a transfer (even if its destination Area was deactivated since).
        private bool CanReceiveAt(int areaId) => _areaAccess.HasFullAreaAccess(User)
            || (_areasById.TryGetValue(areaId, out var area) && _scope.CanReceiveIn(User, area));
        private async Task<Func<int, bool>> ReceiverRuleAsync()
        {
            if (_areasById.Count == 0)
                _areasById = (await _areaRepo.GetAllAreas()).ToDictionary(a => a.Id);
            return CanReceiveAt;
        }
        public bool MayCancel(TrayTransfer t) => CanSend && t.Status == TrayTransferRules.StatusPending
            && SourceAreas.Any(a => a.Id == t.SourceAreaId);

        public async Task OnGetAsync(string? status)
        {
            Status = status;
            await LoadAsync();
            Input = new TrayTransferRequest { TransferDate = Today, SubmissionToken = Guid.NewGuid() };
            if (SourceAreas.Count == 1) Input.SourceAreaId = SourceAreas[0].Id;
            ReceivedDate = Today;
        }

        public async Task<IActionResult> OnPostSendAsync()
        {
            if (!User.CanSendTrayTransfers()) return Forbid();
            ModelState.Clear();
            await LoadAsync();
            ReceivedDate = Today;
            if (!SchemaReady)
                return Page();
            if (Input.SourceAreaId <= 0 || !SourceAreas.Any(a => a.Id == Input.SourceAreaId))
                ModelState.AddModelError("Input.SourceAreaId", "Choose a source Area you may send trays from.");
            if (Input.SourcePolyhouseId <= 0)
                ModelState.AddModelError("Input.SourcePolyhouseId", "Source Polyhouse is required.");
            if (Input.DestinationAreaId <= 0)
                ModelState.AddModelError("Input.DestinationAreaId", "Destination Area is required.");
            if (Input.DestinationPolyhouseId <= 0)
                ModelState.AddModelError("Input.DestinationPolyhouseId", "Destination Polyhouse is required.");
            if (Input.TransferDate == default)
                ModelState.AddModelError("Input.TransferDate", "Date is required.");
            if (!ModelState.IsValid)
                return Page();

            var allowed = SourceAreas.Select(a => a.Id).ToHashSet();
            var (success, message, _, code, duplicate) = await _repo.CreateAsync(Input, User.GetUserId(), User.Identity?.Name, id => allowed.Contains(id),
                anyValidDestination: _areaAccess.HasFullAreaAccess(User));
            if (!success)
            {
                ModelState.AddModelError(string.Empty, message ?? "Failed to send the transfer.");
                return Page();
            }
            TempData["Success"] = duplicate
                ? $"Transfer {code} was already sent -- nothing was sent twice."
                : $"Transfer {code} sent: {QuantityFormat.Qty(Input.Quantity)} {Input.TraySize} trays are now in transit.";
            return RedirectToPage();
        }

        public async Task<IActionResult> OnPostReceiveAsync()
        {
            if (!User.CanReceiveTrayTransfers()) return Forbid();
            var (success, message) = await _repo.ReceiveAsync(TransferId, ReceivedDate, User.GetUserId(), User.Identity?.Name,
                await ReceiverRuleAsync());
            return Done(success, message, "Transfer received: the trays were added to the destination Polyhouse.");
        }

        public async Task<IActionResult> OnPostRejectAsync()
        {
            if (!User.CanReceiveTrayTransfers()) return Forbid();
            var (success, message) = await _repo.ReturnAsync(TransferId, reject: true, Reason, User.GetUserId(), User.Identity?.Name,
                await ReceiverRuleAsync());
            return Done(success, message, "Transfer rejected: the trays were returned to the source Polyhouse.");
        }

        public async Task<IActionResult> OnPostCancelAsync()
        {
            if (!User.CanSendTrayTransfers()) return Forbid();
            var allowed = (await _scope.AddableAreasAsync(User)).Select(a => a.Id).ToHashSet();
            var (success, message) = await _repo.ReturnAsync(TransferId, reject: false, Reason, User.GetUserId(), User.Identity?.Name,
                id => allowed.Contains(id));
            return Done(success, message, "Transfer cancelled: the trays were returned to the source Polyhouse.");
        }

        private IActionResult Done(bool success, string? message, string ok)
        {
            if (success) TempData["Success"] = ok;
            else TempData["Error"] = message ?? "The transfer could not be updated.";
            return RedirectToPage();
        }

        private async Task LoadAsync()
        {
            CanSend = User.CanSendTrayTransfers();
            CanReceive = User.CanReceiveTrayTransfers();
            var areas = await _areaRepo.GetAllAreas();
            _areasById = areas.ToDictionary(a => a.Id);
            SourceAreas = CanSend ? await _scope.AddableAreasAsync(User) : new List<Area>();
            DestinationAreas = areas.Where(a => a.IsActive && a.AreaType != OutletRules.AreaType).OrderBy(a => a.Name).ToList();
            SchemaReady = await _repo.SchemaReadyAsync();
            if (SchemaReady && CanSend && !_areaAccess.HasFullAreaAccess(User))
            {
                // 2026-10-11: only Areas where someone can receive (the repository re-checks on save)
                var receivable = await _repo.GetReceivableAreaIdsAsync();
                DestinationAreas = DestinationAreas.Where(a => receivable.Contains(a.Id)).ToList();
            }
            var sourceIds = SourceAreas.Select(a => a.Id).ToHashSet();
            var listedIds = DestinationAreas.Select(a => a.Id).Concat(sourceIds).ToHashSet();   // both dropdowns
            Polyhouses = (await _polyhouseRepo.GetAllPolyhouses())
                .Where(p => p.AreaId.HasValue && listedIds.Contains(p.AreaId.Value)).OrderBy(p => p.Name).ToList();
            // 2026-10-14: includes a Mother Plant Area's one Area pool (no Polyhouse) -- any of its Polyhouses resolves to it.
            SourcePools = (await _trayStockRepo.GetAllAsync(activeOnly: true))
                .Where(t => sourceIds.Contains(t.AreaId) && t.PhysicalQuantity > 0).ToList();
            if (!SchemaReady)
                return;
            // listed: transfers touching an Area whose trays the user may see, receive in, or send from
            var canViewTrays = await _scope.CanViewPredicateAsync(User);
            var canView = new Func<int, bool>(id => canViewTrays(id) || sourceIds.Contains(id)
                || (CanReceive && CanReceiveAt(id)));
            Transfers = await _repo.GetAllAsync(canView, string.IsNullOrEmpty(Status) ? null : Status);
        }
    }
}
