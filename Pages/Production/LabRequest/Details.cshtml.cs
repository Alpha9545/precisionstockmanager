using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PlantStockManager.Data;
using LabRequestModel = PlantStockManager.Models.LabRequest;

namespace PlantStockManager.Pages.Production.LabRequest
{
    public class DetailsModel : PageModel
    {
        private readonly LabRequestRepository _labRequestRepo;

        public DetailsModel(LabRequestRepository labRequestRepo)
        {
            _labRequestRepo = labRequestRepo;
        }

        public LabRequestModel? Request { get; set; }

        public async Task<IActionResult> OnGetAsync(int id)
        {
            Request = await _labRequestRepo.GetByIdAsync(id);
            if (Request == null)
                return RedirectToPage("/Production/LabRequest/Index");
            return Page();
        }

        public async Task<IActionResult> OnPostCancelAsync(int id)
        {
            var userIdClaim = User.FindFirst("UserId")?.Value;
            int? userId = int.TryParse(userIdClaim, out var parsedUserId) ? parsedUserId : null;

            var (success, message) = await _labRequestRepo.CancelAsync(id, User.Identity?.Name ?? "System", userId);
            if (!success)
            {
                TempData["Error"] = message ?? "Failed to cancel Lab Request.";
                return RedirectToPage("/Production/LabRequest/Details", new { id });
            }

            TempData["Success"] = "Lab Request cancelled. Sample quantity restored to stock.";
            return RedirectToPage("/Production/LabRequest/Index");
        }
    }
}
