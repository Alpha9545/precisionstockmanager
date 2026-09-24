using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PlantStockManager.Authorization;
using PlantStockManager.Data;
using PlantStockManager.Models;
using PropagationBatchModel = PlantStockManager.Models.PropagationBatch;
using CuttingDeliveryModel = PlantStockManager.Models.CuttingDelivery;

namespace PlantStockManager.Pages.Production.PropagationBatch
{
    public class CreateModel : PageModel
    {
        private readonly PropagationBatchRepository _propagationBatchRepo;
        private readonly CuttingDeliveryRepository _cuttingDeliveryRepo;
        private readonly AreaRepository _areaRepo;
        private readonly EmployeeRepository _employeeRepo;
        private readonly MotherPlantAreaScope _areaScope;

        public CreateModel(
            PropagationBatchRepository propagationBatchRepo,
            CuttingDeliveryRepository cuttingDeliveryRepo,
            AreaRepository areaRepo,
            EmployeeRepository employeeRepo,
            MotherPlantAreaScope areaScope)
        {
            _areaScope = areaScope;
            _propagationBatchRepo = propagationBatchRepo;
            _cuttingDeliveryRepo = cuttingDeliveryRepo;
            _areaRepo = areaRepo;
            _employeeRepo = employeeRepo;
        }

        [BindProperty]
        public PropagationBatchModel PropagationBatch { get; set; } = new();

        public List<CuttingDeliveryModel> OpenCuttingDeliveries { get; set; } = new();
        public List<Area> Areas { get; set; } = new();
        public List<Employee> ResponsiblePersons { get; set; } = new();
        public List<Employee> Supervisors { get; set; } = new();

        public async Task OnGetAsync()
        {
            PropagationBatch.PropagationDate = DateTime.Today;
            await LoadDropdownsAsync();
        }

        public async Task<IActionResult> OnPostAsync()
        {
            ModelState.Remove("PropagationBatch.BatchCode");
            ModelState.Remove("PropagationBatch.CreatedBy");
            ModelState.Remove("PropagationBatch.MotherPlantId"); // server-derived from the Cutting Delivery
            ModelState.Remove("PropagationBatch.SpeciesId");      // server-derived from the Cutting Delivery
            ModelState.Remove("PropagationBatch.Status");

            if (PropagationBatch.CuttingDeliveryId <= 0)
                ModelState.AddModelError("PropagationBatch.CuttingDeliveryId", "Cutting Delivery is required.");
            if (PropagationBatch.Quantity <= 0)
                ModelState.AddModelError("PropagationBatch.Quantity", "Quantity must be greater than zero.");

            CuttingDeliveryModel? delivery = null;
            if (PropagationBatch.CuttingDeliveryId > 0)
            {
                delivery = await _cuttingDeliveryRepo.GetByIdAsync(PropagationBatch.CuttingDeliveryId);
                if (delivery == null)
                    ModelState.AddModelError("PropagationBatch.CuttingDeliveryId", "Selected Cutting Delivery does not exist.");
                else if (delivery.Status == "Cancelled")
                    ModelState.AddModelError("PropagationBatch.CuttingDeliveryId", "Cannot start propagation from a Cancelled delivery.");
            }

            if (PropagationBatch.AreaId.HasValue)
            {
                var area = await _areaRepo.GetAreaById(PropagationBatch.AreaId.Value);
                if (area == null || !area.IsActive)
                    ModelState.AddModelError("PropagationBatch.AreaId", "Selected Area is not valid or is inactive.");
            }

            // F1: the delivery's Mother Plant Area AND the chosen propagation
            // Area (when given) must both be Areas the user may act for.
            if (delivery != null && !await _areaScope.CanAccessAsync(User, delivery.MotherPlantId))
                ModelState.AddModelError("PropagationBatch.CuttingDeliveryId", "You are not authorized to start propagation from this Cutting Delivery's Area.");
            if (PropagationBatch.AreaId.HasValue && !await _areaScope.CanAccessAsync(User, null, PropagationBatch.AreaId))
                ModelState.AddModelError("PropagationBatch.AreaId", "You are not authorized to use the selected Area.");

            if (!ModelState.IsValid || delivery == null)
            {
                await LoadDropdownsAsync();
                return Page();
            }

            // Traceability fields are always derived from the delivery
            // itself, never trusted from the posted form.
            PropagationBatch.MotherPlantId = delivery.MotherPlantId;
            PropagationBatch.SpeciesId = delivery.SpeciesId;
            PropagationBatch.CreatedBy = User.Identity?.Name ?? "System";

            var (success, message, _) = await _propagationBatchRepo.InsertAsync(PropagationBatch);
            if (!success)
            {
                // Covers both the "exceeds available Net Quantity"
                // business rule and any DB-level constraint failure --
                // surfaced as a friendly message, never a raw SQL error.
                ModelState.AddModelError(string.Empty, message ?? "Failed to save Propagation Batch.");
                await LoadDropdownsAsync();
                return Page();
            }

            TempData["Success"] = $"Propagation Batch {PropagationBatch.BatchCode} started successfully.";
            return RedirectToPage("/Production/PropagationBatch/Index");
        }

        private async Task LoadDropdownsAsync()
        {
            OpenCuttingDeliveries = await _areaScope.FilterAsync(User, await _cuttingDeliveryRepo.GetOpenForPropagationAsync(), d => (int?)d.MotherPlantId);
            Areas = await _areaRepo.GetAllAreas();
            var activeUsers = await _employeeRepo.GetAllActiveUsers();
            ResponsiblePersons = activeUsers;
            Supervisors = activeUsers;
        }
    }
}
