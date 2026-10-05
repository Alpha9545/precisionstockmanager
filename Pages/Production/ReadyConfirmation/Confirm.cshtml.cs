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
        private readonly AreaAccessService _cuttingAreaAccess;   // Cutting Stock Area isolation (strict: not the seedling-only scope)

        public ConfirmModel(
            SeedSowingRepository seedSowingRepo, ReadyConfirmationRepository readyConfirmationRepo,
            SeedlingAreaScope areaAccessService, AreaAccessService cuttingAreaAccess)
        {
            _seedSowingRepo = seedSowingRepo;
            _readyConfirmationRepo = readyConfirmationRepo;
            _areaAccessService = areaAccessService;
            _cuttingAreaAccess = cuttingAreaAccess;
        }

        public SeedSowingModel SeedSowing { get; set; } = new();

        // Cutting Tray Sowing only: the Available Cutting Stock of the sowing's own
        // pool, in CUTTINGS (Physical - In-Transit), shown when this user may use
        // cuttings held in that Area (else null). Read-only display; the save
        // re-checks it under the pool's lock.
        public decimal? AvailableCuttingStock { get; set; }

        // The most TRAYS that can be entered right now (never fewer than the trays the
        // sowing still expects; otherwise floor(available cuttings / cavity)). Display only.
        public decimal? MaxReadyTrays { get; set; }

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
                sowing.SupervisorId, sowing.CreatedById, sowing.CreatedBy, User.GetUserId(), User.Identity?.Name, sowing.SourceType);
            if (!mayApprove)
            {
                TempData["Error"] = authorityError;
                return RedirectToPage("/Production/ReadyConfirmation/Index");
            }

            SeedSowing = sowing;
            SeedSowingId = sowing.Id;
            LoadCuttingStock(sowing);
            return Page();
        }

        // Cutting sowings draw extra cuttings from the pool they were sown from --
        // only when the user may use cuttings held in that pool's Area (the same
        // Area-isolation rule as recording the sowing, CuttingRules.CanUseAsSource).
        private bool CanUseSourceArea(int areaId)
            => CuttingRules.CanUseAsSource(_cuttingAreaAccess.CanAccessArea(User, areaId));

        private void LoadCuttingStock(SeedSowingModel sowing)
        {
            AvailableCuttingStock = null;
            MaxReadyTrays = null;
            if (!sowing.IsCuttingSource || !sowing.SourceCuttingStockId.HasValue || sowing.SourceAreaId <= 0)
                return;
            if (CanUseSourceArea(sowing.SourceAreaId))
            {
                AvailableCuttingStock = sowing.SourceAvailableQuantity;
                if (AvailableCuttingStock.HasValue)
                    MaxReadyTrays = DirectSowingRules.MaxReadyTrays(sowing.RemainingReadyQuantity, sowing.CavityType, AvailableCuttingStock.Value);
            }
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
                sowing.SupervisorId, sowing.CreatedById, sowing.CreatedBy, User.GetUserId(), User.Identity?.Name, sowing.SourceType);
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
                LoadCuttingStock(sowing);
                return Page();
            }
            var userIdClaim = User.FindFirst("UserId")?.Value;
            int? userId = int.TryParse(userIdClaim, out var parsedUserId) ? parsedUserId : null;
            var createdBy = User.Identity?.Name ?? "System";

            // No generic "Responsible Person": the assigned Sowing Supervisor
            // (already checked above) is who is accountable for this approval.
            var (success, message, _) = await _readyConfirmationRepo.ConfirmAsync(
                sowing.Id, ActualReadyTrays, WastageReason, null, Remarks, createdBy, userId, CanUseSourceArea);

            if (!success)
            {
                ModelState.AddModelError(string.Empty, message ?? "Failed to record the Supervisor Approval.");
                SeedSowing = sowing;
                LoadCuttingStock(sowing);
                return Page();
            }

            var extra = DirectSowingRules.ExtraCuttingsNeeded(sowing.SourceType, ActualReadyTrays, sowing.CavityType, sowing.RemainingReadyQuantity);
            TempData["Success"] = $"Batch {sowing.SowingCode} approved: {QuantityFormat.Qty(ActualReadyTrays)} trays x {sowing.CavityType} = {QuantityFormat.Qty(seedlings)} seedlings added to Ready Stock; wastage {QuantityFormat.Qty(wastage)} ({wastagePct:0.00}%). The sowing is now Completed."
                + (extra > 0 ? $" {QuantityFormat.Qty(DirectSowingRules.ExtraTrays(extra, sowing.CavityType))} extra trays = {QuantityFormat.Qty(extra)} extra cuttings were taken from Cutting Stock." : "");
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

            // Cutting Tray Sowing: how many extra trays / extra cuttings this many TRAYS
            // would take from Cutting Stock, and whether the pool (in CUTTINGS) can cover
            // them (advisory only -- the save decides, under the pool's lock). Never
            // reported for seed sowings.
            var calculated = ok;   // the tray arithmetic itself; a stock problem below does not hide the numbers
            var extra = ok ? DirectSowingRules.ExtraCuttingsNeeded(sowing.SourceType, trays, sowing.CavityType, sowing.RemainingReadyQuantity) : 0m;
            decimal? available = null;
            if (ok && sowing.IsCuttingSource)
            {
                if (extra > 0 && !CanUseSourceArea(sowing.SourceAreaId))
                {
                    ok = false;
                    error = "You are not authorized to use cuttings from this Area.";
                }
                else if (sowing.SourceAvailableQuantity.HasValue && CanUseSourceArea(sowing.SourceAreaId))
                {
                    available = sowing.SourceAvailableQuantity;
                    var (stockOk, _, stockError) = DirectSowingRules.CheckCuttingOverage(
                        seedlings, extra, available.Value, 0, trays, DirectSowingRules.CavityCount(sowing.CavityType));
                    if (!stockOk)
                    {
                        ok = false;
                        error = stockError;
                    }
                }
            }
            var extraTrays = DirectSowingRules.ExtraTrays(extra, sowing.CavityType);
            decimal? maxTrays = available.HasValue ? DirectSowingRules.MaxReadyTrays(sowing.RemainingReadyQuantity, sowing.CavityType, available.Value) : null;
            return new JsonResult(new
            {
                ok, seedlings = calculated ? seedlings : (decimal?)null, wastage, wastagePercent = wastagePct,
                extraTrays, extraCuttings = extra, availableCuttings = available, maxReadyTrays = maxTrays, error
            });
        }

    }
}
