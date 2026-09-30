using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PlantStockManager.Authorization;
using PlantStockManager.Data;
using PlantStockManager.Models;
using PlantStockManager.Services;
using CuttingStockModel = PlantStockManager.Models.CuttingStock;
using SeedSowingModel = PlantStockManager.Models.SeedSowing;

namespace PlantStockManager.Pages.Production.SeedSowing
{
    // Cutting Tray Sowing: cuttings from Cutting Stock are placed in trays.
    //   Complete Trays  = FLOOR(Cutting Quantity / Cavity)
    //   Used Cutting    = Complete Trays x Cavity   (leaves Cutting Stock)
    //   Remaining       = the rest                   (stays in Cutting Stock)
    // Then the same Supervisor Approval -> Ready Seedling Stock as seed sowing.
    public class CreateFromCuttingModel : PageModel
    {
        private readonly SeedSowingRepository _seedSowingRepo;
        private readonly CuttingStockRepository _cuttingStockRepo;
        private readonly UserRoleRepository _userRoleRepo;
        private readonly AreaRepository _areaRepo;
        private readonly PolyhouseRepository _polyhouseRepo;
        private readonly AreaAccessService _areaAccess;

        public CreateFromCuttingModel(SeedSowingRepository seedSowingRepo, CuttingStockRepository cuttingStockRepo,
            UserRoleRepository userRoleRepo, AreaRepository areaRepo, PolyhouseRepository polyhouseRepo, AreaAccessService areaAccess)
        {
            _seedSowingRepo = seedSowingRepo;
            _cuttingStockRepo = cuttingStockRepo;
            _userRoleRepo = userRoleRepo;
            _areaRepo = areaRepo;
            _polyhouseRepo = polyhouseRepo;
            _areaAccess = areaAccess;
        }

        [BindProperty] public int CuttingStockId { get; set; }
        [BindProperty] public decimal CuttingQuantity { get; set; }
        [BindProperty] public string? CavityType { get; set; }
        [BindProperty] public DateTime SowingDate { get; set; } = DateTime.Today;
        [BindProperty] public int? AreaId { get; set; }
        [BindProperty] public int? PolyhouseId { get; set; }
        [BindProperty] public int? SupervisorId { get; set; }
        [BindProperty] public string? Remarks { get; set; }

        public List<CuttingStockModel> StockPools { get; set; } = new();
        public List<Area> Areas { get; set; } = new();
        public List<Employee> Supervisors { get; set; } = new();
        public IReadOnlyList<string> CavityTypes => DirectSowingRules.CavityTypes;

        public async Task OnGetAsync(int? cuttingStockId)
        {
            await LoadAsync();
            if (cuttingStockId.HasValue && StockPools.Any(s => s.Id == cuttingStockId))
                CuttingStockId = cuttingStockId.Value;
            await LoadSupervisorsAsync();
        }

        // Sowing Supervisor list for the GROWING Area (the Area chosen here, else the
        // Polyhouse's Area, else the Cutting Stock's own Area -- the same resolution
        // the save uses): active users assigned to that Area who can approve
        // sowings, whatever their role is called -- the person recording the sowing
        // included, who may choose themselves. Reloaded by the page whenever the
        // Area, Cutting Stock or Polyhouse changes.
        public async Task<JsonResult> OnGetSupervisorsAsync(int areaId, int cuttingStockId, int polyhouseId)
        {
            var growingAreaId = await ResolveGrowingAreaAsync(areaId > 0 ? areaId : null, cuttingStockId, polyhouseId > 0 ? polyhouseId : null);
            if (!growingAreaId.HasValue || !_areaAccess.CanAccessArea(User, growingAreaId))
                return new JsonResult(Array.Empty<object>());
            var list = await _userRoleRepo.GetCuttingSowingSupervisorsAsync(growingAreaId.Value);
            return new JsonResult(list.Select(u => new { id = u.EmployeeID, name = u.Name }));
        }

        // Live preview -- the same function the save uses.
        public JsonResult OnGetTrayCalculation(decimal quantity, string? cavityType)
        {
            var (ok, trays, used, remaining, error) = DirectSowingRules.CalculateTrays(quantity, cavityType, DirectSowingRules.CuttingQuantityLabel);
            return new JsonResult(new { ok, trays, used, remaining, wholeNumber = DirectSowingRules.IsWholeNumber(quantity), error });
        }

        // Area first: only the Polyhouses assigned to the selected Area (plus
        // Polyhouses with no Area assigned yet, same as Direct Seed Sowing's
        // own OnGetPolyhousesAsync) -- never another Area's Polyhouses.
        public async Task<JsonResult> OnGetPolyhousesAsync(int areaId)
        {
            if (areaId > 0 && !_areaAccess.CanAccessArea(User, areaId))
                return new JsonResult(Array.Empty<object>());
            var list = (await _polyhouseRepo.GetAllPolyhouses())
                .Where(p => !p.AreaId.HasValue || (areaId > 0 && p.AreaId == areaId))
                .OrderBy(p => p.Name);
            return new JsonResult(list.Select(p => new { id = p.Id, name = p.AreaId.HasValue ? p.Name : p.Name + " (no Area assigned)" }));
        }

        public async Task<IActionResult> OnPostAsync()
        {
            var stock = await _cuttingStockRepo.GetByIdAsync(CuttingStockId);
            if (stock == null || !CanUseSource(stock))
                ModelState.AddModelError(string.Empty, "Choose a Cutting Stock you can use.");
            if (!DirectSowingRules.IsValidCavityType(CavityType))
                ModelState.AddModelError(string.Empty, $"Tray size must be one of: {string.Join(", ", DirectSowingRules.CavityTypes)}.");
            else if (stock != null)
            {
                var (ok, _, _, _, _, error) = DirectSowingRules.PlanSowing(CuttingQuantity, CavityType, stock.PhysicalQuantity, stock.InTransitQuantity,
                    DirectSowingRules.CuttingQuantityLabel, "Cutting Stock");
                if (!ok)
                    ModelState.AddModelError(string.Empty, error!);
            }
            // Supervisor: eligible for the GROWING Area (re-checked by the repository
            // under its transaction; this gives the message before anything is locked).
            var growingAreaId = await ResolveGrowingAreaAsync(AreaId, CuttingStockId, PolyhouseId);
            if (growingAreaId.HasValue)
            {
                var eligible = (await _userRoleRepo.GetCuttingSowingSupervisorsAsync(growingAreaId.Value))
                    .Select(a => a.EmployeeID).ToList();
                var (supervisorOk, supervisorError) = SowingSupervisorRules.ValidateAssignment(SupervisorId, eligible);
                if (!supervisorOk)
                    ModelState.AddModelError(string.Empty, supervisorError!);
            }
            if (AreaId.HasValue && !_areaAccess.CanAccessArea(User, AreaId))
                ModelState.AddModelError(string.Empty, "You are not authorized to sow in the selected Area.");

            if (!ModelState.IsValid)
            {
                await LoadAsync();
                await LoadSupervisorsAsync();
                return Page();
            }

            var sowing = new SeedSowingModel
            {
                SourceCuttingStockId = CuttingStockId,
                SpeciesId = stock!.SpeciesId,
                SeedQuantity = CuttingQuantity,
                CavityType = CavityType!,
                SowingDate = SowingDate,
                AreaId = AreaId ?? 0,
                PolyhouseId = PolyhouseId,
                SupervisorId = SupervisorId,
                Remarks = Remarks,
                CreatedBy = User.Identity?.Name ?? "System",
                CreatedById = User.GetUserId()
            };
            // Area isolation: the repository re-checks both the growing Area and the pool's own Area under its lock.
            var (success, message, _) = await _seedSowingRepo.InsertFromCuttingAsync(sowing, User.GetUserId(),
                areaId => _areaAccess.CanAccessArea(User, areaId),
                areaId => CuttingRules.CanUseAsSource(_areaAccess.CanAccessArea(User, areaId)));
            if (!success)
            {
                ModelState.AddModelError(string.Empty, message ?? "Failed to record the cutting tray sowing.");
                await LoadAsync();
                await LoadSupervisorsAsync();
                return Page();
            }

            TempData["Success"] = $"Tray sowing {sowing.SowingCode} recorded: {sowing.NumberOfTrays:N0} complete trays, {sowing.QuantitySown:N0} cuttings used; "
                + $"{sowing.SeedQuantity - sowing.QuantitySown:N0} remaining cuttings stay in Cutting Stock. Expected ready: {sowing.ExpectedReadyDate:dd-MM-yyyy}.";
            return RedirectToPage("/Production/SeedSowing/Details", new { id = sowing.Id });
        }

        // Only cuttings held in an Area the user may access (Main Office stock is the Main Office Area's stock).
        private bool CanUseSource(CuttingStockModel s)
            => CuttingRules.CanUseAsSource(_areaAccess.CanAccessArea(User, s.AreaId));

        private async Task LoadAsync()
        {
            StockPools = (await _cuttingStockRepo.GetAllAsync())
                .Where(s => s.AvailableQuantity > 0 && CanUseSource(s))
                .OrderBy(s => s.SpeciesName).ThenBy(s => s.AreaName)
                .ToList();
            Areas = (await _areaRepo.GetAllAreas()).Where(a => a.IsActive && _areaAccess.CanAccessArea(User, a.Id)).OrderBy(a => a.Name).ToList();
        }

        // The supervisors of the growing Area the form currently resolves to (used
        // to draw the list on first load and after a failed save, so the chosen
        // person stays selected). Empty until a Cutting Stock or Area is chosen.
        private async Task LoadSupervisorsAsync()
        {
            var growingAreaId = await ResolveGrowingAreaAsync(AreaId, CuttingStockId, PolyhouseId);
            Supervisors = growingAreaId.HasValue && _areaAccess.CanAccessArea(User, growingAreaId)
                ? await _userRoleRepo.GetCuttingSowingSupervisorsAsync(growingAreaId.Value)
                : new List<Employee>();
        }

        // The growing Area exactly as the save resolves it: the chosen Area, else
        // the chosen Polyhouse's Area, else the Cutting Stock's own Area.
        private async Task<int?> ResolveGrowingAreaAsync(int? requestedAreaId, int cuttingStockId, int? polyhouseId)
        {
            var stockAreaId = 0;
            if (cuttingStockId > 0)
            {
                var stock = await _cuttingStockRepo.GetByIdAsync(cuttingStockId);
                if (stock != null && CanUseSource(stock))
                    stockAreaId = stock.AreaId;
            }
            int? polyhouseAreaId = null;
            if (polyhouseId is > 0)
                polyhouseAreaId = (await _polyhouseRepo.GetAllPolyhouses()).FirstOrDefault(p => p.Id == polyhouseId)?.AreaId;
            var (ok, areaId, _, _) = DirectSowingRules.ResolveGrowingLocation(stockAreaId, requestedAreaId, polyhouseId, polyhouseAreaId);
            return ok && areaId > 0 ? areaId : null;
        }
    }
}
