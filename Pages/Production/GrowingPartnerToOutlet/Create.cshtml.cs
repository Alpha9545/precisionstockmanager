using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PlantStockManager.Authorization;
using PlantStockManager.Data;
using PlantStockManager.Models;
using PottedPlantStockModel = PlantStockManager.Models.PottedPlantStock;
using InternalTransferModel = PlantStockManager.Models.InternalTransfer;

namespace PlantStockManager.Pages.Production.GrowingPartnerToOutlet
{
    // "Send Stock to Outlet" -- Phase 20 (Phase F). Creates a
    // GrowingPartnerToOutlet-type InternalTransfer in 'PendingConfirmation'.
    // Nothing is decremented here except InTransitQuantity (reserved by
    // InternalTransferRepository.InsertAsync itself, via
    // PottedPlantStockRepository.ReserveInTransitAsync) -- PhysicalQuantity
    // and the ledger are untouched until the Outlet Area actually confirms
    // receipt (ConfirmReceipt.cshtml).
    //
    // Both Source (must be an active, Growing-Partner-linked Area) and
    // Destination (must be an active Outlet Area) are re-verified
    // server-side inside InternalTransferRepository.InsertAsync itself --
    // this page's dropdowns are a convenience only, never the actual
    // authorization boundary.
    public class CreateModel : PageModel
    {
        private readonly PottedPlantStockRepository _pottedPlantStockRepo;
        private readonly InternalTransferRepository _internalTransferRepo;
        private readonly AreaRepository _areaRepo;
        private readonly EmployeeRepository _employeeRepo;
        private readonly AreaAccessService _areaAccessService;

        public CreateModel(
            PottedPlantStockRepository pottedPlantStockRepo,
            InternalTransferRepository internalTransferRepo,
            AreaRepository areaRepo,
            EmployeeRepository employeeRepo,
            AreaAccessService areaAccessService)
        {
            _pottedPlantStockRepo = pottedPlantStockRepo;
            _internalTransferRepo = internalTransferRepo;
            _areaRepo = areaRepo;
            _employeeRepo = employeeRepo;
            _areaAccessService = areaAccessService;
        }

        public List<Area> SourceAreas { get; set; } = new();
        public List<Area> OutletAreas { get; set; } = new();
        public List<PottedPlantStockModel> StockPools { get; set; } = new();
        public List<Employee> ResponsiblePersons { get; set; } = new();
        public List<Employee> Supervisors { get; set; } = new();
        public int? SelectedAreaId { get; set; }

        public async Task<IActionResult> OnGetAsync(int? areaId)
        {
            // A Growing Partner Area the caller cannot access (URL
            // tampering, e.g. ?areaId= edited to one they were never
            // assigned) is simply refused here -- never trust that only
            // permitted options were ever offered in the dropdown
            // client-side. Mirrors MainOfficeIssue/Create exactly.
            if (areaId.HasValue && !_areaAccessService.CanAccessArea(User, areaId))
            {
                TempData["Error"] = "You are not authorized to send stock from the selected Area.";
                return RedirectToPage("/Production/GrowingPartnerToOutlet/Create");
            }

            SelectedAreaId = areaId;
            await LoadDropdownsAsync(areaId);
            return Page();
        }

        public async Task<IActionResult> OnPostSendAsync(
            int pottedPlantStockId, decimal quantity, int destinationAreaId,
            int? responsiblePersonId, int? supervisorId, string? remarks, int? areaId)
        {
            if (quantity <= 0)
            {
                TempData["Error"] = "Quantity must be greater than zero.";
                return RedirectToPage("/Production/GrowingPartnerToOutlet/Create", new { areaId });
            }
            if (destinationAreaId <= 0)
            {
                TempData["Error"] = "Select which Outlet this is being sent to.";
                return RedirectToPage("/Production/GrowingPartnerToOutlet/Create", new { areaId });
            }

            // Re-check the SOURCE Area the caller submitted (areaId) is one
            // they may send from -- catches POST tampering on this value
            // before InsertAsync even looks at the stock pool it names.
            if (areaId.HasValue && !_areaAccessService.CanAccessArea(User, areaId))
            {
                TempData["Error"] = "You are not authorized to send stock from the selected Area.";
                return RedirectToPage("/Production/GrowingPartnerToOutlet/Create", new { areaId });
            }

            // Re-fetch the ACTUAL stock row and re-check access against ITS
            // real AreaId -- not just the posted areaId hidden field above.
            // Without this, a Kiran Supervisor could post areaId=Kiran (a
            // legitimate value that passes the check above) together with
            // a pottedPlantStockId that actually belongs to Kunjir's Area,
            // and InsertAsync's own re-derivation would happily accept it
            // (it only re-checks that the source is SOME active Growing
            // Partner Area, not that it is one THIS user may touch, since
            // the repository has no user identity at all). Mirrors the
            // precedent set by Phase D's CreateFromCutting.cshtml.cs, which
            // needed this same extra fetch-and-check for the same reason --
            // MainOfficeIssue/Create never needed it because
            // MainOfficeOfficer is a full-access role by design.
            var stock = await _pottedPlantStockRepo.GetByIdAsync(pottedPlantStockId);
            if (stock == null || !_areaAccessService.CanAccessArea(User, stock.AreaId))
            {
                TempData["Error"] = "You are not authorized to send that stock.";
                return RedirectToPage("/Production/GrowingPartnerToOutlet/Create", new { areaId });
            }

            var userIdClaim = User.FindFirst("UserId")?.Value;
            int? userId = int.TryParse(userIdClaim, out var parsedUserId) ? parsedUserId : null;
            var createdBy = User.Identity?.Name ?? "System";

            var entry = new InternalTransferModel
            {
                StockType = "GrowingPartnerToOutlet",
                SourcePottedPlantStockId = pottedPlantStockId,
                DestinationAreaId = destinationAreaId,
                Quantity = quantity,
                ResponsiblePersonId = responsiblePersonId,
                SupervisorId = supervisorId,
                Remarks = remarks,
                CreatedBy = createdBy
            };

            var (success, message, _) = await _internalTransferRepo.InsertAsync(entry, userId);
            TempData[success ? "Success" : "Error"] = success
                ? $"Sent {quantity:N2} to the selected Outlet. Awaiting their confirmation."
                : (message ?? "Failed to send stock to the Outlet.");

            return RedirectToPage("/Production/GrowingPartnerToOutlet/Create", new { areaId });
        }

        private async Task LoadDropdownsAsync(int? areaId)
        {
            // Source options: active Growing Partner Areas this user may
            // send from. Unlike MainOfficeIssue (whose sender,
            // MainOfficeOfficer, is a full-access role), a Growing Partner
            // Supervisor is NOT full-access, so this list is genuinely
            // restrictive for them, not just a convenience filter.
            var allPartnerAreas = (await _areaRepo.GetAllAreas())
                .Where(a => a.GrowingPartnerId.HasValue && a.IsActive)
                .ToList();
            SourceAreas = _areaAccessService.HasFullAreaAccess(User)
                ? allPartnerAreas
                : allPartnerAreas.Where(a => _areaAccessService.CanAccessArea(User, a.Id)).ToList();

            // Destination options offered here are a convenience only --
            // every active Outlet Area regardless of who is sending, since
            // Outlet access is scoped by the RECEIVING side's own
            // UserRoles.AreaId, not the sender's (mirrors MainOfficeIssue's
            // PartnerAreas dropdown, which is likewise not access-filtered
            // by the sender).
            OutletAreas = await _areaRepo.GetByAreaTypesAsync("Outlet");

            StockPools = areaId.HasValue
                ? (await _pottedPlantStockRepo.GetAllAsync())
                    .Where(s => s.AreaId == areaId.Value && (s.PhysicalQuantity - s.ReservedQuantity - s.InTransitQuantity) > 0)
                    .ToList()
                : new List<PottedPlantStockModel>();

            var activeUsers = await _employeeRepo.GetAllActiveUsers();
            ResponsiblePersons = activeUsers;
            Supervisors = activeUsers;
        }
    }
}
