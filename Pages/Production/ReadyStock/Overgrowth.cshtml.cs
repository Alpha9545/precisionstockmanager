using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Routing;
using PlantStockManager.Authorization;
using PlantStockManager.Data;
using PlantStockManager.Models;
using PlantStockManager.Services;
using ReadyStockModel = PlantStockManager.Models.ReadyStock;

namespace PlantStockManager.Pages.Production.ReadyStock
{
    // 2026-10-22: mark / unmark ONE Ready Stock batch as "⚠ OG -- Overgrowth" (Services/OvergrowthRules.cs), and its history.
    // Page access: ReadyStock.Overgrowth (FeatureAuthorizationConventions). The batch must be in an Area the user is assigned to
    // (AreaAccessService -- the regular rule, never the relaxed seedling scope); ReadyStockRepository.SetOvergrowthAsync then
    // re-checks, under the batch's lock, that the user holds ReadyStock.Overgrowth through a role IN THAT AREA (or is a System
    // Administrator). Bound: the reason only -- the batch is the route id, the user and Area come from the server side.
    // Nothing else about the batch changes: it stays available for booking and dispatch.
    public class OvergrowthModel : PageModel
    {
        private readonly ReadyStockRepository _readyStockRepo;
        private readonly AreaAccessService _areaAccess;
        private readonly DatabaseHelper _db;

        public OvergrowthModel(ReadyStockRepository readyStockRepo, AreaAccessService areaAccess, DatabaseHelper db)
        {
            _readyStockRepo = readyStockRepo;
            _areaAccess = areaAccess;
            _db = db;
        }

        [BindProperty] public string? Reason { get; set; }

        // 2026-10-23: the Ready Stock list's filters, only carried back to it (IndexModel.ListRouteFor re-validates them).
        // Deliberately NOT [BindProperty]: this page binds only Reason. They are read from the query string, and used for
        // nothing but the links / redirect back to the list.
        public string? Stock { get; set; }
        public string? Search { get; set; }
        public bool IncludeEmpty { get; set; }
        // 2026-10-27: the list's option-list filters too (shape-checked only; Index re-validates them).
        public Services.ReadyStockListFilter ListFilter { get; private set; } = Services.ReadyStockListFilter.None;
        public Dictionary<string, string?> ListRoute => IndexModel.ListRouteFor(Stock, Search, IncludeEmpty, ListFilter);

        private void ReadListFilters()
        {
            Stock ??= Request.Query["Stock"].FirstOrDefault();
            Search ??= Request.Query["Search"].FirstOrDefault();
            if (!IncludeEmpty)
                IncludeEmpty = bool.TryParse(Request.Query["IncludeEmpty"].FirstOrDefault(), out var includeEmpty) && includeEmpty;
            ListFilter = Services.ReadyStockListFilter.FromQuery(Request.Query);
        }

        public ReadyStockModel Batch { get; private set; } = new();
        public List<ReadyStockOvergrowthEntry> History { get; private set; } = new();
        public bool SchemaReady { get; private set; }
        public bool CanChange { get; private set; }
        public string? Error { get; private set; }

        public async Task<IActionResult> OnGetAsync(int id)
        {
            ReadListFilters();
            return await LoadAsync(id) ? Page() : Denied();
        }

        public Task<IActionResult> OnPostMarkAsync(int id) => SaveAsync(id, mark: true);
        public Task<IActionResult> OnPostUnmarkAsync(int id) => SaveAsync(id, mark: false);

        private async Task<IActionResult> SaveAsync(int id, bool mark)
        {
            ReadListFilters();
            if (!await LoadAsync(id))
                return Denied();
            var (ok, message, _) = await _readyStockRepo.SetOvergrowthAsync(id, mark, Reason,
                new ReadyStockRepository.OvergrowthActor(User.GetUserId(), User.Identity?.Name, User.IsFullAccess()));
            if (!ok)
            {
                Error = message;
                return Page();
            }
            TempData["Success"] = message;
            var route = new RouteValueDictionary(ListRoute.Where(kv => kv.Value != null).ToDictionary(kv => kv.Key, kv => (object?)kv.Value)) { ["id"] = id };
            return RedirectToPage(route);
        }

        private async Task<bool> LoadAsync(int id)
        {
            var batch = await _readyStockRepo.GetByIdAsync(id);
            if (batch == null || !_areaAccess.CanAccessArea(User, batch.AreaId))
                return false;
            Batch = batch;
            SchemaReady = await ReadyStockOvergrowthSchema.ReadyAsync(_db);
            CanChange = SchemaReady && await _readyStockRepo.CanMarkOvergrowthAsync(User.GetUserId(), User.IsFullAccess(), batch.AreaId);
            History = await _readyStockRepo.GetOvergrowthLogAsync(id);
            return true;
        }

        private IActionResult Denied()
        {
            TempData["Error"] = "Ready Stock batch not found, or it belongs to an Area you cannot access.";
            return RedirectToPage("/Production/ReadyStock/Index");
        }
    }
}
