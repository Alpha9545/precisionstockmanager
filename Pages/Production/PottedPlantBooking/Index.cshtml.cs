using Microsoft.AspNetCore.Mvc.RazorPages;
using PlantStockManager.Authorization;
using PlantStockManager.Data;
using PottedPlantBookingModel = PlantStockManager.Models.PottedPlantBooking;

namespace PlantStockManager.Pages.Production.PottedPlantBooking
{
    // Phase 21/Phase G: this list previously showed every Booking
    // globally, for any Area -- a pre-existing gap dating back to Phase 9
    // (built before Growing Partner/Outlet Areas existed). Now filtered
    // exactly like every other Index page's AreaAccessService rollout
    // since Phase E/F: a full-access user (Admin/Management/
    // MainOfficeOfficer) still sees everything, an Outlet Supervisor sees
    // only Bookings against their own Outlet Area's stock.
    public class IndexModel : PageModel
    {
        private readonly PottedPlantBookingRepository _bookingRepo;
        private readonly AreaAccessService _areaAccessService;

        public IndexModel(PottedPlantBookingRepository bookingRepo, AreaAccessService areaAccessService)
        {
            _bookingRepo = bookingRepo;
            _areaAccessService = areaAccessService;
        }

        public List<PottedPlantBookingModel> Bookings { get; set; } = new();

        public decimal TotalReserved => Bookings.Where(b => b.Status == "Pending" || b.Status == "PartiallyDispatched").Sum(b => b.RemainingQuantity);

        public async Task OnGetAsync()
        {
            var all = await _bookingRepo.GetAllAsync();
            Bookings = _areaAccessService.HasFullAreaAccess(User)
                ? all
                : all.Where(b => _areaAccessService.CanAccessArea(User, b.AreaId)).ToList();
        }
    }
}
