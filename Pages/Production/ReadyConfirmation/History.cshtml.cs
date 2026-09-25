using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PlantStockManager.Authorization;
using PlantStockManager.Data;
using PlantStockManager.Models;
using SeedSowingModel = PlantStockManager.Models.SeedSowing;
// Alias required: this file's own folder (Pages/Production/
// ReadyConfirmation/) makes "ReadyConfirmation" a nested namespace member
// of PlantStockManager.Pages.Production, which shadows the bare
// Models.ReadyConfirmation type (CS0118) even within this same folder.
using ReadyConfirmationModel = PlantStockManager.Models.ReadyConfirmation;

namespace PlantStockManager.Pages.Production.ReadyConfirmation
{
    // Phase 25 (Phase K): the confirmation result/history page for one
    // Seed Sowing -- every ReadyConfirmation ever recorded against it
    // (Confirmed and Cancelled alike, oldest-status included, nothing
    // hidden), and the Cancel action for a still-Confirmed one.
    public class HistoryModel : PageModel
    {
        private readonly SeedSowingRepository _seedSowingRepo;
        private readonly ReadyConfirmationRepository _readyConfirmationRepo;
        private readonly SeedlingAreaScope _areaAccessService; // seedling-only Area scope (Authorization/SeedlingAreaScope.cs)

        public HistoryModel(
            SeedSowingRepository seedSowingRepo, ReadyConfirmationRepository readyConfirmationRepo,
            SeedlingAreaScope areaAccessService)
        {
            _seedSowingRepo = seedSowingRepo;
            _readyConfirmationRepo = readyConfirmationRepo;
            _areaAccessService = areaAccessService;
        }

        public SeedSowingModel SeedSowing { get; set; } = new();
        public List<ReadyConfirmationModel> Confirmations { get; set; } = new();

        public async Task<IActionResult> OnGetAsync(int id)
        {
            var sowing = await _seedSowingRepo.GetByIdAsync(id);
            if (sowing == null)
            {
                TempData["Error"] = "Seed Sowing record not found.";
                return RedirectToPage("/Production/ReadyConfirmation/Index");
            }
            if (!_areaAccessService.CanAccessArea(User, sowing.AreaId))
            {
                TempData["Error"] = "You are not authorized to view this Sowing's Ready Confirmation history.";
                return RedirectToPage("/Production/ReadyConfirmation/Index");
            }

            SeedSowing = sowing;
            Confirmations = await _readyConfirmationRepo.GetBySeedSowingIdAsync(id);
            return Page();
        }

        public async Task<IActionResult> OnPostCancelAsync(int confirmationId, int seedSowingId)
        {
            // Re-derive Area authorization from the Sowing itself --
            // never trust the posted seedSowingId alone without also
            // verifying the confirmation actually belongs to it.
            var sowing = await _seedSowingRepo.GetByIdAsync(seedSowingId);
            if (sowing == null || !_areaAccessService.CanAccessArea(User, sowing.AreaId))
            {
                TempData["Error"] = "You are not authorized to cancel this Ready Confirmation.";
                return RedirectToPage("/Production/ReadyConfirmation/Index");
            }

            var confirmation = await _readyConfirmationRepo.GetByIdAsync(confirmationId);
            if (confirmation == null || confirmation.SeedSowingId != seedSowingId)
            {
                TempData["Error"] = "Ready Confirmation record not found.";
                return RedirectToPage("/Production/ReadyConfirmation/History", new { id = seedSowingId });
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
                TempData["Success"] = "Supervisor Approval cancelled: Ready Stock reversed and the sowing batch re-opened for approval.";
            }

            return RedirectToPage("/Production/ReadyConfirmation/History", new { id = seedSowingId });
        }
    }
}
