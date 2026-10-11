using System.Security.Cryptography;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PlantStockManager.Authorization;
using PlantStockManager.Data;
using PlantStockManager.Models;
using PlantStockManager.Services;

namespace PlantStockManager.Pages.Production.MaterialStock
{
    // Tray Adjustment (2026-10-10, TrayStock.Adjust: Sowing Supervisor + Admin). Corrects an
    // empty-tray balance after a physical count with ONE compensating 'Adjustment' ledger row --
    // never by editing or deleting history. Whole trays, +/-, a mandatory reason, never below
    // zero, only in an active, non-Outlet Area the user is assigned to (AreaAccessService),
    // re-checked on POST and inside TrayStockRepository.AdjustAsync under the pool lock.
    // No opening balance is ever created automatically: every row here is a user's entry.
    public class TrayAdjustModel : PageModel
    {
        private readonly TrayStockRepository _trayStockRepo;
        private readonly AreaRepository _areaRepo;
        private readonly PolyhouseRepository _polyhouseRepo;
        private readonly AreaAccessService _areaAccess;

        public TrayAdjustModel(TrayStockRepository trayStockRepo, AreaRepository areaRepo, PolyhouseRepository polyhouseRepo, AreaAccessService areaAccess)
        {
            _trayStockRepo = trayStockRepo;
            _areaRepo = areaRepo;
            _polyhouseRepo = polyhouseRepo;
            _areaAccess = areaAccess;
        }

        [BindProperty] public int AreaId { get; set; }
        [BindProperty] public int PolyhouseId { get; set; }
        [BindProperty] public string? TraySize { get; set; }
        [BindProperty] public string Direction { get; set; } = "Remove";
        [BindProperty] public decimal? Trays { get; set; }
        [BindProperty] public DateTime BusinessDate { get; set; }
        [BindProperty] public string? Reason { get; set; }
        [BindProperty] public int SubmissionToken { get; set; }

        public List<Area> Areas { get; private set; } = new();
        public List<Polyhouse> Polyhouses { get; private set; } = new();
        public List<Models.TrayStock> Pools { get; private set; } = new();
        public IReadOnlyList<string> TraySizes => DirectSowingRules.CavityTypes;
        public DateTime Today => AppClock.Today;

        public async Task OnGetAsync(int? areaId, int? polyhouseId, string? cavity)
        {
            await LoadAsync();
            SubmissionToken = RandomNumberGenerator.GetInt32(1, int.MaxValue);
            BusinessDate = Today;
            AreaId = areaId ?? (Areas.Count == 1 ? Areas[0].Id : 0);
            PolyhouseId = polyhouseId ?? 0;
            TraySize = cavity;
        }

        public async Task<IActionResult> OnPostAsync()
        {
            if (!User.CanAdjustTrays()) return Forbid();
            ModelState.Clear();
            await LoadAsync();
            if (AreaId <= 0)
                ModelState.AddModelError(nameof(AreaId), "Area is required.");
            else if (!Areas.Any(a => a.Id == AreaId))
                ModelState.AddModelError(nameof(AreaId), "You are not authorized to adjust Tray Stock for the selected Area.");
            if (PolyhouseId <= 0)
                ModelState.AddModelError(nameof(PolyhouseId), "Polyhouse is required.");
            else if (AreaId > 0 && !Polyhouses.Any(p => p.Id == PolyhouseId && p.AreaId == AreaId))
                ModelState.AddModelError(nameof(PolyhouseId), "Selected Polyhouse does not belong to the selected Area.");
            if (!DirectSowingRules.IsValidCavityType(TraySize))
                ModelState.AddModelError(nameof(TraySize), "Cavity is required.");
            if (Direction != "Add" && Direction != "Remove")
                ModelState.AddModelError(nameof(Direction), "Choose whether to add or remove trays.");
            if (Trays is not > 0 || !DirectSowingRules.IsWholeNumber(Trays.Value))
                ModelState.AddModelError(nameof(Trays), "Enter a whole number of trays greater than zero.");
            if (string.IsNullOrWhiteSpace(Reason))
                ModelState.AddModelError(nameof(Reason), "A reason is required for every adjustment.");
            if (BusinessDate == default)
                ModelState.AddModelError(nameof(BusinessDate), "Date is required.");
            else if (BusinessDate.Date > Today)
                ModelState.AddModelError(nameof(BusinessDate), LedgerDates.FutureDateMessage);
            if (!ModelState.IsValid)
                return Page();

            var delta = Direction == "Add" ? Trays!.Value : -Trays!.Value;
            var allowed = Areas.Select(a => a.Id).ToHashSet();
            var (success, message, duplicate, balance) = await _trayStockRepo.AdjustAsync(AreaId, PolyhouseId, TraySize!, delta,
                BusinessDate.Date, SubmissionToken, Reason, User.GetUserId(), User.Identity?.Name, id => allowed.Contains(id));
            if (!success)
            {
                ModelState.AddModelError(string.Empty, message ?? "Failed to save the adjustment.");
                return Page();
            }

            var where = $"{Areas.First(a => a.Id == AreaId).Name}, {Polyhouses.First(p => p.Id == PolyhouseId).Name}, {TraySize}";
            TempData["Success"] = duplicate
                ? $"This adjustment was already saved -- nothing was changed twice. {where}: {QuantityFormat.Qty(balance)} trays."
                : $"Adjusted {where} by {(delta > 0 ? "+" : "")}{QuantityFormat.Qty(delta)} trays. Balance now: {QuantityFormat.Qty(balance)} trays.";
            return RedirectToPage("/Production/MaterialStock/Index",
                new { Material = MaterialStockRules.TraysKind, AreaId, PolyhouseId, Cavity = TraySize });
        }

        private async Task LoadAsync()
        {
            Areas = (await _areaRepo.GetAllAreas())
                .Where(a => a.IsActive && a.AreaType != OutletRules.AreaType && _areaAccess.CanAccessRequiredArea(User, a.Id))
                .OrderBy(a => a.Name).ToList();
            var areaIds = Areas.Select(a => a.Id).ToHashSet();
            Polyhouses = (await _polyhouseRepo.GetAllPolyhouses())
                .Where(p => p.AreaId.HasValue && areaIds.Contains(p.AreaId.Value))
                .OrderBy(p => p.Name).ToList();
            Pools = (await _trayStockRepo.GetAllAsync(activeOnly: true))
                .Where(t => areaIds.Contains(t.AreaId) && t.PolyhouseId.HasValue)
                .ToList();
        }
    }
}
