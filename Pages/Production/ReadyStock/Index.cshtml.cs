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

        // 2026-10-22 Overgrowth monitoring: "og" = Overgrowth batches only, "normal" = the others, anything else = all.
        [BindProperty(SupportsGet = true)]
        public string? Stock { get; set; }
        public const string StockOvergrowth = "og";
        public const string StockNormal = "normal";

        // 2026-10-27: option-list filters (Services/ReadyStockListFilter.cs). Raw query values; only Filter (after Normalize
        // against options built from the user's own Area-scoped rows) is ever applied.
        [BindProperty(SupportsGet = true, Name = ReadyStockListFilter.SpeciesKey)] public string? Species { get; set; }
        [BindProperty(SupportsGet = true, Name = ReadyStockListFilter.VarietyKey)] public int? Variety { get; set; }
        [BindProperty(SupportsGet = true, Name = ReadyStockListFilter.AreaKey)] public int? Area { get; set; }
        [BindProperty(SupportsGet = true, Name = ReadyStockListFilter.PolyhouseKey)] public int? Polyhouse { get; set; }
        [BindProperty(SupportsGet = true, Name = ReadyStockListFilter.OriginKey)] public string? Origin { get; set; }
        [BindProperty(SupportsGet = true, Name = ReadyStockListFilter.StatusKey)] public string? Status { get; set; }

        public ReadyStockListFilter Filter { get; private set; } = ReadyStockListFilter.None;
        public ReadyStockListFilter.ListOptions Options { get; private set; } = ReadyStockListFilter.OptionsFor(Array.Empty<ReadyStockModel>());
        public string? FilterNote { get; private set; }

        // 2026-10-23: the list's filters, carried to the Overgrowth / Trace pages and back, so returning to Ready Stock
        // shows the same list. Only Index's own filters -- every value is re-validated by Index on return.
        // 2026-10-27: plus the option-list filters, each key added only when that filter is set.
        public static Dictionary<string, string?> ListRouteFor(string? stock, string? search, bool includeEmpty, ReadyStockListFilter? filter = null)
        {
            var route = new Dictionary<string, string?>
            {
                ["Stock"] = stock is StockOvergrowth or StockNormal ? stock : null,
                ["Search"] = string.IsNullOrWhiteSpace(search) ? null : search,
                ["IncludeEmpty"] = includeEmpty ? "true" : null,
            };
            foreach (var kv in (filter ?? ReadyStockListFilter.None).RouteValues())
                route[kv.Key] = kv.Value;
            return route;
        }
        public Dictionary<string, string?> ListRoute => ListRouteFor(Stock, Search, IncludeEmpty, Filter);
        // The same list with a different Stock choice (the "Show Overgrowth only" link).
        public Dictionary<string, string?> ListRouteWithStock(string? stock) => ListRouteFor(stock, Search, IncludeEmpty, Filter);

        // The two kinds of stock, counted separately over the rows listed by the Search / IncludeEmpty / option-list filters (before Stock).
        public sealed record StockTotals(int Batches, decimal Physical, decimal Available, decimal Reserved);
        public StockTotals OvergrowthTotals { get; private set; } = new(0, 0, 0, 0);
        public StockTotals NormalTotals { get; private set; } = new(0, 0, 0, 0);

        public List<ReadyStockModel> Items { get; set; } = new();

        public decimal TotalQuantity => Items.Sum(i => i.PhysicalQuantity);

        public async Task OnGetAsync()
        {
            Items = await LoadFilteredAsync(applyStock: false);
            OvergrowthTotals = Totals(Items.Where(i => i.IsOvergrowth));
            NormalTotals = Totals(Items.Where(i => !i.IsOvergrowth));
            Items = ApplyStock(Items);
        }

        private static StockTotals Totals(IEnumerable<ReadyStockModel> rows)
        {
            var list = rows.ToList();
            return new StockTotals(list.Count, list.Sum(r => r.PhysicalQuantity), list.Sum(r => r.AvailableQuantity), list.Sum(r => r.ReservedQuantity));
        }

        private List<ReadyStockModel> ApplyStock(List<ReadyStockModel> rows) => Stock switch
        {
            StockOvergrowth => rows.Where(r => r.IsOvergrowth).ToList(),
            StockNormal => rows.Where(r => !r.IsOvergrowth).ToList(),
            _ => rows
        };

        private async Task<List<ReadyStockModel>> LoadFilteredAsync(bool applyStock = true)
        {
            var all = await _readyStockRepo.GetAllAsync();
            IEnumerable<ReadyStockModel> q = _areaAccessService.HasFullAreaAccess(User)
                ? all
                : all.Where(r => _areaAccessService.CanAccessArea(User, r.AreaId));

            if (!IncludeEmpty)
                q = q.Where(r => r.PhysicalQuantity > 0);

            // Options from exactly the rows this user may list (Area scope + Include-empty), before any other filter.
            var listable = q.ToList();
            Options = ReadyStockListFilter.OptionsFor(listable);
            (Filter, FilterNote) = ReadyStockListFilter.Normalize(Species, Variety, Area, Polyhouse, Origin, Status, Options);
            q = listable.Where(Filter.Matches);

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

            var rows = q.OrderByDescending(r => r.SowingDate).ThenBy(r => r.SowingCode).ToList();
            return applyStock ? ApplyStock(rows) : rows;
        }

        private static readonly string[] ExportHeaders =
            { "Sowing Code", "Overgrowth", "Origin", "Species", "Plant Type", "Area", "Polyhouse", "Cavity", "Physical Qty", "Reserved", "Dispatched", "In Transit", "Available Qty" };

        private static IReadOnlyList<object?> Row(ReadyStockModel r) => new object?[]
            { r.SowingCode, r.IsOvergrowth ? OvergrowthRules.ShortLabel : "", r.OriginLabel, r.SpeciesName, r.PlantTypeName, r.AreaName, r.PolyhouseName, r.CavityType, r.PhysicalQuantity, r.ReservedQuantity, r.DispatchedQuantity, r.InTransitQuantity, r.AvailableQuantity };

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
                r.SowingCode ?? "", r.IsOvergrowth ? "OG" : "", r.OriginLabel, r.SpeciesName ?? "", r.PlantTypeName ?? "", r.AreaName ?? "", r.PolyhouseName ?? "", r.CavityType ?? "",
                QuantityFormat.Qty(r.PhysicalQuantity), QuantityFormat.Qty(r.ReservedQuantity), QuantityFormat.Qty(r.DispatchedQuantity), QuantityFormat.Qty(r.InTransitQuantity), QuantityFormat.Qty(r.AvailableQuantity)
            });
            var bytes = ExportHelper.BuildPdf("Ready Stock", ExportHeaders, rows);
            return File(bytes, "application/pdf", $"ReadyStock_{DateTime.Now:yyyyMMdd_HHmm}.pdf");
        }
    }
}
