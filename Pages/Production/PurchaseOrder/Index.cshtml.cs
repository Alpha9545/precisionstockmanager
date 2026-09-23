using Microsoft.AspNetCore.Mvc.RazorPages;
using PlantStockManager.Data;
using PurchaseOrderModel = PlantStockManager.Models.PurchaseOrder;

namespace PlantStockManager.Pages.Production.PurchaseOrder
{
    public class IndexModel : PageModel
    {
        private readonly PurchaseOrderRepository _purchaseOrderRepo;

        public IndexModel(PurchaseOrderRepository purchaseOrderRepo)
        {
            _purchaseOrderRepo = purchaseOrderRepo;
        }

        public List<PurchaseOrderModel> Orders { get; set; } = new();

        public async Task OnGetAsync()
        {
            Orders = await _purchaseOrderRepo.GetAllAsync();
        }
    }
}
