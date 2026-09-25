using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PlantStockManager.Authorization;
using PlantStockManager.Data;
using PlantStockManager.Services;
using PotProductionBatchModel = PlantStockManager.Models.PotProductionBatch;
using CuttingStockModel = PlantStockManager.Models.CuttingStock;

namespace PlantStockManager.Pages.Production.PotProductionBatch
{
    // Phase 31 (Phase 6): plans a new batch -- Cutting Stock -> select
    // cutting quantity -> Pot Size -> Expected Ready Date -> Area
    // (inherited from the Cutting Stock, never chosen). Mirrors
    // PotProduction/CreateFromCutting.cshtml.cs's own shape closely; the
    // only difference is this page creates NO stock movement -- it is a
    // plan, consumed later, one day at a time, by DailyEntry.cshtml.cs.
    public class CreateModel : PageModel
    {
        private readonly PotProductionBatchRepository _batchRepo;
        private readonly CuttingStockRepository _cuttingStockRepo;
        private readonly EmptyPotInventoryRepository _emptyPotInventoryRepo;
        private readonly SupervisorSelectionService _supervisors;
        private readonly AreaAccessService _areaAccessService;

        public CreateModel(
            PotProductionBatchRepository batchRepo,
            CuttingStockRepository cuttingStockRepo,
            EmptyPotInventoryRepository emptyPotInventoryRepo,
            SupervisorSelectionService supervisors,
            AreaAccessService areaAccessService)
        {
            _batchRepo = batchRepo;
            _cuttingStockRepo = cuttingStockRepo;
            _emptyPotInventoryRepo = emptyPotInventoryRepo;
            _supervisors = supervisors;
            _areaAccessService = areaAccessService;
        }

        [BindProperty]
        public PotProductionBatchModel Batch { get; set; } = new();

        public List<CuttingStockModel> AvailableCuttingStocks { get; set; } = new();
        public List<PlantStockManager.Models.EmptyPotInventory> ActivePotSizes { get; set; } = new();
        public List<KindedSupervisorOption> Supervisors { get; set; } = new();

        public async Task OnGetAsync()
        {
            Batch.ExpectedReadyDate = DateTime.Today.AddDays(21);
            await LoadDropdownsAsync();
        }

        public async Task<IActionResult> OnPostAsync()
        {
            ModelState.Remove("Batch.BatchCode");
            ModelState.Remove("Batch.CreatedBy");
            ModelState.Remove("Batch.SpeciesId");            // server-derived from the source Cutting Stock
            ModelState.Remove("Batch.PotSize");
            ModelState.Remove("Batch.AreaId");                 // server-derived from the source Cutting Stock
            ModelState.Remove("Batch.Status");

            if (Batch.SourceCuttingStockId <= 0)
                ModelState.AddModelError("Batch.SourceCuttingStockId", "Source Cutting Stock is required.");
            if (Batch.EmptyPotInventoryId <= 0)
                ModelState.AddModelError("Batch.EmptyPotInventoryId", "Pot Size is required.");
            if (Batch.PlannedCuttingQuantity <= 0)
                ModelState.AddModelError("Batch.PlannedCuttingQuantity", "Planned Cutting Quantity must be greater than zero.");
            if (Batch.ExpectedReadyDate == default)
                ModelState.AddModelError("Batch.ExpectedReadyDate", "Expected Ready Date is required.");

            if (!ModelState.IsValid)
            {
                await LoadDropdownsAsync();
                return Page();
            }

            // Re-fetch the source Cutting Stock server-side and check
            // Area access BEFORE calling the repository -- a posted
            // SourceCuttingStockId is never trusted to imply an Area the
            // current user is actually allowed to plan production from.
            var sourceStock = await _cuttingStockRepo.GetByIdAsync(Batch.SourceCuttingStockId);
            if (sourceStock == null)
            {
                ModelState.AddModelError("Batch.SourceCuttingStockId", "Selected Cutting Stock no longer exists.");
                await LoadDropdownsAsync();
                return Page();
            }
            if (!_areaAccessService.CanAccessArea(User, sourceStock.AreaId))
            {
                ModelState.AddModelError(string.Empty, "You are not authorized to plan Pot Production from the selected Area.");
                await LoadDropdownsAsync();
                return Page();
            }

            var selectedPool = await _emptyPotInventoryRepo.GetByIdAsync(Batch.EmptyPotInventoryId);
            if (selectedPool == null)
            {
                ModelState.AddModelError("Batch.EmptyPotInventoryId", "Selected Pot Size no longer exists.");
                await LoadDropdownsAsync();
                return Page();
            }
            Batch.PotSize = selectedPool.PotSize;

            var supervisorError = await _supervisors.ValidateForAreaAsync(sourceStock.AreaId, SupervisorKind.ProductionArea, Batch.SupervisorId);
            if (supervisorError != null)
            {
                ModelState.AddModelError("Batch.SupervisorId", supervisorError);
                await LoadDropdownsAsync();
                return Page();
            }

            Batch.CreatedBy = User.Identity?.Name ?? "System";
            var userIdClaim = User.FindFirst("UserId")?.Value;
            int? userId = int.TryParse(userIdClaim, out var parsedUserId) ? parsedUserId : null;
            Batch.CreatedById = userId;

            var (success, message, _) = await _batchRepo.InsertAsync(Batch, userId, areaId => _areaAccessService.CanAccessArea(User, areaId));
            if (!success)
            {
                ModelState.AddModelError(string.Empty, message ?? "Failed to plan the Pot Production Batch.");
                await LoadDropdownsAsync();
                return Page();
            }

            TempData["Success"] = $"Pot Production Batch {Batch.BatchCode} planned. Record daily production against it as cuttings are potted.";
            return RedirectToPage("/Production/PotProductionBatch/Details", new { id = Batch.Id });
        }

        private async Task LoadDropdownsAsync()
        {
            var allStock = await _cuttingStockRepo.GetAllAsync();
            AvailableCuttingStocks = allStock
                .Where(c => _areaAccessService.CanAccessArea(User, c.AreaId) && c.AvailableQuantity > 0)
                .ToList();

            ActivePotSizes = await _emptyPotInventoryRepo.GetAllAsync(activeOnly: true);
            Supervisors = await _supervisors.AllAreaKindOptionsAsync();
        }
    }
}
