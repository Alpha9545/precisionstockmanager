using Microsoft.AspNetCore.Mvc.RazorPages;
using PlantStockManager.Authorization;
using PlantStockManager.Data;
using PlantStockManager.Services;
// Alias required: this file's own namespace is nested under
// PlantStockManager.Pages.Production.TrayStock, which shadows the bare
// "TrayStock" simple name ahead of Models.TrayStock (CS0118). Same
// established fix already used elsewhere in this codebase (see
// PotProduction/CreateFromCutting.cshtml.cs's "PotProductionModel" alias,
// CuttingStock/ConfirmReceipt.cshtml.cs's "InternalTransferModel" alias).
using TrayStockModel = PlantStockManager.Models.TrayStock;

namespace PlantStockManager.Pages.Production.TrayStock
{
    // Tray Stock summary: one row per active Area + Polyhouse + Cavity pool,
    // limited to the Areas this user may access (AreaAccessService), with
    // simple Area / Polyhouse / Cavity filters. The retired Area-level rows
    // are inactive and never listed; their history stays on the
    // Transactions page. Read-only display, never the authority.
    public class IndexModel : PageModel
    {
        private readonly TrayStockRepository _trayStockRepo;
        private readonly AreaAccessService _areaAccess;

        public IndexModel(TrayStockRepository trayStockRepo, AreaAccessService areaAccess)
        {
            _trayStockRepo = trayStockRepo;
            _areaAccess = areaAccess;
        }

        public List<TrayStockModel> Stock { get; set; } = new();
        // Filter choices, built from the pools this user may see.
        public List<(int Id, string Name)> AreaOptions { get; set; } = new();
        public List<(int Id, string Name)> PolyhouseOptions { get; set; } = new();
        public IReadOnlyList<string> TraySizes => DirectSowingRules.CavityTypes;

        public int? AreaId { get; set; }
        public int? PolyhouseId { get; set; }
        public string? TraySize { get; set; }

        public async Task OnGetAsync(int? areaId, int? polyhouseId, string? traySize)
        {
            AreaId = areaId;
            PolyhouseId = polyhouseId;
            TraySize = DirectSowingRules.IsValidCavityType(traySize) ? traySize : null;

            var visible = _areaAccess.FilterByArea(User, await _trayStockRepo.GetAllAsync(activeOnly: true), s => (int?)s.AreaId);
            AreaOptions = visible.Select(s => (s.AreaId, s.AreaName ?? "")).Distinct().OrderBy(a => a.Item2).ToList();
            PolyhouseOptions = visible.Where(s => !AreaId.HasValue || s.AreaId == AreaId)
                .Where(s => s.PolyhouseId.HasValue)
                .Select(s => (s.PolyhouseId!.Value, s.PolyhouseName ?? ""))
                .Distinct().OrderBy(p => p.Item2).ToList();

            Stock = visible
                .Where(s => !AreaId.HasValue || s.AreaId == AreaId)
                .Where(s => !PolyhouseId.HasValue || s.PolyhouseId == PolyhouseId)
                .Where(s => TraySize == null || s.TraySize == TraySize)
                .ToList();
        }
    }
}
