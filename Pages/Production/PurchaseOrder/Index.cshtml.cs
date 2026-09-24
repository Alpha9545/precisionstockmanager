using Microsoft.AspNetCore.Mvc.RazorPages;
using PlantStockManager.Authorization;
using PlantStockManager.Data;
using PurchaseOrderModel = PlantStockManager.Models.PurchaseOrder;

namespace PlantStockManager.Pages.Production.PurchaseOrder
{
    public class IndexModel : PageModel
    {
        private readonly PurchaseOrderRepository _purchaseOrderRepo;
        private readonly AreaAccessService _areaAccessService;

        public IndexModel(PurchaseOrderRepository purchaseOrderRepo, AreaAccessService areaAccessService)
        {
            _areaAccessService = areaAccessService;
            _purchaseOrderRepo = purchaseOrderRepo;
        }

        // F1: a Purchase Order touches an Area only through its EmptyPot
        // lines (PurchaseOrderItem.AreaId -- receiving them adds stock to
        // that Area). Previously these pages trusted the order id alone.
        // The user must be able to access EVERY EmptyPot line's Area
        // (AreaAccessService; unassigned/null Areas keep the existing
        // "nothing to scope" behaviour, so Fertilizer/Other-only orders are
        // unaffected).
        private bool CanAccessOrder(PurchaseOrderModel order)
            => order.Items.All(i => i.ItemCategory != "EmptyPot" || _areaAccessService.CanAccessArea(User, i.AreaId));

        public List<PurchaseOrderModel> Orders { get; set; } = new();

        public async Task OnGetAsync()
        {
            var all = await _purchaseOrderRepo.GetAllAsync();
            if (_areaAccessService.HasFullAreaAccess(User))
            {
                Orders = all;
                return;
            }
            // F1: GetAllAsync returns headers only, so each order's lines
            // are loaded to decide Area scope (only for Area-scoped users).
            Orders = new List<PurchaseOrderModel>();
            foreach (var header in all)
            {
                var full = await _purchaseOrderRepo.GetByIdAsync(header.Id);
                if (full != null && CanAccessOrder(full))
                    Orders.Add(header);
            }
        }
    }
}
