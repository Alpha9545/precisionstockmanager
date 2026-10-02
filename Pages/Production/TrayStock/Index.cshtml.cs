using Microsoft.AspNetCore.Mvc.RazorPages;
using PlantStockManager.Data;
// Alias required: this file's own namespace is nested under
// PlantStockManager.Pages.Production.TrayStock, which shadows the bare
// "TrayStock" simple name ahead of Models.TrayStock (CS0118). Same
// established fix already used elsewhere in this codebase (see
// PotProduction/CreateFromCutting.cshtml.cs's "PotProductionModel" alias,
// CuttingStock/ConfirmReceipt.cshtml.cs's "InternalTransferModel" alias).
using TrayStockModel = PlantStockManager.Models.TrayStock;

namespace PlantStockManager.Pages.Production.TrayStock
{
    // Tray Stock summary: one row per (Polyhouse, Tray Size) pool, Main
    // Office Polyhouses only (TrayStockRepository.GetMainOfficePolyhousesAsync
    // is the same restriction the allocation screen and the Seed/Cutting
    // Sowing consumption both already enforce server-side -- this page is
    // read-only display, never the authority).
    public class IndexModel : PageModel
    {
        private readonly TrayStockRepository _trayStockRepo;

        public IndexModel(TrayStockRepository trayStockRepo)
        {
            _trayStockRepo = trayStockRepo;
        }

        public List<TrayStockModel> Stock { get; set; } = new();
        public string? Search { get; set; }

        public async Task OnGetAsync(string? search)
        {
            Search = search;
            var all = await _trayStockRepo.GetAllAsync(activeOnly: true);
            Stock = string.IsNullOrWhiteSpace(search)
                ? all
                : all.Where(s => (s.PolyhouseName ?? "").Contains(search, StringComparison.OrdinalIgnoreCase)
                               || (s.AreaName ?? "").Contains(search, StringComparison.OrdinalIgnoreCase)
                               || s.TraySize.Contains(search, StringComparison.OrdinalIgnoreCase))
                    .ToList();
        }
    }
}
