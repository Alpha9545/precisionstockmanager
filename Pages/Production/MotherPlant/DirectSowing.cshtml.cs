using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PlantStockManager.Authorization;
using PlantStockManager.Data;
using PlantStockManager.Models;
using PlantStockManager.Services;
using SeedSowingModel = PlantStockManager.Models.SeedSowing;

namespace PlantStockManager.Pages.Production.MotherPlant
{
    // 2026-10-21 (owner requirement 2026-10-13): Mother Plant & Cutting > DIRECT SOWING -- a Direct (seed) Sowing INTO a
    // Mother Plant Area, reusing the Seed Production Direct Sowing (SeedSowing/Create) rules and its one transaction
    // (SeedSowingRepository.InsertMotherPlantDirectAsync):
    //   * seed from a Main Office seed lot (central stock, locked; whole seeds; complete trays = FLOOR(quantity / cavity);
    //     the sub-tray remainder wasted automatically); Ready Date = Sowing Date + the variety's growing days;
    //   * the user chooses the Mother Plant Area -- only active Mother Plant Areas they may access (AreaAccessService, the
    //     regular Area rule, always enforced, as all Mother Plant work); no Polyhouse, no supervisor and no growing material
    //     are taken from the request (any such posted field is refused as a forged form);
    //   * empty trays (CEILING(quantity / cavity)) from THAT Area's own tray pool, under the pool lock;
    //   * the batch is 'Sown' (pending) -- nothing reaches Ready Stock until an Area Supervisor of the Area (or a System
    //     Administrator) approves it under Sowing Approvals (ReadyConfirmation/Confirm -> ReadyConfirmationRepository.ConfirmAsync).
    // Page access: MotherPlant.Enter (FeatureAuthorizationConventions). Needs 2026-10-21_MotherPlantDirectSowing.sql.
    public class DirectSowingModel : PageModel
    {
        private readonly SeedSowingRepository _seedSowingRepo;
        private readonly SeedStockRepository _seedStockRepo;
        private readonly AreaRepository _areaRepo;
        private readonly PlantTypeRepository _plantTypeRepo;
        private readonly PlantSpeciesRepository _plantSpeciesRepo;
        private readonly AreaAccessService _areaAccess;
        private readonly TrayStockRepository _trayStockRepo;
        private readonly DatabaseHelper _db;

        public DirectSowingModel(SeedSowingRepository seedSowingRepo, SeedStockRepository seedStockRepo, AreaRepository areaRepo,
            PlantTypeRepository plantTypeRepo, PlantSpeciesRepository plantSpeciesRepo, AreaAccessService areaAccess,
            TrayStockRepository trayStockRepo, DatabaseHelper db)
        {
            _seedSowingRepo = seedSowingRepo;
            _seedStockRepo = seedStockRepo;
            _areaRepo = areaRepo;
            _plantTypeRepo = plantTypeRepo;
            _plantSpeciesRepo = plantSpeciesRepo;
            _areaAccess = areaAccess;
            _trayStockRepo = trayStockRepo;
            _db = db;
        }

        // The ONLY bound values. No Polyhouse, supervisor, growing material, creator or user id is bound.
        [BindProperty] public int? AreaId { get; set; }
        [BindProperty] public int PlantTypeId { get; set; }                   // Species (dbo.PlantTypes) -> Variety
        [BindProperty] public int SpeciesId { get; set; }
        [BindProperty] public int SourceSeedStockId { get; set; }
        [BindProperty] public decimal SeedQuantity { get; set; }
        [BindProperty] public string? CavityType { get; set; }
        [BindProperty] public DateTime SowingDate { get; set; }
        [BindProperty] public string? Remarks { get; set; }
        [BindProperty] public Guid SubmissionToken { get; set; }              // one-time token: a repeated submit creates nothing twice

        public bool SchemaReady { get; private set; }
        public List<Area> Areas { get; private set; } = new();               // active Mother Plant Areas this user may access
        public List<PlantType> PlantTypes { get; private set; } = new();
        public IReadOnlyList<string> CavityTypes { get; private set; } = DirectSowingRules.CavityTypes;
        // The Direct Sowings of the user's Mother Plant Areas still waiting for approval (display).
        public List<SeedSowingModel> Pending { get; private set; } = new();

        // Request fields this form never has: posted anyway = a forged form, refused (never silently ignored).
        internal static readonly string[] ForbiddenFields =
        {
            "PolyhouseId", "SeedSowing.PolyhouseId", "SupervisorId", "SeedSowing.SupervisorId", "GrowingMaterialTypeId",
            "SeedSowing.GrowingMaterialTypeId", "SeedSowing.AreaId", "CreatedById", "SeedSowing.CreatedById", "UserId", "ApproverId",
        };
        public const string ForgedFieldMessage =
            "This form takes only the Mother Plant Area: the trays come from that Area, and an Area Supervisor of the Area approves the batch. Reload the page and enter the sowing again.";

        public async Task OnGetAsync()
        {
            SowingDate = AppClock.Today;
            SubmissionToken = Guid.NewGuid();
            await LoadAsync();
            if (Areas.Count == 1)
                AreaId = Areas[0].Id;
        }

        public async Task<IActionResult> OnPostAsync()
        {
            ModelState.Clear();   // validated explicitly below
            await LoadAsync();
            if (!SchemaReady)
                ModelState.AddModelError(string.Empty, WorkflowCorrectionsSchema.MotherPlantDirectSowingMigrationMessage);
            if (Request.HasFormContentType && ForbiddenFields.Any(f => Request.Form.ContainsKey(f)))
                ModelState.AddModelError(string.Empty, ForgedFieldMessage);
            var userId = User.GetUserId();
            if (!userId.HasValue)
                ModelState.AddModelError(string.Empty, "Your user could not be identified.");

            if (SowingDate == default)
                ModelState.AddModelError(nameof(SowingDate), "Sowing Date is required.");
            else if (SowingDate.Date > AppClock.Today)
                ModelState.AddModelError(nameof(SowingDate), "The Sowing Date cannot be in the future.");
            // The Area must be one this page offers (active Mother Plant Area + this user's Area access); the repository
            // re-reads it and re-checks the access inside the save transaction.
            if (AreaId is not > 0)
                ModelState.AddModelError(nameof(AreaId), DirectSowingRules.MotherPlantDirectAreaRequiredMessage);
            else if (!Areas.Any(a => a.Id == AreaId.Value))
                ModelState.AddModelError(nameof(AreaId), DirectSowingRules.MotherPlantDirectNotAuthorizedMessage);
            if (SpeciesId <= 0)
                ModelState.AddModelError(nameof(SpeciesId), "Variety is required.");
            if (SourceSeedStockId <= 0)
                ModelState.AddModelError(nameof(SourceSeedStockId), "Main Office seed lot is required.");
            if (SeedQuantity <= 0)
                ModelState.AddModelError(nameof(SeedQuantity), "Seed Quantity must be greater than zero.");
            else if (!DirectSowingRules.IsWholeNumber(SeedQuantity))
                ModelState.AddModelError(nameof(SeedQuantity), "Seed Quantity must be a whole number.");
            if (SeedQuantity > 0)
            {
                var (traysOk, _, _, _, trayError) = DirectSowingRules.CalculateTrays(SeedQuantity, CavityType);
                if (!traysOk)
                    ModelState.AddModelError(nameof(CavityType), trayError!);
            }
            else if (!DirectSowingRules.IsValidCavityType(CavityType))
                ModelState.AddModelError(nameof(CavityType), $"Tray size must be one of: {string.Join(", ", DirectSowingRules.CavityTypes)}.");
            if (DirectSowingRules.IsValidCavityType(CavityType) && !CavityTypes.Contains(CavityType!))
                ModelState.AddModelError(nameof(CavityType), WorkflowCorrectionsSchema.Cavity70MigrationMessage);
            if (SubmissionToken == Guid.Empty)
                ModelState.AddModelError(string.Empty, "This form has expired. Reload the page and enter the sowing again.");
            if (Remarks?.Trim().Length > 500)
                ModelState.AddModelError(nameof(Remarks), "Remarks can be at most 500 characters.");

            if (!ModelState.IsValid)
                return Page();

            var entry = new SeedSowingModel
            {
                AreaId = AreaId!.Value,
                SpeciesId = SpeciesId,
                SourceSeedStockId = SourceSeedStockId,
                SeedQuantity = SeedQuantity,
                CavityType = CavityType!,
                SowingDate = SowingDate.Date,
                Remarks = string.IsNullOrWhiteSpace(Remarks) ? null : Remarks.Trim(),
                SubmissionToken = SubmissionToken,
                CreatedBy = User.Identity?.Name ?? "System",
                CreatedById = userId,   // from the server-side principal only
            };
            var (success, message, _) = await _seedSowingRepo.InsertMotherPlantDirectAsync(entry, userId,
                areaId => _areaAccess.CanAccessArea(User, areaId));   // re-checked on the re-read Area, inside the transaction
            if (!success)
            {
                ModelState.AddModelError(string.Empty, message ?? "Failed to record the sowing.");
                return Page();
            }

            var areaName = Areas.First(a => a.Id == entry.AreaId).Name;
            if (entry.IsDuplicateSubmission)
                TempData["Success"] = $"Sowing batch {entry.SowingCode} was already saved from this form -- nothing was recorded twice.";
            else
            {
                var remainder = entry.SeedQuantity - entry.QuantitySown;
                TempData["Success"] = $"Direct Sowing batch {entry.SowingCode} recorded in {areaName}: {QuantityFormat.Qty(entry.NumberOfTrays)} complete trays, "
                    + $"{QuantityFormat.Qty(entry.QuantitySown)} seeds used (deducted from the Main Office lot), "
                    + $"{QuantityFormat.Qty(DirectSowingRules.PhysicalTraysRequired(entry.SeedQuantity, entry.CavityType))} empty {entry.CavityType} trays taken from {areaName}"
                    + (remainder > 0 ? $"; {QuantityFormat.Qty(remainder)} remaining seeds recorded as waste." : ".")
                    + $" Expected ready: {entry.ExpectedReadyDate:dd-MM-yyyy}. It is waiting for approval by an Area Supervisor of {areaName} under Sowing Approvals.";
            }
            return RedirectToPage("/Production/MotherPlant/DirectSowing");
        }

        // ---- AJAX lookups (GET, read-only) -------------------------------------------------------------------------------

        // Empty trays of the cavity in every Mother Plant Area this user may sow in (the same list the form offers -- never a
        // request value), the chosen one's balance, and the physical trays this sowing takes (CEILING, as the save).
        public async Task<JsonResult> OnGetTrayAvailabilityAsync(string? cavityType, int? areaId, decimal quantity)
        {
            await LoadAreasAsync();
            var eligible = Areas.Select(a => new TrayLocation(a.Id, null)).ToList();
            var selected = eligible.FirstOrDefault(l => l.AreaId == areaId);
            return new JsonResult(await PlantStockManager.Pages.Production.SeedSowing.TrayAvailabilityForm.BuildAsync(_trayStockRepo, cavityType, eligible, selected,
                DirectSowingRules.PhysicalTraysRequired(quantity, cavityType)));
        }

        // The SAME server function as the save (DirectSowingRules.CalculateTrays).
        public JsonResult OnGetTrayCalculation(decimal quantity, string? cavityType)
        {
            var (ok, trays, seedsUsed, remaining, error) = DirectSowingRules.CalculateTrays(quantity, cavityType);
            return new JsonResult(new
            {
                ok, trays, seedsUsed, remainingSeeds = remaining, wholeNumber = DirectSowingRules.IsWholeNumber(quantity),
                physicalTrays = DirectSowingRules.PhysicalTraysRequired(quantity, cavityType), error
            });
        }

        public async Task<JsonResult> OnGetVarietiesAsync(int plantTypeId)
        {
            var list = await _plantSpeciesRepo.GetSpeciesByPlantType(plantTypeId);
            return new JsonResult(list.Select(v => new { id = v.Id, name = v.Name.Trim(), growingDays = v.ReadyStockDays }));
        }

        // Main Office lots with stock only (Direct Sowing consumes central Main Office seed, as in Seed Production).
        public async Task<JsonResult> OnGetFindLotsAsync(string? term)
        {
            if (string.IsNullOrWhiteSpace(term) || term.Trim().Length < 2)
                return new JsonResult(Array.Empty<object>());
            var list = await _seedStockRepo.SearchAsync(term, onlyAvailable: true, mainOfficeOnly: true, max: 25);
            var typeIds = (await _plantSpeciesRepo.GetAllAsync()).ToDictionary(v => v.Id, v => v.PlantTypeId);
            return new JsonResult(list.Select(s => new
            {
                id = s.Id, speciesId = s.SpeciesId, plantTypeId = typeIds.TryGetValue(s.SpeciesId, out var t) ? t : 0,
                variety = (s.SpeciesName ?? "").Trim(), crop = s.PlantTypeName,
                lot = string.IsNullOrEmpty(s.BatchNo) ? "(no lot no.)" : s.BatchNo, source = s.SeedSourceName, available = s.AvailableQuantity
            }));
        }

        public async Task<JsonResult> OnGetSeedLotsAsync(int speciesId)
        {
            var list = (await _seedStockRepo.GetAllAsync())
                .Where(s => s.SpeciesId == speciesId && s.AvailableQuantity > 0 && DirectSowingRules.IsMainOfficeSeedLocation(s.AreaType, true))
                .OrderBy(s => s.CreatedDate);
            return new JsonResult(list.Select(s => new
            {
                id = s.Id, lot = string.IsNullOrEmpty(s.BatchNo) ? "(no lot no.)" : s.BatchNo, source = s.SeedSourceName,
                area = s.AreaName, available = s.AvailableQuantity, unit = s.Unit
            }));
        }

        private async Task LoadAreasAsync()
            => Areas = (await _areaRepo.GetAllAreas())
                .Where(a => a.IsActive && a.AreaType == MotherPlantAreaSchema.MotherPlantAreaType && _areaAccess.CanAccessArea(User, a.Id))
                .OrderBy(a => a.Name).ToList();

        private async Task LoadAsync()
        {
            SchemaReady = await WorkflowCorrectionsSchema.MotherPlantDirectSowingAsync(_db);
            await LoadAreasAsync();
            PlantTypes = await _plantTypeRepo.GetAllPlantTypes();
            CavityTypes = await WorkflowCorrectionsSchema.SelectableCavitiesAsync(_db);
            var mine = Areas.Select(a => a.Id).ToHashSet();
            Pending = SchemaReady
                ? (await _seedSowingRepo.GetReadyForConfirmationAsync())
                    .Where(s => s.IsMotherPlantDirectSowing && mine.Contains(s.AreaId))
                    .OrderByDescending(s => s.SowingDate).ThenByDescending(s => s.Id).ToList()
                : new();
        }
    }
}
