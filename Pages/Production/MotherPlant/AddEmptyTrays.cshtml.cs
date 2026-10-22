using System.Security.Cryptography;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PlantStockManager.Authorization;
using PlantStockManager.Data;
using PlantStockManager.Models;
using PlantStockManager.Services;

namespace PlantStockManager.Pages.Production.MotherPlant
{
    // 2026-10-14: "Add Empty Trays" of the Mother Plant and Cutting sections -- empty trays into a Mother Plant /
    // Cutting Area's own tray pool (one per Area + cavity, 2026-10-14_MotherPlantAreaTraysAndTransferPurpose.sql),
    // with NO polyhouse. The existing tray-adding permissions and Area rule (TrayAreaScope: TrayStock.Enter for the
    // user's own Areas, TrayStock.EnterMotherPlant for every active Mother Plant Area), limited to Mother Plant Areas;
    // nothing is widened. The posted Area must be one this page offers, and TrayStockRepository.AddStockAsync
    // re-checks it (and the one-time form token) inside its stock transaction. The new balance is shown after saving.
    public class AddEmptyTraysModel : PageModel
    {
        private readonly TrayStockRepository _trayStockRepo;
        private readonly TrayAreaScope _scope;
        private readonly DatabaseHelper _db;

        public AddEmptyTraysModel(TrayStockRepository trayStockRepo, AreaRepository areaRepo, AreaAccessService areaAccess, DatabaseHelper db)
        {
            _trayStockRepo = trayStockRepo;
            _scope = new TrayAreaScope(areaRepo, areaAccess);
            _db = db;
        }

        [BindProperty(SupportsGet = true)] public string? Section { get; set; }
        [BindProperty] public int? AreaId { get; set; }
        [BindProperty] public string? TraySize { get; set; }
        [BindProperty] public decimal Quantity { get; set; }
        [BindProperty] public DateTime StockDate { get; set; } = AppClock.Today;
        [BindProperty] public string? Remarks { get; set; }
        [BindProperty] public int SubmissionToken { get; set; }

        public bool SchemaReady { get; private set; }
        public List<Area> Areas { get; private set; } = new();
        public List<Models.TrayStock> Balances { get; private set; } = new();
        public IReadOnlyList<string> TraySizes => DirectSowingRules.CavityTypes;
        public bool IsCuttingSection => Section == EmptyPotStockRules.SectionCutting;

        public async Task OnGetAsync()
        {
            await LoadAsync();
            SubmissionToken = RandomNumberGenerator.GetInt32(1, int.MaxValue);
            StockDate = AppClock.Today;
            if (Areas.Count == 1)
                AreaId = Areas[0].Id;
        }

        public async Task<IActionResult> OnPostAsync()
        {
            if (!User.CanAddTrays()) return Forbid();
            ModelState.Clear();
            await LoadAsync();
            if (!SchemaReady)
                ModelState.AddModelError(string.Empty, MotherPlantAreaSchema.MigrationMessage);
            var area = AreaId.HasValue ? Areas.FirstOrDefault(a => a.Id == AreaId.Value) : null;
            if (area == null)
                ModelState.AddModelError(nameof(AreaId), "Choose one of your Mother Plant / Cutting Areas.");
            if (!DirectSowingRules.IsValidCavityType(TraySize))
                ModelState.AddModelError(nameof(TraySize), $"Cavity must be one of: {string.Join(", ", DirectSowingRules.CavityTypes)}.");
            if (Quantity <= 0 || !DirectSowingRules.IsWholeNumber(Quantity))
                ModelState.AddModelError(nameof(Quantity), "Tray Quantity must be a whole number greater than zero.");
            var (dateOk, transactionUtc, dateError) = LedgerDates.Instant(StockDate == default ? null : StockDate);
            if (StockDate == default)
                ModelState.AddModelError(nameof(StockDate), "Date is required.");
            else if (!dateOk)
                ModelState.AddModelError(nameof(StockDate), dateError!);
            if (Remarks?.Trim().Length > 500)
                ModelState.AddModelError(nameof(Remarks), "Remark can be at most 500 characters.");
            if (!ModelState.IsValid)
                return Page();

            var allowed = Areas.Select(a => a.Id).ToHashSet();
            var (success, message, duplicate, balance) = await _trayStockRepo.AddStockAsync(
                area!.Id, null, TraySize!, Quantity, transactionUtc ?? DateTime.UtcNow, SubmissionToken, User.GetUserId(), User.Identity?.Name,
                string.IsNullOrWhiteSpace(Remarks) ? "Empty trays added to the Area" : Remarks.Trim(), id => allowed.Contains(id));
            if (!success)
            {
                ModelState.AddModelError(string.Empty, message ?? "The trays could not be added.");
                return Page();
            }
            TempData["Success"] = duplicate
                ? $"This form was already saved -- nothing was added twice. Current {TraySize} empty tray stock in {area.Name}: {QuantityFormat.Qty(balance)}."
                : $"{QuantityFormat.Qty(Quantity)} empty {TraySize} trays added to {area.Name}. {TraySize} empty tray stock in {area.Name}: "
                  + $"before {QuantityFormat.Qty(balance - Quantity)}, now {QuantityFormat.Qty(balance)}.";
            return RedirectToPage(new { section = Section });
        }

        private async Task LoadAsync()
        {
            Section = EmptyPotStockRules.NormalizeSection(Section);
            SchemaReady = await MotherPlantAreaSchema.ReadyAsync(_db);
            Areas = (await _scope.AddableAreasAsync(User))
                .Where(a => a.AreaType == MotherPlantAreaSchema.MotherPlantAreaType).ToList();
            var areaIds = Areas.Select(a => a.Id).ToHashSet();
            Balances = (await _trayStockRepo.GetAllAsync(activeOnly: true))
                .Where(t => areaIds.Contains(t.AreaId) && !t.PolyhouseId.HasValue)
                .OrderBy(t => t.AreaName).ThenBy(t => DirectSowingRules.CavityCount(t.TraySize)).ToList();
        }
    }
}
