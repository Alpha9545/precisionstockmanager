using Microsoft.AspNetCore.Mvc.RazorPages;
using PlantStockManager.Authorization;
using PlantStockManager.Data;
using PlantStockManager.Models;
using PlantStockManager.Services;

namespace PlantStockManager.Pages.Store
{
    // Inventory Store dashboard: items, Main Office stock, low stock, requests by status, what is
    // out on issue, returns waiting for confirmation, and what is permanently at the Areas. The
    // Store Keeper sees every Area; a supervisor sees the requests / holdings of their own Areas.
    public class IndexModel : PageModel
    {
        private readonly StoreRepository _repo;
        private readonly StoreAuthorization _auth;

        public IndexModel(StoreRepository repo, StoreAuthorization auth)
        {
            _repo = repo;
            _auth = auth;
        }

        public bool IsManager { get; set; }
        public int TotalItems { get; set; }
        public decimal AvailableUnits { get; set; }
        public List<StoreItem> LowStock { get; set; } = new();
        public int Pending { get; set; }
        public int ApprovedCount { get; set; }
        public decimal IssuedOut { get; set; }
        public int PendingReturns { get; set; }
        public decimal Assigned { get; set; }
        public List<StoreRequest> ActionNeeded { get; set; } = new();

        public async Task OnGetAsync()
        {
            IsManager = _auth.IsManager(User);
            var scope = _auth.AreaScope(User);
            var items = (await _repo.GetItemsAsync()).Where(i => i.IsActive).ToList();
            TotalItems = items.Count;
            AvailableUnits = items.Sum(i => i.AvailableQuantity);
            LowStock = items.Where(i => i.IsLow).OrderBy(i => i.Name).ToList();

            var requests = await _repo.SearchRequestsAsync(scope);
            Pending = requests.Count(r => r.Status == StoreRules.Pending);
            ApprovedCount = requests.Count(r => r.Status == StoreRules.Approved);
            PendingReturns = requests.Sum(r => r.PendingReturns);
            var holdings = await _repo.GetHoldingsAsync(scope);
            IssuedOut = holdings.Sum(h => h.OutOnIssue + h.PendingReturn);
            Assigned = holdings.Sum(h => h.Assigned);

            // What waits for THIS user.
            ActionNeeded = IsManager
                ? requests.Where(r => r.Status is StoreRules.Pending or StoreRules.Approved || r.PendingReturns > 0).ToList()
                : requests.Where(r => r.Status == StoreRules.Issued && r.UnsettledLines > 0 && _auth.CanRequestFor(User, r.AreaId)).ToList();
        }
    }
}
