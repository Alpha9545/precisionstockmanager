using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PlantStockManager.Authorization;
using PlantStockManager.Data;
using PlantStockManager.Models;
using ActualCuttingModel = PlantStockManager.Models.ActualCutting;

namespace PlantStockManager.Pages.Production.ActualCutting
{
    public class EditModel : PageModel
    {
        private readonly ActualCuttingRepository _actualCuttingRepo;
        private readonly EmployeeRepository _employeeRepo;
        private readonly MotherPlantAreaScope _areaScope;

        public EditModel(ActualCuttingRepository actualCuttingRepo, EmployeeRepository employeeRepo, MotherPlantAreaScope areaScope)
        {
            _areaScope = areaScope;
            _actualCuttingRepo = actualCuttingRepo;
            _employeeRepo = employeeRepo;
        }

        // The Cutting Plan / Mother Plant / Species linkage is fixed once
        // an Actual Cutting record exists -- editing lets you correct
        // quantities, dates, people and remarks, but never reassign which
        // plan/batch this record traces back to. That keeps the
        // traceability chain (Mother Plant -> Cutting Plan -> Actual
        // Cutting) immutable after creation.
        [BindProperty]
        public ActualCuttingModel ActualCutting { get; set; } = new();

        public List<Employee> ResponsiblePersons { get; set; } = new();
        public List<Employee> Supervisors { get; set; } = new();

        public async Task<IActionResult> OnGetAsync(int id)
        {
            var existing = await _actualCuttingRepo.GetByIdAsync(id);
            if (existing == null)
                return RedirectToPage("/Production/ActualCutting/Index");

            // F1: Area scope via the record's Mother Plant (MotherPlantAreaScope).
            if (!await _areaScope.CanAccessAsync(User, existing.MotherPlantId))
            {
                TempData["Error"] = "You are not authorized to edit this Actual Cutting record.";
                return RedirectToPage("/Production/ActualCutting/Index");
            }

            ActualCutting = existing;
            await LoadDropdownsAsync();
            return Page();
        }

        public async Task<IActionResult> OnPostAsync()
        {
            ModelState.Remove("ActualCutting.ActualCuttingCode");
            ModelState.Remove("ActualCutting.CreatedBy");
            ModelState.Remove("ActualCutting.MotherPlantId");
            ModelState.Remove("ActualCutting.SpeciesId");
            ModelState.Remove("ActualCutting.CuttingPlanId");
            ModelState.Remove("ActualCutting.PlannedQuantity");

            if (ActualCutting.ActualQuantity < 0)
                ModelState.AddModelError("ActualCutting.ActualQuantity", "Actual Quantity cannot be negative.");
            if (ActualCutting.GoodQuantity < 0 || ActualCutting.DamagedQuantity < 0 || ActualCutting.RejectedQuantity < 0)
                ModelState.AddModelError(string.Empty, "Good, Damaged and Rejected quantities cannot be negative.");
            if (!ActualCuttingModel.IsReconciled(ActualCutting.GoodQuantity, ActualCutting.DamagedQuantity, ActualCutting.RejectedQuantity, ActualCutting.ActualQuantity))
                ModelState.AddModelError(string.Empty, "Good + Damaged + Rejected must add up exactly to Actual Quantity.");

            // Re-load the immutable linkage fields from the DB record --
            // never trust hidden-field values from the client for these.
            var existing = await _actualCuttingRepo.GetByIdAsync(ActualCutting.Id);
            if (existing == null)
            {
                ModelState.AddModelError(string.Empty, "Actual Cutting record not found.");
                await LoadDropdownsAsync();
                return Page();
            }
            // F1: re-check Area scope against the STORED record (the posted
            // Id is otherwise the only input deciding which record changes).
            if (!await _areaScope.CanAccessAsync(User, existing.MotherPlantId))
            {
                TempData["Error"] = "You are not authorized to edit this Actual Cutting record.";
                return RedirectToPage("/Production/ActualCutting/Index");
            }
            ActualCutting.CuttingPlanId = existing.CuttingPlanId;
            ActualCutting.MotherPlantId = existing.MotherPlantId;
            ActualCutting.SpeciesId = existing.SpeciesId;

            if (!ModelState.IsValid)
            {
                ActualCutting.CuttingPlanNumber = existing.CuttingPlanNumber;
                ActualCutting.MotherPlantCode = existing.MotherPlantCode;
                ActualCutting.SpeciesName = existing.SpeciesName;
                await LoadDropdownsAsync();
                return Page();
            }

            ActualCutting.ModifiedBy = User.Identity?.Name ?? "System";

            var (success, message) = await _actualCuttingRepo.UpdateAsync(ActualCutting);
            if (!success)
            {
                ModelState.AddModelError(string.Empty, message ?? "Failed to update Actual Cutting.");
                ActualCutting.CuttingPlanNumber = existing.CuttingPlanNumber;
                ActualCutting.MotherPlantCode = existing.MotherPlantCode;
                ActualCutting.SpeciesName = existing.SpeciesName;
                await LoadDropdownsAsync();
                return Page();
            }

            TempData["Success"] = $"Actual Cutting {ActualCutting.ActualCuttingCode} updated successfully.";
            return RedirectToPage("/Production/ActualCutting/Index");
        }

        private async Task LoadDropdownsAsync()
        {
            var activeUsers = await _employeeRepo.GetAllActiveUsers();
            ResponsiblePersons = activeUsers;
            Supervisors = activeUsers;
        }
    }
}
