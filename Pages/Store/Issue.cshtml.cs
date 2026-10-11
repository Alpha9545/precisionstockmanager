using Microsoft.AspNetCore.Mvc.RazorPages;
using PlantStockManager.Data;
using PlantStockManager.Models;
using PlantStockManager.Services;

namespace PlantStockManager.Pages.Store
{
    // Issue queue (Store.Manage): approved requests of every Area, waiting to be issued.
    public class IssueModel : PageModel
    {
        private readonly StoreRepository _repo;
        public IssueModel(StoreRepository repo) => _repo = repo;
        public List<StoreRequest> Requests { get; set; } = new();
        public async Task OnGetAsync() => Requests = await _repo.SearchRequestsAsync(null, StoreRules.Approved);
    }
}
