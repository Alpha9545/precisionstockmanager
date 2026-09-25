using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PlantStockManager.Authorization;
using PlantStockManager.Data;
using PlantStockManager.Models;
using PlantStockManager.Services;
using PotProductionModel = PlantStockManager.Models.PotProduction;
using PropagationBatchModel = PlantStockManager.Models.PropagationBatch;

namespace PlantStockManager.Pages.Production.PotProduction
{
    public class CreateModel : PageModel
    {
        private readonly PotProductionRepository _potProductionRepo;
        private readonly PropagationBatchRepository _propagationBatchRepo;
        private readonly EmptyPotInventoryRepository _emptyPotInventoryRepo;
        private readonly SupervisorSelectionService _supervisors;
        private readonly AreaAccessService _areaAccessService;
        private readonly MotherPlantAreaScope _areaScope;

        public CreateModel(
            PotProductionRepository potProductionRepo,
            PropagationBatchRepository propagationBatchRepo,
            EmptyPotInventoryRepository emptyPotInventoryRepo,
            SupervisorSelectionService supervisors,
            AreaAccessService areaAccessService,
            MotherPlantAreaScope areaScope)
        {
            _areaScope = areaScope;
            _areaAccessService = areaAccessService;
            _potProductionRepo = potProductionRepo;
            _propagationBatchRepo = propagationBatchRepo;
            _emptyPotInventoryRepo = emptyPotInventoryRepo;
            _supervisors = supervisors;
        }

        [BindProperty]
        public PotProductionModel PotProduction { get; set; } = new();

        public List<PropagationBatchModel> OpenPropagationBatches { get; set; } = new();
        public List<PlantStockManager.Models.EmptyPotInventory> ActivePotSizes { get; set; } = new();
        // Phase 1: Production Area supervisors of the chosen pot pool's Area.
        public List<SupervisorOption> Supervisors { get; set; } = new();

        public async Task OnGetAsync()
        {
            PotProduction.ProductionDate = DateTime.Today;
            await LoadDropdownsAsync();
        }

        public async Task<IActionResult> OnPostAsync()
        {
            ModelState.Remove("PotProduction.ProductionCode");
            ModelState.Remove("PotProduction.CreatedBy");
            ModelState.Remove("PotProduction.MotherPlantId"); // server-derived from the Propagation Batch
            ModelState.Remove("PotProduction.SpeciesId");      // server-derived from the Propagation Batch
            ModelState.Remove("PotProduction.PotSize");        // server-derived from the selected Empty Pot pool
            ModelState.Remove("PotProduction.AreaId");         // server-derived from the selected Empty Pot pool
            ModelState.Remove("PotProduction.Status");

            if (!PotProduction.PropagationBatchId.HasValue || PotProduction.PropagationBatchId.Value <= 0)
                ModelState.AddModelError("PotProduction.PropagationBatchId", "Propagation Batch is required.");
            if (PotProduction.EmptyPotInventoryId <= 0)
                ModelState.AddModelError("PotProduction.EmptyPotInventoryId", "Pot Size / Area is required.");
            if (PotProduction.Quantity <= 0)
                ModelState.AddModelError("PotProduction.Quantity", "Quantity must be greater than zero.");

            if (!ModelState.IsValid)
            {
                await LoadDropdownsAsync();
                return Page();
            }

            // The selected option identifies one specific (PotSize, Area)
            // Empty Pot pool -- PotSize/AreaId are always derived from it
            // server-side, never trusted directly from the form.
            var selectedPool = await _emptyPotInventoryRepo.GetByIdAsync(PotProduction.EmptyPotInventoryId);
            if (selectedPool == null)
            {
                ModelState.AddModelError("PotProduction.EmptyPotInventoryId", "Selected Pot Size / Area no longer exists.");
                await LoadDropdownsAsync();
                return Page();
            }
            PotProduction.PotSize = selectedPool.PotSize;
            PotProduction.AreaId = selectedPool.AreaId;

            // F1: the Empty Pot pool consumed (and the Potted Plant Stock
            // created) belong to selectedPool.AreaId, and the source
            // Propagation Batch belongs to its own/Mother Plant Area -- both
            // were previously unchecked, so any user could consume any
            // Area's pots/batches. Every sibling page (Edit/Details/Index/
            // CreateFromCutting) is already Area-scoped.
            var batch = await _propagationBatchRepo.GetByIdAsync(PotProduction.PropagationBatchId!.Value);
            if (!_areaAccessService.CanAccessArea(User, selectedPool.AreaId)
                || batch == null
                || !await _areaScope.CanAccessAsync(User, batch.MotherPlantId, batch.AreaId))
            {
                ModelState.AddModelError(string.Empty, "You are not authorized to record Pot Production for the selected Area / Propagation Batch.");
                await LoadDropdownsAsync();
                return Page();
            }

            var supervisorError = await _supervisors.ValidateAsync(SupervisorKind.ProductionArea, selectedPool.AreaId, PotProduction.SupervisorId);
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

            var (success, message, _) = await _potProductionRepo.InsertAsync(PotProduction, userId);
            if (!success)
            {
                // Covers the "exceeds Survived Quantity" rule, the "not
                // enough Empty Pot stock" rule, and any DB-level
                // constraint failure -- surfaced as a friendly message,
                // never a raw SQL error.
                ModelState.AddModelError(string.Empty, message ?? "Failed to record Pot Production.");
                await LoadDropdownsAsync();
                return Page();
            }

            TempData["Success"] = $"Pot Production {PotProduction.ProductionCode} recorded successfully. Potted Plant Stock updated.";
            return RedirectToPage("/Production/PotProduction/Index");
        }

        private async Task LoadDropdownsAsync()
        {
            // F1: only the batches / pools of the user's Area(s).
            OpenPropagationBatches = await _areaScope.FilterAsync(User, await _propagationBatchRepo.GetOpenForPotProductionAsync(), pb => (int?)pb.MotherPlantId, pb => pb.AreaId);
            ActivePotSizes = _areaAccessService.FilterByArea(User, await _emptyPotInventoryRepo.GetAllAsync(activeOnly: true), p => p.AreaId);
            Supervisors = await _supervisors.OptionsAsync(SupervisorKind.ProductionArea);
        }
    }
}
