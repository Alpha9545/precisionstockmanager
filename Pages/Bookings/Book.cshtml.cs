using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PlantStockManager.Data;
using PlantStockManager.Models;
using System.Security.Claims;

namespace PlantStockManager.Pages.Bookings
{
    [Authorize]
    public class BookModel : PageModel
    {
        private readonly PlantTypeRepository _plantTypeRepo;
        private readonly PlantSpeciesRepository _plantSpeciesRepo;
        private readonly BookingRepository _bookingRepo;
        private readonly EmployeeRepository _employeeRepo;

        public BookModel(
            PlantTypeRepository plantTypeRepo,
            PlantSpeciesRepository plantSpeciesRepo,
            BookingRepository bookingRepo,
            EmployeeRepository employeeRepo)
        {
            _plantTypeRepo = plantTypeRepo;
            _plantSpeciesRepo = plantSpeciesRepo;
            _bookingRepo = bookingRepo;
            _employeeRepo = employeeRepo;
        }

        public List<PlantType> PlantTypes { get; set; }
        public List<PlantSpecies> Species { get; set; } = new();
        public List<Employee> Employees { get; set; }
        public List<State> States { get; set; } = new();

        [BindProperty]
        public Booking Booking { get; set; }

        public async Task OnGet()
        {
            PlantTypes = await _plantTypeRepo.GetAllPlantTypes();
            Employees = await _employeeRepo.GetAllEmployeesBooking();
            States = await _bookingRepo.GetAllStatesAsync();
        }

        public async Task<JsonResult> OnGetSpeciesByPlantType(int plantTypeId)
        {
            var species = await _plantSpeciesRepo.GetSpeciesByPlantType(plantTypeId);
            return new JsonResult(species);
        }

        public async Task<JsonResult> OnGetDistrictsByState(int stateId)
        {
            var districts = await _bookingRepo.GetByStateAsync(stateId);
            return new JsonResult(districts);
        }

        public async Task<IActionResult> OnPostAsync()
        {
            Booking.AddedBy = User.Identity?.Name ?? "System";
            Booking.BookingType = "Booking";

            // Ignore fields we set here
            ModelState.Remove("Booking.BookingType");
            ModelState.Remove("Booking.AddedBy");

            // ✅ Require State/District and BookedById
            if (Booking.StateId <= 0)
                ModelState.AddModelError("Booking.StateId", "State is required.");
            if (Booking.DistrictId <= 0)
                ModelState.AddModelError("Booking.DistrictId", "District is required.");
            if (Booking.BookedById == null || Booking.BookedById <= 0)
                ModelState.AddModelError("Booking.BookedById", "Booked By is required.");

            if (!ModelState.IsValid)
            {
                // reload lists for redisplay
                PlantTypes = await _plantTypeRepo.GetAllPlantTypes();
                Employees = await _employeeRepo.GetAllEmployeesBooking();
                States = await _bookingRepo.GetAllStatesAsync();
                return Page();
            }

            int success = await _bookingRepo.InsertBookingAsync(Booking);

            if (success > 0)
                return new JsonResult(new { success = true, message = "Booking added successfully!" });

            return new JsonResult(new { success = false, message = "Failed to insert Booking." });
        }
    }
}
