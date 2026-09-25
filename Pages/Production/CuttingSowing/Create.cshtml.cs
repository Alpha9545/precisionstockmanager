using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PlantStockManager.Authorization;
using PlantStockManager.Data;
using PlantStockManager.Models;
using PlantStockManager.Services;
using CuttingSowingModel = PlantStockManager.Models.CuttingSowing;
// Alias required: this folder's own namespace is nested under
// PlantStockManager.Pages.Production, which also has a sibling namespace
// member named "CuttingStock" (Pages/Production/CuttingStock/*.cshtml.cs)
// shadowing the bare Models.CuttingStock type -- the same established fix
// used throughout this codebase (see PotProduction/CreateFromCutting.cshtml.cs).
using CuttingStockModel = PlantStockManager.Models.CuttingStock;

namespace PlantStockManager.Pages.Production.CuttingSowing
{
    // Phase 5 -- CUTTING SOWING: Cutting Stock -> Tray/Cavity -> Ready
    // Stock, the exact mirror of Direct Sowing (Pages/Production/
    // SeedSowing/Create.cshtml.cs) with two deliberate differences: the
    // source is Cutting Stock, never mixed with Seed Stock, and there is
    // no separate growing-Area picker -- the batch is always sown IN
    // PLACE at the source Cutting Stock pool's own Area (same rule
    // PotProduction/CreateFromCutting.cshtml.cs already uses), so Area is
    // preserved automatically.
    public class CreateModel : PageModel
    {
        private readonly CuttingSowingRepository _cuttingSowingRepo;
        private readonly CuttingStockRepository _cuttingStockRepo;
        private readonly UserRoleRepository _userRoleRepo;
        private readonly AreaAccessService _areaAccessService;

        public CreateModel(
            CuttingSowingRepository cuttingSowingRepo,
            CuttingStockRepository cuttingStockRepo,
            UserRoleRepository userRoleRepo,
            AreaAccessService areaAccessService)
        {
            _cuttingSowingRepo = cuttingSowingRepo;
            _cuttingStockRepo = cuttingStockRepo;
            _userRoleRepo = userRoleRepo;
            _areaAccessService = areaAccessService;
        }

        [BindProperty]
        public CuttingSowingModel CuttingSowing { get; set; } = new();

        // The one field the operator actually chooses which pool to draw
        // from -- SpeciesId/AreaId are always re-derived from it server-side.
        [BindProperty]
        public int SourceCuttingStockId { get; set; }

        // Automatically shown: every Cutting Stock pool with available
        // quantity, in an Area this user may sow in.
        public List<CuttingStockModel> AvailableCuttingStocks { get; set; } = new();
        public List<SupervisorOption> Supervisors { get; set; } = new();
        public IReadOnlyList<string> CavityTypes => DirectSowingRules.CavityTypes;

        public async Task OnGetAsync()
        {
            CuttingSowing.SowingDate = DateTime.Today;
            await LoadDropdownsAsync();
        }

        // Live tray calculation for the form -- the SAME server function as
        // the save (DirectSowingRules.CalculateTrays), so the preview and
        // the stored value can never disagree; the save recalculates
        // regardless of what this returns.
        public JsonResult OnGetTrayCalculation(decimal quantity, string? cavityType)
        {
            var (ok, trays, cuttingsUsed, remaining, error) = DirectSowingRules.CalculateTrays(quantity, cavityType);
            return new JsonResult(new
            {
                ok,
                trays,
                cuttingsUsed,
                remainingCuttings = remaining,
                wholeNumber = DirectSowingRules.IsWholeNumber(quantity),
                error
            });
        }

        public async Task<IActionResult> OnPostAsync()
        {
            ModelState.Clear(); // validated explicitly below (server-derived fields are never trusted)

            if (CuttingSowing.SowingDate == default)
                ModelState.AddModelError(string.Empty, "Sowing Date is required.");
            if (SourceCuttingStockId <= 0)
                ModelState.AddModelError(string.Empty, "Cutting batch / variety is required.");
            if (CuttingSowing.CuttingQuantityEntered <= 0)
                ModelState.AddModelError(string.Empty, "Cutting Quantity must be greater than zero.");
            else if (!DirectSowingRules.IsWholeNumber(CuttingSowing.CuttingQuantityEntered))
                ModelState.AddModelError(string.Empty, "Cutting Quantity must be a whole number.");
            // Trays/QuantitySown are never taken from the browser -- the
            // repository recalculates FLOOR(quantity / tray size) from
            // scratch under the source pool's own row lock.
            CuttingSowing.NumberOfTrays = null;
            CuttingSowing.QuantitySown = 0;
            if (CuttingSowing.CuttingQuantityEntered > 0)
            {
                var (traysOk, _, _, _, trayError) = DirectSowingRules.CalculateTrays(CuttingSowing.CuttingQuantityEntered, CuttingSowing.CavityType);
                if (!traysOk)
                    ModelState.AddModelError(string.Empty, trayError!);
            }
            else if (!DirectSowingRules.IsValidCavityType(CuttingSowing.CavityType))
                ModelState.AddModelError(string.Empty, $"Tray Cavity must be one of: {string.Join(", ", DirectSowingRules.CavityTypes)}.");

            // The supervisor who will approve this batch -- the SAME
            // assigned-supervisor eligibility Direct Sowing uses
            // (SupervisorKind.Sowing / ReadyStock.Confirm), re-checked in
            // the repository under lock.
            var approverIds = (await _userRoleRepo.GetEligibleSupervisorsAsync(SupervisorKind.Sowing, areaId: null, enforceArea: false))
                .Select(a => a.EmployeeID).ToList();
            var (supervisorOk, supervisorError) = DirectSowingRules.ValidateSupervisorAssignment(
                CuttingSowing.SupervisorId, User.GetUserId(), approverIds);
            if (!supervisorOk)
                ModelState.AddModelError(string.Empty, supervisorError!);

            if (!ModelState.IsValid)
            {
                await LoadDropdownsAsync();
                return Page();
            }

            CuttingSowing.SourceCuttingStockId = SourceCuttingStockId;
            CuttingSowing.CreatedBy = User.Identity?.Name ?? "System";
            var userId = User.GetUserId();
            CuttingSowing.CreatedById = userId;

            var (success, message, _) = await _cuttingSowingRepo.InsertAsync(CuttingSowing, userId,
                areaId => _areaAccessService.CanAccessArea(User, areaId));
            if (!success)
            {
                ModelState.AddModelError(string.Empty, message ?? "Failed to record Cutting Sowing.");
                await LoadDropdownsAsync();
                return Page();
            }

            TempData["Success"] = $"Cutting Sowing batch {CuttingSowing.SowingCode} recorded: {CuttingSowing.NumberOfTrays:N0} complete trays, {CuttingSowing.QuantitySown:N0} cuttings sown and deducted from the source pool; {CuttingSowing.RemainingCuttings:N0} remaining cuttings stay in stock. Expected ready: {CuttingSowing.ExpectedReadyDate:dd-MM-yyyy}.";
            return RedirectToPage("/Production/CuttingSowing/Details", new { id = CuttingSowing.Id });
        }

        private async Task LoadDropdownsAsync()
        {
            var allStock = await _cuttingStockRepo.GetAllAsync();
            AvailableCuttingStocks = allStock
                .Where(s => s.AvailableQuantity > 0 && _areaAccessService.CanAccessArea(User, s.AreaId))
                .OrderBy(s => s.SpeciesName).ThenBy(s => s.AreaName)
                .ToList();

            var me = User.GetUserId();
            Supervisors = SupervisorRules.Options(
                await _userRoleRepo.GetSupervisorCandidatesAsync(SupervisorKind.Sowing), me.HasValue ? new[] { me.Value } : null);
        }
    }
}
