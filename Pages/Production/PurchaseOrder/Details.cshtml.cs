using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PlantStockManager.Data;
using PlantStockManager.Models;
using PurchaseOrderModel = PlantStockManager.Models.PurchaseOrder;

namespace PlantStockManager.Pages.Production.PurchaseOrder
{
    public class DetailsModel : PageModel
    {
        private readonly PurchaseOrderRepository _purchaseOrderRepo;

        public DetailsModel(PurchaseOrderRepository purchaseOrderRepo)
        {
            _purchaseOrderRepo = purchaseOrderRepo;
        }

        public PurchaseOrderModel? Order { get; set; }
        public List<PurchaseReceipt> Receipts { get; set; } = new();

        public async Task<IActionResult> OnGetAsync(int id)
        {
            Order = await _purchaseOrderRepo.GetByIdAsync(id);
            if (Order == null)
                return RedirectToPage("/Production/PurchaseOrder/Index");

            Receipts = await _purchaseOrderRepo.GetReceiptsAsync(id);
            return Page();
        }

        public async Task<IActionResult> OnPostCancelAsync(int id)
        {
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
