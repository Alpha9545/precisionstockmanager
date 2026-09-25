using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PlantStockManager.Authorization;
using PlantStockManager.Data;
using PlantStockManager.Models;
using PlantStockManager.Services;
using PotProductionModel = PlantStockManager.Models.PotProduction;
// Alias required: Pages/Production/CuttingStock/ makes "CuttingStock" a
// sibling namespace under PlantStockManager.Pages.Production, which
// shadows the bare Models.CuttingStock type (CS0118). Same established
// fix as the PotProductionModel alias above.
using CuttingStockModel = PlantStockManager.Models.CuttingStock;

namespace PlantStockManager.Pages.Production.PotProduction
{
    // Phase 19 ("Phase D"): the Cutting Stock-sourced twin of
    // Create.cshtml.cs. Kept as its own dedicated page -- mirroring the
    // established pattern of Pages/Production/MainOfficeIssue living
    // alongside the generic InternalTransfer pages -- rather than a
    // single form with a source toggle, so the new Area-authorization
    // logic stays isolated from the legacy Propagation Batch path and
    // neither page's validation can accidentally affect the other.
    public class CreateFromCuttingModel : PageModel
    {
        private readonly PotProductionRepository _potProductionRepo;
        private readonly CuttingStockRepository _cuttingStockRepo;
        private readonly EmptyPotInventoryRepository _emptyPotInventoryRepo;
        private readonly SupervisorSelectionService _supervisors;
        private readonly AreaAccessService _areaAccessService;

        public CreateFromCuttingModel(
            PotProductionRepository potProductionRepo,
            CuttingStockRepository cuttingStockRepo,
            EmptyPotInventoryRepository emptyPotInventoryRepo,
            SupervisorSelectionService supervisors,
            AreaAccessService areaAccessService)
        {
            _potProductionRepo = potProductionRepo;
            _cuttingStockRepo = cuttingStockRepo;
            _emptyPotInventoryRepo = emptyPotInventoryRepo;
            _supervisors = supervisors;
            _areaAccessService = areaAccessService;
        }

        [BindProperty]
        public PotProductionModel PotProduction { get; set; } = new();

        public List<CuttingStockModel> AvailableCuttingStocks { get; set; } = new();
        public List<PlantStockManager.Models.EmptyPotInventory> ActivePotSizes { get; set; } = new();
        // Phase 1: supervisors of the source Cutting Stock's Area (narrowed in
        // the browser from the chosen stock, re-checked on save).
        public List<KindedSupervisorOption> Supervisors { get; set; } = new();

        public async Task OnGetAsync()
        {
            PotProduction.ProductionDate = DateTime.Today;
            await LoadDropdownsAsync();
        }

        public async Task<IActionResult> OnPostAsync()
        {
            ModelState.Remove("PotProduction.ProductionCode");
            ModelState.Remove("PotProduction.CreatedBy");
            ModelState.Remove("PotProduction.SpeciesId");      // server-derived from the source Cutting Stock
            ModelState.Remove("PotProduction.PotSize");        // server-derived from the selected Empty Pot pool
            ModelState.Remove("PotProduction.AreaId");         // server-derived from the source Cutting Stock
            ModelState.Remove("PotProduction.Status");
            ModelState.Remove("PotProduction.PropagationBatchId"); // this path never uses it
            ModelState.Remove("PotProduction.MotherPlantId");       // this path never uses it

            if (!PotProduction.SourceCuttingStockId.HasValue || PotProduction.SourceCuttingStockId.Value <= 0)
                ModelState.AddModelError("PotProduction.SourceCuttingStockId", "Source Cutting Stock is required.");
            if (PotProduction.EmptyPotInventoryId <= 0)
                ModelState.AddModelError("PotProduction.EmptyPotInventoryId", "Pot Size is required.");
            if (!PotProduction.CuttingQuantityConsumed.HasValue || PotProduction.CuttingQuantityConsumed.Value <= 0)
                ModelState.AddModelError("PotProduction.CuttingQuantityConsumed", "Cuttings Consumed must be greater than zero.");
            if (PotProduction.Quantity <= 0)
                ModelState.AddModelError("PotProduction.Quantity", "Potted Plants Produced must be greater than zero.");
            if (PotProduction.CuttingQuantityConsumed.HasValue && PotProduction.Quantity > 0
                && PotProduction.CuttingQuantityConsumed.Value < PotProduction.Quantity)
                ModelState.AddModelError("PotProduction.Quantity", "Potted Plants Produced cannot exceed Cuttings Consumed (loss cannot be negative).");

            if (!ModelState.IsValid)
            {
                await LoadDropdownsAsync();
                return Page();
            }

            // Re-fetch the source Cutting Stock server-side and check
            // Area access BEFORE calling the repository -- a posted
            // SourceCuttingStockId is never trusted to imply an Area the
            // current user is actually allowed to draw from.
            var sourceStock = await _cuttingStockRepo.GetByIdAsync(PotProduction.SourceCuttingStockId!.Value);
            if (sourceStock == null)
            {
                ModelState.AddModelError("PotProduction.SourceCuttingStockId", "Selected Cutting Stock no longer exists.");
                await LoadDropdownsAsync();
                return Page();
            }
            if (!_areaAccessService.CanAccessArea(User, sourceStock.AreaId))
            {
                ModelState.AddModelError(string.Empty, "You are not authorized to draw Cutting Stock from the selected Area.");
                await LoadDropdownsAsync();
                return Page();
            }

            // The selected option identifies one specific Empty Pot pool
            // -- PotSize is always derived from it server-side, never
            // trusted directly from the form. The repository itself
            // re-validates that this Pot Size's pool actually belongs to
            // the source Cutting Stock's Area (never trusted from here
            // either).
            var selectedPool = await _emptyPotInventoryRepo.GetByIdAsync(PotProduction.EmptyPotInventoryId);
            if (selectedPool == null)
            {
                ModelState.AddModelError("PotProduction.EmptyPotInventoryId", "Selected Pot Size no longer exists.");
                await LoadDropdownsAsync();
                return Page();
            }
            PotProduction.PotSize = selectedPool.PotSize;

            // Phase 1: supervisor must be eligible for the cutting stock's Area.
            var supervisorError = await _supervisors.ValidateForAreaAsync(sourceStock.AreaId, SupervisorKind.ProductionArea, PotProduction.SupervisorId);
            if (supervisorError != null)
            {
                ModelState.AddModelError("PotProduction.SupervisorId", supervisorError);
                await LoadDropdownsAsync();
                return Page();
            }
            PotProduction.ResponsiblePersonId = null;   // Phase 1: no longer entered

            PotProduction.CreatedBy = User.Identity?.Name ?? "System";
            var userIdClaim = User.FindFirst("UserId")?.Value;
            int? userId = int.TryParse(userIdClaim, out var parsedUserId) ? parsedUserId : null;

            var (success, message, _) = await _potProductionRepo.InsertFromCuttingStockAsync(PotProduction, userId);
            if (!success)
            {
                // Covers "insufficient available Cutting Stock", "not
                // enough Empty Pot stock", the Cuttings-vs-Quantity rule,
                // and any DB-level constraint failure -- surfaced as a
                // friendly message, never a raw SQL error.
                ModelState.AddModelError(string.Empty, message ?? "Failed to record Pot Production.");
                await LoadDropdownsAsync();
                return Page();
            }

            TempData["Success"] = $"Pot Production {PotProduction.ProductionCode} recorded successfully from Cutting Stock. Potted Plant Stock updated.";
            return RedirectToPage("/Production/PotProduction/Index");
        }

        private async Task LoadDropdownsAsync()
        {
            var allStock = await _cuttingStockRepo.GetAllAsync();
            // CanAccessArea already grants full-access roles (Admin/
            // Management/MainOfficeOfficer) every Area, so no separate
            // HasFullAreaAccess branch is needed here.
            AvailableCuttingStocks = allStock
                .Where(c => _areaAccessService.CanAccessArea(User, c.AreaId) && c.AvailableQuantity > 0)
                .ToList();

            ActivePotSizes = await _emptyPotInventoryRepo.GetAllAsync(activeOnly: true);
            Supervisors = await _supervisors.AllAreaKindOptionsAsync();
        }
    }
}
