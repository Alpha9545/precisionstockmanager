using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PlantStockManager.Authorization;
using PlantStockManager.Data;
using LabRequestModel = PlantStockManager.Models.LabRequest;

namespace PlantStockManager.Pages.Production.LabRequest
{
    public class DetailsModel : PageModel
    {
        private readonly LabRequestRepository _labRequestRepo;
        private readonly AreaAccessService _areaAccessService;

        public DetailsModel(LabRequestRepository labRequestRepo, AreaAccessService areaAccessService)
        {
            _areaAccessService = areaAccessService;
            _labRequestRepo = labRequestRepo;
        }

        // F1: a Lab Request belongs to the Area of the stock pool its sample
        // came from (Request.AreaId, server-derived). Previously every page
        // here trusted the id alone. Access = that Area (AreaAccessService),
        // OR the existing Phase 14 lab permissions -- the LabWorker role is
        // deliberately NOT Area-scoped (Phase 17/B: "assignments with no
        // AreaId, e.g. Admin/Management/LabWorker"), so lab staff keep
        // working across Areas exactly as designed.
        private bool CanAccessLabRequest(int? areaId, bool write)
            => _areaAccessService.CanAccessArea(User, areaId)
               || User.HasPermission("Lab.Enter")
               || (!write && User.HasPermission("Lab.View"));

        public LabRequestModel? Request { get; set; }

        public async Task<IActionResult> OnGetAsync(int id)
        {
            Request = await _labRequestRepo.GetByIdAsync(id);
            if (Request == null)
                return RedirectToPage("/Production/LabRequest/Index");
            if (!CanAccessLabRequest(Request.AreaId, write: false))
            {
                TempData["Error"] = "You are not authorized to view this Lab Request.";
                return RedirectToPage("/Production/LabRequest/Index");
            }
            return Page();
        }

        public async Task<IActionResult> OnPostCancelAsync(int id)
        {
            // F1: cancelling restores stock -- check the STORED request's Area.
            var existing = await _labRequestRepo.GetByIdAsync(id);
            if (existing == null || !CanAccessLabRequest(existing.AreaId, write: true))
            {
                TempData["Error"] = "You are not authorized to cancel this Lab Request.";
                return RedirectToPage("/Production/LabRequest/Index");
            }

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
