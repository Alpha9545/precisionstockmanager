using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PlantStockManager.Authorization;
using PlantStockManager.Data;
using PottedPlantBookingModel = PlantStockManager.Models.PottedPlantBooking;

namespace PlantStockManager.Pages.Production.PottedPlantBooking
{
    public class DetailsModel : PageModel
    {
        private readonly PottedPlantBookingRepository _bookingRepo;
        private readonly AreaAccessService _areaAccessService;

        public DetailsModel(PottedPlantBookingRepository bookingRepo, AreaAccessService areaAccessService)
        {
            _bookingRepo = bookingRepo;
            _areaAccessService = areaAccessService;
        }

        public PottedPlantBookingModel? Booking { get; set; }

        public async Task<IActionResult> OnGetAsync(int id)
        {
            Booking = await _bookingRepo.GetByIdAsync(id);
            if (Booking == null)
                return RedirectToPage("/Production/PottedPlantBooking/Index");

            if (!_areaAccessService.CanAccessArea(User, Booking.AreaId))
            {
                TempData["Error"] = "You are not authorized to view this Booking.";
                return RedirectToPage("/Production/PottedPlantBooking/Index");
            }

            return Page();
        }
    }
}
