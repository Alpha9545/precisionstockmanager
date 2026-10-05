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
    public class BookModel : PageModel
    {
        private readonly PlantTypeRepository _plantTypeRepo;
        private readonly PlantSpeciesRepository _plantSpeciesRepo;
        private readonly BookingRepository _bookingRepo;
        private readonly EmployeeRepository _employeeRepo;
        private readonly SeedlingFulfilmentRepository _fulfilmentRepo;
        private readonly BookingOwnershipService _ownership;

        public BookModel(
            PlantTypeRepository plantTypeRepo,
            PlantSpeciesRepository plantSpeciesRepo,
            BookingRepository bookingRepo,
            EmployeeRepository employeeRepo,
            SeedlingFulfilmentRepository fulfilmentRepo,
            BookingOwnershipService ownership)
        {
            _fulfilmentRepo = fulfilmentRepo;
            _ownership = ownership;
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

        // "Add Another Species": every variety of the customer order, posted together
        // (Lines[0].PlantId, Lines[0].SpeciesId, Lines[0].Quantity, Lines[0].DeliveryDate, ...).
        // Saved as ONE order in one transaction (BookingRepository.InsertOrderAsync).
        // Empty = a single-variety post using Booking.PlantId / SpeciesId / Quantity / DeliveryDate.
        [BindProperty]
        public List<OrderLineInput> Lines { get; set; } = new();

        public class OrderLineInput
        {
            public int PlantId { get; set; }
            public int SpeciesId { get; set; }
            public int Quantity { get; set; }
            public DateTime? DeliveryDate { get; set; }
        }

        // "Own bookings only" users (BookingOwnershipService): "Booked By" is always themselves (set on the server).
        public bool BookedBySelfOnly { get; set; }

        public async Task OnGet()
        {
            PlantTypes = await _plantTypeRepo.GetAllPlantTypes();
            await LoadEmployeesAsync();
            States = await _bookingRepo.GetAllStatesAsync();
        }

        private async Task LoadEmployeesAsync()
        {
            Employees = await _employeeRepo.GetAllEmployeesBooking();
            var owner = _ownership.OwnerFilter(User);
            BookedBySelfOnly = owner.HasValue;
            if (owner.HasValue)
                Employees = Employees.Where(e => e.EmployeeID == owner.Value).ToList();
        }

        public async Task<JsonResult> OnGetSpeciesByPlantType(int plantTypeId)
        {
            var species = await _plantSpeciesRepo.GetSpeciesByPlantType(plantTypeId);
            return new JsonResult(species);
        }

        // Phase C: Ready Stock availability for a variety (information only --
        // creating a booking never reserves or reduces stock; a booking larger
        // than what is available is a pre-booking and is reserved later).
        public async Task<JsonResult> OnGetAvailabilityAsync(int speciesId)
        {
            var a = await _fulfilmentRepo.GetAvailabilityAsync(speciesId);
            return new JsonResult(new { ready = a.PhysicalQuantity, reserved = a.ReservedQuantity, available = a.AvailableQuantity, batches = a.Batches });
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

            // "Own bookings only" users always book as themselves -- whatever the form sent.
            if (_ownership.OwnerFilter(User) is int ownerId)
            {
                Booking.BookedById = ownerId;
                ModelState.Remove("Booking.BookedById");
            }

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

            // The variety lines are validated together (one bad line rejects the order).
            var lines = Lines.Count > 0
                ? Lines.Select(l => new BookingOrders.NewLine(l.PlantId, l.SpeciesId, l.Quantity, l.DeliveryDate)).ToList()
                : new List<BookingOrders.NewLine> { new(Booking.PlantId, Booking.SpeciesId, Booking.Quantity, Booking.DeliveryDate == default ? null : Booking.DeliveryDate) };
            foreach (var key in ModelState.Keys.Where(k => k.StartsWith("Lines[", StringComparison.Ordinal)
                                                        || k is "Booking.PlantId" or "Booking.SpeciesId" or "Booking.Quantity" or "Booking.DeliveryDate").ToList())
                ModelState.Remove(key);
            var (linesOk, linesError) = BookingOrders.ValidateNewOrder(lines);
            if (!linesOk)
                ModelState.AddModelError(string.Empty, linesError!);

            if (!ModelState.IsValid)
            {
                // The Book page posts with jQuery and expects JSON: answer with the first
                // error instead of an HTML page (which the script would mistake for success).
                if (Request.Headers.XRequestedWith == "XMLHttpRequest")
                    return new JsonResult(new
                    {
                        success = false,
                        message = ModelState.Values.SelectMany(v => v.Errors).Select(e => e.ErrorMessage).FirstOrDefault(m => !string.IsNullOrWhiteSpace(m))
                                  ?? "Please check the booking details. Nothing was saved."
                    });

                // reload lists for redisplay
                PlantTypes = await _plantTypeRepo.GetAllPlantTypes();
                await LoadEmployeesAsync();
                States = await _bookingRepo.GetAllStatesAsync();
                return Page();
            }

            var (ok, message, orderId, lineIds) = await _bookingRepo.InsertOrderAsync(Booking, lines);
            if (ok)
                return new JsonResult(new { success = true, message, orderId, varieties = lineIds.Count });

            return new JsonResult(new { success = false, message = message ?? "Failed to save the booking. Nothing was saved." });
        }
    }
}
