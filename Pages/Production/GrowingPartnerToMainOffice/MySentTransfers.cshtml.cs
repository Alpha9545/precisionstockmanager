using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PlantStockManager.Authorization;
using PlantStockManager.Data;
using PlantStockManager.Models;
using InternalTransferModel = PlantStockManager.Models.InternalTransfer;

namespace PlantStockManager.Pages.Production.GrowingPartnerToMainOffice
{
    // A Growing Partner's own sent-history view -- mirrors
    // GrowingPartnerToOutlet/MySentTransfers exactly. Reuses
    // InternalTransferRepository.GetByAreaAsync (already fully generic --
    // no change needed there) and filters to this StockType in memory.
    public class MySentTransfersModel : PageModel
    {
        private readonly InternalTransferRepository _internalTransferRepo;
        private readonly AreaRepository _areaRepo;
        private readonly AreaAccessService _areaAccessService;

        public MySentTransfersModel(
            InternalTransferRepository internalTransferRepo,
            AreaRepository areaRepo,
            AreaAccessService areaAccessService)
        {
            _internalTransferRepo = internalTransferRepo;
            _areaRepo = areaRepo;
            _areaAccessService = areaAccessService;
        }

        public List<Area> SourceAreas { get; set; } = new();
        public List<InternalTransferModel> Transfers { get; set; } = new();
        public int? SelectedAreaId { get; set; }

        public async Task<IActionResult> OnGetAsync(int? areaId)
        {
            if (areaId.HasValue && !_areaAccessService.CanAccessArea(User, areaId))
            {
                TempData["Error"] = "You are not authorized to view transfers for the selected Area.";
                return RedirectToPage("/Production/GrowingPartnerToMainOffice/MySentTransfers");
            }

            SelectedAreaId = areaId;

            var allPartnerAreas = (await _areaRepo.GetAllAreas())
                .Where(a => a.GrowingPartnerId.HasValue && a.IsActive)
                .ToList();
            SourceAreas = _areaAccessService.HasFullAreaAccess(User)
                ? allPartnerAreas
                : allPartnerAreas.Where(a => _areaAccessService.CanAccessArea(User, a.Id)).ToList();

            if (areaId.HasValue)
            {
                Transfers = (await _internalTransferRepo.GetByAreaAsync(areaId.Value))
                    .Where(t => t.StockType == "GrowingPartnerToMainOffice")
                    .ToList();
            }

            return Page();
        }
    }
}
