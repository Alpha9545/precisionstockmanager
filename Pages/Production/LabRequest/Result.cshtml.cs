using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PlantStockManager.Data;
using LabRequestModel = PlantStockManager.Models.LabRequest;

namespace PlantStockManager.Pages.Production.LabRequest
{
    public class ResultModel : PageModel
    {
        private readonly LabRequestRepository _labRequestRepo;

        public ResultModel(LabRequestRepository labRequestRepo)
        {
            _labRequestRepo = labRequestRepo;
        }

        public LabRequestModel? Request { get; set; }

        [BindProperty]
        public decimal ReceivedQuantity { get; set; }

        [BindProperty]
        public DateTime ReceivedDate { get; set; } = DateTime.Today;

        [BindProperty]
        public string? ResultNotes { get; set; }

        public async Task<IActionResult> OnGetAsync(int id)
        {
            Request = await _labRequestRepo.GetByIdAsync(id);
            if (Request == null)
                return RedirectToPage("/Production/LabRequest/Index");
            if (Request.Status != "Sent")
            {
                TempData["Error"] = $"This Lab Request is '{Request.Status}' and cannot have a result recorded.";
                return RedirectToPage("/Production/LabRequest/Details", new { id });
            }
            return Page();
        }

        public async Task<IActionResult> OnPostAsync(int id)
        {
            var request = await _labRequestRepo.GetByIdAsync(id);
            if (request == null)
                return RedirectToPage("/Production/LabRequest/Index");

            if (ReceivedQuantity <= 0)
                ModelState.AddModelError("ReceivedQuantity", "Received Quantity must be greater than zero.");

            if (!ModelState.IsValid)
            {
                Request = request;
                return Page();
            }

            var userIdClaim = User.FindFirst("UserId")?.Value;
            int? userId = int.TryParse(userIdClaim, out var parsedUserId) ? parsedUserId : null;

            var (success, message) = await _labRequestRepo.RecordResultAsync(
                id, ReceivedQuantity, ReceivedDate, ResultNotes, User.Identity?.Name ?? "System", userId);

            if (!success)
            {
                ModelState.AddModelError(string.Empty, message ?? "Failed to record result.");
                Request = request;
                return Page();
            }

            TempData["Success"] = $"Result recorded for {request.LabRequestCode}. Stock updated.";
            return RedirectToPage("/Production/LabRequest/Details", new { id });
        }
    }
}
