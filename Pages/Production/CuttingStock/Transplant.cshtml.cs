using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PlantStockManager.Authorization;
using PlantStockManager.Data;
using PlantStockManager.Models;
using PlantStockManager.Services;
// Alias required: Pages/Production/InternalTransfer/ makes "InternalTransfer"
// a sibling namespace under PlantStockManager.Pages.Production, which
// shadows the bare Models.InternalTransfer type (CS0118). Same fix already
// used elsewhere in this codebase (see PotProduction/CreateFromCutting.cshtml.cs).
using InternalTransferModel = PlantStockManager.Models.InternalTransfer;

namespace PlantStockManager.Pages.Production.CuttingStock
{
    // Step 2 of 2 (Model B) -- opened IMMEDIATELY after Confirm Receipt.
    // This is the ONLY point Cutting stock actually moves: one-sided,
    // source CuttingStock down by ConfirmedQuantity, nothing credited to
    // the Destination Polyhouse. If the user leaves this page without
    // submitting, the transfer simply stays 'ConfirmedAwaitingTransplant'
    // and keeps showing up on Pending Transplants -- this page's GET has
    // no side effects, so that guarantee falls out naturally.
    public class TransplantModel : PageModel
    {
        private readonly InternalTransferRepository _internalTransferRepo;
        private readonly AreaRepository _areaRepo;
        private readonly SupervisorSelectionService _supervisors;
        private readonly AreaAccessService _areaAccessService;

        public TransplantModel(InternalTransferRepository internalTransferRepo, AreaRepository areaRepo, SupervisorSelectionService supervisors, AreaAccessService areaAccessService)
        {
            _areaAccessService = areaAccessService;
            _internalTransferRepo = internalTransferRepo;
            _areaRepo = areaRepo;
            _supervisors = supervisors;
        }

        public InternalTransferModel? Transfer { get; set; }
        public List<Area> DestinationAreas { get; set; } = new();
        // Phase 1: supervisors of every Area kind; site.js shows only those
        // who may supervise the chosen destination Area (re-checked on save).
        public List<KindedSupervisorOption> Supervisors { get; set; } = new();

        [BindProperty]
        public int DestinationPolyhouseAreaId { get; set; }

        [BindProperty]
        public int DestinationSupervisorId { get; set; }

        [BindProperty]
        public DateTime TransplantDate { get; set; } = DateTime.Today;

        [BindProperty]
        public string? Remarks { get; set; }

        public async Task<IActionResult> OnGetAsync(int id)
        {
            Transfer = await _internalTransferRepo.GetByIdAsync(id);
            if (Transfer == null || Transfer.StockType != "Cutting")
            {
                TempData["Error"] = "Transfer not found.";
                return RedirectToPage("/Production/CuttingStock/PendingTransplants");
            }
            // F1: only the Main Office Area the transfer is waiting at (or a
            // cross-Area role) may route it.
            if (!_areaAccessService.CanAccessRequiredArea(User, Transfer.PendingConfirmationAreaId))
            {
                TempData["Error"] = "You are not authorized to transplant that transfer.";
                return RedirectToPage("/Production/CuttingStock/PendingTransplants");
            }
            if (Transfer.Status == "Transplanted")
            {
                TempData["Success"] = "This transfer has already been transplanted.";
                return RedirectToPage("/Production/CuttingStock/PendingTransplants");
            }
            if (Transfer.Status != "ConfirmedAwaitingTransplant")
            {
                TempData["Error"] = $"This transfer is '{Transfer.Status}' and cannot be transplanted right now.";
                return RedirectToPage("/Production/CuttingStock/PendingConfirmations");
            }

            TransplantDate = DateTime.Today;
            await LoadDropdownsAsync(Transfer);
            return Page();
        }

        public async Task<IActionResult> OnPostAsync(int id)
        {
            Transfer = await _internalTransferRepo.GetByIdAsync(id);
            if (Transfer == null || Transfer.StockType != "Cutting" || Transfer.Status != "ConfirmedAwaitingTransplant")
            {
                TempData["Error"] = "This transfer is no longer awaiting transplant.";
                return RedirectToPage("/Production/CuttingStock/PendingTransplants");
            }
            if (!_areaAccessService.CanAccessRequiredArea(User, Transfer.PendingConfirmationAreaId))
            {
                TempData["Error"] = "You are not authorized to transplant that transfer.";
                return RedirectToPage("/Production/CuttingStock/PendingTransplants");
            }

            if (DestinationPolyhouseAreaId <= 0)
                ModelState.AddModelError(nameof(DestinationPolyhouseAreaId), "Destination Polyhouse is required.");
            if (DestinationSupervisorId <= 0)
                ModelState.AddModelError(nameof(DestinationSupervisorId), "Destination Supervisor is required.");
            if (TransplantDate == default)
                ModelState.AddModelError(nameof(TransplantDate), "Transplant Date is required.");

            // F1: the posted destination/supervisor ids were trusted as-is.
            // They must be one of the options this page actually offers
            // (a Polyhouse-linked Area other than the source; an active user).
            if (ModelState.IsValid)
            {
                await LoadDropdownsAsync(Transfer);
                if (!DestinationAreas.Any(a => a.Id == DestinationPolyhouseAreaId))
                    ModelState.AddModelError(nameof(DestinationPolyhouseAreaId), "Select a valid Destination Polyhouse.");
                else
                {
                    // Phase 1: must be an eligible supervisor of the destination Area.
                    var supervisorError = await _supervisors.ValidateForAreaAsync(
                        DestinationPolyhouseAreaId, SupervisorKind.ProductionArea, DestinationSupervisorId, required: true);
                    if (supervisorError != null)
                        ModelState.AddModelError(nameof(DestinationSupervisorId), supervisorError);
                }
            }

            if (!ModelState.IsValid)
            {
                await LoadDropdownsAsync(Transfer);
                return Page();
            }

            var userIdClaim = User.FindFirst("UserId")?.Value;
            int? userId = int.TryParse(userIdClaim, out var parsedUserId) ? parsedUserId : null;
            var createdBy = User.Identity?.Name ?? "System";

            var (success, message) = await _internalTransferRepo.ConfirmTransplantAsync(
                id, DestinationPolyhouseAreaId, DestinationSupervisorId, TransplantDate, Remarks, userId, createdBy);
            if (!success)
            {
                ModelState.AddModelError(string.Empty, message ?? "Failed to confirm transplant.");
                await LoadDropdownsAsync(Transfer);
                return Page();
            }

            TempData["Success"] = $"Transplanted {Transfer.ConfirmedQuantity:N2} of {Transfer.SpeciesName}.";
            return RedirectToPage("/Production/CuttingStock/PendingTransplants");
        }

        private async Task LoadDropdownsAsync(InternalTransferModel transfer)
        {
            var allAreas = await _areaRepo.GetAllAreas();
            // Destination Polyhouse = an Area actually linked to a real
            // Polyhouse (PolyhouseId set) -- not just "any non-MainOffice
            // Area." MainOffice Areas have no PolyhouseId (Phase 14) and
            // are excluded by this same condition, so no separate
            // AreaType check is needed.
            DestinationAreas = allAreas.Where(a => a.PolyhouseId.HasValue && a.Id != transfer.SourceAreaId).ToList();
            Supervisors = await _supervisors.AllAreaKindOptionsAsync();
        }
    }
}
