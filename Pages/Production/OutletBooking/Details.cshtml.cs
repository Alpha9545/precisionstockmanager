using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PlantStockManager.Authorization;
using PlantStockManager.Data;
using PlantStockManager.Models;
using PlantStockManager.Services;
using OutletBookingModel = PlantStockManager.Models.OutletBooking;

namespace PlantStockManager.Pages.Production.OutletBooking
{
    // 2026-10-21: a Collect form sent twice (double click / browser retry / replay) is stopped by
    // SubmissionTokenGuard -- it used to record the same hand-over again (up to the booked quantity).
    public class DetailsModel : PageModel
    {
        public const string CollectTokenScope = "OutletBookingCollect";

        private readonly OutletBookingRepository _bookingRepo;
        private readonly AreaAccessService _areaAccess;
        private readonly SubmissionTokenGuard _tokens;

        public DetailsModel(OutletBookingRepository bookingRepo, AreaAccessService areaAccess, SubmissionTokenGuard tokens)
        {
            _bookingRepo = bookingRepo;
            _areaAccess = areaAccess;
            _tokens = tokens;
        }

        public OutletBookingModel Booking { get; set; } = new();
        public bool IsClosed => OutletBookingRules.IsClosed(Booking.Status);

        [BindProperty] public int CollectItemId { get; set; }
        [BindProperty] public decimal CollectQuantity { get; set; }
        [BindProperty] public string? CancelReason { get; set; }
        [BindProperty] public string? SubmitToken { get; set; }

        public async Task<IActionResult> OnGetAsync(int id)
        {
            if (!await LoadAsync(id))
                return Denied();
            SubmitToken = SubmissionTokenGuard.NewToken();
            return Page();
        }

        public async Task<IActionResult> OnPostCollectAsync(int id)
        {
            if (!await LoadAsync(id))
                return Denied();
            if (!_tokens.TryConsume(CollectTokenScope, SubmitToken))
            {
                TempData["Error"] = "This collection was already submitted. Check the collected quantities below before collecting again.";
                return RedirectToPage(new { id });
            }
            var (ok, message) = await _bookingRepo.CollectItemAsync(CollectItemId, CollectQuantity, User.GetUserId(), User.Identity?.Name);
            TempData[ok ? "Success" : "Error"] = ok ? $"Collected {QuantityFormat.Qty(CollectQuantity)}." : message;
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
