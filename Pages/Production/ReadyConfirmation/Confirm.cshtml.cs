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
        // 2026-10-17: Bed Sowing batches on any date; Transplant batches only by their Area Supervisor.
        public ReadyConfirmationRepository.ApprovalSettings Approval { get; private set; } = ReadyConfirmationRepository.ApprovalSettings.Earlier;

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

        // 2026-10-19: "mp" = opened from Mother Plant & Cutting > Sowing Approvals -- every way out returns there.
        [BindProperty(SupportsGet = true)] public string? From { get; set; }
        public const string FromMotherPlant = "mp";
        public string ListPage => From == FromMotherPlant ? "/Production/MotherPlant/SowingApprovals" : "/Production/ReadyConfirmation/Index";
        private IActionResult BackToList() => RedirectToPage(ListPage, ListState.RouteValues());

        // 2026-10-22: Seed Production > Sowing Approvals' list state (workflow, Variety, Sowing Date) to return to -- navigation
        // only (ApprovalListState): never read by the approval below; always empty when opened from Mother Plant & Cutting.
        [BindProperty(SupportsGet = true, Name = "category")] public string? ListCategory { get; set; }
        [BindProperty(SupportsGet = true, Name = "variety")] public int? ListVariety { get; set; }
        [BindProperty(SupportsGet = true, Name = "sowingDate")] public DateTime? ListSowingDate { get; set; }
        public ApprovalListState ListState => From == FromMotherPlant ? ApprovalListState.None
            : ApprovalListState.From(ListCategory, ListVariety, ListSowingDate);

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

        // 2026-10-31: "All Seedlings Damaged -- Approve with 0 Ready Seedlings" (AllSeedlingsDamagedRules). Same authority as the
        // ordinary approval; the reason is required; the server and the repository decide the numbers (never the form).
        [BindProperty]
        public bool AllSeedlingsDamaged { get; set; }

        [BindProperty]
        public string? DamageReason { get; set; }

        // Offered only once 2026-10-31_AllSeedlingsDamagedApproval.sql is applied (the repository re-checks it under lock).
        public bool AllSeedlingsDamagedAvailable { get; private set; }

        public async Task<IActionResult> OnGetAsync(int id)
        {
            var sowing = await _seedSowingRepo.GetByIdAsync(id);
            if (sowing == null)
            {
                TempData["Error"] = "Seed Sowing record not found.";
                return BackToList();
            }
            if (!_areaAccessService.CanAccessArea(User, sowing.AreaId))
            {
                TempData["Error"] = "You are not authorized to Ready-Confirm this Sowing.";
                return BackToList();
            }
            if (sowing.Status != "Sown")
            {
                TempData["Error"] = $"This Sowing is '{sowing.Status}' and cannot be Ready-Confirmed.";
                return BackToList();
            }
            if (sowing.RemainingReadyQuantity <= 0)
            {
                TempData["Error"] = "This sowing batch has already been fully approved.";
                return BackToList();
            }
            // Only the supervisor assigned to this sowing (and never its creator); a Bed Sowing batch only the
            // designated Bed Sowing approver (2026-10-16).
            var (mayApprove, authorityError) = await AuthorityAsync(sowing);
            if (!mayApprove)
            {
                TempData["Error"] = authorityError;
                return BackToList();
            }

            SeedSowing = sowing;
            SeedSowingId = sowing.Id;
            await LoadAsync(sowing);
            return Page();
        }

        private async Task LoadAsync(SeedSowingModel sowing)
        {
            LoadCuttingStock(sowing);
            AllSeedlingsDamagedAvailable = await _readyConfirmationRepo.AllSeedlingsDamagedAvailableAsync();
        }

        private async Task<IActionResult> RedisplayAsync(SeedSowingModel sowing, string error)
        {
            ModelState.AddModelError(string.Empty, error);
            SeedSowing = sowing;
            await LoadAsync(sowing);
            return Page();
        }

        // Only the supervisor assigned to this sowing (and never its creator); a Bed Sowing batch only the designated Bed Sowing
        // approver (2026-10-16); a Mother Plant Area Direct Sowing (2026-10-21) and a Transplant batch only an Area Supervisor of
        // its own Area or a System Administrator. Identity from the server-side principal; the repository re-checks under lock.
        private async Task<(bool Ok, string? Error)> AuthorityAsync(SeedSowingModel sowing)
        {
            Approval = await _readyConfirmationRepo.GetApprovalSettingsAsync();
            var me = User.GetUserId();
            var areaApprover = sowing.IsMotherPlantDirectSowing && me.HasValue && await _readyConfirmationRepo.IsAreaApproverAsync(me.Value, sowing.AreaId);
            var (mayApprove, authorityError) = DirectSowingRules.CanApprove(
                sowing.SupervisorId, sowing.CreatedById, sowing.CreatedBy, me, User.Identity?.Name, sowing.SourceType, Approval.Bed,
                sowing.IsMotherPlantDirectSowing, areaApprover, sowing.AreaName);
            return mayApprove ? await AreaApproverAsync(sowing) : (false, authorityError);
        }

        // 2026-10-17 (TransplantAreaScope): a Transplant tray batch only by an Area Supervisor of its own Area (or a System
        // Administrator) -- checked here against the database, again inside the approval transaction and by the trigger.
        private async Task<(bool Ok, string? Error)> AreaApproverAsync(SeedSowingModel sowing)
        {
            if (sowing.SourceType != SeedSowingModel.SourceTransplant || !Approval.TransplantAreaScope)
                return (true, null);
            var me = User.GetUserId();
            return me.HasValue && await _readyConfirmationRepo.IsAreaApproverAsync(me.Value, sowing.AreaId)
                ? (true, null)
                : (false, string.Format(ReadyConfirmationRepository.NotAreaSupervisorMessageFormat, sowing.AreaName));
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
                    MaxReadyTrays = PartialTrayRules.IsPartialTraySowing(sowing.SourceType, sowing.QuantitySown, sowing.CavityType)
                        ? 1   // 2026-10-31: one partial tray -- exactly 1, never an extra
                        : DirectSowingRules.MaxReadyTrays(sowing.RemainingReadyQuantity, sowing.CavityType, AvailableCuttingStock.Value);
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
                return BackToList();
            }
            if (!_areaAccessService.CanAccessArea(User, sowing.AreaId))
            {
                TempData["Error"] = "You are not authorized to Ready-Confirm this Sowing.";
                return BackToList();
            }

            // Approval authority (the repository re-applies it under lock).
            var (mayApprove, authorityError) = await AuthorityAsync(sowing);
            if (!mayApprove)
            {
                TempData["Error"] = authorityError;
                return BackToList();
            }

            var userIdClaim = User.FindFirst("UserId")?.Value;
            int? userId = int.TryParse(userIdClaim, out var parsedUserId) ? parsedUserId : null;
            var createdBy = User.Identity?.Name ?? "System";

            // 2026-10-31: All Seedlings Damaged -- 0 trays / 0 seedlings, wastage = everything still expected. Checked here
            // and again by the repository under the sowing's lock (and by the database); the ordinary path below is unchanged.
            if (AllSeedlingsDamaged)
            {
                var (damagedOk, damagedWastage, _, _, damagedError) = AllSeedlingsDamagedRules.Compute(
                    sowing.QuantitySown, sowing.ConfirmedReadyQuantity, sowing.WastageQuantity, DamageReason, Remarks);
                if (!damagedOk)
                    return await RedisplayAsync(sowing, damagedError!);
                var (dSuccess, dMessage, _) = await _readyConfirmationRepo.ConfirmAsync(
                    sowing.Id, 0, null, null, Remarks, createdBy, userId, CanUseSourceArea, allDamaged: true, damageReason: DamageReason);
                if (!dSuccess)
                    return await RedisplayAsync(sowing, dMessage ?? "Failed to record the Supervisor Approval.");
                TempData["Success"] = $"Batch {sowing.SowingCode} approved with 0 ready seedlings: all {QuantityFormat.Qty(damagedWastage)} "
                    + $"{sowing.UnitName.ToLowerInvariant()} recorded as damaged (wastage, reason '{AllSeedlingsDamagedRules.WastageReason}'). "
                    + "Nothing was added to Ready Stock. The sowing is now Completed.";
                return From == FromMotherPlant ? BackToList() : RedirectToPage("/Production/ReadyConfirmation/History", ListState.RouteWith("id", sowing.Id));
            }

            // Same rule the repository re-applies under lock (cavity and trays
            // from the stored sowing, never from the form).
            var (ok, _, seedlings, wastage, wastagePct, error) = DirectSowingRules.ComputeTrayApproval(
                sowing.QuantitySown, sowing.NumberOfTrays, sowing.CavityType, sowing.ConfirmedReadyQuantity, sowing.WastageQuantity,
                ActualReadyTrays, WastageReason, sowing.SourceType);
            if (!ok)
                return await RedisplayAsync(sowing, error!);

            // No generic "Responsible Person": the assigned Sowing Supervisor
            // (already checked above) is who is accountable for this approval.
            var (success, message, confirmationId) = await _readyConfirmationRepo.ConfirmAsync(
                sowing.Id, ActualReadyTrays, WastageReason, null, Remarks, createdBy, userId, CanUseSourceArea);

            if (!success)
                return await RedisplayAsync(sowing, message ?? "Failed to record the Supervisor Approval.");

            var partialTray = PartialTrayRules.IsPartialTraySowing(sowing.SourceType, sowing.QuantitySown, sowing.CavityType);
            var extra = DirectSowingRules.ExtraCuttingsNeeded(sowing.SourceType, ActualReadyTrays, sowing.CavityType, sowing.RemainingReadyQuantity, partialTray);
            TempData["Success"] = $"Batch {sowing.SowingCode} approved: "
                + (partialTray ? $"1 partial {sowing.CavityType} tray" : $"{QuantityFormat.Qty(ActualReadyTrays)} trays x {sowing.CavityType}")
                + $" = {QuantityFormat.Qty(seedlings)} seedlings added to Ready Stock; wastage {QuantityFormat.Qty(wastage)} ({wastagePct:0.00}%). The sowing is now Completed."
                + (extra > 0 ? $" {QuantityFormat.Qty(DirectSowingRules.ExtraTrays(extra, sowing.CavityType))} extra trays = {QuantityFormat.Qty(extra)} extra cuttings were taken from Cutting Stock."
                    + await _readyConfirmationRepo.GetExtraMaterialTextAsync(confirmationId) : "");
            return From == FromMotherPlant ? BackToList() : RedirectToPage("/Production/ReadyConfirmation/History", ListState.RouteWith("id", sowing.Id));
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
                trays, DirectSowingRules.WastageReasons[0], sowing.SourceType);

            // Cutting Tray Sowing: how many extra trays / extra cuttings this many TRAYS
            // would take from Cutting Stock, and whether the pool (in CUTTINGS) can cover
            // them (advisory only -- the save decides, under the pool's lock). Never
            // reported for seed sowings.
            var calculated = ok;   // the tray arithmetic itself; a stock problem below does not hide the numbers
            var extra = ok ? DirectSowingRules.ExtraCuttingsNeeded(sowing.SourceType, trays, sowing.CavityType, sowing.RemainingReadyQuantity,
                PartialTrayRules.IsPartialTraySowing(sowing.SourceType, sowing.QuantitySown, sowing.CavityType)) : 0m;
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
            decimal? maxTrays = !available.HasValue ? null
                : PartialTrayRules.IsPartialTraySowing(sowing.SourceType, sowing.QuantitySown, sowing.CavityType) ? 1
                : DirectSowingRules.MaxReadyTrays(sowing.RemainingReadyQuantity, sowing.CavityType, available.Value);
            return new JsonResult(new
            {
                ok, seedlings = calculated ? seedlings : (decimal?)null, wastage, wastagePercent = wastagePct,
                extraTrays, extraCuttings = extra, availableCuttings = available, maxReadyTrays = maxTrays, error
            });
        }

    }
}
