using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PlantStockManager.Authorization;
using PlantStockManager.Data;
using PlantStockManager.Models;
using PlantStockManager.Services;
using PottedPlantStockModel = PlantStockManager.Models.PottedPlantStock;
using ReadyStockModel = PlantStockManager.Models.ReadyStock;
using OutletWastageModel = PlantStockManager.Models.OutletWastage;

namespace PlantStockManager.Pages.Production.OutletWastage
{
    // The Outlet's own record of losing some of its potted-plant or
    // ready-tray stock -- a DIFFERENT concept from nursery production
    // wastage; no automatic percentage is ever applied.
    public class CreateModel : PageModel
    {
        private readonly OutletWastageRepository _wastageRepo;
        private readonly PottedPlantStockRepository _pottedRepo;
        private readonly ReadyStockRepository _readyRepo;
        private readonly AreaRepository _areaRepo;
        private readonly AreaAccessService _areaAccess;

        public CreateModel(OutletWastageRepository wastageRepo, PottedPlantStockRepository pottedRepo, ReadyStockRepository readyRepo,
            AreaRepository areaRepo, AreaAccessService areaAccess)
        {
            _wastageRepo = wastageRepo;
            _pottedRepo = pottedRepo;
            _readyRepo = readyRepo;
            _areaRepo = areaRepo;
            _areaAccess = areaAccess;
        }

        [BindProperty] public int OutletAreaId { get; set; }
        [BindProperty] public string StockType { get; set; } = OutletStockType.Potted;
        [BindProperty] public int StockId { get; set; }
        [BindProperty] public decimal Quantity { get; set; }
        [BindProperty] public string? Reason { get; set; }
        [BindProperty] public DateTime WastageDate { get; set; } = DateTime.Today;
        [BindProperty] public string? Remarks { get; set; }

        public List<Area> Outlets { get; set; } = new();
        public List<PottedPlantStockModel> PottedOptions { get; set; } = new();
        public List<ReadyStockModel> TrayOptions { get; set; } = new();
        public IReadOnlyList<string> Reasons => OutletWastageRules.Reasons;
        public List<OutletWastageModel> Recent { get; set; } = new();

        public async Task OnGetAsync()
        {
            await LoadAsync();
            if (Outlets.Count == 1)
            {
                OutletAreaId = Outlets[0].Id;
                await LoadStockAsync(OutletAreaId);
            }
        }

        public async Task<JsonResult> OnGetStockOptionsAsync(int outletAreaId)
        {
            if (!_areaAccess.CanAccessArea(User, outletAreaId))
                return new JsonResult(new { potted = Array.Empty<object>(), trays = Array.Empty<object>() });
            var potted = (await _pottedRepo.GetAllAsync())
                .Where(s => s.AreaId == outletAreaId && s.AvailableQuantity - s.InTransitQuantity > 0)
                .OrderBy(s => s.SpeciesName)
                .Select(s => new { id = s.Id, label = $"{s.SpeciesName?.Trim()}{(string.IsNullOrEmpty(s.SpeciesColor) ? "" : " (" + s.SpeciesColor + ")")} - {s.PotSize} - available {s.AvailableQuantity - s.InTransitQuantity:N0}", available = s.AvailableQuantity - s.InTransitQuantity });
            var trays = (await _readyRepo.GetAllAsync())
                .Where(s => s.AreaId == outletAreaId)
                .Select(s => new { s, cavity = DirectSowingRules.CavityCount(s.CavityType) })
                .Where(x => x.cavity is > 0 && x.s.AvailableQuantity > 0)
                .Select(x => new { id = x.s.Id, label = $"{x.s.SpeciesName?.Trim()} - {x.s.CavityType} - available {(x.s.AvailableQuantity / x.cavity!.Value):N0} trays", available = Math.Floor(x.s.AvailableQuantity / x.cavity!.Value) })
                .OrderBy(o => o.label);
            return new JsonResult(new { potted, trays });
        }

        public async Task<IActionResult> OnPostAsync()
        {
            if (!_areaAccess.CanAccessArea(User, OutletAreaId))
                ModelState.AddModelError(string.Empty, "You are not authorized for the selected Outlet.");
            if (!ModelState.IsValid)
            {
                await LoadAsync();
                if (OutletAreaId > 0) await LoadStockAsync(OutletAreaId);
                return Page();
            }

            var (success, message, id) = await _wastageRepo.InsertAsync(
                StockType, StockId, Quantity, Reason ?? "", Remarks, WastageDate, OutletAreaId, User.GetUserId(), User.Identity?.Name ?? "System");
            if (!success)
            {
                ModelState.AddModelError(string.Empty, message ?? "Failed to record the wastage.");
                await LoadAsync();
                if (OutletAreaId > 0) await LoadStockAsync(OutletAreaId);
                return Page();
            }
            TempData["Success"] = $"Wastage recorded: {Quantity:N0} {(StockType == OutletStockType.Tray ? "trays" : "pots")}.";
            return RedirectToPage();
        }

        private async Task LoadAsync()
        {
            Outlets = _areaAccess.FilterByArea(User, await _areaRepo.GetByAreaTypesAsync(OutletRules.AreaType), a => (int?)a.Id)
                .Where(a => a.IsActive).ToList();
            var recent = await _wastageRepo.GetAllAsync();
            Recent = _areaAccess.FilterByArea(User, recent, r => (int?)r.OutletAreaId).Take(25).ToList();
        }

        private async Task LoadStockAsync(int outletAreaId)
        {
            PottedOptions = (await _pottedRepo.GetAllAsync())
                .Where(s => s.AreaId == outletAreaId && s.AvailableQuantity - s.InTransitQuantity > 0)
                .OrderBy(s => s.SpeciesName).ToList();
            TrayOptions = (await _readyRepo.GetAllAsync())
                .Where(s => s.AreaId == outletAreaId && s.AvailableQuantity > 0)
                .OrderBy(s => s.SpeciesName).ToList();
        }
    }
}
