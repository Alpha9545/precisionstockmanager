using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PlantStockManager.Authorization;
using PlantStockManager.Data;
using PlantStockManager.Models;

namespace PlantStockManager.Pages.Bookings
{
    // Phase C: Dispatch Executive workspace for one seedling booking.
    //   * view the booking's reservations (batch allocations);
    //   * allocate a specific batch of the booked variety, or substitute a
    //     batch of ANOTHER variety of the same species (reason required,
    //     recorded as a substitution -- the booking itself is not changed);
    //   * release an allocation line (re-allocation);
    //   * dispatch -- partial or full -- only from allocated quantities.
    //   Read : Dispatch.View     Write: Dispatch.Enter
    // Area scope: a user can only allocate / release / dispatch batches in
    // Areas they may access (AreaAccessService), re-checked in the
    // repository inside the transaction.
    public class SeedlingDispatchModel : PageModel
    {
        private readonly SeedlingFulfilmentRepository _repo;
        private readonly EmployeeRepository _employeeRepo;
        private readonly AreaAccessService _areaAccess;

        public SeedlingDispatchModel(SeedlingFulfilmentRepository repo, EmployeeRepository employeeRepo, AreaAccessService areaAccess)
        {
            _repo = repo;
            _employeeRepo = employeeRepo;
            _areaAccess = areaAccess;
        }

        [BindProperty(SupportsGet = true)]
        public int Id { get; set; }

        public SeedlingBookingSummary? Booking { get; set; }
        public List<BookingBatchAllocation> Allocations { get; set; } = new();
        public List<ReadyBatchOption> BatchOptions { get; set; } = new();
        public List<Employee> Staff { get; set; } = new();

        // Dispatch
        [BindProperty] public List<DispatchLineInput> Lines { get; set; } = new();
        [BindProperty] public DateTime DispatchDate { get; set; } = DateTime.Today;
        [BindProperty] public int? ResponsiblePersonId { get; set; }
        [BindProperty] public string? Remarks { get; set; }

        // Allocate / substitute
        [BindProperty] public int AllocateReadyStockId { get; set; }
        [BindProperty] public int AllocateQuantity { get; set; }
        [BindProperty] public string? SubstitutionReason { get; set; }

        // Release
        [BindProperty] public int ReleaseAllocationId { get; set; }
        [BindProperty] public int ReleaseQuantity { get; set; }

        public class DispatchLineInput
        {
            public int AllocationId { get; set; }
            public int Quantity { get; set; }
        }

        private SeedlingFulfilmentRepository.Actor Actor => new(User.Identity?.Name, User.GetUserId());
        private bool CanAccessArea(int areaId) => _areaAccess.CanAccessArea(User, areaId);
        public bool CanUseArea(int? areaId) => _areaAccess.CanAccessArea(User, areaId);

        public async Task<IActionResult> OnGetAsync()
        {
            if (!await LoadAsync())
            {
                TempData["Error"] = "Booking not found.";
                return RedirectToPage("/Bookings/Fulfilment");
            }
            return Page();
        }

        public async Task<IActionResult> OnPostDispatchAsync()
        {
            var lines = Lines.Where(l => l.Quantity != 0).Select(l => (l.AllocationId, (decimal)l.Quantity)).ToList();
            var (ok, message) = await _repo.DispatchAsync(Id, lines, DispatchDate, ResponsiblePersonId, Remarks, Actor, CanAccessArea);
            TempData[ok ? "Success" : "Error"] = message;
            return RedirectToPage(new { id = Id });
        }

        public async Task<IActionResult> OnPostAllocateAsync()
        {
            var (ok, message) = await _repo.AllocateBatchAsync(Id, AllocateReadyStockId, AllocateQuantity, SubstitutionReason, Actor, CanAccessArea);
            TempData[ok ? "Success" : "Error"] = message;
            return RedirectToPage(new { id = Id });
        }

        public async Task<IActionResult> OnPostReleaseAsync()
        {
            var (ok, message) = await _repo.ReleaseAllocationAsync(Id, ReleaseAllocationId, ReleaseQuantity, Actor, CanAccessArea);
            TempData[ok ? "Success" : "Error"] = message;
            return RedirectToPage(new { id = Id });
        }

        private async Task<bool> LoadAsync()
        {
            Booking = await _repo.GetBookingAsync(Id);
            if (Booking == null) return false;
            Allocations = await _repo.GetAllocationsAsync(Id);
            BatchOptions = (await _repo.GetBatchOptionsAsync(Booking.PlantId))
                .Where(o => _areaAccess.CanAccessArea(User, o.AreaId))
                .ToList();
            Staff = await _employeeRepo.GetAllActiveUsers();
            Lines = Allocations.Where(a => a.Status == "Active" && a.OpenQuantity > 0)
                .Select(a => new DispatchLineInput { AllocationId = a.Id, Quantity = 0 }).ToList();
            return true;
        }
    }
}
