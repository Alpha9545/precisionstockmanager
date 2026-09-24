using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PlantStockManager.Authorization;
using PlantStockManager.Data;
using PlantStockManager.Models;
using CuttingDeliveryModel = PlantStockManager.Models.CuttingDelivery;

namespace PlantStockManager.Pages.Production.CuttingDelivery
{
    public class EditModel : PageModel
    {
        private readonly CuttingDeliveryRepository _cuttingDeliveryRepo;
        private readonly EmployeeRepository _employeeRepo;
        private readonly MotherPlantAreaScope _areaScope;

        public EditModel(CuttingDeliveryRepository cuttingDeliveryRepo, EmployeeRepository employeeRepo, MotherPlantAreaScope areaScope)
        {
            _areaScope = areaScope;
            _cuttingDeliveryRepo = cuttingDeliveryRepo;
            _employeeRepo = employeeRepo;
        }

        // The Actual Cutting / Mother Plant / Species linkage is fixed
        // once a Cutting Delivery record exists -- editing lets you
        // correct quantities, dates, people, status and remarks, but
        // never reassign which actual-cutting batch this delivery draws
        // from. That keeps the traceability chain (Mother Plant ->
        // Cutting Plan -> Actual Cutting -> Cutting Delivery) immutable
        // after creation.
        [BindProperty]
        public CuttingDeliveryModel CuttingDelivery { get; set; } = new();

        public List<Employee> ResponsiblePersons { get; set; } = new();
        public List<Employee> Supervisors { get; set; } = new();

        public async Task<IActionResult> OnGetAsync(int id)
        {
            var existing = await _cuttingDeliveryRepo.GetByIdAsync(id);
            if (existing == null)
                return RedirectToPage("/Production/CuttingDelivery/Index");

            // F1: Area scope via the record's Mother Plant (MotherPlantAreaScope).
            if (!await _areaScope.CanAccessAsync(User, existing.MotherPlantId))
            {
                TempData["Error"] = "You are not authorized to edit this Cutting Delivery record.";
                return RedirectToPage("/Production/CuttingDelivery/Index");
            }

            CuttingDelivery = existing;
            await LoadDropdownsAsync();
            return Page();
        }

        public async Task<IActionResult> OnPostAsync()
        {
            ModelState.Remove("CuttingDelivery.DeliveryCode");
            ModelState.Remove("CuttingDelivery.CreatedBy");
            ModelState.Remove("CuttingDelivery.ActualCuttingId");
            ModelState.Remove("CuttingDelivery.MotherPlantId");
            ModelState.Remove("CuttingDelivery.SpeciesId");

            if (CuttingDelivery.DeliveredQuantity < 0)
                ModelState.AddModelError("CuttingDelivery.DeliveredQuantity", "Delivered Quantity cannot be negative.");
            if (CuttingDelivery.LossQuantity < 0 || CuttingDelivery.RemovedQuantity < 0 || CuttingDelivery.RejectedQuantity < 0 || CuttingDelivery.DamagedQuantity < 0)
                ModelState.AddModelError(string.Empty, "Loss, Removed, Rejected and Damaged quantities cannot be negative.");
            if (!CuttingDeliveryModel.IsWithinDelivered(CuttingDelivery.LossQuantity, CuttingDelivery.RemovedQuantity, CuttingDelivery.RejectedQuantity, CuttingDelivery.DamagedQuantity, CuttingDelivery.DeliveredQuantity))
                ModelState.AddModelError(string.Empty, "Loss + Removed + Rejected + Damaged cannot exceed the Delivered Quantity.");

            // Re-load the immutable linkage fields from the DB record --
            // never trust hidden-field values from the client for these.
            var existing = await _cuttingDeliveryRepo.GetByIdAsync(CuttingDelivery.Id);
            if (existing == null)
            {
                ModelState.AddModelError(string.Empty, "Cutting Delivery record not found.");
                await LoadDropdownsAsync();
                return Page();
            }
            // F1: re-check Area scope against the STORED record (the posted
            // Id is otherwise the only input deciding which record changes).
            if (!await _areaScope.CanAccessAsync(User, existing.MotherPlantId))
            {
                TempData["Error"] = "You are not authorized to edit this Cutting Delivery record.";
                return RedirectToPage("/Production/CuttingDelivery/Index");
            }
            CuttingDelivery.ActualCuttingId = existing.ActualCuttingId;
            CuttingDelivery.MotherPlantId = existing.MotherPlantId;
            CuttingDelivery.SpeciesId = existing.SpeciesId;

            if (!ModelState.IsValid)
            {
                CuttingDelivery.ActualCuttingCode = existing.ActualCuttingCode;
                CuttingDelivery.MotherPlantCode = existing.MotherPlantCode;
                CuttingDelivery.SpeciesName = existing.SpeciesName;
                await LoadDropdownsAsync();
                return Page();
            }

            CuttingDelivery.ModifiedBy = User.Identity?.Name ?? "System";

            var (success, message) = await _cuttingDeliveryRepo.UpdateAsync(CuttingDelivery);
            if (!success)
            {
                ModelState.AddModelError(string.Empty, message ?? "Failed to update Cutting Delivery.");
                CuttingDelivery.ActualCuttingCode = existing.ActualCuttingCode;
                CuttingDelivery.MotherPlantCode = existing.MotherPlantCode;
                CuttingDelivery.SpeciesName = existing.SpeciesName;
                await LoadDropdownsAsync();
                return Page();
            }

            TempData["Success"] = $"Cutting Delivery {CuttingDelivery.DeliveryCode} updated successfully.";
            return RedirectToPage("/Production/CuttingDelivery/Index");
        }

        private async Task LoadDropdownsAsync()
        {
            var activeUsers = await _employeeRepo.GetAllActiveUsers();
            ResponsiblePersons = activeUsers;
            Supervisors = activeUsers;
        }
    }
}
