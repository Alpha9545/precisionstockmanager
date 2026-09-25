using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PlantStockManager.Authorization;
using PlantStockManager.Data;
using PlantStockManager.Models;
using PlantStockManager.Services;
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
        private readonly SupervisorSelectionService _supervisors;
        private readonly AreaAccessService _areaAccessService;

        public EditModel(
            MotherPlantRepository motherPlantRepo,
            PolyhouseRepository polyhouseRepo,
            PlantTypeRepository plantTypeRepo,
            PlantSpeciesRepository plantSpeciesRepo,
            AreaRepository areaRepo,
            SupervisorSelectionService supervisors,
            AreaAccessService areaAccessService)
        {
            _motherPlantRepo = motherPlantRepo;
            _polyhouseRepo = polyhouseRepo;
            _plantTypeRepo = plantTypeRepo;
            _plantSpeciesRepo = plantSpeciesRepo;
            _areaRepo = areaRepo;
            _supervisors = supervisors;
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
        // Phase 1: Production Area supervisors only (narrowed to the chosen Area).
        public List<SupervisorOption> Supervisors { get; set; } = new();

        // The stored supervisor (kept selectable even if no longer eligible).
        private int? _currentSupervisorId;
        private string? _currentSupervisorName;

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
            _currentSupervisorId = existing.SupervisorId;
            _currentSupervisorName = existing.SupervisorName;
            SelectedPlantTypeId = await _plantSpeciesRepo.GetPlantTypeIdBySpeciesId(existing.SpeciesId);
            await LoadDropdownsAsync(SelectedPlantTypeId, existing.PolyhouseId);
            return Page();
        }

        public async Task<JsonResult> OnGetSpeciesByPlantType(int plantTypeId)
        {
            var species = await _plantSpeciesRepo.GetSpeciesByPlantType(plantTypeId);
            return new JsonResult(species);
        }

        // Cascading dropdown: an Area belongs to exactly one Polyhouse, so
        // the Area list is re-fetched whenever the user changes Polyhouse.
        public async Task<JsonResult> OnGetAreasByPolyhouseId(int polyhouseId)
        {
            var areas = await _areaRepo.GetAreasByPolyhouseId(polyhouseId);
            return new JsonResult(areas.Select(a => new { id = a.Id, name = a.Name }));
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

            // Phase 17/B: check the record's CURRENT (pre-edit) Area, not
            // just the submitted one -- otherwise a user restricted to
            // Area 7 could still POST edits to an existing Area-8 record
            // by id as long as they also happened to submit AreaId=7 in
            // the same request (which would silently reassign someone
            // else's record). Both the existing and the submitted Area
            // must be ones this user can access.
            var existingForAuth = await _motherPlantRepo.GetByIdAsync(MotherPlant.Id);
            _currentSupervisorId = existingForAuth?.SupervisorId;
            _currentSupervisorName = existingForAuth?.SupervisorName;
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
                if (area == null || area.PolyhouseId != MotherPlant.PolyhouseId)
                {
                    ModelState.AddModelError("MotherPlant.AreaId", "The selected Area does not belong to the selected Polyhouse.");
                }
                else if (!_areaAccessService.CanAccessArea(User, MotherPlant.AreaId))
                {
                    ModelState.AddModelError("MotherPlant.AreaId", "You are not authorized to move this record to the selected Area.");
                }
            }

            // Phase 1: the supervisor must be an eligible Production Area
            // supervisor for this Area; an unchanged value is always kept.
            var supervisorError = await _supervisors.ValidateAsync(
                SupervisorKind.ProductionArea, MotherPlant.AreaId, MotherPlant.SupervisorId, existingForAuth?.SupervisorId);
            if (supervisorError != null)
                ModelState.AddModelError("MotherPlant.SupervisorId", supervisorError);

            if (!ModelState.IsValid)
            {
                await LoadDropdownsAsync(SelectedPlantTypeId, MotherPlant.PolyhouseId);
                return Page();
            }

            MotherPlant.ModifiedBy = User.Identity?.Name ?? "System";

            var (success, message) = await _motherPlantRepo.UpdateAsync(MotherPlant);
            if (!success)
            {
                ModelState.AddModelError(string.Empty, message ?? "Failed to update Mother Plant batch.");
                await LoadDropdownsAsync(SelectedPlantTypeId, MotherPlant.PolyhouseId);
                return Page();
            }

            TempData["Success"] = $"Mother Plant batch {MotherPlant.MotherPlantCode} updated successfully.";
            return RedirectToPage("/Production/MotherPlant/Index");
        }

        private async Task LoadDropdownsAsync(int plantTypeId, int polyhouseId)
        {
            Polyhouses = await _polyhouseRepo.GetAllPolyhouses();
            PlantTypes = await _plantTypeRepo.GetAllPlantTypes();
            SpeciesForSelectedType = plantTypeId > 0
                ? await _plantSpeciesRepo.GetSpeciesByPlantType(plantTypeId)
                : new List<PlantSpecies>();
            Areas = polyhouseId > 0
                ? await _areaRepo.GetAreasByPolyhouseId(polyhouseId)
                : new List<Area>();
            Supervisors = SupervisorRules.IncludeCurrent(
                await _supervisors.OptionsAsync(SupervisorKind.ProductionArea), _currentSupervisorId, _currentSupervisorName);
        }
    }
}
