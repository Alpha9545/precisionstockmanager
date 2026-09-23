using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PlantStockManager.Authorization;
using PlantStockManager.Data;
using PlantStockManager.Models;
// Alias required: Pages/Production/InternalTransfer/ makes "InternalTransfer"
// a sibling namespace under PlantStockManager.Pages.Production, which
// shadows the bare Models.InternalTransfer type (CS0118). Same fix already
// used elsewhere in this codebase (see PotProduction/CreateFromCutting.cshtml.cs).
using InternalTransferModel = PlantStockManager.Models.InternalTransfer;

namespace PlantStockManager.Pages.Production.GrowingPartnerToOutlet
{
    // A Growing Partner's own sent-history view -- mirrors
    // MainOfficeIssue/MyIssues exactly. Reuses
    // InternalTransferRepository.GetByAreaAsync (already fully generic --
    // no change needed there) and filters to this StockType in memory,
    // the same reuse pattern the redesign has used elsewhere rather than
    // adding a StockType parameter to a shared method.
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
                return RedirectToPage("/Production/GrowingPartnerToOutlet/MySentTransfers");
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
                    .Where(t => t.StockType == "GrowingPartnerToOutlet")
                    .ToList();
            }

            return Page();
        }
    }
}
