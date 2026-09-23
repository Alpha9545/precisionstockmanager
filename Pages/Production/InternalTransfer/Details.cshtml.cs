using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PlantStockManager.Data;
using InternalTransferModel = PlantStockManager.Models.InternalTransfer;

namespace PlantStockManager.Pages.Production.InternalTransfer
{
    public class DetailsModel : PageModel
    {
        private readonly InternalTransferRepository _internalTransferRepo;

        public DetailsModel(InternalTransferRepository internalTransferRepo)
        {
            _internalTransferRepo = internalTransferRepo;
        }

        public InternalTransferModel? InternalTransfer { get; set; }

        public async Task<IActionResult> OnGetAsync(int id)
        {
            InternalTransfer = await _internalTransferRepo.GetByIdAsync(id);
            if (InternalTransfer == null)
                return RedirectToPage("/Production/InternalTransfer/Index");

            return Page();
        }
    }
}
