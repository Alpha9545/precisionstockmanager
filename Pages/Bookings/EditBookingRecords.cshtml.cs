using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PlantStockManager.Data;
using PlantStockManager.Models;
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

        public List<Booking> Bookings { get; set; } = new();
        public List<PlantType> PlantTypes { get; set; } = new();
        public List<PlantSpecies> PlantSpecies { get; set; } = new();
        public List<Employee> Employees { get; set; } = new();
        public List<State> States { get; set; } = new();

        [BindProperty(SupportsGet = true)] public int? SelectedPlantType { get; set; }
        [BindProperty(SupportsGet = true)] public int? SelectedSpecies { get; set; }
        [BindProperty(SupportsGet = true)] public int SelectedMonth { get; set; } = 0;
        [BindProperty(SupportsGet = true)] public int SelectedYear { get; set; } = DateTime.Now.Year;
        [BindProperty(SupportsGet = true)] public string SelectedStatus { get; set; } = "Pending";

        [BindProperty] public Booking Booking { get; set; }

        public BookingRecordsModel(
            BookingRepository bookingRepository,
            PlantTypeRepository plantTypeRepository,
            PlantSpeciesRepository plantSpeciesRepository,
            EmployeeRepository employeeRepo)
        {
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
            PlantTypes = await _plantTypeRepository.GetAllPlantTypes();
            States = await _bookingRepository.GetAllStatesAsync();
            Employees = await _employeeRepo.GetAllEmployees(); // used for display and edit dropdown

            if (SelectedPlantType.HasValue)
                PlantSpecies = await _plantSpeciesRepository.GetSpeciesByPlantType(SelectedPlantType.Value);

            Bookings = await _bookingRepository.GetBookingRecords(SelectedPlantType, SelectedSpecies, SelectedMonth, SelectedYear, SelectedStatus);

            var uid = User.FindFirstValue(ClaimTypes.NameIdentifier);
            ViewData["CurrentUserId"] = uid ?? "0";
        }

        public async Task<IActionResult> OnPostEditBookingAsync()
        {
            // Remove filter validation noise
            ModelState.Remove("SelectedStatus");
            ModelState.Remove("SelectedPlantType");
            ModelState.Remove("SelectedSpecies");
            ModelState.Remove("SelectedMonth");
            ModelState.Remove("SelectedYear");

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

            var ok = await _bookingRepository.UpdateBooking(Booking);
            if (ok) return new JsonResult(new { success = true, message = "Booking updated successfully!" });
            return new JsonResult(new { success = false, message = "Failed to update Booking." });
        }

        public async Task<IActionResult> OnPostDeleteBookingAsync(int id)
        {
            var result = await _bookingRepository.DeleteBookingWithTransactions(id);
            if (result.Success) return new JsonResult(new { success = true });
            return new JsonResult(new { success = false, message = result.Message });
        }
    }
}
