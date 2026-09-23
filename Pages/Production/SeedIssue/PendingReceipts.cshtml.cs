using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PlantStockManager.Authorization;
using PlantStockManager.Data;
using PlantStockManager.Models;

namespace PlantStockManager.Pages.Production.SeedIssue
{
    // "Polyhouse Pending Seed Receipts" -- the confirmation queue on
    // the RECEIVING side (Phase 22/Phase H). A Polyhouse/Growing
    // Area's supervisor either confirms receipt (-> ConfirmReceipt.cshtml,
    // which finishes the whole issue in one step) or rejects it
    // outright here.
    //
    // Area-scoped like every other pending-receipt queue in this app
    // (MainOfficeIssue/PendingReceipts, GrowingPartnerToOutlet/PendingReceipts):
    // a user without cross-Area access only ever sees pending receipts
    // for Areas they are actually assigned to.
    public class PendingReceiptsModel : PageModel
    {
        private readonly SeedIssueRepository _seedIssueRepo;
        private readonly AreaRepository _areaRepo;
        private readonly AreaAccessService _areaAccessService;

        private static readonly string[] ValidDestinationAreaTypes = { "Kunjir", "Kiran" };

        public PendingReceiptsModel(
            SeedIssueRepository seedIssueRepo,
            AreaRepository areaRepo,
            AreaAccessService areaAccessService)
        {
            _seedIssueRepo = seedIssueRepo;
            _areaRepo = areaRepo;
            _areaAccessService = areaAccessService;
        }

        public List<Area> DestinationAreas { get; set; } = new();
        public List<PlantStockManager.Models.SeedIssue> Pending { get; set; } = new();
        public int? SelectedAreaId { get; set; }

        public async Task<IActionResult> OnGetAsync(int? areaId)
        {
            if (areaId.HasValue && !_areaAccessService.CanAccessArea(User, areaId))
            {
                TempData["Error"] = "You are not authorized to view receipts for the selected Area.";
                return RedirectToPage("/Production/SeedIssue/PendingReceipts");
            }

            SelectedAreaId = areaId;

            var allDestinationAreas = await _areaRepo.GetByAreaTypesAsync(ValidDestinationAreaTypes);
            DestinationAreas = _areaAccessService.HasFullAreaAccess(User)
                ? allDestinationAreas
                : allDestinationAreas.Where(a => _areaAccessService.CanAccessArea(User, a.Id)).ToList();

            if (areaId.HasValue)
            {
                Pending = await _seedIssueRepo.GetPendingReceiptsAsync(areaId);
            }
            else if (_areaAccessService.HasFullAreaAccess(User))
            {
                Pending = await _seedIssueRepo.GetPendingReceiptsAsync(null);
            }
            else
            {
                // No single Area picked and no cross-Area access --
                // only ever show what this user's own accessible
                // Area(s) actually hold, mirroring every other pending
                // queue's in-memory filter rather than trusting the
                // (absent) areaId alone.
                var accessibleIds = _areaAccessService.GetAccessibleAreaIds(User);
                var all = await _seedIssueRepo.GetPendingReceiptsAsync(null);
                Pending = all.Where(i => accessibleIds.Contains(i.DestinationAreaId)).ToList();
            }

            return Page();
        }

        public async Task<IActionResult> OnPostRejectAsync(int id, string reason, int? areaId)
        {
            // Re-verify against the issue's ACTUAL destination Area
            // before rejecting -- catches a tampered issue Id (e.g.
            // someone else's pending receipt) even if the form's own
            // areaId field was left alone.
            var issue = await _seedIssueRepo.GetByIdAsync(id);
            if (issue == null || !_areaAccessService.CanAccessArea(User, issue.DestinationAreaId))
            {
                TempData["Error"] = "You are not authorized to reject that Seed Issue.";
                return RedirectToPage("/Production/SeedIssue/PendingReceipts", new { areaId });
            }

            var (success, message) = await _seedIssueRepo.RejectAsync(id, reason, User.Identity?.Name ?? "System");
            TempData[success ? "Success" : "Error"] = success ? "Seed Issue rejected." : (message ?? "Failed to reject.");
            return RedirectToPage("/Production/SeedIssue/PendingReceipts", new { areaId });
        }
    }
}
