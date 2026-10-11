using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PlantStockManager.Authorization;
using PlantStockManager.Data;
using PlantStockManager.Models;
using PlantStockManager.Services;

namespace PlantStockManager.Pages.Production.MotherPlantTransfers
{
    // Main Area Ready Stock -> Mother Plant Area transfers (MotherPlantTransferRepository).
    //   Incoming (ReadyStock.ReceiveTransfer, destination Area assigned to the user):
    //            receive (all or part, a shortfall needs a reason) or reject.
    //   Sent     (ReadyStock.TransferToMotherPlant, source Area assigned to the user):
    //            cancel while still in transit.
    // The page opens for either permission (FeatureAuthorizationConventions); each POST
    // handler re-checks ITS permission, and the repository re-checks the Area and the
    // transfer's state under lock -- a repeated or concurrent click changes nothing twice.
    // Area rules: the Mother Plant side (receive / reject) uses AreaAccessService, always
    // enforced like the Mother Plant module; the Main Area side (cancel) uses the Ready Stock
    // pages' SeedlingAreaScope, exactly as sending does.
    public class IndexModel : PageModel
    {
        public const string SendPermission = "ReadyStock.TransferToMotherPlant";
        public const string ReceivePermission = "ReadyStock.ReceiveTransfer";

        private readonly MotherPlantTransferRepository _repo;
        private readonly SeedlingAreaScope _senderAccess;   // Main Area (source) side
        private readonly AreaAccessService _receiverAccess; // Mother Plant (destination) side

        public IndexModel(MotherPlantTransferRepository repo, SeedlingAreaScope senderAccess, AreaAccessService receiverAccess)
        {
            _repo = repo;
            _senderAccess = senderAccess;
            _receiverAccess = receiverAccess;
        }

        [BindProperty(SupportsGet = true)] public string? Status { get; set; }
        [BindProperty(SupportsGet = true)] public int? Highlight { get; set; }

        public bool CanSend { get; private set; }
        public bool CanReceive { get; private set; }
        public List<MotherPlantTransfer> Incoming { get; private set; } = new();   // pending, to my Areas
        public List<MotherPlantTransfer> Outgoing { get; private set; } = new();   // pending, from my Areas
        public List<MotherPlantTransfer> History { get; private set; } = new();

        public async Task OnGetAsync()
        {
            CanSend = User.HasPermission(SendPermission);
            CanReceive = User.HasPermission(ReceivePermission);
            if (Status != null && !new[] { MotherPlantTransferRules.StatusPending, MotherPlantTransferRules.StatusCompleted,
                    MotherPlantTransferRules.StatusRejected, MotherPlantTransferRules.StatusCancelled }.Contains(Status))
                Status = null;

            var all = (await _repo.ListAsync(null))
                .Where(t => _senderAccess.CanAccessArea(User, t.SourceAreaId) || _receiverAccess.CanAccessArea(User, t.DestinationAreaId))
                .ToList();
            var pending = all.Where(t => t.Status == MotherPlantTransferRules.StatusPending).ToList();
            Incoming = CanReceive ? pending.Where(t => _receiverAccess.CanAccessArea(User, t.DestinationAreaId)).ToList() : new();
            Outgoing = CanSend ? pending.Where(t => _senderAccess.CanAccessArea(User, t.SourceAreaId)).ToList() : new();
            History = all.Where(t => Status == null || t.Status == Status).ToList();
        }

        // receivedDate (2026-10-11): the business date the plants arrived (default today, India);
        // an earlier date needs receiptDateReason -- re-checked in MotherPlantTransferRepository.ReceiveAsync.
        public async Task<IActionResult> OnPostReceiveAsync(int id, decimal receivedQuantity, string? shortfallReason, DateTime? receivedDate, string? receiptDateReason)
        {
            if (!User.HasPermission(ReceivePermission)) return Forbid();
            var (ok, msg, receivedBatchId) = await _repo.ReceiveAsync(id, receivedQuantity, shortfallReason, Actor(), a => _receiverAccess.CanAccessArea(User, a),
                receivedDate, receiptDateReason);
            // 2026-10-13: straight on to "Transplant into Trays" for the batch just received.
            if (ok && receivedBatchId is int batchId)
                TempData["TransplantBatchId"] = batchId;
            return Done(ok, msg, id);
        }

        public async Task<IActionResult> OnPostRejectAsync(int id, string? reason)
        {
            if (!User.HasPermission(ReceivePermission)) return Forbid();
            var (ok, msg) = await _repo.RejectAsync(id, reason, Actor(), a => _receiverAccess.CanAccessArea(User, a));
            return Done(ok, msg, id);
        }

        public async Task<IActionResult> OnPostCancelAsync(int id, string? reason)
        {
            if (!User.HasPermission(SendPermission)) return Forbid();
            var (ok, msg) = await _repo.CancelAsync(id, reason, Actor(), a => _senderAccess.CanAccessArea(User, a));
            return Done(ok, msg, id);
        }

        private MotherPlantTransferRepository.Actor Actor() => new(User.GetUserId(), User.Identity?.Name);

        private IActionResult Done(bool ok, string? msg, int id)
        {
            TempData[ok ? "Success" : "Error"] = msg ?? (ok ? "Done." : "The transfer could not be updated.");
            return RedirectToPage(new { highlight = id });
        }
    }
}
