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
        private readonly UserRoleRepository _userRoleRepo;

        public CreateModel(
            MotherPlantRepository motherPlantRepo,
            PolyhouseRepository polyhouseRepo,
            PlantTypeRepository plantTypeRepo,
            PlantSpeciesRepository plantSpeciesRepo,
            AreaRepository areaRepo,
            EmployeeRepository employeeRepo,
            AreaAccessService areaAccessService,
            UserRoleRepository userRoleRepo)
        {
            _userRoleRepo = userRoleRepo;
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
        // Only users holding the "Mother Plant Supervisor" role.
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

        // Area first: the Polyhouses of that Area (dbo.Polyhouses.AreaId) and
        // the Mother Plant Supervisors assigned to that Area.
        public async Task<JsonResult> OnGetAreaOptionsAsync(int areaId)
        {
            if (!_areaAccessService.CanAccessArea(User, areaId))
                return new JsonResult(new { polyhouses = Array.Empty<object>(), supervisors = Array.Empty<object>() });
            var polyhouses = await _polyhouseRepo.GetByAreaIdAsync(areaId);
            var supervisors = await _userRoleRepo.GetUsersInRoleAsync(PlantStockManager.Services.SupervisorRules.MotherPlantSupervisor, areaId);
            return new JsonResult(new
            {
                polyhouses = polyhouses.Select(x => new { id = x.Id, name = x.Name }),
                supervisors = supervisors.Select(x => new { id = x.EmployeeID, name = x.Name })
            });
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
            // Phase D: every Mother Plant belongs to an Area and has a Mother
            // Plant Supervisor (also enforced by TR_MotherPlants_AreaAndSupervisor).
            if (!MotherPlant.AreaId.HasValue)
                ModelState.AddModelError("MotherPlant.AreaId", "Area is required.");
            var eligibleSupervisors = MotherPlant.AreaId.HasValue
                ? (await _userRoleRepo.GetUsersInRoleAsync(PlantStockManager.Services.SupervisorRules.MotherPlantSupervisor, MotherPlant.AreaId.Value)).Select(u => u.EmployeeID)
                : Enumerable.Empty<int>();
            if (!MotherPlant.SupervisorId.HasValue || !eligibleSupervisors.Contains(MotherPlant.SupervisorId.Value))
                ModelState.AddModelError("MotherPlant.SupervisorId", "Choose a Mother Plant Supervisor of this Area.");

            // A Mother Plant cannot select an Area belonging to a different
            // Polyhouse. The client-side cascade already restricts the
            // dropdown, but a tampered/replayed request could still submit
            // a mismatched pair, so this is re-checked on the server.
            if (MotherPlant.AreaId.HasValue)
            {
                var area = await _areaRepo.GetAreaById(MotherPlant.AreaId.Value);
                var polyhouse = await _polyhouseRepo.GetByIdAsync(MotherPlant.AreaId ?? 0);
                if (area == null || polyhouse == null || polyhouse.AreaId != MotherPlant.AreaId)
                {
                    ModelState.AddModelError("MotherPlant.PolyhouseId", "Choose a Polyhouse of the selected Area (assign Polyhouses to Areas in Admin > Polyhouses).");
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
                await LoadDropdownsAsync(MotherPlant.AreaId ?? 0);
                return Page();
            }

            MotherPlant.CreatedBy = User.Identity?.Name ?? "System";
            MotherPlant.ResponsiblePersonId = null;

            var (success, message, _) = await _motherPlantRepo.InsertAsync(MotherPlant);
            if (!success)
            {
                ModelState.AddModelError(string.Empty, message ?? "Failed to save Mother Plant batch.");
                await LoadDropdownsAsync(MotherPlant.AreaId ?? 0);
                return Page();
            }

            TempData["Success"] = $"Mother Plant batch {MotherPlant.MotherPlantCode} added successfully.";
            return RedirectToPage("/Production/MotherPlant/Index");
        }

        private async Task LoadDropdownsAsync(int areaId)
        {
            Polyhouses = areaId > 0 ? await _polyhouseRepo.GetByAreaIdAsync(areaId) : new List<Polyhouse>();
            PlantTypes = await _plantTypeRepo.GetAllPlantTypes();
            Areas = (await _areaRepo.GetAllAreas()).Where(a => a.IsActive && _areaAccessService.CanAccessArea(User, a.Id)).OrderBy(a => a.Name).ToList();
            Supervisors = areaId > 0
                ? await _userRoleRepo.GetUsersInRoleAsync(PlantStockManager.Services.SupervisorRules.MotherPlantSupervisor, areaId)
                : new List<Employee>();
        }
    }
}
