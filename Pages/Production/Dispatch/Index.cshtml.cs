using Microsoft.AspNetCore.Mvc.RazorPages;
using PlantStockManager.Authorization;
using PlantStockManager.Data;
using DispatchModel = PlantStockManager.Models.Dispatch;

namespace PlantStockManager.Pages.Production.Dispatch
{
    // Phase 21/Phase G: closes the same pre-existing "no Area check at
    // all" gap Booking/Index had -- an Outlet Supervisor must only see
    // Dispatches against their own Outlet Area's stock.
    public class IndexModel : PageModel
    {
        private readonly DispatchRepository _dispatchRepo;
        private readonly AreaAccessService _areaAccessService;

        public IndexModel(DispatchRepository dispatchRepo, AreaAccessService areaAccessService)
        {
            _dispatchRepo = dispatchRepo;
            _areaAccessService = areaAccessService;
        }

        public List<DispatchModel> Dispatches { get; set; } = new();

        public decimal TotalDispatched => Dispatches.Where(d => d.Status == "Completed").Sum(d => d.Quantity);

        public async Task OnGetAsync()
        {
            var all = await _dispatchRepo.GetAllAsync();
            Dispatches = _areaAccessService.HasFullAreaAccess(User)
                ? all
                : all.Where(d => _areaAccessService.CanAccessArea(User, d.AreaId)).ToList();
        }
    }
}
