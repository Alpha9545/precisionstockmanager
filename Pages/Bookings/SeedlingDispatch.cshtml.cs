using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PlantStockManager.Authorization;
using PlantStockManager.Data;
using PlantStockManager.Services;
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
    // Allocate Batch: a searchable, filtered, paged table (server-side) instead
    // of one large dropdown. Each row posts its own ReadyStock.Id -- the short
    // batch number (e.g. J-10) is only a label several batches may share. A
    // one-time token per page view stops a double-submitted allocation.
    public class SeedlingDispatchModel : PageModel
    {
        public const string AllocateTokenScope = "SeedlingDispatch.Allocate";

        private readonly SeedlingFulfilmentRepository _repo;
        private readonly UserRoleRepository _userRoleRepo;
        private readonly AreaAccessService _areaAccess;
        private readonly SubmissionTokenGuard _tokens;

        public SeedlingDispatchModel(SeedlingFulfilmentRepository repo, UserRoleRepository userRoleRepo, AreaAccessService areaAccess, SubmissionTokenGuard tokens)
        {
            _repo = repo;
            _userRoleRepo = userRoleRepo;
            _areaAccess = areaAccess;
            _tokens = tokens;
        }

        [BindProperty(SupportsGet = true)]
        public int Id { get; set; }

        public SeedlingBookingSummary? Booking { get; set; }
        public List<BookingBatchAllocation> Allocations { get; set; } = new();
        public BatchSearchResult Batches { get; set; } = new();
        public List<BatchFilterOption> AreaOptions { get; set; } = new();
        public List<BatchFilterOption> PolyhouseOptions { get; set; } = new();
        public List<Employee> Staff { get; set; } = new();

        // Allocate Batch table filters (GET; kept on the URL so paging/back work).
        [BindProperty(SupportsGet = true)] public string? Q { get; set; }
        [BindProperty(SupportsGet = true)] public DateTime? SowingDate { get; set; }
        [BindProperty(SupportsGet = true)] public int? AreaId { get; set; }
        [BindProperty(SupportsGet = true)] public int? PolyhouseId { get; set; }
        [BindProperty(SupportsGet = true)] public string? AllocationType { get; set; }
        [BindProperty(SupportsGet = true)] public int PageNo { get; set; } = 1;
        public bool HasBatchFilter => !string.IsNullOrWhiteSpace(Q) || SowingDate.HasValue || AreaId.HasValue || PolyhouseId.HasValue || !string.IsNullOrEmpty(AllocationType);

        // Dispatch
        [BindProperty] public List<DispatchLineInput> Lines { get; set; } = new();
        [BindProperty] public DateTime DispatchDate { get; set; } = DateTime.Today;
        [BindProperty] public int? ResponsiblePersonId { get; set; }
        [BindProperty] public string? Remarks { get; set; }

        // Allocate / substitute
        [BindProperty] public int AllocateReadyStockId { get; set; }
        [BindProperty] public int AllocateQuantity { get; set; }
        [BindProperty] public string? SubstitutionReason { get; set; }
        [BindProperty] public string? AllocateToken { get; set; }

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
            if (ResponsiblePersonId.HasValue
                && (await _userRoleRepo.GetUsersInAnyRoleAsync(SupervisorRules.DispatchExecutive)).All(u => u.EmployeeID != ResponsiblePersonId.Value))
            {
                TempData["Error"] = "The dispatch person must be a Dispatch Executive.";
                return RedirectToPage(new { id = Id });
            }
            var (ok, message) = await _repo.DispatchAsync(Id, lines, DispatchDate, ResponsiblePersonId, Remarks, Actor, CanAccessArea);
            TempData[ok ? "Success" : "Error"] = message;
            return RedirectToPage(new { id = Id });
        }

        public async Task<IActionResult> OnPostAllocateAsync()
        {
            // Duplicate-submit guard (server-side): each page view's token allocates at most once.
            if (!_tokens.TryConsume(AllocateTokenScope, AllocateToken))
            {
                TempData["Error"] = "This allocation was already submitted (or the page is out of date) -- nothing was allocated again. Check the allocations below.";
                return RedirectToPage(FilterRoute());
            }
            if (AllocateReadyStockId <= 0)
            {
                TempData["Error"] = "Select a batch in the table first.";
                return RedirectToPage(FilterRoute());
            }
            var (ok, message) = await _repo.AllocateBatchAsync(Id, AllocateReadyStockId, AllocateQuantity, SubstitutionReason, Actor, CanAccessArea);
            TempData[ok ? "Success" : "Error"] = message;
            return RedirectToPage(FilterRoute());
        }

        // The current filters, so the table looks the same after an allocation.
        public object FilterRoute(int? pageNo = null) => new
        {
            id = Id, q = Q, sowingDate = SowingDate?.ToString("yyyy-MM-dd"), areaId = AreaId, polyhouseId = PolyhouseId,
            allocationType = AllocationType, pageNo = pageNo ?? PageNo
        };

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
            IReadOnlyCollection<int>? allowed = _areaAccess.HasFullAreaAccess(User) ? null : _areaAccess.GetAccessibleAreaIds(User);
            Batches = await _repo.SearchBatchOptionsAsync(Booking.PlantId, Booking.SpeciesId, new BatchSearch
            {
                Query = Q, SowingDate = SowingDate, AreaId = AreaId, PolyhouseId = PolyhouseId,
                AllocationType = AllocationType, Page = PageNo
            }, allowed);
            (AreaOptions, PolyhouseOptions) = await _repo.GetBatchFilterOptionsAsync(Booking.PlantId, allowed);
            AllocateToken = SubmissionTokenGuard.NewToken();
            Staff = await _userRoleRepo.GetUsersInAnyRoleAsync(SupervisorRules.DispatchExecutive);   // dispatch staff only
            Lines = Allocations.Where(a => a.Status == "Active" && a.OpenQuantity > 0)
                .Select(a => new DispatchLineInput { AllocationId = a.Id, Quantity = 0 }).ToList();
            return true;
        }
    }
}
