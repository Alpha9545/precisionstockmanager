using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PlantStockManager.Authorization;
using PlantStockManager.Data;
using PlantStockManager.Models;
using PlantStockManager.Services;
using SeedSowingModel = PlantStockManager.Models.SeedSowing;

namespace PlantStockManager.Pages.Production.SeedSowing
{
    // Phase B -- DIRECT SOWING (no Seed Issue step).
    //
    // The user picks: Sowing Date, Area (site) -> Polyhouse (inside it),
    // Supervisor, Species -> Variety -> Main Office seed lot, Seed Quantity,
    // Cavity, Tray Count. The seed is consumed directly from Main Office
    // Seed Stock inside one locked transaction (SeedSowingRepository), which
    // also generates the batch number (YYYY-MM-DD-L-NNN) and the Expected
    // Ready Date (Sowing Date + the variety's growing days).
    //
    // Page access: "Sowing.Enter" (FeatureAuthorizationConventions).
    // Area scope: the user must be allowed to work in the chosen growing Area
    // (AreaAccessService) -- checked on every request, never trusted from the
    // form. Main Office seed is central stock, so the lot itself is not
    // Area-scoped to the operator.
    public class CreateModel : PageModel
    {
        private readonly SeedSowingRepository _seedSowingRepo;
        private readonly SeedStockRepository _seedStockRepo;
        private readonly EmployeeRepository _employeeRepo;
        private readonly AreaRepository _areaRepo;
        private readonly PolyhouseRepository _polyhouseRepo;
        private readonly PlantTypeRepository _plantTypeRepo;
        private readonly PlantSpeciesRepository _plantSpeciesRepo;
        private readonly AreaAccessService _areaAccessService;

        public CreateModel(
            SeedSowingRepository seedSowingRepo,
            SeedStockRepository seedStockRepo,
            EmployeeRepository employeeRepo,
            AreaRepository areaRepo,
            PolyhouseRepository polyhouseRepo,
            PlantTypeRepository plantTypeRepo,
            PlantSpeciesRepository plantSpeciesRepo,
            AreaAccessService areaAccessService)
        {
            _seedSowingRepo = seedSowingRepo;
            _seedStockRepo = seedStockRepo;
            _employeeRepo = employeeRepo;
            _areaRepo = areaRepo;
            _polyhouseRepo = polyhouseRepo;
            _plantTypeRepo = plantTypeRepo;
            _plantSpeciesRepo = plantSpeciesRepo;
            _areaAccessService = areaAccessService;
        }

        [BindProperty]
        public SeedSowingModel SeedSowing { get; set; } = new();

        // Species (dbo.PlantTypes) -> Variety (dbo.PlantSpecies).
        [BindProperty]
        public int PlantTypeId { get; set; }

        public List<Area> Areas { get; set; } = new();
        public List<PlantType> PlantTypes { get; set; } = new();
        public List<Employee> Supervisors { get; set; } = new();
        public List<Employee> ResponsiblePersons { get; set; } = new();
        public IReadOnlyList<string> CavityTypes => DirectSowingRules.CavityTypes;

        public async Task OnGetAsync()
        {
            SeedSowing.SowingDate = DateTime.Today;
            await LoadDropdownsAsync();
        }

        // ---- AJAX lookups (GET, read-only) -------------------------------

        // Polyhouses inside an Area the user may sow in.
        public async Task<JsonResult> OnGetPolyhousesAsync(int areaId)
        {
            if (areaId <= 0 || !_areaAccessService.CanAccessArea(User, areaId))
                return new JsonResult(Array.Empty<object>());
            var list = await _polyhouseRepo.GetByAreaIdAsync(areaId);
            return new JsonResult(list.Select(p => new { id = p.Id, name = p.Name }));
        }

        // Varieties of a Species, with their growing days.
        public async Task<JsonResult> OnGetVarietiesAsync(int plantTypeId)
        {
            var list = await _plantSpeciesRepo.GetSpeciesByPlantType(plantTypeId);
            return new JsonResult(list.Select(v => new { id = v.Id, name = v.Name.Trim(), growingDays = v.ReadyStockDays }));
        }

        // Main Office seed lots of a Variety that still have stock.
        public async Task<JsonResult> OnGetSeedLotsAsync(int speciesId)
        {
            var list = (await _seedStockRepo.GetAllAsync())
                .Where(s => s.SpeciesId == speciesId
                            && s.AvailableQuantity > 0
                            && DirectSowingRules.IsMainOfficeSeedLocation(s.AreaType, true))
                .OrderBy(s => s.CreatedDate);
            return new JsonResult(list.Select(s => new
            {
                id = s.Id,
                lot = string.IsNullOrEmpty(s.BatchNo) ? "(no lot no.)" : s.BatchNo,
                source = s.SeedSourceName,
                area = s.AreaName,
                available = s.AvailableQuantity,
                unit = s.Unit
            }));
        }

        public async Task<IActionResult> OnPostAsync()
        {
            ModelState.Clear(); // validated explicitly below (server-derived fields are never trusted)

            if (SeedSowing.SowingDate == default)
                ModelState.AddModelError(string.Empty, "Sowing Date is required.");
            if (SeedSowing.AreaId <= 0)
                ModelState.AddModelError(string.Empty, "Area is required.");
            if (!SeedSowing.PolyhouseId.HasValue || SeedSowing.PolyhouseId <= 0)
                ModelState.AddModelError(string.Empty, "Polyhouse is required.");
            if (SeedSowing.SpeciesId <= 0)
                ModelState.AddModelError(string.Empty, "Variety is required.");
            if (SeedSowing.SourceSeedStockId <= 0)
                ModelState.AddModelError(string.Empty, "Main Office seed lot is required.");
            if (SeedSowing.QuantitySown <= 0)
                ModelState.AddModelError(string.Empty, "Seed Quantity must be greater than zero.");
            if (!DirectSowingRules.IsValidCavityType(SeedSowing.CavityType))
                ModelState.AddModelError(string.Empty, $"Cavity must be one of: {string.Join(", ", DirectSowingRules.CavityTypes)}.");
            if (SeedSowing.NumberOfTrays.HasValue && SeedSowing.NumberOfTrays <= 0)
                ModelState.AddModelError(string.Empty, "Tray Count must be greater than zero when provided.");

            // Area authorization on the growing Area (tamper-proof: checked here,
            // the repository re-checks that the Polyhouse belongs to this Area).
            if (SeedSowing.AreaId > 0 && !_areaAccessService.CanAccessArea(User, SeedSowing.AreaId))
                ModelState.AddModelError(string.Empty, "You are not authorized to sow in the selected Area.");

            if (!ModelState.IsValid)
            {
                await LoadDropdownsAsync();
                return Page();
            }

            SeedSowing.CreatedBy = User.Identity?.Name ?? "System";
            var userId = User.GetUserId();

            var (success, message, _) = await _seedSowingRepo.InsertAsync(SeedSowing, userId);
            if (!success)
            {
                ModelState.AddModelError(string.Empty, message ?? "Failed to record Sowing.");
                await LoadDropdownsAsync();
                return Page();
            }

            TempData["Success"] = $"Sowing batch {SeedSowing.SowingCode} recorded. Expected ready: {SeedSowing.ExpectedReadyDate:dd-MM-yyyy}. Main Office seed stock reduced by {SeedSowing.QuantitySown:N2}.";
            return RedirectToPage("/Production/SeedSowing/Details", new { id = SeedSowing.Id });
        }

        private async Task LoadDropdownsAsync()
        {
            // Growing Areas = active Areas that contain at least one Polyhouse
            // (Area -> Polyhouse hierarchy) and that this user may work in.
            var polyhouseAreaIds = (await _polyhouseRepo.GetAllPolyhouses())
                .Where(p => p.AreaId.HasValue)
                .Select(p => p.AreaId!.Value)
                .ToHashSet();
            Areas = (await _areaRepo.GetAllAreas())
                .Where(a => polyhouseAreaIds.Contains(a.Id) && _areaAccessService.CanAccessArea(User, a.Id))
                .OrderBy(a => a.Name)
                .ToList();

            PlantTypes = await _plantTypeRepo.GetAllPlantTypes();
            var activeUsers = await _employeeRepo.GetAllActiveUsers();
            Supervisors = activeUsers;
            ResponsiblePersons = activeUsers;
        }
    }
}
