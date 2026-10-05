using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PlantStockManager.Authorization;
using PlantStockManager.Data;
using PlantStockManager.Models;
using PlantStockManager.Services;
using System.Security.Claims;

namespace PlantStockManager.Pages.Bookings
{
    [Authorize]
    public class BookingRecordsModel : PageModel
    {
        private readonly BookingRepository _bookingRepository;
        private readonly PlantTypeRepository _plantTypeRepository;
        private readonly PlantSpeciesRepository _plantSpeciesRepository;
        private readonly EmployeeRepository _employeeRepo;
        private readonly SeedlingFulfilmentRepository _fulfilmentRepo;
        private readonly BookingOwnershipService _ownership;

        // "Own bookings only" users (BookingOwnershipService): the list shows only their own bookings.
        public bool ShowingOwnBookingsOnly { get; set; }

        public List<Booking> Bookings { get; set; } = new();
        public List<PlantType> PlantTypes { get; set; } = new();
        public List<PlantSpecies> PlantSpecies { get; set; } = new();
        public List<Employee> Employees { get; set; } = new();
        public List<State> States { get; set; } = new();

        [BindProperty(SupportsGet = true)] public int? SelectedPlantType { get; set; }
        [BindProperty(SupportsGet = true)] public int? SelectedSpecies { get; set; }
        [BindProperty(SupportsGet = true)] public int SelectedMonth { get; set; } = DateTime.Now.Month;
        [BindProperty(SupportsGet = true)] public int SelectedYear { get; set; } = DateTime.Now.Year;
        [BindProperty(SupportsGet = true)] public string SelectedStatus { get; set; } = "Pending";

        // Correction #6: "Booking By" (same value format and query as Seedling Booking Records): "" = all,
        // "U:<id>" a person, "O:<name>" a free-text name, "X:none" not recorded.
        [BindProperty(SupportsGet = true)] public string? SelectedBookedBy { get; set; }
        public List<BookedByFilter.Option> BookedByOptions { get; set; } = new();
        public string? Notice { get; set; }
        public bool BookedByActive { get; set; }

        [BindProperty] public Booking Booking { get; set; }

        // Phase C: kept in dbo.BookingRevisions with the previous values.
        [BindProperty] public string? RevisionReason { get; set; }

        public BookingRecordsModel(
            BookingRepository bookingRepository,
            PlantTypeRepository plantTypeRepository,
            PlantSpeciesRepository plantSpeciesRepository,
            EmployeeRepository employeeRepo,
            SeedlingFulfilmentRepository fulfilmentRepo,
            BookingOwnershipService ownership)
        {
            _fulfilmentRepo = fulfilmentRepo;
            _ownership = ownership;
            _bookingRepository = bookingRepository;
            _plantTypeRepository = plantTypeRepository;
            _plantSpeciesRepository = plantSpeciesRepository;
            _employeeRepo = employeeRepo;
        }

        public async Task<JsonResult> OnGetSpeciesByPlantType(int plantTypeId)
        {
            var species = await _plantSpeciesRepository.GetSpeciesByPlantType(plantTypeId);
            return new JsonResult(species);
        }

        public async Task<JsonResult> OnGetDistrictsByState(int stateId)
        {
            var districts = await _bookingRepository.GetByStateAsync(stateId);
            return new JsonResult(districts.Select(d => new { districtId = d.DistrictId, districtName = d.DistrictName }));
        }

        public async Task OnGetAsync()
        {
            if (SelectedMonth == 0)
            {
                SelectedMonth = DateTime.Now.Month;
            }
            // extract UserId claim
            var userIdClaim = User.FindFirst("UserId")?.Value;
            int? userId = null;
            if (!string.IsNullOrEmpty(userIdClaim) && int.TryParse(userIdClaim, out var parsedUserId))
            {
                userId = parsedUserId;
            }

            PlantTypes = await _plantTypeRepository.GetAllPlantTypes();
            States = await _bookingRepository.GetAllStatesAsync();
            Employees = await _employeeRepo.GetAllEmployeesBooking(); // used for display and edit dropdown

            if (SelectedPlantType.HasValue)
                PlantSpecies = await _plantSpeciesRepository.GetSpeciesByPlantType(SelectedPlantType.Value);

            var (bookedBy, notice) = BookedByFilter.Parse(SelectedBookedBy);
            SelectedBookedBy = bookedBy.Value;
            BookedByActive = bookedBy.IsActive;
            Notice = notice;
            var owner = _ownership.OwnerFilter(User);
            ShowingOwnBookingsOnly = owner.HasValue;
            if (owner.HasValue)
                Employees = Employees.Where(e => e.EmployeeID == owner.Value).ToList();   // Booked By = themselves only
            BookedByOptions = await _bookingRepository.GetBookedByOptionsAsync(owner);

            Bookings = await _bookingRepository.GetBookingRecords(SelectedPlantType, SelectedSpecies, SelectedMonth, SelectedYear, SelectedStatus, userId, bookedBy, owner);

            var uid = User.FindFirstValue(ClaimTypes.NameIdentifier);
            ViewData["CurrentUserId"] = uid ?? "0";
        }

        public async Task<IActionResult> OnPostEditBookingAsync()
        {
            // Remove filter validation noise
            ModelState.Remove("SelectedStatus");
            ModelState.Remove("SelectedBookedBy");
            ModelState.Remove("SelectedPlantType");
            ModelState.Remove("SelectedSpecies");
            ModelState.Remove("SelectedMonth");
            ModelState.Remove("SelectedYear");

            // "Own bookings only" users: the booking must be theirs (owner read from the
            // database, not the form), and it stays booked by them.
            if (!await _ownership.CanAccessBookingIdAsync(User, Booking.Id, _fulfilmentRepo))
                return new JsonResult(new { success = false, message = BookingOwnershipService.NotFoundMessage });
            if (_ownership.OwnerFilter(User) is int ownerId)
            {
                Booking.BookedById = ownerId;
                ModelState.Remove("Booking.BookedById");
            }

            // Set updater
            Booking.AddedBy = User.Identity?.Name ?? "System";
            ModelState.Remove("Booking.AddedBy");
            ModelState.Remove("Booking.BookingType");


            // Ensure required fields
            if (!(Booking.StateId.HasValue && Booking.StateId.Value > 0))
                return new JsonResult(new { success = false, message = "State is required." });
            if (!(Booking.DistrictId.HasValue && Booking.DistrictId.Value > 0))
                return new JsonResult(new { success = false, message = "District is required." });
            if (!(Booking.BookedById.HasValue && Booking.BookedById.Value > 0))
                return new JsonResult(new { success = false, message = "Booked By is required." });

            // Clear "other"
            Booking.BookedByOther = null;

            if (!ModelState.IsValid)
                return new JsonResult(new { success = false, message = "Invalid input data." });

            var (ok, message) = await _bookingRepository.UpdateBooking(Booking, RevisionReason, User.GetUserId());
            return new JsonResult(new { success = ok, message });
        }

        // Phase C: "Delete" no longer removes the booking. It CANCELS it
        // (history kept, reservations released, reason recorded).
        public async Task<IActionResult> OnPostDeleteBookingAsync(int id, string? reason)
        {
            if (!await _ownership.CanAccessBookingIdAsync(User, id, _fulfilmentRepo))
                return new JsonResult(new { success = false, message = BookingOwnershipService.NotFoundMessage });
            var (ok, message) = await _fulfilmentRepo.CancelAsync(id, reason,
                new SeedlingFulfilmentRepository.Actor(User.Identity?.Name, User.GetUserId()));
            return new JsonResult(new { success = ok, message });
        }
    }
}
