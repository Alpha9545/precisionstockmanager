using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PlantStockManager.Authorization;
using PlantStockManager.Data;
using PlantStockManager.Models;
using PurchaseOrderModel = PlantStockManager.Models.PurchaseOrder;

namespace PlantStockManager.Pages.Production.PurchaseOrder
{
    public class DetailsModel : PageModel
    {
        private readonly PurchaseOrderRepository _purchaseOrderRepo;
        private readonly AreaAccessService _areaAccessService;

        public DetailsModel(PurchaseOrderRepository purchaseOrderRepo, AreaAccessService areaAccessService)
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

        public PurchaseOrderModel? Order { get; set; }
        public List<PurchaseReceipt> Receipts { get; set; } = new();

        public async Task<IActionResult> OnGetAsync(int id)
        {
            Order = await _purchaseOrderRepo.GetByIdAsync(id);
            if (Order == null)
                return RedirectToPage("/Production/PurchaseOrder/Index");
            if (!CanAccessOrder(Order))
            {
                TempData["Error"] = "You are not authorized to view this Purchase Order.";
                return RedirectToPage("/Production/PurchaseOrder/Index");
            }

            Receipts = await _purchaseOrderRepo.GetReceiptsAsync(id);
            return Page();
        }

        public async Task<IActionResult> OnPostCancelAsync(int id)
        {
            // F1: check the STORED order before cancelling it.
            var existing = await _purchaseOrderRepo.GetByIdAsync(id);
            if (existing == null || !CanAccessOrder(existing))
            {
                TempData["Error"] = "You are not authorized to cancel this Purchase Order.";
                return RedirectToPage("/Production/PurchaseOrder/Index");
            }

            var (success, message) = await _purchaseOrderRepo.CancelAsync(id, User.Identity?.Name ?? "System");
            if (!success)
            {
                TempData["Error"] = message ?? "Failed to cancel Purchase Order.";
                return RedirectToPage("/Production/PurchaseOrder/Details", new { id });
            }

            TempData["Success"] = "Purchase Order cancelled.";
            return RedirectToPage("/Production/PurchaseOrder/Index");
        }
    }
}
