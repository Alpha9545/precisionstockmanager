using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PlantStockManager.Authorization;
using PlantStockManager.Data;
using PlantStockManager.Models;
using PlantStockManager.Services;
using PurchaseOrderModel = PlantStockManager.Models.PurchaseOrder;

namespace PlantStockManager.Pages.Production.PurchaseOrder
{
    // 2026-10-21: a receipt form sent twice (double click / browser retry / replay) is stopped by
    // SubmissionTokenGuard -- it used to record the same delivery again (up to the ordered quantity).
    public class ReceiveModel : PageModel
    {
        public const string TokenScope = "PurchaseOrderReceive";

        private readonly PurchaseOrderRepository _purchaseOrderRepo;
        private readonly SubmissionTokenGuard _tokens;
        private readonly EmployeeRepository _employeeRepo;
        private readonly AreaAccessService _areaAccessService;

        public ReceiveModel(PurchaseOrderRepository purchaseOrderRepo, EmployeeRepository employeeRepo, AreaAccessService areaAccessService,
            SubmissionTokenGuard tokens)
        {
            _tokens = tokens;
            _areaAccessService = areaAccessService;
            _purchaseOrderRepo = purchaseOrderRepo;
            _employeeRepo = employeeRepo;
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

        [BindProperty]
        public string? SubmitToken { get; set; }

        public async Task<IActionResult> OnGetAsync(int id)
        {
            Order = await _purchaseOrderRepo.GetByIdAsync(id);
            if (Order == null)
                return RedirectToPage("/Production/PurchaseOrder/Index");
            if (!CanAccessOrder(Order))
            {
                TempData["Error"] = "You are not authorized to receive this Purchase Order.";
                return RedirectToPage("/Production/PurchaseOrder/Index");
            }            if (Order.Status != "Pending" && Order.Status != "PartiallyReceived")
            {
                TempData["Error"] = $"This Purchase Order is '{Order.Status}' and cannot receive any more stock.";
                return RedirectToPage("/Production/PurchaseOrder/Details", new { id });
            }

            PersonOptions = await _employeeRepo.GetAllActiveUsers();
            SubmitToken = SubmissionTokenGuard.NewToken();
            return Page();
        }

        public async Task<IActionResult> OnPostAsync(int id)
        {
            var order = await _purchaseOrderRepo.GetByIdAsync(id);
            if (order == null)
                return RedirectToPage("/Production/PurchaseOrder/Index");
            if (!CanAccessOrder(order))
            {
                TempData["Error"] = "You are not authorized to receive this Purchase Order.";
                return RedirectToPage("/Production/PurchaseOrder/Index");
            }
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

            if (!_tokens.TryConsume(TokenScope, SubmitToken))
            {
                TempData["Error"] = "This receipt form was already submitted. Check the received quantities below before receiving again.";
                return RedirectToPage("/Production/PurchaseOrder/Details", new { id });
            }

            var createdBy = User.Identity?.Name ?? "System";
            var userIdClaim = User.FindFirst("UserId")?.Value;
            int? userId = int.TryParse(userIdClaim, out var parsedUserId) ? parsedUserId : null;

            var (success, message, _) = await _purchaseOrderRepo.InsertReceiptAsync(
                id, lines, ReceiptDate, ReceivedById, Remarks, createdBy, userId);

            if (!success)
            {
                ModelState.AddModelError(string.Empty, message ?? "Failed to record receipt.");
                // The token was used up; issue a fresh one so the corrected form can be sent again.
                ModelState.Remove(nameof(SubmitToken));
                SubmitToken = SubmissionTokenGuard.NewToken();
                Order = order;
                PersonOptions = await _employeeRepo.GetAllActiveUsers();
                return Page();
            }

            TempData["Success"] = "Receipt recorded. Stock updated for Fertilizer and Empty Pot lines.";
            return RedirectToPage("/Production/PurchaseOrder/Details", new { id });
        }
    }
}
