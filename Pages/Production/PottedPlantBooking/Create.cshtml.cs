using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PlantStockManager.Authorization;
using PlantStockManager.Data;
using PlantStockManager.Models;
using PottedPlantBookingModel = PlantStockManager.Models.PottedPlantBooking;

namespace PlantStockManager.Pages.Production.PottedPlantBooking
{
    // Phase 21/Phase G: Outlet A must not be able to book from Outlet B's
    // (or any other Area's) stock. Enforced two ways, per spec section 5
    // ("never trust a posted StockId or AreaId"): (1) the dropdown itself
    // only ever offers pools the user can access (LoadDropdownsAsync);
    // (2) OnPostAsync independently re-fetches the ACTUAL PottedPlantStock
    // row for the posted PottedPlantStockId and checks CanAccessArea
    // against its real AreaId before calling InsertAsync -- closing the
    // tampering vector where a stray/hand-crafted PottedPlantStockId
    // belonging to another Area is posted regardless of what the dropdown
    // showed. Mirrors the identical fix already applied in Phase D's
    // CreateFromCutting.cshtml.cs and Phase F's GrowingPartnerToOutlet/
    // Create.cshtml.cs for the same structural reason.
    public class CreateModel : PageModel
    {
        private readonly PottedPlantBookingRepository _bookingRepo;
        private readonly PottedPlantStockRepository _pottedPlantStockRepo;
        private readonly BookingRepository _legacyBookingRepo; // reused read-only for the shared States/Districts lookups
        private readonly EmployeeRepository _employeeRepo;
        private readonly AreaAccessService _areaAccessService;

        public CreateModel(
            PottedPlantBookingRepository bookingRepo,
            PottedPlantStockRepository pottedPlantStockRepo,
            BookingRepository legacyBookingRepo,
            EmployeeRepository employeeRepo,
            AreaAccessService areaAccessService)
        {
            _bookingRepo = bookingRepo;
            _pottedPlantStockRepo = pottedPlantStockRepo;
            _legacyBookingRepo = legacyBookingRepo;
            _employeeRepo = employeeRepo;
            _areaAccessService = areaAccessService;
        }

        [BindProperty]
        public PottedPlantBookingModel Booking { get; set; } = new();

        // Only pools with Available quantity > 0 are offered.
        public List<PlantStockManager.Models.PottedPlantStock> StockPools { get; set; } = new();
        public List<State> States { get; set; } = new();
        public List<Employee> BookedByOptions { get; set; } = new();

        public async Task OnGetAsync()
        {
            Booking.BookingDate = DateTime.Today;
            await LoadDropdownsAsync();
        }

        public async Task<JsonResult> OnGetDistrictsByStateAsync(int stateId)
        {
            var districts = await _legacyBookingRepo.GetByStateAsync(stateId);
            return new JsonResult(districts);
        }

        public async Task<IActionResult> OnPostAsync()
        {
            ModelState.Remove("Booking.BookingCode");
            ModelState.Remove("Booking.CreatedBy");
            ModelState.Remove("Booking.Status");
            ModelState.Remove("Booking.SpeciesId");   // server-derived from the selected pool
            ModelState.Remove("Booking.PotSize");     // server-derived from the selected pool
            ModelState.Remove("Booking.AreaId");      // server-derived from the selected pool

            if (Booking.PottedPlantStockId <= 0)
                ModelState.AddModelError("Booking.PottedPlantStockId", "Species / Pot Size / Area is required.");
            if (Booking.Quantity <= 0)
                ModelState.AddModelError("Booking.Quantity", "Quantity must be greater than zero.");
            if (string.IsNullOrWhiteSpace(Booking.CustomerName))
                ModelState.AddModelError("Booking.CustomerName", "Customer Name is required.");
            if (Booking.AdvanceTaken && (Booking.AdvanceTakenAmount == null || Booking.AdvanceTakenAmount <= 0))
                ModelState.AddModelError("Booking.AdvanceTakenAmount", "Advance Amount is required when Advance Taken is checked.");

            if (!ModelState.IsValid)
            {
                await LoadDropdownsAsync();
                return Page();
            }

            // Never trust the posted PottedPlantStockId's implied Area --
            // re-fetch the actual stock row and check its REAL AreaId.
            var targetStock = await _pottedPlantStockRepo.GetByIdAsync(Booking.PottedPlantStockId);
            if (targetStock == null)
            {
                ModelState.AddModelError("Booking.PottedPlantStockId", "Selected Species / Pot Size / Area pool no longer exists.");
                await LoadDropdownsAsync();
                return Page();
            }
            if (!_areaAccessService.CanAccessArea(User, targetStock.AreaId))
            {
                ModelState.AddModelError(string.Empty, "You are not authorized to book stock for the selected Area.");
                await LoadDropdownsAsync();
                return Page();
            }

            Booking.CreatedBy = User.Identity?.Name ?? "System";
            var userIdClaim = User.FindFirst("UserId")?.Value;
            int? userId = int.TryParse(userIdClaim, out var parsedUserId) ? parsedUserId : null;

            var (success, message, _) = await _bookingRepo.InsertAsync(Booking, userId);
            if (!success)
            {
                // Covers "not enough Available stock" and any DB-level
                // constraint failure -- surfaced as a friendly message.
                ModelState.AddModelError(string.Empty, message ?? "Failed to record Booking.");
                await LoadDropdownsAsync();
                return Page();
            }

            TempData["Success"] = $"Booking {Booking.BookingCode} recorded successfully. Stock reserved.";
            return RedirectToPage("/Production/PottedPlantBooking/Index");
        }

        private async Task LoadDropdownsAsync()
        {
            var allStock = await _pottedPlantStockRepo.GetAllAsync();
            var withAvailable = allStock.Where(s => s.AvailableQuantity > 0);
            StockPools = _areaAccessService.HasFullAreaAccess(User)
                ? withAvailable.ToList()
                : withAvailable.Where(s => _areaAccessService.CanAccessArea(User, s.AreaId)).ToList();
            States = await _legacyBookingRepo.GetAllStatesAsync();
            BookedByOptions = await _employeeRepo.GetAllActiveUsers();
        }
    }
}
