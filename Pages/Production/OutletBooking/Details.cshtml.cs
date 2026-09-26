using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PlantStockManager.Authorization;
using PlantStockManager.Data;
using PlantStockManager.Models;
using PlantStockManager.Services;
using OutletBookingModel = PlantStockManager.Models.OutletBooking;

namespace PlantStockManager.Pages.Production.OutletBooking
{
    public class DetailsModel : PageModel
    {
        private readonly OutletBookingRepository _bookingRepo;
        private readonly AreaAccessService _areaAccess;

        public DetailsModel(OutletBookingRepository bookingRepo, AreaAccessService areaAccess)
        {
            _bookingRepo = bookingRepo;
            _areaAccess = areaAccess;
        }

        public OutletBookingModel Booking { get; set; } = new();
        public bool IsClosed => OutletBookingRules.IsClosed(Booking.Status);

        [BindProperty] public int CollectItemId { get; set; }
        [BindProperty] public decimal CollectQuantity { get; set; }
        [BindProperty] public string? CancelReason { get; set; }

        public async Task<IActionResult> OnGetAsync(int id) => await LoadAsync(id) ? Page() : Denied();

        public async Task<IActionResult> OnPostCollectAsync(int id)
        {
            if (!await LoadAsync(id))
                return Denied();
            var (ok, message) = await _bookingRepo.CollectItemAsync(CollectItemId, CollectQuantity, User.GetUserId(), User.Identity?.Name);
            TempData[ok ? "Success" : "Error"] = ok ? $"Collected {CollectQuantity:N0}." : message;
            return RedirectToPage(new { id });
        }

        public async Task<IActionResult> OnPostCancelAsync(int id)
        {
            if (!await LoadAsync(id))
                return Denied();
            var me = User.GetUserId();
            var (ok, message) = me.HasValue
                ? await _bookingRepo.CancelAsync(id, CancelReason ?? "", me.Value, User.Identity?.Name)
                : (false, "Your user could not be identified.");
            TempData[ok ? "Success" : "Error"] = ok ? $"Booking {Booking.BookingCode} cancelled; every open reservation was released." : message;
            return RedirectToPage(new { id });
        }

        private async Task<bool> LoadAsync(int id)
        {
            var booking = await _bookingRepo.GetByIdAsync(id);
            if (booking == null || !_areaAccess.CanAccessArea(User, booking.OutletAreaId))
                return false;
            Booking = booking;
            return true;
        }

        private IActionResult Denied()
        {
            TempData["Error"] = "Booking not found, or it belongs to an Outlet you cannot access.";
            return RedirectToPage("/Production/OutletBooking/Index");
        }
    }
}
