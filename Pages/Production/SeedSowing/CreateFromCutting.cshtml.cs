using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PlantStockManager.Authorization;
using PlantStockManager.Data;
using PlantStockManager.Models;
using PlantStockManager.Services;
using CuttingStockModel = PlantStockManager.Models.CuttingStock;
using SeedSowingModel = PlantStockManager.Models.SeedSowing;

namespace PlantStockManager.Pages.Production.SeedSowing
{
    // Cutting Tray Sowing: the Sowing Supervisor records the actual quantity,
    // Tray Cavity, destination Main Area / Polyhouse and Sowing Supervisor in
    // ONE confirmation screen (never the Mother Plant Supervisor -- their
    // involvement ends at Cutting Stock/GiveToMainOffice; this page is
    // Sowing.Enter only). The destination is REQUIRED: a real, active
    // Polyhouse (never Outlet, never inactive, never unassigned --
    // PolyhouseRepository.GetGrowingDestinationsAsync excludes those; a real
    // named Polyhouse filed under a Main Office-type Area IS a valid
    // destination), and the growing Area is always derived FROM the chosen
    // Polyhouse -- there is no separate Area dropdown.
    //   Complete Trays  = FLOOR(Cutting Quantity / Cavity)
    //   Used Cutting    = Complete Trays x Cavity   (leaves Cutting Stock, 'Sown')
    //   Remaining       = the rest                   (wasted automatically, 'Wastage' --
    //                                                  never left in or returned to Cutting Stock)
    // 2026-10-31: a Cutting Quantity under one complete tray (e.g. 62 at 150 Cavity) is ONE partial tray once
    // 2026-10-31_CuttingPartialTraySowing.sql is applied: a yellow warning, never an error -- 1 tray deducted, the
    // cuttings recorded as sown exactly (never rounded up), nothing wasted (PartialTrayRules).
    public class CreateFromCuttingModel : PageModel
    {
        private readonly SeedSowingRepository _seedSowingRepo;
        private readonly CuttingStockRepository _cuttingStockRepo;
        private readonly UserRoleRepository _userRoleRepo;
        private readonly PolyhouseRepository _polyhouseRepo;
        private readonly InternalTransferRepository _internalTransferRepo;
        private readonly AreaAccessService _areaAccess;
        private readonly MaterialStockRepository _materialRepo;
        private readonly TrayStockRepository _trayStockRepo;

        public CreateFromCuttingModel(SeedSowingRepository seedSowingRepo, CuttingStockRepository cuttingStockRepo,
            UserRoleRepository userRoleRepo, PolyhouseRepository polyhouseRepo, InternalTransferRepository internalTransferRepo,
            AreaAccessService areaAccess, MaterialStockRepository materialRepo, TrayStockRepository trayStockRepo)
        {
            _materialRepo = materialRepo;
            _trayStockRepo = trayStockRepo;
            _seedSowingRepo = seedSowingRepo;
            _cuttingStockRepo = cuttingStockRepo;
            _userRoleRepo = userRoleRepo;
            _polyhouseRepo = polyhouseRepo;
            _internalTransferRepo = internalTransferRepo;
            _areaAccess = areaAccess;
        }

        [BindProperty] public int CuttingStockId { get; set; }
        [BindProperty] public decimal CuttingQuantity { get; set; }
        [BindProperty] public string? CavityType { get; set; }
        [BindProperty] public DateTime SowingDate { get; set; } = DateTime.Today;
        [BindProperty] public int? PolyhouseId { get; set; }
        [BindProperty] public int? SupervisorId { get; set; }
        [BindProperty] public string? Remarks { get; set; }
        // Set only when this page was reached straight from Main Office
        // Confirm Receipt for ONE specific delivery (ConfirmReceipt.cshtml.cs's
        // redirect). When present, Cutting Quantity is anchored to THAT
        // delivery's own dbo.InternalTransfers.ConfirmedQuantity -- never the
        // pool's combined total, which could include other deliveries. Absent
        // (page reached via Cutting Stock's own "Confirm Sowing" button, not
        // tied to one delivery) falls back to the pool's Available Quantity.
        [BindProperty] public int? TransferId { get; set; }

        // Growing Material (Main Office Polyhouses): exactly one, deducted from the
        // destination Polyhouse. One-time form token: a repeated submit creates nothing twice.
        public const string MaterialField = "GrowingMaterialTypeId";
        [BindProperty] public int? GrowingMaterialTypeId { get; set; }
        [BindProperty] public Guid SubmissionToken { get; set; }
        public GrowingMaterialViewModel GrowingMaterial { get; set; } = new();

        public List<CuttingStockModel> StockPools { get; set; } = new();
        // The chosen Cutting Stock pool, for display in the anchored (one
        // specific delivery) view -- resolved directly, not from StockPools,
        // since a pool a delivery just fully consumed (Available = 0) is
        // correctly absent from that list but must still display here.
        public CuttingStockModel? SelectedStock { get; set; }
        // Every valid growing destination Polyhouse this user may sow into
        // (active, belongs to a real growing Area -- never Main Office /
        // Outlet / Area-less / inactive). Required: the page never offers "no
        // destination".
        public List<Polyhouse> Destinations { get; set; } = new();
        public List<Employee> Supervisors { get; set; } = new();
        public IReadOnlyList<string> CavityTypes => DirectSowingRules.CavityTypes;

        public async Task<IActionResult> OnGetAsync(int? cuttingStockId, int? transferId = null)
        {
            SubmissionToken = Guid.NewGuid();
            await LoadAsync();
            if (cuttingStockId.HasValue)
            {
                // StockPools (the generic "pick a variety" list) only ever
                // contains pools with Available > 0 -- correct for that picker,
                // but WRONG to gate a transfer-anchored view on: a delivery
                // that already consumed the pool down to zero (the normal
                // outcome of sowing it) must still resolve far enough to be
                // recognised as "already consumed" and redirected, not silently
                // fall through to an empty generic picker. So when a
                // transferId is present, the pool is read directly instead of
                // from the filtered list.
                var stock = StockPools.FirstOrDefault(s => s.Id == cuttingStockId)
                    ?? (transferId.HasValue ? await _cuttingStockRepo.GetByIdAsync(cuttingStockId.Value) : null);
                if (stock != null && CanUseSource(stock))
                {
                    CuttingStockId = cuttingStockId.Value;
                    SelectedStock = stock;
                    // Cutting Quantity is never typed. Anchored to the specific
                    // delivery when we were sent here for one (its own confirmed
                    // quantity); otherwise the chosen pool's Available Quantity.
                    // Display only -- OnPostAsync/InsertFromCuttingAsync re-derive
                    // this themselves and never trust a posted value.
                    if (transferId.HasValue)
                    {
                        var (match, quantity) = await MatchTransferAsync(transferId.Value, stock);
                        if (match == TransferMatch.AlreadyConsumed)
                        {
                            TempData["Error"] = "This delivery has already been used for a Cutting Sowing and cannot be sown again.";
                            return RedirectToPage("/Production/CuttingStock/Index");
                        }
                        if (match == TransferMatch.Available)
                        {
                            TransferId = transferId;
                            CuttingQuantity = quantity;
                        }
                        else
                        {
                            CuttingQuantity = stock.AvailableQuantity;
                        }
                    }
                    else
                    {
                        CuttingQuantity = stock.AvailableQuantity;
                    }
                }
            }
            await LoadSupervisorsAsync();
            return Page();
        }

        private enum TransferMatch { Unusable, AlreadyConsumed, Available }

        // Whether a specific, completed Cutting delivery may be used to
        // anchor this sowing's quantity: it must genuinely have landed in the
        // chosen pool (same destination Area and Species as the pool's own
        // source pool) -- a stale or tampered transferId can never substitute
        // an unrelated delivery's quantity -- AND must not already be
        // consumed by an earlier sowing (dbo.InternalTransfers.
        // ConsumedBySeedSowingId). This is the page-level check (fast,
        // friendly messages); InsertFromCuttingAsync repeats the same
        // decision under a row lock as the actual authority.
        private async Task<(TransferMatch Match, decimal Quantity)> MatchTransferAsync(int transferId, CuttingStockModel stock)
        {
            var transfer = await _internalTransferRepo.GetByIdAsync(transferId);
            if (transfer == null || transfer.StockType != "Cutting" || transfer.Status != "Completed"
                || !transfer.ConfirmedQuantity.HasValue || transfer.ConfirmedQuantity.Value <= 0
                || transfer.DestinationAreaId != stock.AreaId || !transfer.SourceCuttingStockId.HasValue)
                return (TransferMatch.Unusable, 0);
            var sourcePool = await _cuttingStockRepo.GetByIdAsync(transfer.SourceCuttingStockId.Value);
            if (sourcePool == null || sourcePool.SpeciesId != stock.SpeciesId)
                return (TransferMatch.Unusable, 0);
            if (transfer.ConsumedBySeedSowingId.HasValue)
                return (TransferMatch.AlreadyConsumed, 0);
            return (TransferMatch.Available, transfer.ConfirmedQuantity.Value);
        }

        // Sowing Supervisor list for the chosen destination Polyhouse's Area --
        // only when that Polyhouse is one of this user's valid destinations.
        public async Task<JsonResult> OnGetSupervisorsAsync(int polyhouseId)
        {
            var destination = (await LoadDestinationsAsync()).FirstOrDefault(p => p.Id == polyhouseId);
            if (destination == null)
                return new JsonResult(Array.Empty<object>());
            var list = await _userRoleRepo.GetCuttingSowingSupervisorsAsync(destination.AreaId!.Value);
            return new JsonResult(list.Select(u => new { id = u.EmployeeID, name = u.Name }));
        }

        // Growing Material preview for a destination Polyhouse this user may sow into
        // (its Area comes from the Polyhouse); physical trays = CEILING(quantity / cavity).
        public async Task<JsonResult> OnGetMaterialPreviewAsync(int polyhouseId, string? cavityType, decimal quantity)
        {
            var destination = (await LoadDestinationsAsync()).FirstOrDefault(p => p.Id == polyhouseId);
            if (destination == null)
                return new JsonResult(new { applicable = false, trays = 0, materials = Array.Empty<object>() });
            return new JsonResult(await GrowingMaterialForm.PreviewAsync(_materialRepo, destination.AreaId!.Value, destination.Id, cavityType, quantity));
        }

        // Empty trays of the cavity at every destination this user may sow into (the form's own list; a Mother Plant
        // Area's Polyhouses share its one Area pool row), the chosen one's balance, and the physical trays needed (CEILING).
        public async Task<JsonResult> OnGetTrayAvailabilityAsync(string? cavityType, int? polyhouseId, decimal quantity)
        {
            var eligible = (await LoadDestinationsAsync()).Select(p => new TrayLocation(p.AreaId!.Value, p.Id)).ToList();
            var selected = eligible.FirstOrDefault(l => l.PolyhouseId == polyhouseId);
            return new JsonResult(await TrayAvailabilityForm.BuildAsync(_trayStockRepo, cavityType, eligible, selected,
                DirectSowingRules.PhysicalTraysRequired(quantity, cavityType)));
        }

        // Live preview -- the same function the save uses. warning: the partial-tray notice (informational only).
        public async Task<JsonResult> OnGetTrayCalculationAsync(decimal quantity, string? cavityType)
        {
            var allowPartial = await _seedSowingRepo.CuttingPartialTrayAvailableAsync();
            var (ok, trays, used, remaining, error) = DirectSowingRules.CalculateTrays(quantity, cavityType, DirectSowingRules.CuttingQuantityLabel, allowPartial);
            return new JsonResult(new { ok, trays, used, remaining, wholeNumber = DirectSowingRules.IsWholeNumber(quantity), error,
                warning = ok ? PartialTrayWarning(quantity, cavityType) : null });
        }

        // The yellow notice for a quantity under one complete tray (null otherwise), shown again after a refused POST.
        public string? PartialTrayWarningText { get; private set; }
        private static string? PartialTrayWarning(decimal quantity, string? cavityType)
            => PartialTrayRules.IsUnderOneTray(quantity, cavityType) ? PartialTrayRules.SowingWarning(quantity, cavityType) : null;

        public async Task<IActionResult> OnPostAsync()
        {
            var stock = await _cuttingStockRepo.GetByIdAsync(CuttingStockId);
            SelectedStock = stock;
            if (stock == null || !CanUseSource(stock))
                ModelState.AddModelError(string.Empty, "Choose a Cutting Stock you can use.");
            else if (TransferId.HasValue)
            {
                // SECURITY: Cutting Quantity is never a user-editable value --
                // whatever the browser posted for it (including a tampered
                // value from dev tools) is discarded. The posted TransferId is
                // likewise never trusted as-is: it is re-resolved the same way
                // OnGetAsync does, against the live database, and only
                // accepted when it still genuinely matches this pool's own
                // species/Area AND has not already been used for a sowing.
                // (InsertFromCuttingAsync repeats this exact check again under
                // a row lock -- the real authority, race-safe; this is the
                // early, friendly rejection.)
                var (match, quantity) = await MatchTransferAsync(TransferId.Value, stock);
                if (match == TransferMatch.AlreadyConsumed)
                    ModelState.AddModelError(string.Empty, "This delivery has already been used for a Cutting Sowing and cannot be sown again.");
                else if (match == TransferMatch.Unusable)
                    ModelState.AddModelError(string.Empty, "The linked delivery no longer matches the selected Cutting Stock.");
                else
                    CuttingQuantity = quantity;
            }
            else
            {
                // No specific delivery behind this page view (reached via
                // Cutting Stock's own "Confirm Sowing" button): Cutting
                // Quantity is the pool's own Available Quantity, freshly read
                // for THIS specific (SpeciesId, AreaId) pool -- never a
                // generic Main Office total, never a client-trusted figure.
                CuttingQuantity = stock.AvailableQuantity;
            }

            if (!DirectSowingRules.IsValidCavityType(CavityType))
                ModelState.AddModelError(string.Empty, $"Tray size must be one of: {string.Join(", ", DirectSowingRules.CavityTypes)}.");
            else if (stock != null)
            {
                var (ok, _, _, _, _, error) = DirectSowingRules.PlanSowing(CuttingQuantity, CavityType, stock.PhysicalQuantity, stock.InTransitQuantity,
                    DirectSowingRules.CuttingQuantityLabel, "Cutting Stock", await _seedSowingRepo.CuttingPartialTrayAvailableAsync());
                if (!ok)
                    ModelState.AddModelError(string.Empty, error!);
                else
                    PartialTrayWarningText = PartialTrayWarning(CuttingQuantity, CavityType);
            }

            // Destination: a required, real growing Polyhouse (never Main Office / Outlet),
            // re-loaded from the database -- the posted PolyhouseId is never trusted as-is.
            var destinations = await LoadDestinationsAsync();
            var destination = destinations.FirstOrDefault(p => p.Id == PolyhouseId);
            if (PolyhouseId is not > 0)
                ModelState.AddModelError(string.Empty, CuttingSowingDestinationRules.PolyhouseRequiredMessage);
            else if (destination == null)
                ModelState.AddModelError(string.Empty, CuttingSowingDestinationRules.InvalidDestinationMessage);

            var materialPostError = GrowingMaterialForm.ValidatePosted(Request, MaterialField);
            if (materialPostError != null)
                ModelState.AddModelError(string.Empty, materialPostError);
            if (SubmissionToken == Guid.Empty)
                ModelState.AddModelError(string.Empty, "This form has expired. Reload the page and confirm the sowing again.");

            if (!SupervisorId.HasValue || SupervisorId.Value <= 0)
                ModelState.AddModelError(string.Empty, CuttingSowingDestinationRules.SupervisorRequiredMessage);
            else if (destination != null)
            {
                var eligible = (await _userRoleRepo.GetCuttingSowingSupervisorsAsync(destination.AreaId!.Value)).Select(e => e.EmployeeID).ToList();
                var (supervisorOk, supervisorError) = SowingSupervisorRules.ValidateAssignment(SupervisorId, eligible);
                if (!supervisorOk)
                    ModelState.AddModelError(string.Empty, supervisorError!);
            }

            if (!ModelState.IsValid)
            {
                await LoadAsync();
                await LoadSupervisorsAsync();
                return Page();
            }

            var sowing = new SeedSowingModel
            {
                SourceCuttingStockId = CuttingStockId,
                SpeciesId = stock!.SpeciesId,
                SeedQuantity = CuttingQuantity,
                CavityType = CavityType!,
                SowingDate = SowingDate,
                AreaId = destination!.AreaId!.Value,
                PolyhouseId = destination.Id,
                SupervisorId = SupervisorId,
                Remarks = Remarks,
                CreatedBy = User.Identity?.Name ?? "System",
                CreatedById = User.GetUserId(),
                GrowingMaterialTypeId = GrowingMaterialTypeId,
                SubmissionToken = SubmissionToken
            };
            // Area isolation: the repository re-checks both the growing Area and the pool's own Area under its lock,
            // and re-validates the destination is a real growing Area (never Main Office / Outlet).
            var (success, message, _) = await _seedSowingRepo.InsertFromCuttingAsync(sowing, User.GetUserId(),
                areaId => _areaAccess.CanAccessArea(User, areaId),
                areaId => CuttingRules.CanUseAsSource(_areaAccess.CanAccessArea(User, areaId)),
                TransferId);
            if (!success)
            {
                ModelState.AddModelError(string.Empty, message ?? "Failed to record the cutting tray sowing.");
                await LoadAsync();
                await LoadSupervisorsAsync();
                return Page();
            }

            if (sowing.IsDuplicateSubmission)
            {
                TempData["Success"] = $"Tray sowing {sowing.SowingCode} was already saved from this form -- nothing was recorded twice.";
                return RedirectToPage("/Production/SeedSowing/Details", new { id = sowing.Id });
            }

            var cuttingRemainder = sowing.SeedQuantity - sowing.QuantitySown;
            var partialTray = PartialTrayRules.IsPartialTraySowing(SeedSowingModel.SourceCutting, sowing.QuantitySown, sowing.CavityType);
            TempData["Success"] = $"Tray sowing {sowing.SowingCode} recorded: "
                + (partialTray
                    ? $"1 partial {sowing.CavityType} tray (less than one complete tray), {QuantityFormat.Qty(sowing.QuantitySown)} cuttings recorded as sown"
                    : $"{QuantityFormat.Qty(sowing.NumberOfTrays)} complete trays, {QuantityFormat.Qty(sowing.QuantitySown)} cuttings used")
                + (cuttingRemainder > 0 ? $"; {QuantityFormat.Qty(cuttingRemainder)} remaining cuttings recorded as waste (never returned to Cutting Stock)." : ".")
                + $" Expected ready: {sowing.ExpectedReadyDate:dd-MM-yyyy}."
                + await CreateModel.MaterialUsedTextAsync(_materialRepo, sowing.Id);
            return RedirectToPage("/Production/SeedSowing/Details", new { id = sowing.Id });
        }

        // Only cuttings held in an Area the user may access (Main Office stock is the Main Office Area's stock).
        private bool CanUseSource(CuttingStockModel s)
            => CuttingRules.CanUseAsSource(_areaAccess.CanAccessArea(User, s.AreaId));

        // Every valid growing destination (GetGrowingDestinationsAsync already excludes
        // Main Office / Outlet / Area-less / inactive) that THIS user may also sow into.
        private async Task<List<Polyhouse>> LoadDestinationsAsync()
            => (await _polyhouseRepo.GetGrowingDestinationsAsync())
                .Where(p => _areaAccess.CanAccessArea(User, p.AreaId!.Value))
                .ToList();

        private async Task LoadAsync()
        {
            StockPools = (await _cuttingStockRepo.GetAllAsync())
                .Where(s => s.AvailableQuantity > 0 && CanUseSource(s))
                .OrderBy(s => s.SpeciesName).ThenBy(s => s.AreaName)
                .ToList();
            Destinations = await LoadDestinationsAsync();
            GrowingMaterial = new GrowingMaterialViewModel { Materials = await _materialRepo.GetTypesAsync(), SelectedId = GrowingMaterialTypeId, FieldName = MaterialField };
        }

        // The supervisors of the currently chosen destination Polyhouse (used to
        // draw the list on first load and after a failed save, so the chosen
        // person stays selected). Empty until a Polyhouse is chosen.
        private async Task LoadSupervisorsAsync()
        {
            var destination = PolyhouseId is > 0 ? (await LoadDestinationsAsync()).FirstOrDefault(p => p.Id == PolyhouseId) : null;
            Supervisors = destination != null
                ? await _userRoleRepo.GetCuttingSowingSupervisorsAsync(destination.AreaId!.Value)
                : new List<Employee>();
        }
    }
}
