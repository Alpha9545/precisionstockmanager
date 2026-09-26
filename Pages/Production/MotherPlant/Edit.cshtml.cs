using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PlantStockManager.Authorization;
using PlantStockManager.Data;
using PlantStockManager.Models;
using MotherPlantModel = PlantStockManager.Models.MotherPlant;

namespace PlantStockManager.Pages.Production.MotherPlant
{
    public class EditModel : PageModel
    {
        private readonly MotherPlantRepository _motherPlantRepo;
        private readonly PolyhouseRepository _polyhouseRepo;
        private readonly PlantTypeRepository _plantTypeRepo;
        private readonly PlantSpeciesRepository _plantSpeciesRepo;
        private readonly AreaRepository _areaRepo;
        private readonly EmployeeRepository _employeeRepo;
        private readonly AreaAccessService _areaAccessService;
        private readonly UserRoleRepository _userRoleRepo;

        public EditModel(
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

        // MotherPlant only stores SpeciesId; this tracks which Plant Type
        // owns it so the cascading dropdown can be pre-selected on GET and
        // round-tripped through POST.
        [BindProperty]
        public int SelectedPlantTypeId { get; set; }

        public List<Polyhouse> Polyhouses { get; set; } = new();
        public List<PlantType> PlantTypes { get; set; } = new();
        public List<PlantSpecies> SpeciesForSelectedType { get; set; } = new();
        public List<Area> Areas { get; set; } = new();
        // Only users holding the "Mother Plant Supervisor" role.
        public List<Employee> Supervisors { get; set; } = new();

        public decimal PreviewExpectedCuttingQuantity =>
            MotherPlantModel.CalculateExpectedCuttingQuantity(MotherPlant.MotherPlantQuantity, MotherPlant.CuttingRate);

        public decimal PreviewExpectedMonthlyCuttingQuantity =>
            MotherPlantModel.CalculateExpectedMonthlyCuttingQuantity(PreviewExpectedCuttingQuantity, MotherPlant.CuttingPeriodDays);

        public async Task<IActionResult> OnGetAsync(int id)
        {
            var existing = await _motherPlantRepo.GetByIdAsync(id);
            if (existing == null)
                return RedirectToPage("/Production/MotherPlant/Index");

            // Phase 17/B: block opening another Area's record for editing
            // by direct URL/id, not just hiding it from the list -- "page
            // access" as well as "data access" per the Area-isolation
            // requirement.
            if (!_areaAccessService.CanAccessArea(User, existing.AreaId))
            {
                TempData["Error"] = "You are not authorized to view or edit this Mother Plant record.";
                return RedirectToPage("/Production/MotherPlant/Index");
            }

            MotherPlant = existing;
            SelectedPlantTypeId = await _plantSpeciesRepo.GetPlantTypeIdBySpeciesId(existing.SpeciesId);
            await LoadDropdownsAsync(SelectedPlantTypeId, existing.AreaId ?? 0);
            return Page();
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

            // Phase 17/B: check the record's CURRENT (pre-edit) Area, not
            // just the submitted one -- otherwise a user restricted to
            // Area 7 could still POST edits to an existing Area-8 record
            // by id as long as they also happened to submit AreaId=7 in
            // the same request (which would silently reassign someone
            // else's record). Both the existing and the submitted Area
            // must be ones this user can access.
            var existingForAuth = await _motherPlantRepo.GetByIdAsync(MotherPlant.Id);
            if (existingForAuth == null)
            {
                ModelState.AddModelError(string.Empty, "Mother Plant batch not found.");
            }
            else if (!_areaAccessService.CanAccessArea(User, existingForAuth.AreaId))
            {
                ModelState.AddModelError(string.Empty, "You are not authorized to edit this Mother Plant record.");
            }

            // A Mother Plant cannot select an Area belonging to a different
            // Polyhouse -- re-checked server-side regardless of the
            // client-side cascade.
            if (MotherPlant.AreaId.HasValue)
            {
                var area = await _areaRepo.GetAreaById(MotherPlant.AreaId.Value);
                var polyhouse = await _polyhouseRepo.GetByIdAsync(MotherPlant.AreaId ?? 0);
                if (area == null || polyhouse == null || polyhouse.AreaId != MotherPlant.AreaId)
                {
                    ModelState.AddModelError("MotherPlant.PolyhouseId", "Choose a Polyhouse of the selected Area (assign Polyhouses to Areas in Admin > Polyhouses).");
                }
                else if (!_areaAccessService.CanAccessArea(User, MotherPlant.AreaId))
                {
                    ModelState.AddModelError("MotherPlant.AreaId", "You are not authorized to move this record to the selected Area.");
                }
            }

            if (!ModelState.IsValid)
            {
                await LoadDropdownsAsync(SelectedPlantTypeId, MotherPlant.AreaId ?? 0);
                return Page();
            }

            // The variety is fixed once created (cutting records reference it;
            // FK_CuttingProductions_MotherPlant also refuses a change).
            MotherPlant.SpeciesId = existingForAuth!.SpeciesId;
            MotherPlant.ResponsiblePersonId = existingForAuth.ResponsiblePersonId;
            MotherPlant.ModifiedBy = User.Identity?.Name ?? "System";

            var (success, message) = await _motherPlantRepo.UpdateAsync(MotherPlant);
            if (!success)
            {
                ModelState.AddModelError(string.Empty, message ?? "Failed to update Mother Plant batch.");
                await LoadDropdownsAsync(SelectedPlantTypeId, MotherPlant.AreaId ?? 0);
                return Page();
            }

            TempData["Success"] = $"Mother Plant batch {MotherPlant.MotherPlantCode} updated successfully.";
            return RedirectToPage("/Production/MotherPlant/Index");
        }

        private async Task LoadDropdownsAsync(int plantTypeId, int areaId)
        {
            Polyhouses = areaId > 0 ? await _polyhouseRepo.GetByAreaIdAsync(areaId) : new List<Polyhouse>();
            PlantTypes = await _plantTypeRepo.GetAllPlantTypes();
            SpeciesForSelectedType = plantTypeId > 0
                ? await _plantSpeciesRepo.GetSpeciesByPlantType(plantTypeId)
                : new List<PlantSpecies>();
            Areas = (await _areaRepo.GetAllAreas()).Where(a => a.IsActive && _areaAccessService.CanAccessArea(User, a.Id)).OrderBy(a => a.Name).ToList();
            Supervisors = areaId > 0
                ? await _userRoleRepo.GetUsersInRoleAsync(PlantStockManager.Services.SupervisorRules.MotherPlantSupervisor, areaId)
                : new List<Employee>();
        }
    }
}
