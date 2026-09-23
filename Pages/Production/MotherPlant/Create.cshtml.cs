using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PlantStockManager.Authorization;
using PlantStockManager.Data;
using PlantStockManager.Models;
using MotherPlantModel = PlantStockManager.Models.MotherPlant;

namespace PlantStockManager.Pages.Production.MotherPlant
{
    public class CreateModel : PageModel
    {
        private readonly MotherPlantRepository _motherPlantRepo;
        private readonly PolyhouseRepository _polyhouseRepo;
        private readonly PlantTypeRepository _plantTypeRepo;
        private readonly PlantSpeciesRepository _plantSpeciesRepo;
        private readonly AreaRepository _areaRepo;
        private readonly EmployeeRepository _employeeRepo;
        private readonly AreaAccessService _areaAccessService;

        public CreateModel(
            MotherPlantRepository motherPlantRepo,
            PolyhouseRepository polyhouseRepo,
            PlantTypeRepository plantTypeRepo,
            PlantSpeciesRepository plantSpeciesRepo,
            AreaRepository areaRepo,
            EmployeeRepository employeeRepo,
            AreaAccessService areaAccessService)
        {
            _motherPlantRepo = motherPlantRepo;
            _polyhouseRepo = polyhouseRepo;
            _plantTypeRepo = plantTypeRepo;
            _plantSpeciesRepo = plantSpeciesRepo;
            _areaRepo = areaRepo;
            _employeeRepo = employeeRepo;
            _areaAccessService = areaAccessService;
        }

        [BindProperty]
        public MotherPlantModel MotherPlant { get; set; } = new();

        public List<Polyhouse> Polyhouses { get; set; } = new();
        public List<PlantType> PlantTypes { get; set; } = new();
        public List<Area> Areas { get; set; } = new();
        public List<Employee> ResponsiblePersons { get; set; } = new();
        public List<Employee> Supervisors { get; set; } = new();

        // Server-computed preview shown on the form before saving. The
        // repository recalculates these again on save regardless -- this
        // is display only, never trusted from the client.
        public decimal PreviewExpectedCuttingQuantity =>
            MotherPlantModel.CalculateExpectedCuttingQuantity(MotherPlant.MotherPlantQuantity, MotherPlant.CuttingRate);

        public decimal PreviewExpectedMonthlyCuttingQuantity =>
            MotherPlantModel.CalculateExpectedMonthlyCuttingQuantity(PreviewExpectedCuttingQuantity, MotherPlant.CuttingPeriodDays);

        public async Task OnGetAsync()
        {
            MotherPlant.PlantingDate = DateTime.Today;
            MotherPlant.Status = "Active";
            await LoadDropdownsAsync(0);
        }

        public async Task<JsonResult> OnGetSpeciesByPlantType(int plantTypeId)
        {
            var species = await _plantSpeciesRepo.GetSpeciesByPlantType(plantTypeId);
            return new JsonResult(species);
        }

        // Cascading dropdown: an Area belongs to exactly one Polyhouse, so
        // the Area dropdown is scoped to whichever Polyhouse the user has
        // selected (mirrors the Plant Type -> Species pattern above).
        public async Task<JsonResult> OnGetAreasByPolyhouseId(int polyhouseId)
        {
            var areas = await _areaRepo.GetAreasByPolyhouseId(polyhouseId);
            return new JsonResult(areas.Select(a => new { id = a.Id, name = a.Name }));
        }

        public async Task<IActionResult> OnPostAsync()
        {
            ModelState.Remove("MotherPlant.MotherPlantCode");
            ModelState.Remove("MotherPlant.CreatedBy");

            // Spec section 27 validation rules.
            if (MotherPlant.MotherPlantQuantity <= 0)
                ModelState.AddModelError("MotherPlant.MotherPlantQuantity", "Mother Plant Quantity must be greater than zero.");
            if (MotherPlant.CuttingRate < 0)
                ModelState.AddModelError("MotherPlant.CuttingRate", "Cutting Rate cannot be negative.");
            if (MotherPlant.CuttingPeriodDays <= 0)
                ModelState.AddModelError("MotherPlant.CuttingPeriodDays", "Cutting Period (days) must be greater than zero.");

            // A Mother Plant cannot select an Area belonging to a different
            // Polyhouse. The client-side cascade already restricts the
            // dropdown, but a tampered/replayed request could still submit
            // a mismatched pair, so this is re-checked on the server.
            if (MotherPlant.AreaId.HasValue)
            {
                var area = await _areaRepo.GetAreaById(MotherPlant.AreaId.Value);
                if (area == null || area.PolyhouseId != MotherPlant.PolyhouseId)
                {
                    ModelState.AddModelError("MotherPlant.AreaId", "The selected Area does not belong to the selected Polyhouse.");
                }

                // Phase 17/B: server-side Area-scope check. Never trust a
                // client-side dropdown alone -- a tampered/replayed POST
                // could submit any AreaId regardless of what the visible
                // form offered.
                else if (!_areaAccessService.CanAccessArea(User, MotherPlant.AreaId))
                {
                    ModelState.AddModelError("MotherPlant.AreaId", "You are not authorized to enter Mother Plant records for this Area.");
                }
            }

            if (!ModelState.IsValid)
            {
                await LoadDropdownsAsync(MotherPlant.PolyhouseId);
                return Page();
            }

            MotherPlant.CreatedBy = User.Identity?.Name ?? "System";

            var (success, message, _) = await _motherPlantRepo.InsertAsync(MotherPlant);
            if (!success)
            {
                ModelState.AddModelError(string.Empty, message ?? "Failed to save Mother Plant batch.");
                await LoadDropdownsAsync(MotherPlant.PolyhouseId);
                return Page();
            }

            TempData["Success"] = $"Mother Plant batch {MotherPlant.MotherPlantCode} added successfully.";
            return RedirectToPage("/Production/MotherPlant/Index");
        }

        private async Task LoadDropdownsAsync(int polyhouseId)
        {
            Polyhouses = await _polyhouseRepo.GetAllPolyhouses();
            PlantTypes = await _plantTypeRepo.GetAllPlantTypes();
            Areas = polyhouseId > 0
                ? await _areaRepo.GetAreasByPolyhouseId(polyhouseId)
                : new List<Area>();
            var activeUsers = await _employeeRepo.GetAllActiveUsers();
            ResponsiblePersons = activeUsers;
            Supervisors = activeUsers;
        }
    }
}
