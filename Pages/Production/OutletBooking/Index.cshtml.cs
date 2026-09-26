using Microsoft.AspNetCore.Mvc.RazorPages;
using PlantStockManager.Authorization;
using PlantStockManager.Data;
using PlantStockManager.Models;
using OutletBookingModel = PlantStockManager.Models.OutletBooking;

namespace PlantStockManager.Pages.Production.OutletBooking
{
    // Outlet Booking History: every customer booking, Area-scoped, with an
    // open/closed filter (open = Pending or PartiallyCollected).
    public class IndexModel : PageModel
    {
        private readonly OutletBookingRepository _bookingRepo;
        private readonly AreaAccessService _areaAccess;

        public IndexModel(OutletBookingRepository bookingRepo, AreaAccessService areaAccess)
        {
            _bookingRepo = bookingRepo;
            _areaAccess = areaAccess;
        }

        public bool OpenOnly { get; set; } = true;
        public List<OutletBookingModel> Bookings { get; set; } = new();

        public async Task OnGetAsync(bool openOnly = true)
        {
            OpenOnly = openOnly;
            var all = await _bookingRepo.GetAllAsync();
            var scoped = _areaAccess.HasFullAreaAccess(User) ? all : all.Where(b => _areaAccess.CanAccessArea(User, b.OutletAreaId));
            Bookings = (openOnly ? scoped.Where(b => !PlantStockManager.Services.OutletBookingRules.IsClosed(b.Status)) : scoped)
                .OrderByDescending(b => b.BookingDate).ToList();
        }
    }
}
