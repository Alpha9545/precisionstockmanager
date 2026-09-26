using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PlantStockManager.Authorization;
using PlantStockManager.Data;
using PlantStockManager.Services;
using ReadyStockModel = PlantStockManager.Models.ReadyStock;

namespace PlantStockManager.Pages.Production.ReadyStock
{
    // Phase B: approved Ready Stock, one row per sowing batch, traceable to
    // Batch, Species, Variety, Area, Polyhouse, Sowing Date and Approved By.
    // Read-only (permission ReadyStock.View). Area-scoped with
    // AreaAccessService exactly like every other list page.
    // Phase C: shows Reserved (bookings), Dispatched, Physical and Available.
    public class IndexModel : PageModel
    {
        private readonly ReadyStockRepository _readyStockRepo;
        private readonly SeedlingAreaScope _areaAccessService; // seedling-only Area scope (Authorization/SeedlingAreaScope.cs)

        public IndexModel(ReadyStockRepository readyStockRepo, SeedlingAreaScope areaAccessService)
        {
            _readyStockRepo = readyStockRepo;
            _areaAccessService = areaAccessService;
        }

        [BindProperty(SupportsGet = true)]
        public bool IncludeEmpty { get; set; }

        [BindProperty(SupportsGet = true)]
        public string? Search { get; set; }

        public List<ReadyStockModel> Items { get; set; } = new();

        public decimal TotalQuantity => Items.Sum(i => i.PhysicalQuantity);

        public async Task OnGetAsync()
        {
            Items = await LoadFilteredAsync();
        }

        private async Task<List<ReadyStockModel>> LoadFilteredAsync()
        {
            var all = await _readyStockRepo.GetAllAsync();
            IEnumerable<ReadyStockModel> q = _areaAccessService.HasFullAreaAccess(User)
                ? all
                : all.Where(r => _areaAccessService.CanAccessArea(User, r.AreaId));

            if (!IncludeEmpty)
                q = q.Where(r => r.PhysicalQuantity > 0);

            if (!string.IsNullOrWhiteSpace(Search))
            {
                var term = Search.Trim();
                q = q.Where(r =>
                    (r.SowingCode ?? "").Contains(term, StringComparison.OrdinalIgnoreCase) ||
                    (r.SpeciesName ?? "").Contains(term, StringComparison.OrdinalIgnoreCase) ||
                    (r.PlantTypeName ?? "").Contains(term, StringComparison.OrdinalIgnoreCase) ||
                    (r.AreaName ?? "").Contains(term, StringComparison.OrdinalIgnoreCase) ||
                    (r.PolyhouseName ?? "").Contains(term, StringComparison.OrdinalIgnoreCase));
            }

            return q.OrderByDescending(r => r.SowingDate).ThenBy(r => r.SowingCode).ToList();
        }

        private static readonly string[] ExportHeaders =
            { "Sowing Code", "Species", "Plant Type", "Area", "Polyhouse", "Cavity", "Physical Qty", "Reserved", "Dispatched", "Available Qty" };

        private static IReadOnlyList<object?> Row(ReadyStockModel r) => new object?[]
            { r.SowingCode, r.SpeciesName, r.PlantTypeName, r.AreaName, r.PolyhouseName, r.CavityType, r.PhysicalQuantity, r.ReservedQuantity, r.DispatchedQuantity, r.AvailableQuantity };

        public async Task<IActionResult> OnGetExportExcelAsync()
        {
            var list = await LoadFilteredAsync();
            var bytes = ExportHelper.BuildExcel("Ready Stock", ExportHeaders, list.Select(Row));
            return File(bytes, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", $"ReadyStock_{DateTime.Now:yyyyMMdd_HHmm}.xlsx");
        }

        public async Task<IActionResult> OnGetExportPdfAsync()
        {
            var list = await LoadFilteredAsync();
            var rows = list.Select(r => (IReadOnlyList<string>)new[]
            {
                r.SowingCode ?? "", r.SpeciesName ?? "", r.PlantTypeName ?? "", r.AreaName ?? "", r.PolyhouseName ?? "", r.CavityType ?? "",
                r.PhysicalQuantity.ToString("N0"), r.ReservedQuantity.ToString("N0"), r.DispatchedQuantity.ToString("N0"), r.AvailableQuantity.ToString("N0")
            });
            var bytes = ExportHelper.BuildPdf("Ready Stock", ExportHeaders, rows);
            return File(bytes, "application/pdf", $"ReadyStock_{DateTime.Now:yyyyMMdd_HHmm}.pdf");
        }
    }
}
