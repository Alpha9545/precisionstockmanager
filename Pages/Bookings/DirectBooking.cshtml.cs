using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PlantStockManager.Data;
using PlantStockManager.Models;
using System.Security.Claims;

namespace PlantStockManager.Pages.Bookings
{
    [Authorize]
    public class DirectBookingModel : PageModel
    {
        private readonly PlantTypeRepository _plantTypeRepo;
        private readonly PlantSpeciesRepository _plantSpeciesRepo;
        private readonly BookingRepository _bookingRepo;
        private readonly EmployeeRepository _employeeRepo;
        //private readonly StateRepository _stateRepo;            // ✅ NEW
        //private readonly DistrictRepository _districtRepo;      // ✅ NEW

        public DirectBookingModel(
            PlantTypeRepository plantTypeRepo,
            PlantSpeciesRepository plantSpeciesRepo,
            BookingRepository bookingRepo,
            EmployeeRepository employeeRepo
            //StateRepository stateRepo,            // ✅ NEW
            //DistrictRepository districtRepo
            )      // ✅ NEW
        {
            _plantTypeRepo = plantTypeRepo;
            _plantSpeciesRepo = plantSpeciesRepo;
            _bookingRepo = bookingRepo;
            _employeeRepo = employeeRepo;
            //_stateRepo = stateRepo;               // ✅ NEW
            //_districtRepo = districtRepo;         // ✅ NEW
        }

    

        public List<PlantType> PlantTypes { get; set; }
        public List<PlantSpecies> Species { get; set; } = new();
        public List<Employee> Employees { get; set; }
        public List<State> States { get; set; } = new();    // ✅ NEW

        [BindProperty]
        public Booking Booking { get; set; }

        public async Task OnGet()
        {
            PlantTypes = await _plantTypeRepo.GetAllPlantTypes();
            Employees = await _employeeRepo.GetAllEmployeesBooking();
            States = await _bookingRepo.GetAllStatesAsync();  // ✅ Load states
        }

        public async Task<JsonResult> OnGetSpeciesByPlantType(int plantTypeId)
        {
            var species = await _plantSpeciesRepo.GetSpeciesByPlantType(plantTypeId);
            return new JsonResult(species);
        }

        // ✅ New: Districts by State
        public async Task<JsonResult> OnGetDistrictsByState(int stateId)
        {
            var districts = await _bookingRepo.GetByStateAsync(stateId);
            return new JsonResult(districts);
        }

        public async Task<IActionResult> OnPostAsync()
        {
            Booking.AddedBy = User.Identity?.Name ?? "System";
            var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
            Booking.BookedById = string.IsNullOrEmpty(userId) ? (int?)null : int.Parse(userId);
            Booking.BookingType = "Direct Booking";
            ModelState.Remove("Booking.BookingType");
            ModelState.Remove("Booking.AddedBy");

            // Ensure State/District required
            if (Booking.StateId <= 0)
                ModelState.AddModelError("Booking.StateId", "State is required.");
            if (Booking.DistrictId <= 0)
                ModelState.AddModelError("Booking.DistrictId", "District is required.");

            if (!ModelState.IsValid)
            {
                // For redisplay on error
                PlantTypes = await _plantTypeRepo.GetAllPlantTypes();
                Employees = await _employeeRepo.GetAllEmployeesBooking();
                States = await _bookingRepo.GetAllStatesAsync();
                
                return Page();
            }

            int bookingId = await _bookingRepo.InsertBookingAsync(Booking);

            if (bookingId > 0)
            {
                var url = Url.Page("/Bookings/FulfillBooking", new { BookingId = bookingId });
                return new JsonResult(new
                {
                    success = true,
                    message = "Booking added successfully!",
                    bookingId,
                    redirectUrl = url
                });
            }

            return new JsonResult(new { success = false, message = "Failed to insert Booking." });
        }
    }
}
