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
        private readonly SeedlingAreaScope _areaAccessService; // seedling-only Area scope (Authorization/SeedlingAreaScope.cs)

        public CreateModel(
            SeedSowingRepository seedSowingRepo,
            SeedStockRepository seedStockRepo,
            EmployeeRepository employeeRepo,
            AreaRepository areaRepo,
            PolyhouseRepository polyhouseRepo,
            PlantTypeRepository plantTypeRepo,
            PlantSpeciesRepository plantSpeciesRepo,
            SeedlingAreaScope areaAccessService,
            UserRoleRepository userRoleRepo)
        {
            _userRoleRepo = userRoleRepo;
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

        private readonly UserRoleRepository _userRoleRepo;

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

        // Polyhouse is OPTIONAL. Offered: the Polyhouses assigned to the
        // chosen Area plus the Polyhouses that have no Area assigned yet
        // (no Area chosen = only the unassigned ones).
        public async Task<JsonResult> OnGetPolyhousesAsync(int areaId)
        {
            if (areaId > 0 && !_areaAccessService.CanAccessArea(User, areaId))
                return new JsonResult(Array.Empty<object>());
            var list = (await _polyhouseRepo.GetAllPolyhouses())
                .Where(p => !p.AreaId.HasValue || (areaId > 0 && p.AreaId == areaId))
                .OrderBy(p => p.Name);
            return new JsonResult(list.Select(p => new { id = p.Id, name = p.AreaId.HasValue ? p.Name : p.Name + " (no Area assigned)" }));
        }

        // Live tray calculation for the form. Uses the SAME server function as
        // the save (DirectSowingRules.CalculateTrays), so the preview and the
        // stored value can never disagree; the save recalculates regardless.
        public JsonResult OnGetTrayCalculation(decimal quantity, string? cavityType)
        {
            var (ok, trays, seedsUsed, remaining, error) = DirectSowingRules.CalculateTrays(quantity, cavityType);
            return new JsonResult(new
            {
                ok,
                trays,
                seedsUsed,
                remainingSeeds = remaining,
                wholeNumber = DirectSowingRules.IsWholeNumber(quantity),
                error
            });
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
            // Area and Polyhouse are optional for now (resolved in
            // SeedSowingRepository.InsertAsync via DirectSowingRules.ResolveGrowingLocation).
            if (SeedSowing.SpeciesId <= 0)
                ModelState.AddModelError(string.Empty, "Variety is required.");
            if (SeedSowing.SourceSeedStockId <= 0)
                ModelState.AddModelError(string.Empty, "Main Office seed lot is required.");
            if (SeedSowing.SeedQuantity <= 0)
                ModelState.AddModelError(string.Empty, "Seed Quantity must be greater than zero.");
            else if (!DirectSowingRules.IsWholeNumber(SeedSowing.SeedQuantity))
                ModelState.AddModelError(string.Empty, "Seed Quantity must be a whole number.");
            // Trays are never taken from the browser: FLOOR(SeedQuantity / TraySize).
            SeedSowing.NumberOfTrays = null;
            // Sown quantity is never taken from the browser either: the
            // repository sets it to the seeds used in complete trays.
            SeedSowing.QuantitySown = 0;
            if (SeedSowing.SeedQuantity > 0)
            {
                var (traysOk, _, _, _, trayError) = DirectSowingRules.CalculateTrays(SeedSowing.SeedQuantity, SeedSowing.CavityType);
                if (!traysOk)
                    ModelState.AddModelError(string.Empty, trayError!);
            }
            else if (!DirectSowingRules.IsValidCavityType(SeedSowing.CavityType))
                ModelState.AddModelError(string.Empty, $"Tray size must be one of: {string.Join(", ", DirectSowingRules.CavityTypes)}.");

            // The supervisor who will approve this batch (re-checked in the
            // repository inside the save transaction).
            var approverIds = (await _userRoleRepo.GetSowingApproversAsync()).Select(a => a.EmployeeID).ToList();
            var (supervisorOk, supervisorError) = DirectSowingRules.ValidateSupervisorAssignment(
                SeedSowing.SupervisorId, User.GetUserId(), approverIds);
            if (!supervisorOk)
                ModelState.AddModelError(string.Empty, supervisorError!);

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
            SeedSowing.CreatedById = userId;   // used by the self-approval rule

            var (success, message, _) = await _seedSowingRepo.InsertAsync(SeedSowing, userId,
                areaId => _areaAccessService.CanAccessArea(User, areaId));   // re-checked on the resolved growing Area
            if (!success)
            {
                ModelState.AddModelError(string.Empty, message ?? "Failed to record Sowing.");
                await LoadDropdownsAsync();
                return Page();
            }

            TempData["Success"] = $"Sowing batch {SeedSowing.SowingCode} recorded: {SeedSowing.NumberOfTrays:N0} complete trays, {SeedSowing.QuantitySown:N0} seeds sown and deducted from the Main Office lot; {SeedSowing.SeedQuantity - SeedSowing.QuantitySown:N0} remaining seeds stay in the lot. Expected ready: {SeedSowing.ExpectedReadyDate:dd-MM-yyyy}.";
            return RedirectToPage("/Production/SeedSowing/Details", new { id = SeedSowing.Id });
        }

        private async Task LoadDropdownsAsync()
        {
            // Growing Area is OPTIONAL (empty = the seed lot's Main Office
            // Area). Offered: active Areas this user may work in.
            Areas = (await _areaRepo.GetAllAreas())
                .Where(a => a.IsActive && _areaAccessService.CanAccessArea(User, a.Id))
                .OrderBy(a => a.Name)
                .ToList();

            PlantTypes = await _plantTypeRepo.GetAllPlantTypes();
            var activeUsers = await _employeeRepo.GetAllActiveUsers();
            // Supervisor = who will approve: users who can approve sowings,
            // never the person recording this one.
            var me = User.GetUserId();
            Supervisors = (await _userRoleRepo.GetSowingApproversAsync()).Where(a => a.EmployeeID != me).ToList();
            ResponsiblePersons = activeUsers;
        }
    }
}
