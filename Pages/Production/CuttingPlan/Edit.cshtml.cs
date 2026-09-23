using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PlantStockManager.Data;
using PlantStockManager.Models;
using CuttingPlanModel = PlantStockManager.Models.CuttingPlan;
using MotherPlantModel = PlantStockManager.Models.MotherPlant;

namespace PlantStockManager.Pages.Production.CuttingPlan
{
    public class EditModel : PageModel
    {
        private readonly CuttingPlanRepository _cuttingPlanRepo;
        private readonly MotherPlantRepository _motherPlantRepo;
        private readonly EmployeeRepository _employeeRepo;

        public EditModel(
            CuttingPlanRepository cuttingPlanRepo,
            MotherPlantRepository motherPlantRepo,
            EmployeeRepository employeeRepo)
        {
            _cuttingPlanRepo = cuttingPlanRepo;
            _motherPlantRepo = motherPlantRepo;
            _employeeRepo = employeeRepo;
        }

        [BindProperty]
        public CuttingPlanModel CuttingPlan { get; set; } = new();

        // The plan's own Mother Plant is always included even if it is no
        // longer Active, so editing an existing plan never loses its
        // current selection; other choices are still restricted to Active
        // batches.
        public List<MotherPlantModel> SelectableMotherPlants { get; set; } = new();
        public List<Employee> ResponsiblePersons { get; set; } = new();
        public List<Employee> Supervisors { get; set; } = new();

        public async Task<IActionResult> OnGetAsync(int id)
        {
            var existing = await _cuttingPlanRepo.GetByIdAsync(id);
            if (existing == null)
                return RedirectToPage("/Production/CuttingPlan/Index");

            CuttingPlan = existing;
            await LoadDropdownsAsync(existing.MotherPlantId);
            return Page();
        }

        public async Task<IActionResult> OnPostAsync()
        {
            ModelState.Remove("CuttingPlan.PlanNumber");
            ModelState.Remove("CuttingPlan.CreatedBy");
            ModelState.Remove("CuttingPlan.SpeciesId");

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
            }

            if (!ModelState.IsValid || motherPlant == null)
            {
                await LoadDropdownsAsync(CuttingPlan.MotherPlantId);
                return Page();
            }

            CuttingPlan.SpeciesId = motherPlant.SpeciesId;
            CuttingPlan.ModifiedBy = User.Identity?.Name ?? "System";

            var (success, message) = await _cuttingPlanRepo.UpdateAsync(CuttingPlan);
            if (!success)
            {
                ModelState.AddModelError(string.Empty, message ?? "Failed to update Cutting Plan.");
                await LoadDropdownsAsync(CuttingPlan.MotherPlantId);
                return Page();
            }

            TempData["Success"] = $"Cutting Plan {CuttingPlan.PlanNumber} updated successfully.";
            return RedirectToPage("/Production/CuttingPlan/Index");
        }

        private async Task LoadDropdownsAsync(int currentMotherPlantId)
        {
            var active = await _motherPlantRepo.GetAllAsync(status: "Active");
            if (!active.Any(m => m.Id == currentMotherPlantId))
            {
                var current = await _motherPlantRepo.GetByIdAsync(currentMotherPlantId);
                if (current != null)
                    active.Add(current);
            }
            SelectableMotherPlants = active;

            var activeUsers = await _employeeRepo.GetAllActiveUsers();
            ResponsiblePersons = activeUsers;
            Supervisors = activeUsers;
        }
    }
}
