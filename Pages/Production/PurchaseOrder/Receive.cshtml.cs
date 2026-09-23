using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PlantStockManager.Data;
using PlantStockManager.Models;
using PurchaseOrderModel = PlantStockManager.Models.PurchaseOrder;

namespace PlantStockManager.Pages.Production.PurchaseOrder
{
    public class ReceiveModel : PageModel
    {
        private readonly PurchaseOrderRepository _purchaseOrderRepo;
        private readonly EmployeeRepository _employeeRepo;

        public ReceiveModel(PurchaseOrderRepository purchaseOrderRepo, EmployeeRepository employeeRepo)
        {
            _purchaseOrderRepo = purchaseOrderRepo;
            _employeeRepo = employeeRepo;
        }

        public PurchaseOrderModel? Order { get; set; }
        public List<Employee> PersonOptions { get; set; } = new();

        [BindProperty]
        public DateTime ReceiptDate { get; set; } = DateTime.Today;

        [BindProperty]
        public int? ReceivedById { get; set; }

        [BindProperty]
        public string? Remarks { get; set; }

        // One entry per outstanding line item, indexed the same order as
        // Order.Items -- bound as a parallel array of quantities the
        // user wants to receive now (0 = not received in this event).
        [BindProperty]
        public List<decimal> ReceiveQuantities { get; set; } = new();

        [BindProperty]
        public List<int> ItemIds { get; set; } = new();

        public async Task<IActionResult> OnGetAsync(int id)
        {
            Order = await _purchaseOrderRepo.GetByIdAsync(id);
            if (Order == null)
                return RedirectToPage("/Production/PurchaseOrder/Index");
            if (Order.Status != "Pending" && Order.Status != "PartiallyReceived")
            {
                TempData["Error"] = $"This Purchase Order is '{Order.Status}' and cannot receive any more stock.";
                return RedirectToPage("/Production/PurchaseOrder/Details", new { id });
            }

            PersonOptions = await _employeeRepo.GetAllActiveUsers();
            return Page();
        }

        public async Task<IActionResult> OnPostAsync(int id)
        {
            var order = await _purchaseOrderRepo.GetByIdAsync(id);
            if (order == null)
                return RedirectToPage("/Production/PurchaseOrder/Index");

            var lines = new List<(int PurchaseOrderItemId, decimal ReceivedQuantity)>();
            for (int i = 0; i < ItemIds.Count && i < ReceiveQuantities.Count; i++)
            {
                if (ReceiveQuantities[i] > 0)
                {
                    lines.Add((ItemIds[i], ReceiveQuantities[i]));
                }
            }

            if (lines.Count == 0)
            {
                ModelState.AddModelError(string.Empty, "Enter a quantity greater than zero for at least one line to receive.");
                Order = order;
                PersonOptions = await _employeeRepo.GetAllActiveUsers();
                return Page();
            }

            var createdBy = User.Identity?.Name ?? "System";
            var userIdClaim = User.FindFirst("UserId")?.Value;
            int? userId = int.TryParse(userIdClaim, out var parsedUserId) ? parsedUserId : null;

            var (success, message, _) = await _purchaseOrderRepo.InsertReceiptAsync(
                id, lines, ReceiptDate, ReceivedById, Remarks, createdBy, userId);

            if (!success)
            {
                ModelState.AddModelError(string.Empty, message ?? "Failed to record receipt.");
                Order = order;
                PersonOptions = await _employeeRepo.GetAllActiveUsers();
                return Page();
            }

            TempData["Success"] = "Receipt recorded. Stock updated for Fertilizer and Empty Pot lines.";
            return RedirectToPage("/Production/PurchaseOrder/Details", new { id });
        }
    }
}
