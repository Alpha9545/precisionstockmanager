using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PlantStockManager.Authorization;
using PlantStockManager.Data;
using PlantStockManager.Models;
using PlantStockManager.Services;
using PottedPlantStockModel = PlantStockManager.Models.PottedPlantStock;
using InternalTransferModel = PlantStockManager.Models.InternalTransfer;

namespace PlantStockManager.Pages.Production.GrowingPartnerToMainOffice
{
    // "Send Stock to Main Office" -- Phase 32 (Phase 7). Creates a
    // GrowingPartnerToMainOffice-type InternalTransfer in
    // 'PendingConfirmation'. Nothing is decremented here except
    // InTransitQuantity (reserved by InternalTransferRepository.InsertAsync
    // itself, via PottedPlantStockRepository.ReserveInTransitAsync) --
    // PhysicalQuantity and the ledger are untouched until Main Office
    // actually confirms receipt (ConfirmReceipt.cshtml). Mirrors
    // GrowingPartnerToOutlet/Create.cshtml.cs exactly, with the Outlet
    // destination swapped for Main Office.
    //
    // Both Source (must be an active, Growing-Partner-linked Area) and
    // Destination (must be an active Main Office Area) are re-verified
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

        public List<Area> SourceAreas { get; set; } = new();
        public List<Area> MainOfficeAreas { get; set; } = new();
        public List<PottedPlantStockModel> StockPools { get; set; } = new();
        public List<SupervisorOption> Supervisors { get; set; } = new();
        public int? SelectedAreaId { get; set; }

        public async Task<IActionResult> OnGetAsync(int? areaId)
        {
            if (areaId.HasValue && !_areaAccessService.CanAccessArea(User, areaId))
            {
                TempData["Error"] = "You are not authorized to send stock from the selected Area.";
                return RedirectToPage("/Production/GrowingPartnerToMainOffice/Create");
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
                return RedirectToPage("/Production/GrowingPartnerToMainOffice/Create", new { areaId });
            }
            if (destinationAreaId <= 0)
            {
                TempData["Error"] = "Select which Main Office this is being sent to.";
                return RedirectToPage("/Production/GrowingPartnerToMainOffice/Create", new { areaId });
            }

            if (areaId.HasValue && !_areaAccessService.CanAccessArea(User, areaId))
            {
                TempData["Error"] = "You are not authorized to send stock from the selected Area.";
                return RedirectToPage("/Production/GrowingPartnerToMainOffice/Create", new { areaId });
            }

            // Re-fetch the ACTUAL stock row and re-check access against ITS
            // real AreaId -- not just the posted areaId hidden field above
            // (same reasoning as GrowingPartnerToOutlet/Create.cshtml.cs).
            var stock = await _pottedPlantStockRepo.GetByIdAsync(pottedPlantStockId);
            if (stock == null || !_areaAccessService.CanAccessArea(User, stock.AreaId))
            {
                TempData["Error"] = "You are not authorized to send that stock.";
                return RedirectToPage("/Production/GrowingPartnerToMainOffice/Create", new { areaId });
            }

            var supervisorError = await _supervisors.ValidateAsync(SupervisorKind.ProductionArea, stock.AreaId, supervisorId);
            if (supervisorError != null)
            {
                TempData["Error"] = supervisorError;
                return RedirectToPage("/Production/GrowingPartnerToMainOffice/Create", new { areaId });
            }

            var userIdClaim = User.FindFirst("UserId")?.Value;
            int? userId = int.TryParse(userIdClaim, out var parsedUserId) ? parsedUserId : null;
            var createdBy = User.Identity?.Name ?? "System";

            var entry = new InternalTransferModel
            {
                StockType = "GrowingPartnerToMainOffice",
                SourcePottedPlantStockId = pottedPlantStockId,
                DestinationAreaId = destinationAreaId,
                Quantity = quantity,
                SupervisorId = supervisorId,
                Remarks = remarks,
                CreatedBy = createdBy
            };

            var (success, message, _) = await _internalTransferRepo.InsertAsync(entry, userId);
            TempData[success ? "Success" : "Error"] = success
                ? $"Sent {quantity:N2} to the selected Main Office. Awaiting their confirmation."
                : (message ?? "Failed to send stock to Main Office.");

            return RedirectToPage("/Production/GrowingPartnerToMainOffice/Create", new { areaId });
        }

        private async Task LoadDropdownsAsync(int? areaId)
        {
            var allPartnerAreas = (await _areaRepo.GetAllAreas())
                .Where(a => a.GrowingPartnerId.HasValue && a.IsActive)
                .ToList();
            SourceAreas = _areaAccessService.HasFullAreaAccess(User)
                ? allPartnerAreas
                : allPartnerAreas.Where(a => _areaAccessService.CanAccessArea(User, a.Id)).ToList();

            // Destination options offered here are a convenience only --
            // every active Main Office Area regardless of who is sending,
            // mirroring GrowingPartnerToOutlet's own OutletAreas dropdown.
            MainOfficeAreas = await _areaRepo.GetByAreaTypesAsync("MainOffice");

            StockPools = areaId.HasValue
                ? (await _pottedPlantStockRepo.GetAllAsync())
                    .Where(s => s.AreaId == areaId.Value && (s.PhysicalQuantity - s.ReservedQuantity - s.InTransitQuantity) > 0)
                    .ToList()
                : new List<PottedPlantStockModel>();

            Supervisors = areaId.HasValue
                ? (await _supervisors.OptionsAsync(SupervisorKind.ProductionArea)).Where(o => o.CanServe(areaId)).ToList()
                : new List<SupervisorOption>();
        }
    }
}
