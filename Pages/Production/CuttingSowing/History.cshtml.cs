using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PlantStockManager.Authorization;
using PlantStockManager.Data;
using PlantStockManager.Services;
using CuttingSowingModel = PlantStockManager.Models.CuttingSowing;
using ReadyConfirmationModel = PlantStockManager.Models.ReadyConfirmation;

namespace PlantStockManager.Pages.Production.CuttingSowing
{
    // Phase 5: the confirmation history page for one Cutting Sowing --
    // mirrors Pages/Production/ReadyConfirmation/History.cshtml.cs exactly,
    // just against dbo.CuttingSowings/CuttingSowingId instead of
    // dbo.SeedSowings/SeedSowingId. Cancel calls the SAME
    // ReadyConfirmationRepository.CancelAsync (source-agnostic).
    public class HistoryModel : PageModel
    {
        private readonly CuttingSowingRepository _cuttingSowingRepo;
        private readonly ReadyConfirmationRepository _readyConfirmationRepo;
        private readonly AreaAccessService _areaAccessService;

        public HistoryModel(
            CuttingSowingRepository cuttingSowingRepo, ReadyConfirmationRepository readyConfirmationRepo,
            AreaAccessService areaAccessService)
        {
            _cuttingSowingRepo = cuttingSowingRepo;
            _readyConfirmationRepo = readyConfirmationRepo;
            _areaAccessService = areaAccessService;
        }

        public CuttingSowingModel CuttingSowing { get; set; } = new();
        public List<ReadyConfirmationModel> Confirmations { get; set; } = new();

        // Phase 1 (F2): only the sowing's assigned supervisor sees / may use
        // Cancel (the repository re-checks under the sowing's row lock).
        public bool CanCancelApproval => DirectSowingRules.CanCancelApproval(CuttingSowing.SupervisorId, User.GetUserId()).Ok;

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
                TempData["Error"] = "You are not authorized to view this Cutting Sowing's approval history.";
                return RedirectToPage("/Production/CuttingSowing/Index");
            }

            CuttingSowing = sowing;
            Confirmations = await _readyConfirmationRepo.GetByCuttingSowingIdAsync(id);
            return Page();
        }

        public async Task<IActionResult> OnPostCancelAsync(int confirmationId, int cuttingSowingId)
        {
            var sowing = await _cuttingSowingRepo.GetByIdAsync(cuttingSowingId);
            if (sowing == null || !_areaAccessService.CanAccessArea(User, sowing.AreaId))
            {
                TempData["Error"] = "You are not authorized to cancel this Ready Confirmation.";
                return RedirectToPage("/Production/CuttingSowing/Index");
            }

            var confirmation = await _readyConfirmationRepo.GetByIdAsync(confirmationId);
            if (confirmation == null || confirmation.CuttingSowingId != cuttingSowingId)
            {
                TempData["Error"] = "Ready Confirmation record not found.";
                return RedirectToPage("/Production/CuttingSowing/History", new { id = cuttingSowingId });
            }

            var userIdClaim = User.FindFirst("UserId")?.Value;
            int? userId = int.TryParse(userIdClaim, out var parsedUserId) ? parsedUserId : null;

            var (success, message) = await _readyConfirmationRepo.CancelAsync(confirmationId, User.Identity?.Name ?? "System", userId);
            if (!success)
            {
                TempData["Error"] = message ?? "Failed to cancel Ready Confirmation.";
            }
            else
            {
                TempData["Success"] = "Supervisor Approval cancelled: Ready Stock reversed and the Cutting Sowing batch re-opened for approval.";
            }

            return RedirectToPage("/Production/CuttingSowing/History", new { id = cuttingSowingId });
        }
    }
}
