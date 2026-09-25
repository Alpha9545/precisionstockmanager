using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PlantStockManager.Authorization;
using PlantStockManager.Data;
using PlantStockManager.Models;
using PlantStockManager.Services;
using PottedPlantStockModel = PlantStockManager.Models.PottedPlantStock;
using InternalTransferModel = PlantStockManager.Models.InternalTransfer;

namespace PlantStockManager.Pages.Production.MainOfficeIssue
{
    // "Issue Starter Material to Growing Partner" -- Phase 18 (Phase C).
    // Creates a MainOfficeIssue-type InternalTransfer in
    // 'PendingConfirmation'. Nothing is decremented here except
    // InTransitQuantity (reserved by InternalTransferRepository.InsertAsync
    // itself, via PottedPlantStockRepository.ReserveInTransitAsync) --
    // PhysicalQuantity and the ledger are untouched until the Growing
    // Partner Area actually confirms receipt (ConfirmReceipt.cshtml).
    //
    // Both Source (must be a genuine Main Office pool) and Destination
    // (must be an active, Growing-Partner-linked Area) are re-verified
    // server-side inside InternalTransferRepository.InsertAsync itself --
    // this page's dropdowns are a convenience only, never the actual
    // authorization boundary.
    public class CreateModel : PageModel
    {
        private readonly PottedPlantStockRepository _pottedPlantStockRepo;
        private readonly InternalTransferRepository _internalTransferRepo;
        private readonly AreaRepository _areaRepo;
        private readonly SupervisorSelectionService _supervisors;
        private readonly AreaAccessService _areaAccessService;

        public CreateModel(
            PottedPlantStockRepository pottedPlantStockRepo,
            InternalTransferRepository internalTransferRepo,
            AreaRepository areaRepo,
            SupervisorSelectionService supervisors,
            AreaAccessService areaAccessService)
        {
            _pottedPlantStockRepo = pottedPlantStockRepo;
            _internalTransferRepo = internalTransferRepo;
            _areaRepo = areaRepo;
            _supervisors = supervisors;
            _areaAccessService = areaAccessService;
        }

        public List<Area> MainOfficeAreas { get; set; } = new();
        public List<Area> PartnerAreas { get; set; } = new();
        public List<PottedPlantStockModel> StockPools { get; set; } = new();
        // Phase 1: supervisors of the selected source Area only.
        public List<SupervisorOption> Supervisors { get; set; } = new();
        public int? SelectedAreaId { get; set; }

        public async Task<IActionResult> OnGetAsync(int? areaId)
        {
            // A Main Office Area the caller cannot access (URL tampering,
            // e.g. ?areaId= edited to one they were never assigned) is
            // simply refused here -- never trust that only permitted
            // options were ever offered in the dropdown client-side.
            if (areaId.HasValue && !_areaAccessService.CanAccessArea(User, areaId))
            {
                TempData["Error"] = "You are not authorized to issue stock from the selected Area.";
                return RedirectToPage("/Production/MainOfficeIssue/Create");
            }

            SelectedAreaId = areaId;
            await LoadDropdownsAsync(areaId);
            return Page();
        }

        public async Task<IActionResult> OnPostSendAsync(
            int pottedPlantStockId, decimal quantity, int destinationAreaId,
            int? supervisorId, string? remarks, int? areaId)
        {
            if (quantity <= 0)
            {
                TempData["Error"] = "Quantity must be greater than zero.";
                return RedirectToPage("/Production/MainOfficeIssue/Create", new { areaId });
            }
            if (destinationAreaId <= 0)
            {
                TempData["Error"] = "Select which Growing Partner Area this is being sent to.";
                return RedirectToPage("/Production/MainOfficeIssue/Create", new { areaId });
            }

            // Re-check the SOURCE Area the caller submitted (areaId) is one
            // they may issue from -- catches POST tampering on this value
            // before InsertAsync even looks at the stock pool it names.
            // (InsertAsync itself re-derives and re-validates the actual
            // source/destination Areas independently of anything posted
            // here -- this is a defense-in-depth check, not the only one.)
            if (areaId.HasValue && !_areaAccessService.CanAccessArea(User, areaId))
            {
                TempData["Error"] = "You are not authorized to issue stock from the selected Area.";
                return RedirectToPage("/Production/MainOfficeIssue/Create", new { areaId });
            }

            // Phase 1: the supervisor must be a Main Office supervisor of the
            // stock pool's real Area (never the posted areaId alone).
            var pool = await _pottedPlantStockRepo.GetByIdAsync(pottedPlantStockId);
            var supervisorError = await _supervisors.ValidateAsync(SupervisorKind.MainOffice, pool?.AreaId, supervisorId);
            if (pool == null || supervisorError != null)
            {
                TempData["Error"] = pool == null ? "Selected stock pool not found." : supervisorError;
                return RedirectToPage("/Production/MainOfficeIssue/Create", new { areaId });
            }

            var userIdClaim = User.FindFirst("UserId")?.Value;
            int? userId = int.TryParse(userIdClaim, out var parsedUserId) ? parsedUserId : null;
            var createdBy = User.Identity?.Name ?? "System";

            var entry = new InternalTransferModel
            {
                StockType = "MainOfficeIssue",
                SourcePottedPlantStockId = pottedPlantStockId,
                DestinationAreaId = destinationAreaId,
                Quantity = quantity,
                SupervisorId = supervisorId,
                Remarks = remarks,
                CreatedBy = createdBy
            };

            var (success, message, _) = await _internalTransferRepo.InsertAsync(entry, userId);
            TempData[success ? "Success" : "Error"] = success
                ? $"Issued {quantity:N2} to the selected Growing Partner Area. Awaiting their confirmation."
                : (message ?? "Failed to issue starter material.");

            return RedirectToPage("/Production/MainOfficeIssue/Create", new { areaId });
        }

        private async Task LoadDropdownsAsync(int? areaId)
        {
            var allMainOffice = await _areaRepo.GetByAreaTypesAsync("MainOffice");
            MainOfficeAreas = _areaAccessService.HasFullAreaAccess(User)
                ? allMainOffice
                : allMainOffice.Where(a => _areaAccessService.CanAccessArea(User, a.Id)).ToList();

            // Destination options offered here are a convenience only --
            // still an active, Growing-Partner-linked Area regardless of
            // who is issuing, since Growing Partner access is scoped by
            // the RECEIVING side's UserRoles.AreaId, not the sender's.
            PartnerAreas = (await _areaRepo.GetAllAreas())
                .Where(a => a.GrowingPartnerId.HasValue)
                .ToList();

            StockPools = areaId.HasValue
                ? (await _pottedPlantStockRepo.GetAllAsync())
                    .Where(s => s.AreaId == areaId.Value && (s.PhysicalQuantity - s.ReservedQuantity - s.InTransitQuantity) > 0)
                    .ToList()
                : new List<PottedPlantStockModel>();

            Supervisors = areaId.HasValue
                ? (await _supervisors.OptionsAsync(SupervisorKind.MainOffice)).Where(o => o.CanServe(areaId)).ToList()
                : new List<SupervisorOption>();
        }
    }
}
