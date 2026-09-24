using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PlantStockManager.Authorization;
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
        private readonly MotherPlantAreaScope _areaScope;

        public EditModel(
            CuttingPlanRepository cuttingPlanRepo,
            MotherPlantRepository motherPlantRepo,
            EmployeeRepository employeeRepo,
            MotherPlantAreaScope areaScope)
        {
            _areaScope = areaScope;
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

            // F1: Area scope via the plan's Mother Plant (MotherPlantAreaScope).
            if (!await _areaScope.CanAccessAsync(User, existing.MotherPlantId))
            {
                TempData["Error"] = "You are not authorized to edit this Cutting Plan record.";
                return RedirectToPage("/Production/CuttingPlan/Index");
            }

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

            // F1: this handler used the POSTED CuttingPlan.Id without ever
            // loading the stored record, so any user could overwrite any
            // Area's plan. Load it and check its CURRENT Mother Plant Area,
            // then the (possibly changed) new Mother Plant's Area as well.
            var existingPlan = await _cuttingPlanRepo.GetByIdAsync(CuttingPlan.Id);
            if (existingPlan == null || !await _areaScope.CanAccessAsync(User, existingPlan.MotherPlantId))
            {
                TempData["Error"] = "You are not authorized to edit this Cutting Plan record.";
                return RedirectToPage("/Production/CuttingPlan/Index");
            }

            MotherPlantModel? motherPlant = null;
            if (CuttingPlan.MotherPlantId > 0)
            {
                motherPlant = await _motherPlantRepo.GetByIdAsync(CuttingPlan.MotherPlantId);
                if (motherPlant == null)
                {
                    ModelState.AddModelError("CuttingPlan.MotherPlantId", "Selected Mother Plant does not exist.");
                }
                else if (!await _areaScope.CanAccessAsync(User, motherPlant.Id))
                {
                    ModelState.AddModelError("CuttingPlan.MotherPlantId", "You are not authorized to plan cuttings for this Mother Plant's Area.");
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
            // F1: other choices limited to the user's Areas; the plan's own
            // current Mother Plant stays selectable (see comment above).
            SelectableMotherPlants = (await _areaScope.FilterAsync(User, active, m => (int?)m.Id))
                .Union(active.Where(m => m.Id == currentMotherPlantId))
                .ToList();

            var activeUsers = await _employeeRepo.GetAllActiveUsers();
            ResponsiblePersons = activeUsers;
            Supervisors = activeUsers;
        }
    }
}
