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
        public int? SourceWholeTrayCavity => Source.IsSowingBatch ? Source.CavitySize : null;
        // The output goes to Sowing Approvals (complete trays, Ready Date) -- 2026-10-13 migration applied.
        public bool ToSowingApprovals { get; private set; }
        public int? ReadyDays { get; private set; }

        [BindProperty] public int? TargetPolyhouseId { get; set; }
        [BindProperty] public string? TargetCavityType { get; set; }
        [BindProperty] public DateTime TransplantDate { get; set; } = AppClock.Today;
        [BindProperty] public decimal? TransplantedQuantity { get; set; }
        [BindProperty] public decimal? LostQuantity { get; set; } = 0;
        [BindProperty] public string? Notes { get; set; }
        [BindProperty] public Guid SubmissionToken { get; set; }

        public async Task<IActionResult> OnGetAsync(int id)
        {
            if (!await LoadAsync(id)) return NotFoundHere();
            TargetPolyhouseId = TargetPolyhouses.Any(p => p.Id == Source.PolyhouseId) ? Source.PolyhouseId
                              : TargetPolyhouses.Count == 1 ? TargetPolyhouses[0].Id : null;
            SubmissionToken = Guid.NewGuid();
            return Page();
        }

        public async Task<IActionResult> OnPostAsync(int id)
        {
            if (!await LoadAsync(id)) return NotFoundHere();

            if (SubmissionToken == Guid.Empty)
                ModelState.AddModelError(string.Empty, "The form has expired. Open it again.");
            if (TargetPolyhouseId is not > 0 || !TargetPolyhouses.Any(p => p.Id == TargetPolyhouseId))
                ModelState.AddModelError(nameof(TargetPolyhouseId), "Choose a Polyhouse of this Mother Plant Area.");
            if (!DirectSowingRules.IsValidCavityType(TargetCavityType))
                ModelState.AddModelError(nameof(TargetCavityType), $"Choose the target tray: {string.Join(", ", TransplantRules.TargetCavityTypes)}.");
            if (TransplantDate == default)
                ModelState.AddModelError(nameof(TransplantDate), "Enter the transplanting date.");
            else if (TransplantDate.Date > AppClock.Today)
                ModelState.AddModelError(nameof(TransplantDate), "The transplanting date cannot be in the future.");
            CheckWhole(nameof(TransplantedQuantity), TransplantedQuantity, "Transplanted", allowZero: false);
            CheckWhole(nameof(LostQuantity), LostQuantity ?? 0, "Lost / damaged", allowZero: true);
            var selected = (TransplantedQuantity ?? 0) + (LostQuantity ?? 0);
            if (ModelState.IsValid)
            {
                if (selected > Source.AvailableQuantity)
                    ModelState.AddModelError(nameof(TransplantedQuantity), $"Only {QuantityFormat.Qty(Source.AvailableQuantity)} plants of this batch are available.");
                else if (SourceWholeTrayCavity is > 0 && selected % SourceWholeTrayCavity.Value != 0)
                    ModelState.AddModelError(nameof(TransplantedQuantity), $"This sowing batch is kept in whole {Source.CavityType} trays: transplanted + lost must be a multiple of {SourceWholeTrayCavity.Value}.");
                else if (ToSowingApprovals)
                {
                    var (whole, _, wholeError) = TrayBatchRules.CompleteTrays(TransplantedQuantity!.Value, TargetCavityType, "in the received batch");
                    if (!whole) ModelState.AddModelError(nameof(TransplantedQuantity), wholeError!);
                }
            }
            if (!ModelState.IsValid) return Page();

            // The target Area is the batch's own Mother Plant Area -- never a posted value.
            var actor = new MotherPlantTransferRepository.Actor(User.GetUserId(), User.Identity?.Name);
            var (ok, msg, transplantId, _) = await _transplantRepo.CreateAsync(
                new TransplantRepository.CreateRequest(id, Source.AreaId, TargetPolyhouseId, TargetCavityType,
                    selected, TransplantedQuantity!.Value, LostQuantity ?? 0, TransplantDate.Date, Notes, SubmissionToken),
                actor, areaId => _areaAccess.CanAccessArea(User, areaId));
            if (!ok)
            {
                ModelState.AddModelError(string.Empty, msg ?? "The transplanting could not be saved.");
                return Page();
            }
            TempData["Success"] = msg;
            return RedirectToPage("/Production/Transplanting/Details", new { id = transplantId });
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
            TargetPolyhouses = (await _polyhouseRepo.GetAllPolyhouses())
                .Where(p => p.AreaId == source.AreaId).OrderBy(p => p.Name).ToList();
            var polyhouseIds = TargetPolyhouses.Select(p => p.Id).ToHashSet();
            TrayBalances = (await _trayStockRepo.GetAllAsync(activeOnly: true))
                .Where(t => t.PolyhouseId.HasValue && polyhouseIds.Contains(t.PolyhouseId.Value))
                .GroupBy(t => $"{t.PolyhouseId}|{t.TraySize}")
                .ToDictionary(g => g.Key, g => g.Sum(t => t.PhysicalQuantity));
            using (var conn = _db.GetConnection())
            {
                await conn.OpenAsync();
                ToSowingApprovals = await BedSowingSchema.TraysToApprovalsAsync(conn);
            }
            ReadyDays = (await _speciesRepo.GetByIdAsync(source.SpeciesId))?.ReadyStockDays is int d && d > 0 ? d : null;
            return true;
        }

        private IActionResult NotFoundHere()
        {
            TempData["Error"] = "That Ready Stock batch is not in one of your Mother Plant Areas.";
            return RedirectToPage("/Production/Transplanting/Index");
        }
    }
}
