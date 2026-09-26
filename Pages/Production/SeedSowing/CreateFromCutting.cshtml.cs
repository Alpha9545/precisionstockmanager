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
        private readonly AreaAccessService _areaAccess;

        public CreateFromCuttingModel(SeedSowingRepository seedSowingRepo, CuttingStockRepository cuttingStockRepo,
            UserRoleRepository userRoleRepo, AreaRepository areaRepo, AreaAccessService areaAccess)
        {
            _seedSowingRepo = seedSowingRepo;
            _cuttingStockRepo = cuttingStockRepo;
            _userRoleRepo = userRoleRepo;
            _areaRepo = areaRepo;
            _areaAccess = areaAccess;
        }

        [BindProperty] public int CuttingStockId { get; set; }
        [BindProperty] public decimal CuttingQuantity { get; set; }
        [BindProperty] public string? CavityType { get; set; }
        [BindProperty] public DateTime SowingDate { get; set; } = DateTime.Today;
        [BindProperty] public int? AreaId { get; set; }
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
        }

        // Live preview -- the same function the save uses.
        public JsonResult OnGetTrayCalculation(decimal quantity, string? cavityType)
        {
            var (ok, trays, used, remaining, error) = DirectSowingRules.CalculateTrays(quantity, cavityType, DirectSowingRules.CuttingQuantityLabel);
            return new JsonResult(new { ok, trays, used, remaining, wholeNumber = DirectSowingRules.IsWholeNumber(quantity), error });
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
            var approverIds = (await _userRoleRepo.GetSowingApproversAsync()).Select(a => a.EmployeeID).ToList();
            var (supervisorOk, supervisorError) = DirectSowingRules.ValidateSupervisorAssignment(SupervisorId, User.GetUserId(), approverIds);
            if (!supervisorOk)
                ModelState.AddModelError(string.Empty, supervisorError!);
            if (AreaId.HasValue && !_areaAccess.CanAccessArea(User, AreaId))
                ModelState.AddModelError(string.Empty, "You are not authorized to sow in the selected Area.");

            if (!ModelState.IsValid)
            {
                await LoadAsync();
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
                SupervisorId = SupervisorId,
                Remarks = Remarks,
                CreatedBy = User.Identity?.Name ?? "System",
                CreatedById = User.GetUserId()
            };
            var mainOfficeIds = (await _areaRepo.GetAllAreas()).Where(a => a.AreaType == DirectSowingRules.MainOfficeAreaType).Select(a => a.Id).ToHashSet();
            var (success, message, _) = await _seedSowingRepo.InsertFromCuttingAsync(sowing, User.GetUserId(),
                areaId => _areaAccess.CanAccessArea(User, areaId),
                areaId => CuttingRules.CanUseAsSource(mainOfficeIds.Contains(areaId), _areaAccess.CanAccessArea(User, areaId)));
            if (!success)
            {
                ModelState.AddModelError(string.Empty, message ?? "Failed to record the cutting tray sowing.");
                await LoadAsync();
                return Page();
            }

            TempData["Success"] = $"Tray sowing {sowing.SowingCode} recorded: {sowing.NumberOfTrays:N0} complete trays, {sowing.QuantitySown:N0} cuttings used; "
                + $"{sowing.SeedQuantity - sowing.QuantitySown:N0} remaining cuttings stay in Cutting Stock. Expected ready: {sowing.ExpectedReadyDate:dd-MM-yyyy}.";
            return RedirectToPage("/Production/SeedSowing/Details", new { id = sowing.Id });
        }

        // Main Office Cutting Stock, or cuttings held in the user's own Areas.
        private bool CanUseSource(CuttingStockModel s)
            => CuttingRules.CanUseAsSource(s.AreaType == DirectSowingRules.MainOfficeAreaType, _areaAccess.CanAccessArea(User, s.AreaId));

        private async Task LoadAsync()
        {
            StockPools = (await _cuttingStockRepo.GetAllAsync())
                .Where(s => s.AvailableQuantity > 0 && CanUseSource(s))
                .OrderBy(s => s.SpeciesName).ThenBy(s => s.AreaName)
                .ToList();
            Areas = (await _areaRepo.GetAllAreas()).Where(a => a.IsActive && _areaAccess.CanAccessArea(User, a.Id)).OrderBy(a => a.Name).ToList();
            var me = User.GetUserId();
            Supervisors = (await _userRoleRepo.GetSowingApproversAsync()).Where(s => s.EmployeeID != me).ToList();
        }
    }
}
