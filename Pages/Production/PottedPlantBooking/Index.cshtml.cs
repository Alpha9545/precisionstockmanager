using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PlantStockManager.Authorization;
using PlantStockManager.Data;
using PlantStockManager.Services;
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
    //
    // Correction #6: "Booking By" filter (existing BookedById / BookedByOther columns). The user's Areas are part of the
    // query itself (PottedPlantBookingRepository.SearchAsync) and of the dropdown options, so the filter can only narrow
    // what the user may already see; the previous in-memory Area check is kept as a second guard.
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

        // "" = all, "U:<id>" a person, "O:<name>" a free-text name, "X:none" not recorded
        [BindProperty(SupportsGet = true)] public string? SelectedBookedBy { get; set; }
        public List<BookedByFilter.Option> BookedByOptions { get; set; } = new();
        public string? Notice { get; set; }
        public bool BookedByActive { get; set; }

        public decimal TotalReserved => Bookings.Where(b => b.Status == "Pending" || b.Status == "PartiallyDispatched").Sum(b => b.RemainingQuantity);

        public async Task OnGetAsync()
        {
            var (bookedBy, notice) = BookedByFilter.Parse(SelectedBookedBy);
            SelectedBookedBy = bookedBy.Value;
            BookedByActive = bookedBy.IsActive;
            Notice = notice;

            IReadOnlyCollection<int>? allowedAreas = _areaAccessService.HasFullAreaAccess(User) ? null : _areaAccessService.GetAccessibleAreaIds(User);
            BookedByOptions = await _bookingRepo.GetBookedByOptionsAsync(allowedAreas);
            var all = await _bookingRepo.SearchAsync(bookedBy, allowedAreas);
            Bookings = _areaAccessService.HasFullAreaAccess(User)
                ? all
                : all.Where(b => _areaAccessService.CanAccessArea(User, b.AreaId)).ToList();
        }
    }
}
