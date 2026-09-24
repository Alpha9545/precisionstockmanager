using Microsoft.AspNetCore.Mvc.RazorPages;
using PlantStockManager.Authorization;
using PlantStockManager.Data;
using InternalTransferModel = PlantStockManager.Models.InternalTransfer;

namespace PlantStockManager.Pages.Production.InternalTransfer
{
    public class IndexModel : PageModel
    {
        private readonly InternalTransferRepository _internalTransferRepo;
        private readonly AreaAccessService _areaAccessService;

        public IndexModel(InternalTransferRepository internalTransferRepo, AreaAccessService areaAccessService)
        {
            _internalTransferRepo = internalTransferRepo;
            _areaAccessService = areaAccessService;
        }

        public List<InternalTransferModel> InternalTransfers { get; set; } = new();

        public async Task OnGetAsync()
        {
            // F1: previously listed EVERY Area's transfers to every user.
            // Now only transfers touching one of the user's Areas (source,
            // destination or Main Office Area); cross-Area roles see all.
            var all = await _internalTransferRepo.GetAllAsync();
            InternalTransfers = all.Where(t => _areaAccessService.CanAccessAnyArea(User, t.SourceAreaId, t.DestinationAreaId, t.PendingConfirmationAreaId)).ToList();
        }
    }
}
