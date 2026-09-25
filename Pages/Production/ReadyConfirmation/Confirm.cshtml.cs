using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PlantStockManager.Authorization;
using PlantStockManager.Data;
using PlantStockManager.Models;
using PlantStockManager.Services;
using SeedSowingModel = PlantStockManager.Models.SeedSowing;

namespace PlantStockManager.Pages.Production.ReadyConfirmation
{
    // Phase 25 (Phase K): the Ready Confirmation entry page. GET shows
    // the Sowing's read-only info (including its immutable, historical
    // Ready Stock Days / Expected Ready Date from Phase J/24 -- this
    // page never lets those be edited) plus the Ready Quantity input;
    // POST performs the actual confirmation via
    // ReadyConfirmationRepository.ConfirmAsync.
    //
    // Area authorization: this form posts NO AreaId field at all --
    // every check is against the Sowing's own AreaId, freshly re-read
    // from the database on every GET and every POST via
    // SeedSowingRepository.GetByIdAsync (never a cached/posted value),
    // exactly mirroring SeedSowing/Edit.cshtml.cs's own discipline.
    //
    // Phase B: this is the SUPERVISOR APPROVAL page (permission
    // ReadyStock.Confirm). The supervisor enters the Actual Ready Quantity;
    // Wastage = remaining - Ready is calculated (never typed), a reason is
    // required when it is > 0, Approved By = the logged-in user, and the
    // batch closes (Status 'Completed').
    public class ConfirmModel : PageModel
    {
        private readonly SeedSowingRepository _seedSowingRepo;
        private readonly ReadyConfirmationRepository _readyConfirmationRepo;
        private readonly SeedlingAreaScope _areaAccessService; // seedling-only Area scope (Authorization/SeedlingAreaScope.cs)

        public ConfirmModel(
            SeedSowingRepository seedSowingRepo, ReadyConfirmationRepository readyConfirmationRepo,
            SeedlingAreaScope areaAccessService)
        {
            _seedSowingRepo = seedSowingRepo;
            _readyConfirmationRepo = readyConfirmationRepo;
            _areaAccessService = areaAccessService;
        }

        public SeedSowingModel SeedSowing { get; set; } = new();

        [BindProperty]
        public int SeedSowingId { get; set; }

        // The supervisor's ONLY quantity input: complete trays actually ready.
        // Seedlings, wastage and cavity are never posted -- they are derived
        // on the server from the stored sowing (DirectSowingRules.ComputeTrayApproval).
        // Bound as decimal so a fractional value is rejected with a clear message.
        [BindProperty]
        public decimal ActualReadyTrays { get; set; }

        // Phase B: required whenever Wastage (= remaining - Ready) > 0.
        [BindProperty]
        public string? WastageReason { get; set; }

        public IReadOnlyList<string> WastageReasons => DirectSowingRules.WastageReasons;

        [BindProperty]
        public string? Remarks { get; set; }

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
                TempData["Error"] = "You are not authorized to Ready-Confirm this Sowing.";
                return RedirectToPage("/Production/ReadyConfirmation/Index");
            }
            if (sowing.Status != "Sown")
            {
                TempData["Error"] = $"This Sowing is '{sowing.Status}' and cannot be Ready-Confirmed.";
                return RedirectToPage("/Production/ReadyConfirmation/Index");
            }
            if (sowing.RemainingReadyQuantity <= 0)
            {
                TempData["Error"] = "This sowing batch has already been fully approved.";
                return RedirectToPage("/Production/ReadyConfirmation/Index");
            }
            // Only the supervisor assigned to this sowing (and never its creator).
            var (mayApprove, authorityError) = DirectSowingRules.CanApprove(
                sowing.SupervisorId, sowing.CreatedById, sowing.CreatedBy, User.GetUserId(), User.Identity?.Name);
            if (!mayApprove)
            {
                TempData["Error"] = authorityError;
                return RedirectToPage("/Production/ReadyConfirmation/Index");
            }

            SeedSowing = sowing;
            SeedSowingId = sowing.Id;
            return Page();
        }

        public async Task<IActionResult> OnPostAsync()
        {
            // Never trust a posted AreaId (there isn't one on this form
            // at all) -- always re-derive from the freshly fetched
            // Sowing, exactly like SeedSowing/Edit.cshtml.cs.
            var sowing = await _seedSowingRepo.GetByIdAsync(SeedSowingId);
            if (sowing == null)
            {
                TempData["Error"] = "Seed Sowing record not found.";
                return RedirectToPage("/Production/ReadyConfirmation/Index");
            }
            if (!_areaAccessService.CanAccessArea(User, sowing.AreaId))
            {
                TempData["Error"] = "You are not authorized to Ready-Confirm this Sowing.";
                return RedirectToPage("/Production/ReadyConfirmation/Index");
            }

            // Approval authority (the repository re-applies it under lock).
            var (mayApprove, authorityError) = DirectSowingRules.CanApprove(
                sowing.SupervisorId, sowing.CreatedById, sowing.CreatedBy, User.GetUserId(), User.Identity?.Name);
            if (!mayApprove)
            {
                TempData["Error"] = authorityError;
                return RedirectToPage("/Production/ReadyConfirmation/Index");
            }

            // Same rule the repository re-applies under lock (cavity and trays
            // from the stored sowing, never from the form).
            var (ok, _, seedlings, wastage, wastagePct, error) = DirectSowingRules.ComputeTrayApproval(
                sowing.QuantitySown, sowing.NumberOfTrays, sowing.CavityType, sowing.ConfirmedReadyQuantity, sowing.WastageQuantity,
                ActualReadyTrays, WastageReason);
            if (!ok)
            {
                ModelState.AddModelError(string.Empty, error!);
                SeedSowing = sowing;
                    return Page();
            }

            var userIdClaim = User.FindFirst("UserId")?.Value;
            int? userId = int.TryParse(userIdClaim, out var parsedUserId) ? parsedUserId : null;
            var createdBy = User.Identity?.Name ?? "System";

            var (success, message, _) = await _readyConfirmationRepo.ConfirmAsync(
                sowing.Id, ActualReadyTrays, WastageReason, responsiblePersonId: null, Remarks, createdBy, userId);

            if (!success)
            {
                ModelState.AddModelError(string.Empty, message ?? "Failed to record the Supervisor Approval.");
                SeedSowing = sowing;
                    return Page();
            }

            TempData["Success"] = $"Batch {sowing.SowingCode} approved: {ActualReadyTrays:N0} trays x {sowing.CavityType} = {seedlings:N0} seedlings added to Ready Stock; wastage {wastage:N0} ({wastagePct:0.00}%). The sowing is now Completed.";
            return RedirectToPage("/Production/ReadyConfirmation/History", new { id = sowing.Id });
        }

        // Live preview for the approval form. Takes ONLY the sowing id (route)
        // and a tray count; the cavity, sowing trays and seeds sown are read
        // from the stored sowing -- the browser cannot supply them.
        public async Task<JsonResult> OnGetTrayPreviewAsync(int id, decimal trays)
        {
            var sowing = await _seedSowingRepo.GetByIdAsync(id);
            if (sowing == null || !_areaAccessService.CanAccessArea(User, sowing.AreaId))
                return new JsonResult(new { ok = false, seedlings = (decimal?)null, wastage = 0m, wastagePercent = 0m, error = "Sowing not found." });
            // The reason does not change the numbers; a valid placeholder keeps
            // the preview from reporting "reason required" before one is chosen.
            var (ok, _, seedlings, wastage, wastagePct, error) = DirectSowingRules.ComputeTrayApproval(
                sowing.QuantitySown, sowing.NumberOfTrays, sowing.CavityType, sowing.ConfirmedReadyQuantity, sowing.WastageQuantity,
                trays, DirectSowingRules.WastageReasons[0]);
            return new JsonResult(new { ok, seedlings = ok ? seedlings : (decimal?)null, wastage, wastagePercent = wastagePct, error });
        }
    }
}
