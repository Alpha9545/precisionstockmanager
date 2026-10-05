using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PlantStockManager.Authorization;
using PlantStockManager.Data;
using PlantStockManager.Services;
using CuttingStockModel = PlantStockManager.Models.CuttingStock;

namespace PlantStockManager.Pages.Production.CuttingStock
{
    // Cutting Stock by variety and Area:
    //   Physical   = cuttings held in the Area
    //   In transit = sent to Main Office, not yet confirmed
    //   Available  = Physical - In transit (can be sent, tray-sown or potted)
    public class IndexModel : PageModel
    {
        private readonly CuttingStockRepository _cuttingStockRepo;
        private readonly AreaAccessService _areaAccess;

        public IndexModel(CuttingStockRepository cuttingStockRepo, AreaAccessService areaAccess)
        {
            _cuttingStockRepo = cuttingStockRepo;
            _areaAccess = areaAccess;
        }

        public List<CuttingStockModel> Stock { get; set; } = new();
        public string? Search { get; set; }
        public bool ShowEmpty { get; set; }

        public async Task OnGetAsync(string? search, bool showEmpty = false)
        {
            Search = search;
            ShowEmpty = showEmpty;
            Stock = await LoadFilteredAsync(search, showEmpty);
        }

        private async Task<List<CuttingStockModel>> LoadFilteredAsync(string? search, bool showEmpty)
        {
            var all = _areaAccess.FilterByArea(User, await _cuttingStockRepo.GetAllAsync(), s => (int?)s.AreaId);
            if (!showEmpty)
                all = all.Where(s => s.PhysicalQuantity > 0).ToList();
            if (!string.IsNullOrWhiteSpace(search))
            {
                var t = search.Trim();
                all = all.Where(s => (s.SpeciesName ?? "").Contains(t, StringComparison.OrdinalIgnoreCase)
                                     || (s.PlantTypeName ?? "").Contains(t, StringComparison.OrdinalIgnoreCase)
                                     || (s.SpeciesColor ?? "").Contains(t, StringComparison.OrdinalIgnoreCase)
                                     || (s.AreaName ?? "").Contains(t, StringComparison.OrdinalIgnoreCase)).ToList();
            }
            return all;
        }

        private static readonly string[] ExportHeaders =
            { "Species", "Plant Type", "Area", "Physical Qty", "In Transit", "Available Qty" };

        private static IReadOnlyList<object?> Row(CuttingStockModel s) => new object?[]
            { s.SpeciesName, s.PlantTypeName, s.AreaName, s.PhysicalQuantity, s.InTransitQuantity, s.AvailableQuantity };

        public async Task<IActionResult> OnGetExportExcelAsync(string? search, bool showEmpty = false)
        {
            var list = await LoadFilteredAsync(search, showEmpty);
            var bytes = ExportHelper.BuildExcel("Cutting Stock", ExportHeaders, list.Select(Row));
            return File(bytes, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", $"CuttingStock_{DateTime.Now:yyyyMMdd_HHmm}.xlsx");
        }

        public async Task<IActionResult> OnGetExportPdfAsync(string? search, bool showEmpty = false)
        {
            var list = await LoadFilteredAsync(search, showEmpty);
            var rows = list.Select(s => (IReadOnlyList<string>)new[]
            {
                s.SpeciesName ?? "", s.PlantTypeName ?? "", s.AreaName ?? "",
                QuantityFormat.Qty(s.PhysicalQuantity), QuantityFormat.Qty(s.InTransitQuantity), QuantityFormat.Qty(s.AvailableQuantity)
            });
            var bytes = ExportHelper.BuildPdf("Cutting Stock", ExportHeaders, rows);
            return File(bytes, "application/pdf", $"CuttingStock_{DateTime.Now:yyyyMMdd_HHmm}.pdf");
        }
    }
}
