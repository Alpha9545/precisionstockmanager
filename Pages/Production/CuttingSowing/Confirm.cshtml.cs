using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PlantStockManager.Authorization;
using PlantStockManager.Data;
using PlantStockManager.Services;
using CuttingSowingModel = PlantStockManager.Models.CuttingSowing;

namespace PlantStockManager.Pages.Production.CuttingSowing
{
    // Phase 5 -- CUTTING SOWING SUPERVISOR APPROVAL. Exact mirror of
    // Pages/Production/ReadyConfirmation/Confirm.cshtml.cs: the supervisor's
    // ONLY quantity input is Actual Ready Trays; Actual Seedlings and
    // Wastage are always derived server-side from the stored Cutting
    // Sowing (DirectSowingRules.ComputeTrayApproval) -- never trusted from
    // the browser. Approval authority is the SAME existing assigned-
    // supervisor rule (DirectSowingRules.CanApprove): only the supervisor
    // assigned to this Cutting Sowing, and never the person who recorded it.
    public class ConfirmModel : PageModel
    {
        private readonly CuttingSowingRepository _cuttingSowingRepo;
        private readonly ReadyConfirmationRepository _readyConfirmationRepo;
        private readonly AreaAccessService _areaAccessService;

        public ConfirmModel(
            CuttingSowingRepository cuttingSowingRepo, ReadyConfirmationRepository readyConfirmationRepo,
            AreaAccessService areaAccessService)
        {
            _cuttingSowingRepo = cuttingSowingRepo;
            _readyConfirmationRepo = readyConfirmationRepo;
            _areaAccessService = areaAccessService;
        }

        public CuttingSowingModel CuttingSowing { get; set; } = new();

        [BindProperty]
        public int CuttingSowingId { get; set; }

        [BindProperty]
        public decimal ActualReadyTrays { get; set; }

        [BindProperty]
        public string? WastageReason { get; set; }

        public IReadOnlyList<string> WastageReasons => DirectSowingRules.WastageReasons;

        [BindProperty]
        public string? Remarks { get; set; }

        public async Task<IActionResult> OnGetAsync(int id)
        {
            var sowing = await _cuttingSowingRepo.GetByIdAsync(id);
            if (sowing == null)
            {
                TempData["Error"] = "Cutting Sowing record not found.";
                return RedirectToPage("/Production/CuttingSowing/Index");
            }
            if (!_areaAccessService.CanAccessArea(User, sowing.AreaId))
            {
                TempData["Error"] = "You are not authorized to approve this Cutting Sowing.";
                return RedirectToPage("/Production/CuttingSowing/Index");
            }
            if (sowing.Status != "Sown")
            {
                TempData["Error"] = $"This Cutting Sowing is '{sowing.Status}' and cannot be approved.";
                return RedirectToPage("/Production/CuttingSowing/Index");
            }
            if (sowing.RemainingReadyQuantity <= 0)
            {
                TempData["Error"] = "This batch has already been fully approved.";
                return RedirectToPage("/Production/CuttingSowing/Index");
            }
            var (mayApprove, authorityError) = DirectSowingRules.CanApprove(
                sowing.SupervisorId, sowing.CreatedById, sowing.CreatedBy, User.GetUserId(), User.Identity?.Name);
            if (!mayApprove)
            {
                TempData["Error"] = authorityError;
                return RedirectToPage("/Production/CuttingSowing/Index");
            }

            CuttingSowing = sowing;
            CuttingSowingId = sowing.Id;
            return Page();
        }

        public async Task<IActionResult> OnPostAsync()
        {
            var sowing = await _cuttingSowingRepo.GetByIdAsync(CuttingSowingId);
            if (sowing == null)
            {
                TempData["Error"] = "Cutting Sowing record not found.";
                return RedirectToPage("/Production/CuttingSowing/Index");
            }
            if (!_areaAccessService.CanAccessArea(User, sowing.AreaId))
            {
                TempData["Error"] = "You are not authorized to approve this Cutting Sowing.";
                return RedirectToPage("/Production/CuttingSowing/Index");
            }

            var (mayApprove, authorityError) = DirectSowingRules.CanApprove(
                sowing.SupervisorId, sowing.CreatedById, sowing.CreatedBy, User.GetUserId(), User.Identity?.Name);
            if (!mayApprove)
            {
                TempData["Error"] = authorityError;
                return RedirectToPage("/Production/CuttingSowing/Index");
            }

            var (ok, _, seedlings, wastage, wastagePct, error) = DirectSowingRules.ComputeTrayApproval(
                sowing.QuantitySown, sowing.NumberOfTrays, sowing.CavityType, sowing.ConfirmedReadyQuantity, sowing.WastageQuantity,
                ActualReadyTrays, WastageReason);
            if (!ok)
            {
                ModelState.AddModelError(string.Empty, error!);
                CuttingSowing = sowing;
                return Page();
            }

            var userIdClaim = User.FindFirst("UserId")?.Value;
            int? userId = int.TryParse(userIdClaim, out var parsedUserId) ? parsedUserId : null;
            var createdBy = User.Identity?.Name ?? "System";

            var (success, message, _) = await _readyConfirmationRepo.ConfirmCuttingSowingAsync(
                sowing.Id, ActualReadyTrays, WastageReason, Remarks, createdBy, userId);

            if (!success)
            {
                ModelState.AddModelError(string.Empty, message ?? "Failed to record the Supervisor Approval.");
                CuttingSowing = sowing;
                return Page();
            }

            TempData["Success"] = $"Batch {sowing.SowingCode} approved: {ActualReadyTrays:N0} trays x {sowing.CavityType} = {seedlings:N0} seedlings added to Ready Stock; wastage {wastage:N0} ({wastagePct:0.00}%). The batch is now Completed.";
            return RedirectToPage("/Production/CuttingSowing/History", new { id = sowing.Id });
        }

        // Live preview -- takes ONLY the sowing id (route) and a tray
        // count; cavity, sowing trays and quantity sown are read from the
        // stored Cutting Sowing -- the browser cannot supply them.
        public async Task<JsonResult> OnGetTrayPreviewAsync(int id, decimal trays)
        {
            var sowing = await _cuttingSowingRepo.GetByIdAsync(id);
            if (sowing == null || !_areaAccessService.CanAccessArea(User, sowing.AreaId))
                return new JsonResult(new { ok = false, seedlings = (decimal?)null, wastage = 0m, wastagePercent = 0m, error = "Cutting Sowing not found." });
            var (ok, _, seedlings, wastage, wastagePct, error) = DirectSowingRules.ComputeTrayApproval(
                sowing.QuantitySown, sowing.NumberOfTrays, sowing.CavityType, sowing.ConfirmedReadyQuantity, sowing.WastageQuantity,
                trays, DirectSowingRules.WastageReasons[0]);
            return new JsonResult(new { ok, seedlings = ok ? seedlings : (decimal?)null, wastage, wastagePercent = wastagePct, error });
        }
    }
}
