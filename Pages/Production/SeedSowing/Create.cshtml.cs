using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PlantStockManager.Authorization;
using PlantStockManager.Data;
using PlantStockManager.Models;
using PlantStockManager.Services;
using SeedSowingModel = PlantStockManager.Models.SeedSowing;

namespace PlantStockManager.Pages.Production.SeedSowing
{
    // Phase B -- DIRECT SOWING (no Seed Issue step).
    //
    // The user picks: Sowing Date, Polyhouse, Supervisor, Species -> Variety -> Main Office seed lot, Seed Quantity,
    // Cavity, Tray Count. The seed is consumed directly from Main Office
    // Seed Stock inside one locked transaction (SeedSowingRepository), which
    // also generates the batch number (YYYY-MM-DD-L-NNN) and the Expected
    // Ready Date (Sowing Date + the variety's growing days).
    //
    // Page access: "Sowing.Enter" (FeatureAuthorizationConventions).
    // Location (2026-10-08): Direct Seed Sowing is done ONLY in the Main Areas
    // (AreaType MainOffice). There is no Area field: the user picks a Polyhouse
    // of a Main Area they may work in, and the Area is that Polyhouse's own Area
    // from the database (shown read-only). A posted AreaId is never trusted --
    // one that disagrees with the Polyhouse is refused, here and again inside
    // SeedSowingRepository.InsertAsync (DirectSowingRules.ResolveDirectSowingArea).
    // Area scope: the user must be allowed to work in that Area
    // (SeedlingAreaScope) -- checked on every request. Main Office seed is central stock, so the lot itself is not
    // Area-scoped to the operator.
    public class CreateModel : PageModel
    {
        private readonly SeedSowingRepository _seedSowingRepo;
        private readonly SeedStockRepository _seedStockRepo;
        private readonly EmployeeRepository _employeeRepo;
        private readonly AreaRepository _areaRepo;
        private readonly PolyhouseRepository _polyhouseRepo;
        private readonly PlantTypeRepository _plantTypeRepo;
        private readonly PlantSpeciesRepository _plantSpeciesRepo;
        private readonly SeedlingAreaScope _areaAccessService; // seedling-only Area scope (Authorization/SeedlingAreaScope.cs)
        private readonly MaterialStockRepository _materialRepo;
        private readonly TrayStockRepository _trayStockRepo;

        public CreateModel(
            SeedSowingRepository seedSowingRepo,
            SeedStockRepository seedStockRepo,
            EmployeeRepository employeeRepo,
            AreaRepository areaRepo,
            PolyhouseRepository polyhouseRepo,
            PlantTypeRepository plantTypeRepo,
            PlantSpeciesRepository plantSpeciesRepo,
            SeedlingAreaScope areaAccessService,
            UserRoleRepository userRoleRepo,
            MaterialStockRepository materialRepo,
            TrayStockRepository trayStockRepo)
        {
            _materialRepo = materialRepo;
            _trayStockRepo = trayStockRepo;
            _userRoleRepo = userRoleRepo;
            _seedSowingRepo = seedSowingRepo;
            _seedStockRepo = seedStockRepo;
            _employeeRepo = employeeRepo;
            _areaRepo = areaRepo;
            _polyhouseRepo = polyhouseRepo;
            _plantTypeRepo = plantTypeRepo;
            _plantSpeciesRepo = plantSpeciesRepo;
            _areaAccessService = areaAccessService;
        }

        [BindProperty]
        public SeedSowingModel SeedSowing { get; set; } = new();

        // Species (dbo.PlantTypes) -> Variety (dbo.PlantSpecies).
        [BindProperty]
        public int PlantTypeId { get; set; }

        private readonly UserRoleRepository _userRoleRepo;

        // Main Area Polyhouses this user may sow in (each carries its AreaId / AreaName).
        public List<Polyhouse> Polyhouses { get; set; } = new();
        // The Main Area shown read-only: the selected Polyhouse's Area, or the only
        // Main Area offered. Display only -- never posted.
        public string? MainAreaName { get; set; }
        public List<PlantType> PlantTypes { get; set; } = new();
        public List<Employee> Supervisors { get; set; } = new();
        public IReadOnlyList<string> CavityTypes => DirectSowingRules.CavityTypes;
        public GrowingMaterialViewModel GrowingMaterial { get; set; } = new();

        public async Task OnGetAsync()
        {
            SeedSowing.SowingDate = DateTime.Today;
            SeedSowing.SubmissionToken = Guid.NewGuid();   // one-time token: a repeated submit creates nothing twice
            await LoadDropdownsAsync();
        }

        // Growing Material preview for the chosen Polyhouse, tray size and seed
        // quantity (physical trays = CEILING(quantity / cavity), as the save uses).
        // The Area is the Polyhouse's own (database), never a request value.
        public async Task<JsonResult> OnGetMaterialPreviewAsync(int polyhouseId, string? cavityType, decimal quantity)
        {
            var polyhouse = (await LoadPolyhousesAsync()).FirstOrDefault(p => p.Id == polyhouseId);
            if (polyhouse?.AreaId is not > 0)
                return new JsonResult(new { applicable = false, trays = 0, materials = Array.Empty<object>() });
            return new JsonResult(await GrowingMaterialForm.PreviewAsync(_materialRepo, polyhouse.AreaId.Value, polyhouse.Id, cavityType, quantity));
        }

        // ---- AJAX lookups (GET, read-only) -------------------------------

        // Empty trays of the cavity in every Main Office Polyhouse this user may sow in (the same list the form
        // offers), the chosen one's balance, and the physical trays this sowing takes (CEILING, as the save).
        public async Task<JsonResult> OnGetTrayAvailabilityAsync(string? cavityType, int? polyhouseId, decimal quantity)
        {
            var eligible = (await LoadPolyhousesAsync()).Select(p => new TrayLocation(p.AreaId!.Value, p.Id)).ToList();
            var selected = eligible.FirstOrDefault(l => l.PolyhouseId == polyhouseId);
            return new JsonResult(await TrayAvailabilityForm.BuildAsync(_trayStockRepo, cavityType, eligible, selected,
                DirectSowingRules.PhysicalTraysRequired(quantity, cavityType)));
        }

        // Live tray calculation for the form. Uses the SAME server function as
        // the save (DirectSowingRules.CalculateTrays), so the preview and the
        // stored value can never disagree; the save recalculates regardless.
        public JsonResult OnGetTrayCalculation(decimal quantity, string? cavityType)
        {
            var (ok, trays, seedsUsed, remaining, error) = DirectSowingRules.CalculateTrays(quantity, cavityType);
            return new JsonResult(new
            {
                ok,
                trays,
                seedsUsed,
                remainingSeeds = remaining,
                wholeNumber = DirectSowingRules.IsWholeNumber(quantity),
                error
            });
        }

        // Varieties of a Species, with their growing days.
        public async Task<JsonResult> OnGetVarietiesAsync(int plantTypeId)
        {
            var list = await _plantSpeciesRepo.GetSpeciesByPlantType(plantTypeId);
            return new JsonResult(list.Select(v => new { id = v.Id, name = v.Name.Trim(), growingDays = v.ReadyStockDays }));
        }

        // Phase D: find a seed lot by typing part of the variety, crop,
        // colour, supplier or lot number (1,000+ varieties -- no giant list).
        public async Task<JsonResult> OnGetFindLotsAsync(string? term)
        {
            if (string.IsNullOrWhiteSpace(term) || term.Trim().Length < 2)
                return new JsonResult(Array.Empty<object>());
            var list = await _seedStockRepo.SearchAsync(term, onlyAvailable: true, mainOfficeOnly: true, max: 25);
            var typeIds = (await _plantSpeciesRepo.GetAllAsync()).ToDictionary(v => v.Id, v => v.PlantTypeId);
            return new JsonResult(list.Select(s => new
            {
                id = s.Id,
                speciesId = s.SpeciesId,
                plantTypeId = typeIds.TryGetValue(s.SpeciesId, out var t) ? t : 0,
                variety = (s.SpeciesName ?? "").Trim(),
                crop = s.PlantTypeName,
                lot = string.IsNullOrEmpty(s.BatchNo) ? "(no lot no.)" : s.BatchNo,
                source = s.SeedSourceName,
                available = s.AvailableQuantity
            }));
        }

        // Main Office seed lots of a Variety that still have stock.
        public async Task<JsonResult> OnGetSeedLotsAsync(int speciesId)
        {
            var list = (await _seedStockRepo.GetAllAsync())
                .Where(s => s.SpeciesId == speciesId
                            && s.AvailableQuantity > 0
                            && DirectSowingRules.IsMainOfficeSeedLocation(s.AreaType, true))
                .OrderBy(s => s.CreatedDate);
            return new JsonResult(list.Select(s => new
            {
                id = s.Id,
                lot = string.IsNullOrEmpty(s.BatchNo) ? "(no lot no.)" : s.BatchNo,
                source = s.SeedSourceName,
                area = s.AreaName,
                available = s.AvailableQuantity,
                unit = s.Unit
            }));
        }

        public async Task<IActionResult> OnPostAsync()
        {
            ModelState.Clear(); // validated explicitly below (server-derived fields are never trusted)

            if (SeedSowing.SowingDate == default)
                ModelState.AddModelError(string.Empty, "Sowing Date is required.");
            // Polyhouse is required: the sowing's physical trays come from that
            // Polyhouse's Tray Stock. The Area is NOT an input: it is derived from
            // the Polyhouse below (and again in SeedSowingRepository.InsertAsync).
            if (SeedSowing.PolyhouseId is not > 0)
                ModelState.AddModelError(string.Empty, PlantStockManager.Data.TrayStockRepository.PolyhouseRequiredMessage);
            if (SeedSowing.SpeciesId <= 0)
                ModelState.AddModelError(string.Empty, "Variety is required.");
            if (SeedSowing.SourceSeedStockId <= 0)
                ModelState.AddModelError(string.Empty, "Main Office seed lot is required.");
            if (SeedSowing.SeedQuantity <= 0)
                ModelState.AddModelError(string.Empty, "Seed Quantity must be greater than zero.");
            else if (!DirectSowingRules.IsWholeNumber(SeedSowing.SeedQuantity))
                ModelState.AddModelError(string.Empty, "Seed Quantity must be a whole number.");
            // Trays are never taken from the browser: FLOOR(SeedQuantity / TraySize).
            SeedSowing.NumberOfTrays = null;
            // Sown quantity is never taken from the browser either: the
            // repository sets it to the seeds used in complete trays.
            SeedSowing.QuantitySown = 0;
            if (SeedSowing.SeedQuantity > 0)
            {
                var (traysOk, _, _, _, trayError) = DirectSowingRules.CalculateTrays(SeedSowing.SeedQuantity, SeedSowing.CavityType);
                if (!traysOk)
                    ModelState.AddModelError(string.Empty, trayError!);
            }
            else if (!DirectSowingRules.IsValidCavityType(SeedSowing.CavityType))
                ModelState.AddModelError(string.Empty, $"Tray size must be one of: {string.Join(", ", DirectSowingRules.CavityTypes)}.");

            // Growing material: one radio choice (two posted values = a tampered form).
            // Whether one is REQUIRED depends on the growing Area -- decided by the
            // repository under its transaction (Main Office Polyhouses only).
            var materialPostError = GrowingMaterialForm.ValidatePosted(Request);
            if (materialPostError != null)
                ModelState.AddModelError(string.Empty, materialPostError);
            if (SeedSowing.SubmissionToken is not { } token || token == Guid.Empty)
                ModelState.AddModelError(string.Empty, "This form has expired. Reload the page and enter the sowing again.");

            // The supervisor who will approve this batch (re-checked in the
            // repository inside the save transaction).
            var approverIds = (await _userRoleRepo.GetSowingApproversAsync()).Select(a => a.EmployeeID).ToList();
            var (supervisorOk, supervisorError) = DirectSowingRules.ValidateSupervisorAssignment(
                SeedSowing.SupervisorId, User.GetUserId(), approverIds);
            if (!supervisorOk)
                ModelState.AddModelError(string.Empty, supervisorError!);

            // Growing location: the Area comes from the selected Polyhouse in the
            // database. The Polyhouse must belong to an active Main Area; a posted
            // AreaId (the form has none) that disagrees with it is refused; then
            // the user must be allowed to work in that Area. The repository
            // re-checks all of this inside the save transaction.
            if (SeedSowing.PolyhouseId is > 0)
            {
                var polyhouse = await _polyhouseRepo.GetByIdAsync(SeedSowing.PolyhouseId.Value);
                var area = polyhouse?.AreaId is > 0 ? await _areaRepo.GetAreaById(polyhouse.AreaId.Value) : null;
                var (locationOk, derivedAreaId, locationError) = DirectSowingRules.ResolveDirectSowingArea(
                    SeedSowing.AreaId, SeedSowing.PolyhouseId, polyhouse != null, polyhouse?.AreaId, area?.AreaType, area?.IsActive == true);
                if (!locationOk)
                    ModelState.AddModelError(string.Empty, locationError!);
                else if (!_areaAccessService.CanAccessArea(User, derivedAreaId))
                    ModelState.AddModelError(string.Empty, "You are not authorized to sow in the selected Polyhouse's Area.");
                else
                    SeedSowing.AreaId = derivedAreaId;
            }

            if (!ModelState.IsValid)
            {
                await LoadDropdownsAsync();
                return Page();
            }

            SeedSowing.CreatedBy = User.Identity?.Name ?? "System";
            var userId = User.GetUserId();
            SeedSowing.CreatedById = userId;   // used by the self-approval rule
            SeedSowing.ResponsiblePersonId = null;

            var (success, message, _) = await _seedSowingRepo.InsertAsync(SeedSowing, userId,
                areaId => _areaAccessService.CanAccessArea(User, areaId));   // re-checked on the resolved growing Area
            if (!success)
            {
                ModelState.AddModelError(string.Empty, message ?? "Failed to record Sowing.");
                await LoadDropdownsAsync();
                return Page();
            }
            if (SeedSowing.IsDuplicateSubmission)
            {
                TempData["Success"] = $"Sowing batch {SeedSowing.SowingCode} was already saved from this form -- nothing was recorded twice.";
                return RedirectToPage("/Production/SeedSowing/Details", new { id = SeedSowing.Id });
            }

            // The sub-tray remainder is recorded as 'Wastage' on the seed lot by
            // SeedSowingRepository.InsertAsync (step 6b) -- it never stays in the lot.
            var seedRemainder = SeedSowing.SeedQuantity - SeedSowing.QuantitySown;
            TempData["Success"] = $"Sowing batch {SeedSowing.SowingCode} recorded: {QuantityFormat.Qty(SeedSowing.NumberOfTrays)} complete trays, {QuantityFormat.Qty(SeedSowing.QuantitySown)} seeds used (deducted from the lot)"
                + (seedRemainder > 0 ? $"; {QuantityFormat.Qty(seedRemainder)} remaining seeds recorded as waste (not returned to the lot)." : ".")
                + $" Expected ready: {SeedSowing.ExpectedReadyDate:dd-MM-yyyy}."
                + await MaterialUsedTextAsync(_materialRepo, SeedSowing.Id);
            return RedirectToPage("/Production/SeedSowing/Details", new { id = SeedSowing.Id });
        }

        // " Cocopeat used: 150.00 kg from Facility-3." -- read back from the ledger.
        internal static async Task<string> MaterialUsedTextAsync(MaterialStockRepository repo, int seedSowingId)
        {
            var used = (await repo.GetForSowingAsync(seedSowingId)).FirstOrDefault(t => t.TransactionType == MaterialStockRules.Consume);
            return used == null ? "" : $" {used.MaterialName} used: {MaterialStockRules.Kg(used.StockQuantity, used.Unit)} from {used.PolyhouseName}.";
        }

        private async Task LoadDropdownsAsync()
        {
            // Offered: Polyhouses of the active Main Areas this user may work in.
            Polyhouses = await LoadPolyhousesAsync();
            var areaNames = Polyhouses.Select(p => p.AreaName).Distinct().ToList();
            MainAreaName = Polyhouses.FirstOrDefault(p => p.Id == SeedSowing.PolyhouseId)?.AreaName
                           ?? (areaNames.Count == 1 ? areaNames[0] : null);

            PlantTypes = await _plantTypeRepo.GetAllPlantTypes();
            // Supervisor = who will approve: Sowing Supervisors only, never
            // the person recording this sowing.
            var me = User.GetUserId();
            Supervisors = (await _userRoleRepo.GetSowingApproversAsync()).Where(a => a.EmployeeID != me).ToList();
            GrowingMaterial = new GrowingMaterialViewModel { Materials = await _materialRepo.GetTypesAsync(), SelectedId = SeedSowing.GrowingMaterialTypeId };
        }

        private async Task<List<Polyhouse>> LoadPolyhousesAsync()
            => (await _polyhouseRepo.GetDirectSowingPolyhousesAsync())
                .Where(p => p.AreaId is > 0 && _areaAccessService.CanAccessArea(User, p.AreaId))
                .ToList();
    }
}
