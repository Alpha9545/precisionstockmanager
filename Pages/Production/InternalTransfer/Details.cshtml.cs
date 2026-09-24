using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PlantStockManager.Authorization;
using PlantStockManager.Data;
using InternalTransferModel = PlantStockManager.Models.InternalTransfer;

namespace PlantStockManager.Pages.Production.InternalTransfer
{
    public class DetailsModel : PageModel
    {
        private readonly InternalTransferRepository _internalTransferRepo;
        private readonly AreaAccessService _areaAccessService;

        public DetailsModel(InternalTransferRepository internalTransferRepo, AreaAccessService areaAccessService)
        {
            _internalTransferRepo = internalTransferRepo;
            _areaAccessService = areaAccessService;
        }

        public InternalTransferModel? InternalTransfer { get; set; }

        public async Task<IActionResult> OnGetAsync(int id)
        {
            InternalTransfer = await _internalTransferRepo.GetByIdAsync(id);
            if (InternalTransfer == null)
                return RedirectToPage("/Production/InternalTransfer/Index");

            // F1: block viewing another Area's transfer by id.
            if (!_areaAccessService.CanAccessAnyArea(User, InternalTransfer.SourceAreaId, InternalTransfer.DestinationAreaId, InternalTransfer.PendingConfirmationAreaId))
            {
                TempData["Error"] = "You are not authorized to view this Internal Transfer.";
                return RedirectToPage("/Production/InternalTransfer/Index");
            }

            return Page();
        }
    }
}
