using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PlantStockManager.Authorization;
using PlantStockManager.Data;
using PlantStockManager.Models;
using PlantStockManager.Services;
using ReadyStockModel = PlantStockManager.Models.ReadyStock;

namespace PlantStockManager.Pages.Production.Transplanting
{
    // Transplant into Trays -- one operation on one received batch (Transplant.Enter), opened straight
    // from the receipt (Transfers to Mother Plant) or from the Transplanting list.
    // 2026-10-13 simplification: the target Area is the batch's own Mother Plant Area (no choice); the
    // Polyhouse is preselected (the batch's Polyhouse); the user chooses the CAVITY and enters the
    // quantity transplanted (+ any lost); trays, the Ready Date (transplant date + the variety's Ready
    // Days) and what is left are shown as they type. The authority is TransplantRepository.CreateAsync:
    // it re-reads everything under lock and records all of it or nothing. With
    // 2026-10-13_TraysToSowingApprovals.sql the output is a tray batch in Sowing Approvals.
    // 2026-10-14 (MotherPlantAreaSchema): no Polyhouse -- the empty trays come from the Mother Plant Area's own
    // tray pool -- and a batch received for Pot Production is not transplanted (refused here and under lock).
    // 2026-10-16 (owner): like Main Office bed transplanting -- the user enters the seedlings to PROCESS (default: all
    // available); complete trays of the cavity are transplanted and the remainder is WASTE with a reason, in the same
    // save (TransplantRules.PlanProcessing). Fewer than one tray: only an explicitly confirmed waste-only closure. The
    // unprocessed part stays in the batch. No growing material (Cocopeat / Pitmos) is asked for or used.
    public class CreateModel : PageModel
    {
        private readonly ReadyStockRepository _readyStockRepo;
        private readonly TransplantRepository _transplantRepo;
        private readonly TrayStockRepository _trayStockRepo;
        private readonly PolyhouseRepository _polyhouseRepo;
        private readonly PlantSpeciesRepository _speciesRepo;
        private readonly DatabaseHelper _db;
        private readonly AreaAccessService _areaAccess;   // Mother Plant Area work: always Area-scoped (no Development switch), as the Mother Plant module

        public CreateModel(ReadyStockRepository readyStockRepo, TransplantRepository transplantRepo, TrayStockRepository trayStockRepo,
            PolyhouseRepository polyhouseRepo, PlantSpeciesRepository speciesRepo, DatabaseHelper db, AreaAccessService areaAccess)
        {
            _readyStockRepo = readyStockRepo;
            _transplantRepo = transplantRepo;
            _trayStockRepo = trayStockRepo;
            _polyhouseRepo = polyhouseRepo;
            _speciesRepo = speciesRepo;
            _db = db;
            _areaAccess = areaAccess;
        }

        public ReadyStockModel Source { get; private set; } = new();
        public List<Polyhouse> TargetPolyhouses { get; private set; } = new();
        // Empty trays in Tray Stock per "polyhouseId|cavity" (shown live; re-checked on save).
        public Dictionary<string, decimal> TrayBalances { get; private set; } = new();
        // Plants in one source tray: the cavity, or a partial-tray batch's whole quantity (2026-10-31, PartialTrayRules).
        public int? SourceWholeTrayCavity => Source.IsSowingBatch ? Source.TraySize : null;
        // The output goes to Sowing Approvals (complete trays, Ready Date) -- 2026-10-13 migration applied.
        public bool ToSowingApprovals { get; private set; }
        public int? ReadyDays { get; private set; }
        // 2026-10-14 migration applied: the Area's trays, no Polyhouse selector (TrayBalances keyed "area|cavity").
        public bool AreaOnly { get; private set; }

        [BindProperty] public int? TargetPolyhouseId { get; set; }
        [BindProperty] public string? TargetCavityType { get; set; }
        [BindProperty] public DateTime TransplantDate { get; set; } = AppClock.Today;
        // before 2026-10-13_TraysToSowingApprovals.sql only (exact plant counts, entered transplanted + lost)
        [BindProperty] public decimal? TransplantedQuantity { get; set; }
        [BindProperty] public decimal? LostQuantity { get; set; } = 0;
        // 2026-10-16: the tray-batch path
        [BindProperty] public decimal? ProcessQuantity { get; set; }
        [BindProperty] public string? WasteReason { get; set; }
        [BindProperty] public bool DiscardAll { get; set; }
        // 2026-10-16_MotherPlantTransplantWaste.sql applied: a waste-only closure is possible.
        public bool WasteOnlyAvailable { get; private set; }
        [BindProperty] public string? Notes { get; set; }
        [BindProperty] public Guid SubmissionToken { get; set; }

        public async Task<IActionResult> OnGetAsync(int id)
        {
            if (!await LoadAsync(id)) return NotFoundHere();
            if (await IsPotProductionAsync(id)) return PotProductionHere();
            TargetPolyhouseId = AreaOnly ? null
                              : TargetPolyhouses.Any(p => p.Id == Source.PolyhouseId) ? Source.PolyhouseId
                              : TargetPolyhouses.Count == 1 ? TargetPolyhouses[0].Id : null;
            SubmissionToken = Guid.NewGuid();
            ProcessQuantity = Source.AvailableQuantity;   // default: every available seedling
            return Page();
        }

        public async Task<IActionResult> OnPostAsync(int id)
        {
            if (!await LoadAsync(id)) return NotFoundHere();
            if (await IsPotProductionAsync(id)) return PotProductionHere();

            if (SubmissionToken == Guid.Empty)
                ModelState.AddModelError(string.Empty, "The form has expired. Open it again.");
            if (AreaOnly)
                TargetPolyhouseId = null;   // the Area's trays, never a posted Polyhouse
            else if (TargetPolyhouseId is not > 0 || !TargetPolyhouses.Any(p => p.Id == TargetPolyhouseId))
                ModelState.AddModelError(nameof(TargetPolyhouseId), "Choose a Polyhouse of this Mother Plant Area.");
            if (!DirectSowingRules.IsValidCavityType(TargetCavityType))
                ModelState.AddModelError(nameof(TargetCavityType), $"Choose the target tray: {string.Join(", ", TransplantRules.TargetCavityTypes)}.");
            if (TransplantDate == default)
                ModelState.AddModelError(nameof(TransplantDate), "Enter the transplanting date.");
            else if (TransplantDate.Date > AppClock.Today)
                ModelState.AddModelError(nameof(TransplantDate), "The transplanting date cannot be in the future.");
            TransplantRepository.CreateRequest request;
            if (ToSowingApprovals)
            {
                // 2026-10-16: processed -> complete trays + waste; the repository re-plans it under lock.
                if (DiscardAll)
                    ModelState.Remove(nameof(TargetCavityType));      // a waste-only closure needs no cavity
                var (planOk, plan, planError) = TransplantRules.PlanProcessing(Source.AvailableQuantity, ProcessQuantity ?? 0,
                    TargetCavityType, DiscardAll, WasteReason, SourceWholeTrayCavity, Source.IsPartialTrayBatch);
                if (!planOk)
                    ModelState.AddModelError(nameof(ProcessQuantity), planError!);
                else if (!plan!.MakesTrayBatch && !WasteOnlyAvailable)
                    ModelState.AddModelError(string.Empty, WorkflowCorrectionsSchema.TransplantWasteMigrationMessage);
                if (!ModelState.IsValid) return Page();
                request = new TransplantRepository.CreateRequest(id, Source.AreaId, TargetPolyhouseId, TargetCavityType,
                    plan!.Processed, plan.Transplanted, plan.Waste, TransplantDate.Date, Notes, SubmissionToken, WasteReason, DiscardAll);
            }
            else
            {
                CheckWhole(nameof(TransplantedQuantity), TransplantedQuantity, "Transplanted", allowZero: false);
                CheckWhole(nameof(LostQuantity), LostQuantity ?? 0, "Lost / damaged", allowZero: true);
                var selected = (TransplantedQuantity ?? 0) + (LostQuantity ?? 0);
                if (ModelState.IsValid)
                {
                    if (selected > Source.AvailableQuantity)
                        ModelState.AddModelError(nameof(TransplantedQuantity), $"Only {QuantityFormat.Qty(Source.AvailableQuantity)} plants of this batch are available.");
                    else if (SourceWholeTrayCavity is > 0 && selected % SourceWholeTrayCavity.Value != 0)
                        ModelState.AddModelError(nameof(TransplantedQuantity), Source.IsPartialTrayBatch
                            ? PartialTrayRules.WholeBatchMessage(Source.Quantity, Source.CavityType)
                            : $"This sowing batch is kept in whole {Source.CavityType} trays: transplanted + lost must be a multiple of {SourceWholeTrayCavity.Value}.");
                }
                if (!ModelState.IsValid) return Page();
                request = new TransplantRepository.CreateRequest(id, Source.AreaId, TargetPolyhouseId, TargetCavityType,
                    selected, TransplantedQuantity!.Value, LostQuantity ?? 0, TransplantDate.Date, Notes, SubmissionToken);
            }

            // The target Area is the batch's own Mother Plant Area -- never a posted value.
            var actor = new MotherPlantTransferRepository.Actor(User.GetUserId(), User.Identity?.Name);
            var (ok, msg, transplantId, _) = await _transplantRepo.CreateAsync(request, actor, areaId => _areaAccess.CanAccessArea(User, areaId));
            if (!ok)
            {
                ModelState.AddModelError(string.Empty, msg ?? "The transplanting could not be saved.");
                return Page();
            }
            TempData["Success"] = msg;
            return RedirectToPage("/Production/Transplanting/Details", new { id = transplantId });
        }

        // Empty trays of the cavity in this batch's own Mother Plant Area (its Area pool; before the 2026-10-14 migration,
        // the Area's Polyhouses), the chosen pool's balance, and the trays this transplant takes: complete trays only
        // (TransplantRules.PlanProcessing) -- none for a waste-only closure; before 2026-10-13 every started tray.
        public async Task<IActionResult> OnGetTrayAvailabilityAsync(int id, string? cavityType, int? polyhouseId, decimal quantity, bool discard)
        {
            if (!await LoadAsync(id))
                return new JsonResult(new { ok = false, rows = Array.Empty<object>(), message = "That Ready Stock batch is not in one of your Mother Plant Areas." });
            var eligible = AreaOnly
                ? new List<TrayLocation> { new(Source.AreaId, null) }
                : TargetPolyhouses.Select(p => new TrayLocation(Source.AreaId, p.Id)).ToList();
            var selected = AreaOnly ? eligible[0] : eligible.FirstOrDefault(l => l.PolyhouseId == polyhouseId);
            var needed = discard ? 0
                : ToSowingApprovals ? Pages.Production.SeedSowing.TrayAvailabilityForm.CompleteTrays(quantity, cavityType)
                : DirectSowingRules.PhysicalTraysRequired(quantity, cavityType);
            return new JsonResult(await Pages.Production.SeedSowing.TrayAvailabilityForm.BuildAsync(_trayStockRepo, cavityType, eligible, selected, needed));
        }

        private void CheckWhole(string key, decimal? value, string label, bool allowZero)
        {
            if (value is null)
                ModelState.AddModelError(key, $"{label} is required.");
            else if (!DirectSowingRules.IsWholeNumber(value.Value) || value < 0 || (!allowZero && value == 0))
                ModelState.AddModelError(key, $"{label} must be a whole number{(allowZero ? " (0 or more)" : " greater than zero")}.");
        }

        private async Task<bool> LoadAsync(int id)
        {
            var source = await _readyStockRepo.GetByIdAsync(id);
            if (source == null || !source.InMotherPlantArea || !_areaAccess.CanAccessArea(User, source.AreaId))
                return false;
            Source = source;
            using (var conn = _db.GetConnection())
            {
                await conn.OpenAsync();
                ToSowingApprovals = await BedSowingSchema.TraysToApprovalsAsync(conn);
                AreaOnly = await MotherPlantAreaSchema.ReadyAsync(conn);
                WasteOnlyAvailable = await WorkflowCorrectionsSchema.TransplantWasteAsync(conn);
            }
            if (AreaOnly)
            {
                // the Mother Plant Area's own tray pool (PolyhouseId NULL), one per cavity
                TrayBalances = (await _trayStockRepo.GetAllAsync(activeOnly: true))
                    .Where(t => t.AreaId == source.AreaId && !t.PolyhouseId.HasValue)
                    .GroupBy(t => $"area|{t.TraySize}")
                    .ToDictionary(g => g.Key, g => g.Sum(t => t.PhysicalQuantity));
            }
            else
            {
                TargetPolyhouses = (await _polyhouseRepo.GetAllPolyhouses())
                    .Where(p => p.AreaId == source.AreaId).OrderBy(p => p.Name).ToList();
                var polyhouseIds = TargetPolyhouses.Select(p => p.Id).ToHashSet();
                TrayBalances = (await _trayStockRepo.GetAllAsync(activeOnly: true))
                    .Where(t => t.PolyhouseId.HasValue && polyhouseIds.Contains(t.PolyhouseId.Value))
                    .GroupBy(t => $"{t.PolyhouseId}|{t.TraySize}")
                    .ToDictionary(g => g.Key, g => g.Sum(t => t.PhysicalQuantity));
            }
            ReadyDays = (await _speciesRepo.GetByIdAsync(source.SpeciesId))?.ReadyStockDays is int d && d > 0 ? d : null;
            return true;
        }

        private async Task<bool> IsPotProductionAsync(int readyStockId)
        {
            using var conn = _db.GetConnection();
            await conn.OpenAsync();
            return !MotherPlantTransferRules.ValidateTransplantPurpose(
                await MotherPlantTransferRepository.ReceivedBatchPurposeAsync(conn, null, readyStockId)).Ok;
        }

        private IActionResult PotProductionHere()
        {
            TempData["Error"] = MotherPlantTransferRules.PotProductionNotTransplantedMessage;
            return RedirectToPage("/Production/Transplanting/Index");
        }

        private IActionResult NotFoundHere()
        {
            TempData["Error"] = "That Ready Stock batch is not in one of your Mother Plant Areas.";
            return RedirectToPage("/Production/Transplanting/Index");
        }
    }
}
