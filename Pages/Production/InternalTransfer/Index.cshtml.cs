using Microsoft.AspNetCore.Mvc.RazorPages;
using PlantStockManager.Data;
using InternalTransferModel = PlantStockManager.Models.InternalTransfer;

namespace PlantStockManager.Pages.Production.InternalTransfer
{
    public class IndexModel : PageModel
    {
        private readonly InternalTransferRepository _internalTransferRepo;

        public IndexModel(InternalTransferRepository internalTransferRepo)
        {
            _internalTransferRepo = internalTransferRepo;
        }

        public List<InternalTransferModel> InternalTransfers { get; set; } = new();

        public async Task OnGetAsync()
        {
            InternalTransfers = await _internalTransferRepo.GetAllAsync();
        }
    }
}
