using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PlantStockManager.Authorization;
using PlantStockManager.Data;
using PlantStockManager.Models;
using CuttingDeliveryModel = PlantStockManager.Models.CuttingDelivery;
using ActualCuttingModel = PlantStockManager.Models.ActualCutting;

namespace PlantStockManager.Pages.Production.CuttingDelivery
{
    public class CreateModel : PageModel
    {
        private readonly CuttingDeliveryRepository _cuttingDeliveryRepo;
        private readonly ActualCuttingRepository _actualCuttingRepo;
        private readonly EmployeeRepository _employeeRepo;
        private readonly MotherPlantAreaScope _areaScope;

        public CreateModel(
            CuttingDeliveryRepository cuttingDeliveryRepo,
            ActualCuttingRepository actualCuttingRepo,
            EmployeeRepository employeeRepo,
            MotherPlantAreaScope areaScope)
        {
            _areaScope = areaScope;
            _cuttingDeliveryRepo = cuttingDeliveryRepo;
            _actualCuttingRepo = actualCuttingRepo;
            _employeeRepo = employeeRepo;
        }

        [BindProperty]
        public CuttingDeliveryModel CuttingDelivery { get; set; } = new();

        public List<ActualCuttingModel> OpenActualCuttings { get; set; } = new();
        public List<Employee> ResponsiblePersons { get; set; } = new();
        public List<Employee> Supervisors { get; set; } = new();

        public async Task OnGetAsync()
        {
            CuttingDelivery.DeliveryDate = DateTime.Today;
            await LoadDropdownsAsync();
        }

        public async Task<IActionResult> OnPostAsync()
        {
            ModelState.Remove("CuttingDelivery.DeliveryCode");
            ModelState.Remove("CuttingDelivery.CreatedBy");
            ModelState.Remove("CuttingDelivery.MotherPlantId"); // server-derived from the Actual Cutting
            ModelState.Remove("CuttingDelivery.SpeciesId");      // server-derived from the Actual Cutting

            if (CuttingDelivery.ActualCuttingId <= 0)
                ModelState.AddModelError("CuttingDelivery.ActualCuttingId", "Actual Cutting record is required.");
            if (CuttingDelivery.DeliveredQuantity < 0)
                ModelState.AddModelError("CuttingDelivery.DeliveredQuantity", "Delivered Quantity cannot be negative.");
            if (CuttingDelivery.LossQuantity < 0 || CuttingDelivery.RemovedQuantity < 0 || CuttingDelivery.RejectedQuantity < 0 || CuttingDelivery.DamagedQuantity < 0)
                ModelState.AddModelError(string.Empty, "Loss, Removed, Rejected and Damaged quantities cannot be negative.");
            if (!CuttingDeliveryModel.IsWithinDelivered(CuttingDelivery.LossQuantity, CuttingDelivery.RemovedQuantity, CuttingDelivery.RejectedQuantity, CuttingDelivery.DamagedQuantity, CuttingDelivery.DeliveredQuantity))
                ModelState.AddModelError(string.Empty, "Loss + Removed + Rejected + Damaged cannot exceed the Delivered Quantity.");

            ActualCuttingModel? actualCutting = null;
            if (CuttingDelivery.ActualCuttingId > 0)
            {
                actualCutting = await _actualCuttingRepo.GetByIdAsync(CuttingDelivery.ActualCuttingId);
                if (actualCutting == null)
                    ModelState.AddModelError("CuttingDelivery.ActualCuttingId", "Selected Actual Cutting record does not exist.");
            }

            // F1: the selected Actual Cutting's Mother Plant Area must be one of the user's.
            if (actualCutting != null && !await _areaScope.CanAccessAsync(User, actualCutting.MotherPlantId))
                ModelState.AddModelError("CuttingDelivery.ActualCuttingId", "You are not authorized to record deliveries for this Actual Cutting's Area.");

            if (!ModelState.IsValid || actualCutting == null)
            {
                await LoadDropdownsAsync();
                return Page();
            }

            // Traceability fields are always derived from the actual
            // cutting record itself, never trusted from the posted form.
            CuttingDelivery.MotherPlantId = actualCutting.MotherPlantId;
            CuttingDelivery.SpeciesId = actualCutting.SpeciesId;
            CuttingDelivery.CreatedBy = User.Identity?.Name ?? "System";

            var (success, message, _) = await _cuttingDeliveryRepo.InsertAsync(CuttingDelivery);
            if (!success)
            {
                // Covers both the "exceeds available Good Quantity"
                // business rule and any DB-level constraint failure --
                // surfaced as a friendly message, never a raw SQL error.
                ModelState.AddModelError(string.Empty, message ?? "Failed to save Cutting Delivery.");
                await LoadDropdownsAsync();
                return Page();
            }

            TempData["Success"] = $"Cutting Delivery {CuttingDelivery.DeliveryCode} recorded successfully.";
            return RedirectToPage("/Production/CuttingDelivery/Index");
        }

        private async Task LoadDropdownsAsync()
        {
            OpenActualCuttings = await _areaScope.FilterAsync(User, await _actualCuttingRepo.GetOpenForDeliveryAsync(), a => (int?)a.MotherPlantId);
            var activeUsers = await _employeeRepo.GetAllActiveUsers();
            ResponsiblePersons = activeUsers;
            Supervisors = activeUsers;
        }
    }
}
