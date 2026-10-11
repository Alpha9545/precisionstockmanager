using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PlantStockManager.Authorization;
using PlantStockManager.Data;
using PlantStockManager.Models;
using PlantStockManager.Services;

namespace PlantStockManager.Pages.Store
{
    // Store requests: the Store Keeper sees every Area's requests; a supervisor sees the requests
    // of their own Areas (another Area's id in the URL is ignored, never trusted).
    public class RequestsModel : PageModel
    {
        private readonly StoreRepository _repo;
        private readonly AreaRepository _areaRepo;
        private readonly StoreAuthorization _auth;

        public RequestsModel(StoreRepository repo, AreaRepository areaRepo, StoreAuthorization auth)
        {
            _repo = repo;
            _areaRepo = areaRepo;
            _auth = auth;
        }

        [BindProperty(SupportsGet = true)] public string? Status { get; set; }
        [BindProperty(SupportsGet = true)] public int? AreaId { get; set; }
        [BindProperty(SupportsGet = true)] public bool Mine { get; set; }

        public List<Area> AreaOptions { get; set; } = new();
        public List<StoreRequest> Requests { get; set; } = new();

        public async Task OnGetAsync()
        {
            if (!StoreRules.Statuses.Contains(Status)) Status = null;
            AreaOptions = (await _areaRepo.GetAllAreas()).Where(a => _auth.CanSeeArea(User, a.Id)).OrderBy(a => a.Name).ToList();
            if (AreaId.HasValue && !AreaOptions.Any(a => a.Id == AreaId)) AreaId = null;
            Requests = await _repo.SearchRequestsAsync(_auth.AreaScope(User), Status, AreaId, Mine ? User.GetUserId() : null);
        }
    }
}
