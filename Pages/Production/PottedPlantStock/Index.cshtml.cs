using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PlantStockManager.Authorization;
using PlantStockManager.Data;
using PlantStockManager.Services;
using PottedPlantStockModel = PlantStockManager.Models.PottedPlantStock;

namespace PlantStockManager.Pages.Production.PottedPlantStock
{
    public class IndexModel : PageModel
    {
        private readonly PottedPlantStockRepository _pottedPlantStockRepo;
        private readonly AreaAccessService _areaAccessService;

        public IndexModel(PottedPlantStockRepository pottedPlantStockRepo, AreaAccessService areaAccessService)
        {
            _pottedPlantStockRepo = pottedPlantStockRepo;
            _areaAccessService = areaAccessService;
        }

        public List<PottedPlantStockModel> Stocks { get; set; } = new();

        public decimal TotalPhysical => Stocks.Sum(s => s.PhysicalQuantity);
        public decimal TotalReserved => Stocks.Sum(s => s.ReservedQuantity);
        public decimal TotalAvailable => Stocks.Sum(s => s.AvailableQuantity);

        public string? Search { get; set; }
        public bool ShowEmpty { get; set; }

        public async Task OnGetAsync(string? search, bool showEmpty = false)
        {
            Search = search;
            ShowEmpty = showEmpty;
            Stocks = await LoadFilteredAsync(search, showEmpty);
        }

        private async Task<List<PottedPlantStockModel>> LoadFilteredAsync(string? search, bool showEmpty)
        {
            var all = await _pottedPlantStockRepo.GetAllAsync();
            if (!showEmpty)
                all = all.Where(s => s.PhysicalQuantity > 0).ToList();
            if (!string.IsNullOrWhiteSpace(search))
            {
                var t = search.Trim();
                all = all.Where(s => (s.SpeciesName ?? "").Contains(t, StringComparison.OrdinalIgnoreCase)
                                     || (s.PlantTypeName ?? "").Contains(t, StringComparison.OrdinalIgnoreCase)
                                     || (s.SpeciesColor ?? "").Contains(t, StringComparison.OrdinalIgnoreCase)
                                     || s.PotSize.Contains(t, StringComparison.OrdinalIgnoreCase)
                                     || (s.AreaName ?? "").Contains(t, StringComparison.OrdinalIgnoreCase)
                                     || (s.BatchCodes ?? "").Contains(t, StringComparison.OrdinalIgnoreCase)).ToList();
            }

            // Phase E (spec item 12): a Growing Partner Supervisor must only
            // see PottedPlantStock belonging to Areas they are scoped to.
            // Same in-memory post-filter pattern as MotherPlant/Index
            // (Phase 17/B) -- a full-access user (Admin/Management/
            // MainOfficeOfficer) still sees every row unfiltered.
            return _areaAccessService.HasFullAreaAccess(User)
                ? all
                : all.Where(s => _areaAccessService.CanAccessArea(User, s.AreaId)).ToList();
        }

        private static readonly string[] ExportHeaders =
            { "Species", "Plant Type", "Pot Size", "Area", "Batches", "Physical Qty", "Reserved", "Available Qty" };

        private static IReadOnlyList<object?> Row(PottedPlantStockModel s) => new object?[]
            { s.SpeciesName, s.PlantTypeName, s.PotSize, s.AreaName, s.BatchCodes, s.PhysicalQuantity, s.ReservedQuantity, s.AvailableQuantity };

        public async Task<IActionResult> OnGetExportExcelAsync(string? search, bool showEmpty = false)
        {
            var list = await LoadFilteredAsync(search, showEmpty);
            var bytes = ExportHelper.BuildExcel("Potted Plant Stock", ExportHeaders, list.Select(Row));
            return File(bytes, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", $"PottedPlantStock_{DateTime.Now:yyyyMMdd_HHmm}.xlsx");
        }

        public async Task<IActionResult> OnGetExportPdfAsync(string? search, bool showEmpty = false)
        {
            var list = await LoadFilteredAsync(search, showEmpty);
            var rows = list.Select(s => (IReadOnlyList<string>)new[]
            {
                s.SpeciesName ?? "", s.PlantTypeName ?? "", s.PotSize ?? "", s.AreaName ?? "", s.BatchCodes ?? "",
                s.PhysicalQuantity.ToString("N0"), s.ReservedQuantity.ToString("N0"), s.AvailableQuantity.ToString("N0")
            });
            var bytes = ExportHelper.BuildPdf("Potted Plant Stock", ExportHeaders, rows);
            return File(bytes, "application/pdf", $"PottedPlantStock_{DateTime.Now:yyyyMMdd_HHmm}.pdf");
        }
    }
}
