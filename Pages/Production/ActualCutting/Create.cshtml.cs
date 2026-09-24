using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PlantStockManager.Authorization;
using PlantStockManager.Data;
using PlantStockManager.Models;
using ActualCuttingModel = PlantStockManager.Models.ActualCutting;
using CuttingPlanModel = PlantStockManager.Models.CuttingPlan;

namespace PlantStockManager.Pages.Production.ActualCutting
{
    public class CreateModel : PageModel
    {
        private readonly ActualCuttingRepository _actualCuttingRepo;
        private readonly CuttingPlanRepository _cuttingPlanRepo;
        private readonly EmployeeRepository _employeeRepo;
        private readonly MotherPlantAreaScope _areaScope;

        public CreateModel(
            ActualCuttingRepository actualCuttingRepo,
            CuttingPlanRepository cuttingPlanRepo,
            EmployeeRepository employeeRepo,
            MotherPlantAreaScope areaScope)
        {
            _areaScope = areaScope;
            _actualCuttingRepo = actualCuttingRepo;
            _cuttingPlanRepo = cuttingPlanRepo;
            _employeeRepo = employeeRepo;
        }

        [BindProperty]
        public ActualCuttingModel ActualCutting { get; set; } = new();

        public List<CuttingPlanModel> OpenCuttingPlans { get; set; } = new();
        public List<Employee> ResponsiblePersons { get; set; } = new();
        public List<Employee> Supervisors { get; set; } = new();

        public async Task OnGetAsync()
        {
            ActualCutting.CuttingDate = DateTime.Today;
            await LoadDropdownsAsync();
        }

        public async Task<IActionResult> OnPostAsync()
        {
            ModelState.Remove("ActualCutting.ActualCuttingCode");
            ModelState.Remove("ActualCutting.CreatedBy");
            ModelState.Remove("ActualCutting.MotherPlantId"); // server-derived from the Cutting Plan
            ModelState.Remove("ActualCutting.SpeciesId");      // server-derived from the Cutting Plan
            ModelState.Remove("ActualCutting.PlannedQuantity"); // server-derived snapshot

            if (ActualCutting.CuttingPlanId <= 0)
                ModelState.AddModelError("ActualCutting.CuttingPlanId", "Cutting Plan is required.");
            if (ActualCutting.ActualQuantity < 0)
                ModelState.AddModelError("ActualCutting.ActualQuantity", "Actual Quantity cannot be negative.");
            if (ActualCutting.GoodQuantity < 0 || ActualCutting.DamagedQuantity < 0 || ActualCutting.RejectedQuantity < 0)
                ModelState.AddModelError(string.Empty, "Good, Damaged and Rejected quantities cannot be negative.");
            if (!ActualCuttingModel.IsReconciled(ActualCutting.GoodQuantity, ActualCutting.DamagedQuantity, ActualCutting.RejectedQuantity, ActualCutting.ActualQuantity))
                ModelState.AddModelError(string.Empty, "Good + Damaged + Rejected must add up exactly to Actual Quantity.");

            CuttingPlanModel? plan = null;
            if (ActualCutting.CuttingPlanId > 0)
            {
                plan = await _cuttingPlanRepo.GetByIdAsync(ActualCutting.CuttingPlanId);
                if (plan == null)
                    ModelState.AddModelError("ActualCutting.CuttingPlanId", "Selected Cutting Plan does not exist.");
                else if (plan.Status == "Cancelled")
                    ModelState.AddModelError("ActualCutting.CuttingPlanId", "Cannot record Actual Cutting against a Cancelled plan.");
            }

            // F1: the selected plan's Mother Plant Area must be one of the user's.
            if (plan != null && !await _areaScope.CanAccessAsync(User, plan.MotherPlantId))
                ModelState.AddModelError("ActualCutting.CuttingPlanId", "You are not authorized to record cuttings for this Cutting Plan's Area.");

            if (!ModelState.IsValid || plan == null)
            {
                await LoadDropdownsAsync();
                return Page();
            }

            // Traceability fields are always derived from the plan itself,
            // never trusted from the posted form.
            ActualCutting.MotherPlantId = plan.MotherPlantId;
            ActualCutting.SpeciesId = plan.SpeciesId;
            ActualCutting.CreatedBy = User.Identity?.Name ?? "System";

            var (success, message, _) = await _actualCuttingRepo.InsertAsync(ActualCutting);
            if (!success)
            {
                // Covers both the "exceeds planned quantity" business rule
                // and any DB-level constraint failure -- surfaced as a
                // friendly message, never a raw SQL error.
                ModelState.AddModelError(string.Empty, message ?? "Failed to save Actual Cutting.");
                await LoadDropdownsAsync();
                return Page();
            }

            TempData["Success"] = $"Actual Cutting {ActualCutting.ActualCuttingCode} recorded successfully.";
            return RedirectToPage("/Production/ActualCutting/Index");
        }

        private async Task LoadDropdownsAsync()
        {
            OpenCuttingPlans = await _areaScope.FilterAsync(User, await _cuttingPlanRepo.GetOpenForActualCuttingAsync(), c => (int?)c.MotherPlantId);
            var activeUsers = await _employeeRepo.GetAllActiveUsers();
            ResponsiblePersons = activeUsers;
            Supervisors = activeUsers;
        }
    }
}
