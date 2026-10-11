using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PlantStockManager.Authorization;
using PlantStockManager.Data;
using PlantStockManager.Models;
using PlantStockManager.Services;

namespace PlantStockManager.Pages.Store
{
    // Store Reports (Store.View / Store.Manage). Every report is limited to the user's Areas (the
    // Store Keeper sees every Area); an Area / Polyhouse the user may not see is ignored, never
    // trusted. Main Office purchases / adjustments have no Area and are shown to the Store Keeper.
    public class ReportsModel : PageModel
    {
        public static readonly (string Key, string Title)[] ReportList =
        {
            ("Stock", "Inventory Stock"), ("Transactions", "Transaction History"), ("Purchase", "Purchases"), ("Issue", "Issues"),
            ("Return", "Returns"), ("Transfer", "Permanent Transfers"), ("Consumption", "Consumption"),
            ("AreaWise", "Area-wise Inventory"), ("PolyhouseWise", "Polyhouse-wise Inventory"), ("ItemHistory", "Item-wise History"),
        };

        private readonly StoreRepository _repo;
        private readonly AreaRepository _areaRepo;
        private readonly PolyhouseRepository _polyhouseRepo;
        private readonly StoreAuthorization _auth;

        public ReportsModel(StoreRepository repo, AreaRepository areaRepo, PolyhouseRepository polyhouseRepo, StoreAuthorization auth)
        {
            _repo = repo;
            _areaRepo = areaRepo;
            _polyhouseRepo = polyhouseRepo;
            _auth = auth;
        }

        [BindProperty(SupportsGet = true)] public string? Report { get; set; }
        [BindProperty(SupportsGet = true)] public DateTime? FromDate { get; set; }
        [BindProperty(SupportsGet = true)] public DateTime? ToDate { get; set; }
        [BindProperty(SupportsGet = true)] public int? ItemId { get; set; }
        [BindProperty(SupportsGet = true)] public int? AreaId { get; set; }
        [BindProperty(SupportsGet = true)] public int? PolyhouseId { get; set; }

        public bool IsManager { get; set; }
        public string Title => ReportList.First(r => r.Key == Report).Title;
        public List<StoreItem> Items { get; set; } = new();
        public List<Area> AreaOptions { get; set; } = new();
        public List<Polyhouse> PolyhouseOptions { get; set; } = new();
        public List<StoreTransaction> Transactions { get; set; } = new();
        public List<StoreHolding> Holdings { get; set; } = new();
        public Dictionary<int, decimal> LedgerSums { get; set; } = new();

        public async Task OnGetAsync()
        {
            if (!ReportList.Any(r => r.Key == Report)) Report = "Stock";
            IsManager = _auth.IsManager(User);
            var scope = _auth.AreaScope(User);
            Items = await _repo.GetItemsAsync();
            AreaOptions = (await _areaRepo.GetAllAreas()).Where(a => _auth.CanSeeArea(User, a.Id)).OrderBy(a => a.Name).ToList();
            if (AreaId.HasValue && !AreaOptions.Any(a => a.Id == AreaId)) AreaId = null;
            if (AreaId.HasValue) PolyhouseOptions = (await _polyhouseRepo.GetByAreaIdAsync(AreaId.Value)).OrderBy(p => p.Name).ToList();
            if (!PolyhouseOptions.Any(p => p.Id == PolyhouseId)) PolyhouseId = null;
            if (FromDate > ToDate) (FromDate, ToDate) = (ToDate, FromDate);

            var type = Report switch
            {
                "Purchase" => StoreRules.Purchase, "Issue" => StoreRules.Issue, "Return" => StoreRules.Return, "Transfer" => StoreRules.PermanentTransfer, _ => null,
            };
            if (Report is "Transactions" or "Purchase" or "Issue" or "Return" or "Transfer" or "ItemHistory")
                Transactions = await _repo.SearchTransactionsAsync(new StoreTransactionFilter
                {
                    TransactionType = type, FromDate = FromDate, ToDate = ToDate, ItemId = ItemId, AreaId = AreaId, PolyhouseId = PolyhouseId,
                }, scope);
            if (Report is "Stock" or "Consumption" or "AreaWise" or "PolyhouseWise" or "ItemHistory")
                Holdings = await _repo.GetHoldingsAsync(scope, AreaId, PolyhouseId, ItemId);
            if (Report == "Stock" && IsManager)
                LedgerSums = await _repo.GetLedgerSumsAsync();
        }

        // Area-wise: holdings summed over the Polyhouses of each Area.
        public IEnumerable<StoreHolding> AreaWise => Holdings.GroupBy(h => (h.ItemId, h.AreaId)).Select(g =>
        {
            var f = g.First();
            return new StoreHolding
            {
                ItemId = f.ItemId, ItemCode = f.ItemCode, ItemName = f.ItemName, UnitName = f.UnitName, ItemType = f.ItemType, AreaId = f.AreaId, AreaName = f.AreaName,
                OutOnIssue = g.Sum(x => x.OutOnIssue), PendingReturn = g.Sum(x => x.PendingReturn), Kept = g.Sum(x => x.Kept),
                Transferred = g.Sum(x => x.Transferred), Consumed = g.Sum(x => x.Consumed),
            };
        }).OrderBy(h => h.AreaName).ThenBy(h => h.ItemName);
    }
}
