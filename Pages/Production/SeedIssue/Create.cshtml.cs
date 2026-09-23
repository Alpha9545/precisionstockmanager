using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PlantStockManager.Authorization;
using PlantStockManager.Data;
using PlantStockManager.Models;
using SeedStockModel = PlantStockManager.Models.SeedStock;

namespace PlantStockManager.Pages.Production.SeedIssue
{
    // "Issue Seed to Polyhouse/Growing Area" -- Phase 22 (Phase H).
    // Creates a Seed Issue in 'PendingConfirmation'. Nothing is
    // decremented here except InTransitQuantity (reserved by
    // SeedIssueRepository.InsertAsync itself, via
    // SeedStockRepository.ReserveInTransitAsync) -- PhysicalQuantity
    // and the ledger are untouched until the destination Area actually
    // confirms receipt (ConfirmReceipt.cshtml).
    //
    // Both Source (must be a genuine Main Office pool) and Destination
    // (must be an active Kunjir/Kiran Area) are re-verified server-side
    // inside SeedIssueRepository.InsertAsync itself -- this page's
    // dropdowns are a convenience only, never the actual authorization
    // boundary (spec item 11).
    public class CreateModel : PageModel
    {
        private readonly SeedStockRepository _seedStockRepo;
        private readonly SeedIssueRepository _seedIssueRepo;
        private readonly AreaRepository _areaRepo;
        private readonly EmployeeRepository _employeeRepo;
        private readonly AreaAccessService _areaAccessService;

        private static readonly string[] ValidDestinationAreaTypes = { "Kunjir", "Kiran" };

        public CreateModel(
            SeedStockRepository seedStockRepo,
            SeedIssueRepository seedIssueRepo,
            AreaRepository areaRepo,
            EmployeeRepository employeeRepo,
            AreaAccessService areaAccessService)
        {
            _seedStockRepo = seedStockRepo;
            _seedIssueRepo = seedIssueRepo;
            _areaRepo = areaRepo;
            _employeeRepo = employeeRepo;
            _areaAccessService = areaAccessService;
        }

        public List<Area> MainOfficeAreas { get; set; } = new();
        public List<Area> DestinationAreas { get; set; } = new();
        public List<SeedStockModel> StockPools { get; set; } = new();
        public List<Employee> ResponsiblePersons { get; set; } = new();
        public List<Employee> Supervisors { get; set; } = new();
        public int? SelectedAreaId { get; set; }

        public async Task<IActionResult> OnGetAsync(int? areaId)
        {
            // A Main Office Area the caller cannot access (URL
            // tampering, e.g. ?areaId= edited to one they were never
            // assigned) is simply refused here -- never trust that
            // only permitted options were ever offered client-side.
            if (areaId.HasValue && !_areaAccessService.CanAccessArea(User, areaId))
            {
                TempData["Error"] = "You are not authorized to issue seed from the selected Area.";
                return RedirectToPage("/Production/SeedIssue/Create");
            }

            SelectedAreaId = areaId;
            await LoadDropdownsAsync(areaId);
            return Page();
        }

        public async Task<IActionResult> OnPostSendAsync(
            int seedStockId, decimal quantity, int destinationAreaId,
            int? responsiblePersonId, int? supervisorId, string? remarks, int? areaId)
        {
            if (quantity <= 0)
            {
                TempData["Error"] = "Quantity must be greater than zero.";
                return RedirectToPage("/Production/SeedIssue/Create", new { areaId });
            }
            if (destinationAreaId <= 0)
            {
                TempData["Error"] = "Select which Polyhouse/Growing Area this is being issued to.";
                return RedirectToPage("/Production/SeedIssue/Create", new { areaId });
            }

            // Re-check the SOURCE Area the caller submitted (areaId) is
            // one they may issue from -- catches POST tampering on this
            // value before InsertAsync even looks at the stock pool it
            // names. (InsertAsync itself re-derives and re-validates the
            // actual source/destination Areas independently of anything
            // posted here -- this is defense-in-depth, not the only check.)
            if (areaId.HasValue && !_areaAccessService.CanAccessArea(User, areaId))
            {
                TempData["Error"] = "You are not authorized to issue seed from the selected Area.";
                return RedirectToPage("/Production/SeedIssue/Create", new { areaId });
            }

            var userIdClaim = User.FindFirst("UserId")?.Value;
            int? userId = int.TryParse(userIdClaim, out var parsedUserId) ? parsedUserId : null;
            var createdBy = User.Identity?.Name ?? "System";

            var entry = new PlantStockManager.Models.SeedIssue
            {
                SourceSeedStockId = seedStockId,
                DestinationAreaId = destinationAreaId,
                IssuedQuantity = quantity,
                IssueDate = DateTime.Today,
                ResponsiblePersonId = responsiblePersonId,
                SupervisorId = supervisorId,
                Remarks = remarks,
                CreatedBy = createdBy
            };

            var (success, message, _) = await _seedIssueRepo.InsertAsync(entry, userId);
            TempData[success ? "Success" : "Error"] = success
                ? $"Issued {quantity:N2} to the selected Polyhouse/Growing Area. Awaiting their confirmation."
                : (message ?? "Failed to issue seed.");

            return RedirectToPage("/Production/SeedIssue/Create", new { areaId });
        }

        private async Task LoadDropdownsAsync(int? areaId)
        {
            var allMainOffice = await _areaRepo.GetByAreaTypesAsync("MainOffice");
            MainOfficeAreas = _areaAccessService.HasFullAreaAccess(User)
                ? allMainOffice
                : allMainOffice.Where(a => _areaAccessService.CanAccessArea(User, a.Id)).ToList();

            // Destination options offered here are a convenience only
            // -- still an active Kunjir/Kiran Area regardless of who is
            // issuing, since the RECEIVING supervisor's own access is
            // scoped by their own UserRoles.AreaId, not the sender's.
            DestinationAreas = (await _areaRepo.GetByAreaTypesAsync(ValidDestinationAreaTypes));

            StockPools = areaId.HasValue
                ? (await _seedStockRepo.GetAllAsync())
                    .Where(s => s.AreaId == areaId.Value && s.AvailableQuantity > 0)
                    .ToList()
                : new List<SeedStockModel>();

            var activeUsers = await _employeeRepo.GetAllActiveUsers();
            ResponsiblePersons = activeUsers;
            Supervisors = activeUsers;
        }
    }
}
