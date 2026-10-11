using Microsoft.AspNetCore.Mvc.RazorPages;
using PlantStockManager.Authorization;
using PlantStockManager.Data;
using PlantStockManager.Models;
using PlantStockManager.Services;

namespace PlantStockManager.Pages.Store
{
    // Returns: returned items declared by the Areas and waiting for the Store Keeper to confirm the
    // physical return (the Store Keeper sees every Area; a supervisor their own Areas).
    public class ReturnsModel : PageModel
    {
        private readonly StoreRepository _repo;
        private readonly StoreAuthorization _auth;
        public ReturnsModel(StoreRepository repo, StoreAuthorization auth) { _repo = repo; _auth = auth; }
        public List<StoreRequest> Requests { get; set; } = new();
        public List<StoreTransaction> Confirmed { get; set; } = new();
        public async Task OnGetAsync()
        {
            var scope = _auth.AreaScope(User);
            Requests = await _repo.SearchRequestsAsync(scope, pendingReturnsOnly: true);
            Confirmed = (await _repo.SearchTransactionsAsync(new StoreTransactionFilter { TransactionType = StoreRules.Return }, scope)).Take(50).ToList();
        }
    }
}
