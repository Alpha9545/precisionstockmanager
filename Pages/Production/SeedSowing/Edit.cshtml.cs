using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PlantStockManager.Authorization;
using PlantStockManager.Data;
using SeedSowingModel = PlantStockManager.Models.SeedSowing;

namespace PlantStockManager.Pages.Production.SeedSowing
{
    // Phase D: a saved sowing is final -- its source, variety, cavity, trays,
    // quantities, dates, recorder and ASSIGNED SUPERVISOR (the only person who
    // may approve it) never change (TR_SeedSowings_ImmutableTrayData). Only
    // the Remarks can be edited. A wrong sowing is cancelled (the seeds or
    // cuttings go back to their stock) and recorded again.
    public class EditModel : PageModel
    {
        private readonly SeedSowingRepository _seedSowingRepo;
        private readonly SeedlingAreaScope _areaAccessService;

        public EditModel(SeedSowingRepository seedSowingRepo, SeedlingAreaScope areaAccessService)
        {
            _seedSowingRepo = seedSowingRepo;
            _areaAccessService = areaAccessService;
        }

        // The only values accepted from the form.
        [BindProperty]
        public int Id { get; set; }

        [BindProperty]
        public string? Remarks { get; set; }

        public SeedSowingModel SeedSowing { get; set; } = new();

        public async Task<IActionResult> OnGetAsync(int id)
        {
            var existing = await _seedSowingRepo.GetByIdAsync(id);
            if (existing == null)
                return RedirectToPage("/Production/SeedSowing/Index");
            if (!_areaAccessService.CanAccessArea(User, existing.AreaId))
            {
                TempData["Error"] = "You are not authorized to edit this Sowing record.";
                return RedirectToPage("/Production/SeedSowing/Index");
            }
            SeedSowing = existing;
            Id = existing.Id;
            Remarks = existing.Remarks;
            return Page();
        }

        public async Task<IActionResult> OnPostAsync()
        {
            var existing = await _seedSowingRepo.GetByIdAsync(Id);
            if (existing == null)
            {
                TempData["Error"] = "Seed Sowing record not found.";
                return RedirectToPage("/Production/SeedSowing/Index");
            }
            if (!_areaAccessService.CanAccessArea(User, existing.AreaId))
            {
                TempData["Error"] = "You are not authorized to edit this Sowing record.";
                return RedirectToPage("/Production/SeedSowing/Index");
            }

            var (success, message) = await _seedSowingRepo.UpdateRemarksAsync(existing.Id, Remarks, User.Identity?.Name ?? "System");
            if (!success)
            {
                ModelState.AddModelError(string.Empty, message ?? "Failed to update Sowing.");
                SeedSowing = existing;
                return Page();
            }

            TempData["Success"] = $"Sowing {existing.SowingCode} updated.";
            return RedirectToPage("/Production/SeedSowing/Details", new { id = existing.Id });
        }

        public async Task<IActionResult> OnPostCancelAsync(int id)
        {
            var target = await _seedSowingRepo.GetByIdAsync(id);
            if (target == null)
            {
                TempData["Error"] = "Seed Sowing record not found.";
                return RedirectToPage("/Production/SeedSowing/Index");
            }
            if (!_areaAccessService.CanAccessArea(User, target.AreaId))
            {
                TempData["Error"] = "You are not authorized to cancel this Sowing record.";
                return RedirectToPage("/Production/SeedSowing/Index");
            }

            var userIdClaim = User.FindFirst("UserId")?.Value;
            int? userId = int.TryParse(userIdClaim, out var parsedUserId) ? parsedUserId : null;

            var (success, message) = await _seedSowingRepo.CancelAsync(id, User.Identity?.Name ?? "System", userId);
            if (!success)
            {
                TempData["Error"] = message ?? "Failed to cancel Sowing.";
                return RedirectToPage("/Production/SeedSowing/Edit", new { id });
            }

            TempData["Success"] = target.IsCuttingSource
                ? "Sowing cancelled. The cuttings were returned to Cutting Stock."
                : "Sowing cancelled. The seeds were returned to Seed Stock.";
            return RedirectToPage("/Production/SeedSowing/Index");
        }
    }
}
