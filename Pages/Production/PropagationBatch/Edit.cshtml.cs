using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PlantStockManager.Data;
using PlantStockManager.Models;
using PropagationBatchModel = PlantStockManager.Models.PropagationBatch;

namespace PlantStockManager.Pages.Production.PropagationBatch
{
    public class EditModel : PageModel
    {
        private readonly PropagationBatchRepository _propagationBatchRepo;
        private readonly AreaRepository _areaRepo;
        private readonly EmployeeRepository _employeeRepo;

        public EditModel(PropagationBatchRepository propagationBatchRepo, AreaRepository areaRepo, EmployeeRepository employeeRepo)
        {
            _propagationBatchRepo = propagationBatchRepo;
            _areaRepo = areaRepo;
            _employeeRepo = employeeRepo;
        }

        // The Cutting Delivery / Mother Plant / Species linkage and the
        // planted Quantity are fixed once a Propagation Batch exists --
        // editing lets you record the propagation OUTCOME (Survived /
        // Loss / Status / Area / people / remarks) as the batch
        // progresses through its lifecycle, but never reassign which
        // delivery it draws from or how much was originally planted.
        [BindProperty]
        public PropagationBatchModel PropagationBatch { get; set; } = new();

        public List<Area> Areas { get; set; } = new();
        public List<Employee> ResponsiblePersons { get; set; } = new();
        public List<Employee> Supervisors { get; set; } = new();

        public async Task<IActionResult> OnGetAsync(int id)
        {
            var existing = await _propagationBatchRepo.GetByIdAsync(id);
            if (existing == null)
                return RedirectToPage("/Production/PropagationBatch/Index");

            PropagationBatch = existing;
            await LoadDropdownsAsync();
            return Page();
        }

        public async Task<IActionResult> OnPostAsync()
        {
            ModelState.Remove("PropagationBatch.BatchCode");
            ModelState.Remove("PropagationBatch.CreatedBy");
            ModelState.Remove("PropagationBatch.CuttingDeliveryId");
            ModelState.Remove("PropagationBatch.MotherPlantId");
            ModelState.Remove("PropagationBatch.SpeciesId");
            ModelState.Remove("PropagationBatch.Quantity");

            if (PropagationBatch.SurvivedQuantity < 0 || PropagationBatch.LossQuantity < 0)
                ModelState.AddModelError(string.Empty, "Survived and Loss quantities cannot be negative.");

            if (PropagationBatch.AreaId.HasValue)
            {
                var area = await _areaRepo.GetAreaById(PropagationBatch.AreaId.Value);
                if (area == null || !area.IsActive)
                    ModelState.AddModelError("PropagationBatch.AreaId", "Selected Area is not valid or is inactive.");
            }

            // Re-load the immutable linkage fields (and Quantity) from
            // the DB record -- never trust hidden-field values from the
            // client for these.
            var existing = await _propagationBatchRepo.GetByIdAsync(PropagationBatch.Id);
            if (existing == null)
            {
                ModelState.AddModelError(string.Empty, "Propagation Batch record not found.");
                await LoadDropdownsAsync();
                return Page();
            }
            PropagationBatch.CuttingDeliveryId = existing.CuttingDeliveryId;
            PropagationBatch.MotherPlantId = existing.MotherPlantId;
            PropagationBatch.SpeciesId = existing.SpeciesId;
            PropagationBatch.Quantity = existing.Quantity;

            if ((PropagationBatch.Status == "ReadyForPotting" || PropagationBatch.Status == "Completed")
                && !PropagationBatchModel.CanReconcile(PropagationBatch.SurvivedQuantity, PropagationBatch.LossQuantity, PropagationBatch.Quantity))
            {
                ModelState.AddModelError(string.Empty, "Survived + Loss must add up exactly to the batch Quantity before it can move to Ready for Potting or Completed.");
            }

            if (!ModelState.IsValid)
            {
                PropagationBatch.CuttingDeliveryCode = existing.CuttingDeliveryCode;
                PropagationBatch.MotherPlantCode = existing.MotherPlantCode;
                PropagationBatch.SpeciesName = existing.SpeciesName;
                await LoadDropdownsAsync();
                return Page();
            }

            PropagationBatch.ModifiedBy = User.Identity?.Name ?? "System";

            var (success, message) = await _propagationBatchRepo.UpdateAsync(PropagationBatch);
            if (!success)
            {
                ModelState.AddModelError(string.Empty, message ?? "Failed to update Propagation Batch.");
                PropagationBatch.CuttingDeliveryCode = existing.CuttingDeliveryCode;
                PropagationBatch.MotherPlantCode = existing.MotherPlantCode;
                PropagationBatch.SpeciesName = existing.SpeciesName;
                await LoadDropdownsAsync();
                return Page();
            }

            TempData["Success"] = $"Propagation Batch {PropagationBatch.BatchCode} updated successfully.";
            return RedirectToPage("/Production/PropagationBatch/Index");
        }

        private async Task LoadDropdownsAsync()
        {
            Areas = await _areaRepo.GetAllAreas();
            var activeUsers = await _employeeRepo.GetAllActiveUsers();
            ResponsiblePersons = activeUsers;
            Supervisors = activeUsers;
        }
    }
}
