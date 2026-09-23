using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PlantStockManager.Authorization;
using PlantStockManager.Data;
using PlantStockManager.Models;

namespace PlantStockManager.Pages.Production.SeedIssue
{
    // Confirms receipt of a pending Seed Issue -- finishes the whole
    // issue in one step (ConfirmAsync both releases InTransit and
    // moves the actual stock), because the destination Area was
    // already fixed at creation. Mirrors MainOfficeIssue/ConfirmReceipt
    // exactly.
    public class ConfirmReceiptModel : PageModel
    {
        private readonly SeedIssueRepository _seedIssueRepo;
        private readonly AreaAccessService _areaAccessService;

        public ConfirmReceiptModel(SeedIssueRepository seedIssueRepo, AreaAccessService areaAccessService)
        {
            _seedIssueRepo = seedIssueRepo;
            _areaAccessService = areaAccessService;
        }

        public PlantStockManager.Models.SeedIssue? Issue { get; set; }

        [BindProperty]
        public decimal ConfirmedQuantity { get; set; }

        [BindProperty]
        public string? DiscrepancyReason { get; set; }

        public async Task<IActionResult> OnGetAsync(int id)
        {
            Issue = await _seedIssueRepo.GetByIdAsync(id);
            if (!IsValidPendingReceipt(Issue))
            {
                TempData["Error"] = "That Seed Issue is not awaiting confirmation.";
                return RedirectToPage("/Production/SeedIssue/PendingReceipts");
            }

            // Tampering vector: a valid issue Id whose destination Area
            // this user was never assigned to (direct URL, Id guessed/
            // incremented). Re-checked again on POST below.
            if (!_areaAccessService.CanAccessArea(User, Issue!.DestinationAreaId))
            {
                TempData["Error"] = "You are not authorized to confirm receipt for that Seed Issue.";
                return RedirectToPage("/Production/SeedIssue/PendingReceipts");
            }

            ConfirmedQuantity = Issue.IssuedQuantity;
            return Page();
        }

        public async Task<IActionResult> OnPostAsync(int id)
        {
            Issue = await _seedIssueRepo.GetByIdAsync(id);
            if (!IsValidPendingReceipt(Issue))
            {
                TempData["Error"] = "That Seed Issue is not awaiting confirmation.";
                return RedirectToPage("/Production/SeedIssue/PendingReceipts");
            }
            if (!_areaAccessService.CanAccessArea(User, Issue!.DestinationAreaId))
            {
                TempData["Error"] = "You are not authorized to confirm receipt for that Seed Issue.";
                return RedirectToPage("/Production/SeedIssue/PendingReceipts");
            }

            if (ConfirmedQuantity < 0)
                ModelState.AddModelError(nameof(ConfirmedQuantity), "Confirmed quantity cannot be negative.");
            if (ConfirmedQuantity != Issue.IssuedQuantity && string.IsNullOrWhiteSpace(DiscrepancyReason))
                ModelState.AddModelError(nameof(DiscrepancyReason), "A reason is required when the confirmed quantity differs from the issued quantity.");

            if (!ModelState.IsValid)
                return Page();

            var userIdClaim = User.FindFirst("UserId")?.Value;
            int? userId = int.TryParse(userIdClaim, out var parsedUserId) ? parsedUserId : null;
            var modifiedBy = User.Identity?.Name ?? "System";

            var (success, message) = await _seedIssueRepo.ConfirmAsync(id, ConfirmedQuantity, DiscrepancyReason, userId, modifiedBy);
            if (!success)
            {
                ModelState.AddModelError(string.Empty, message ?? "Failed to confirm receipt.");
                return Page();
            }

            TempData["Success"] = "Receipt confirmed -- seed stock has been credited to your Area.";
            return RedirectToPage("/Production/SeedIssue/PendingReceipts");
        }

        private static bool IsValidPendingReceipt(PlantStockManager.Models.SeedIssue? i)
            => i != null && i.Status == "PendingConfirmation";
    }
}
