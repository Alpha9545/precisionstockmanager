using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PlantStockManager.Authorization;
using PlantStockManager.Data;
using PlantStockManager.Models;
using PlantStockManager.Services;
using CuttingPlanModel = PlantStockManager.Models.CuttingPlan;
using MotherPlantModel = PlantStockManager.Models.MotherPlant;

namespace PlantStockManager.Pages.Production.CuttingPlan
{
    public class CreateModel : PageModel
    {
        private readonly CuttingPlanRepository _cuttingPlanRepo;
        private readonly MotherPlantRepository _motherPlantRepo;
        private readonly SupervisorSelectionService _supervisors;
        private readonly MotherPlantAreaScope _areaScope;

        public CreateModel(
            CuttingPlanRepository cuttingPlanRepo,
            MotherPlantRepository motherPlantRepo,
            SupervisorSelectionService supervisors,
            MotherPlantAreaScope areaScope)
        {
            _areaScope = areaScope;
            _cuttingPlanRepo = cuttingPlanRepo;
            _motherPlantRepo = motherPlantRepo;
            _supervisors = supervisors;
        }

        [BindProperty]
        public CuttingPlanModel CuttingPlan { get; set; } = new();

        // Only Active Mother Plants can be planned against -- a completed
        // or removed batch has nothing left to plan cuttings from.
        public List<MotherPlantModel> ActiveMotherPlants { get; set; } = new();
        // Phase 1: Production Area supervisors, narrowed to the chosen Mother Plant's Area.
        public List<SupervisorOption> Supervisors { get; set; } = new();

        public async Task OnGetAsync()
        {
            CuttingPlan.PlannedCuttingDate = DateTime.Today;
            CuttingPlan.Status = "Planned";
            await LoadDropdownsAsync();
        }

        public async Task<IActionResult> OnPostAsync()
        {
            ModelState.Remove("CuttingPlan.PlanNumber");
            ModelState.Remove("CuttingPlan.CreatedBy");
            ModelState.Remove("CuttingPlan.SpeciesId"); // always server-derived, never posted by the form

            if (CuttingPlan.MotherPlantId <= 0)
                ModelState.AddModelError("CuttingPlan.MotherPlantId", "Mother Plant is required.");
            if (CuttingPlan.PlannedQuantity <= 0)
                ModelState.AddModelError("CuttingPlan.PlannedQuantity", "Planned Quantity must be greater than zero.");
            if (CuttingPlan.CuttingRate < 0)
                ModelState.AddModelError("CuttingPlan.CuttingRate", "Cutting Rate cannot be negative.");

            MotherPlantModel? motherPlant = null;
            if (CuttingPlan.MotherPlantId > 0)
            {
                motherPlant = await _motherPlantRepo.GetByIdAsync(CuttingPlan.MotherPlantId);
                if (motherPlant == null)
                {
                    ModelState.AddModelError("CuttingPlan.MotherPlantId", "Selected Mother Plant does not exist.");
                }
                else if (motherPlant.Status != "Active")
                {
                    ModelState.AddModelError("CuttingPlan.MotherPlantId", "Cutting can only be planned against an Active Mother Plant batch.");
                }
            }

            // F1: the Mother Plant's Area must be one of the user's.
            if (motherPlant != null && !await _areaScope.CanAccessAsync(User, motherPlant.Id))
                ModelState.AddModelError("CuttingPlan.MotherPlantId", "You are not authorized to plan cuttings for this Mother Plant's Area.");

            // Phase 1: the supervisor must be eligible for the Mother Plant's Area.
            if (motherPlant != null)
            {
                var supervisorError = await _supervisors.ValidateAsync(SupervisorKind.ProductionArea, motherPlant.AreaId, CuttingPlan.SupervisorId);
                if (supervisorError != null)
                    ModelState.AddModelError("CuttingPlan.SupervisorId", supervisorError);
            }

            if (!ModelState.IsValid || motherPlant == null)
            {
                await LoadDropdownsAsync();
                return Page();
            }

            // SpeciesId is never taken from the client -- always copied from
            // the actual Mother Plant record just loaded above, matching the
            // DB-level CK_CuttingPlans_SpeciesMatchesMotherPlant guarantee.
            CuttingPlan.SpeciesId = motherPlant.SpeciesId;
            CuttingPlan.ResponsiblePersonId = null; // Phase 1: Responsible Person retired
            CuttingPlan.CreatedBy = User.Identity?.Name ?? "System";

            var (success, message, _) = await _cuttingPlanRepo.InsertAsync(CuttingPlan);
            if (!success)
            {
                ModelState.AddModelError(string.Empty, message ?? "Failed to save Cutting Plan.");
                await LoadDropdownsAsync();
                return Page();
            }

            TempData["Success"] = $"Cutting Plan {CuttingPlan.PlanNumber} added successfully.";
            return RedirectToPage("/Production/CuttingPlan/Index");
        }

        private async Task LoadDropdownsAsync()
        {
            var allMotherPlants = await _motherPlantRepo.GetAllAsync(status: "Active");
            ActiveMotherPlants = await _areaScope.FilterAsync(User, allMotherPlants, m => (int?)m.Id); // F1
            Supervisors = await _supervisors.OptionsAsync(SupervisorKind.ProductionArea);
        }
    }
}
