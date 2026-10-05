using System.Security.Cryptography;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PlantStockManager.Authorization;
using PlantStockManager.Data;
using PlantStockManager.Models;
using PlantStockManager.Services;

namespace PlantStockManager.Pages.Production.TrayStock
{
    // Add Tray Stock: the Sowing Supervisor (TrayStock.Enter) adds empty trays
    // to an AREA + POLYHOUSE + CAVITY pool, for an Area they are authorized
    // for. Replaces the retired Main Office "Allocate Trays" screen. Area
    // authorization is AreaAccessService's regular rule, checked on GET (both
    // dropdowns), on POST, and again inside TrayStockRepository.AddStockAsync,
    // which also verifies (under its transaction) that the Polyhouse belongs
    // to that Area -- a posted AreaId/PolyhouseId pair is never trusted.
    public class AddModel : PageModel
    {
        private readonly TrayStockRepository _trayStockRepo;
        private readonly AreaRepository _areaRepo;
        private readonly PolyhouseRepository _polyhouseRepo;
        private readonly AreaAccessService _areaAccess;

        public AddModel(TrayStockRepository trayStockRepo, AreaRepository areaRepo, PolyhouseRepository polyhouseRepo, AreaAccessService areaAccess)
        {
            _trayStockRepo = trayStockRepo;
            _areaRepo = areaRepo;
            _polyhouseRepo = polyhouseRepo;
            _areaAccess = areaAccess;
        }

        [BindProperty] public int AreaId { get; set; }
        [BindProperty] public int PolyhouseId { get; set; }
        [BindProperty] public string? TraySize { get; set; }
        [BindProperty] public decimal Quantity { get; set; }
        [BindProperty] public DateTime TransactionDate { get; set; } = DateTime.Now;
        [BindProperty] public string? Remarks { get; set; }
        // One-time token per form: a repeated submit of the same form adds nothing twice.
        [BindProperty] public int SubmissionToken { get; set; }

        public List<Area> Areas { get; set; } = new();
        public List<Polyhouse> Polyhouses { get; set; } = new();
        public IReadOnlyList<string> TraySizes => DirectSowingRules.CavityTypes;

        public async Task OnGetAsync()
        {
            TransactionDate = DateTime.Now;
            SubmissionToken = RandomNumberGenerator.GetInt32(1, int.MaxValue);
            await LoadAreasAsync();
            if (Areas.Count == 1)
                AreaId = Areas[0].Id;
            Polyhouses = await PolyhousesForAsync(AreaId);
        }

        // Dependent dropdown: only the Polyhouses of the chosen Area, and only
        // when the user may add Tray Stock for that Area.
        public async Task<JsonResult> OnGetPolyhousesAsync(int areaId)
            => new JsonResult((await PolyhousesForAsync(areaId)).Select(p => new { id = p.Id, name = p.Name }));

        public async Task<IActionResult> OnPostAsync()
        {
            await LoadAreasAsync();
            Polyhouses = await PolyhousesForAsync(AreaId);

            if (AreaId <= 0)
                ModelState.AddModelError(nameof(AreaId), "Area is required.");
            else if (!_areaAccess.CanAccessRequiredArea(User, AreaId) || !Areas.Any(a => a.Id == AreaId))
                ModelState.AddModelError(nameof(AreaId), "You are not authorized to add Tray Stock for the selected Area.");
            if (PolyhouseId <= 0)
                ModelState.AddModelError(nameof(PolyhouseId), "Polyhouse is required.");
            else if (AreaId > 0 && !Polyhouses.Any(p => p.Id == PolyhouseId))
                ModelState.AddModelError(nameof(PolyhouseId), "Selected Polyhouse does not belong to the selected Area.");
            if (!DirectSowingRules.IsValidCavityType(TraySize))
                ModelState.AddModelError(nameof(TraySize), "Cavity is required.");
            if (Quantity <= 0 || !DirectSowingRules.IsWholeNumber(Quantity))
                ModelState.AddModelError(nameof(Quantity), "Tray Quantity must be a whole number greater than zero.");
            if (TransactionDate == default)
                ModelState.AddModelError(nameof(TransactionDate), "Date / Time is required.");
            else if (TransactionDate > DateTime.Now.AddMinutes(5))
                ModelState.AddModelError(nameof(TransactionDate), "Date / Time cannot be in the future.");

            if (!ModelState.IsValid)
                return Page();

            // datetime-local is the employee's local business time; the ledger stores UTC.
            var transactionDateUtc = TransactionDate.Kind == DateTimeKind.Utc ? TransactionDate : TransactionDate.ToUniversalTime();
            var (success, message, duplicate, balance) = await _trayStockRepo.AddStockAsync(
                AreaId, PolyhouseId, TraySize!, Quantity, transactionDateUtc, SubmissionToken, User.GetUserId(), User.Identity?.Name, Remarks,
                areaId => _areaAccess.CanAccessRequiredArea(User, areaId));
            if (!success)
            {
                ModelState.AddModelError(string.Empty, message ?? "Failed to add Tray Stock.");
                return Page();
            }

            var where = $"{Areas.First(a => a.Id == AreaId).Name}, {Polyhouses.First(p => p.Id == PolyhouseId).Name}, {TraySize}";
            TempData["Success"] = duplicate
                ? $"This Tray Stock entry was already saved -- nothing was added twice. {where}: {QuantityFormat.Qty(balance)} trays available."
                : $"Added {QuantityFormat.Qty(Quantity)} trays to {where}. Available now: {QuantityFormat.Qty(balance)}.";
            return RedirectToPage("/Production/TrayStock/Index");
        }

        // Active, non-Outlet Areas this user is authorized for (a full-access
        // user sees every one).
        private async Task LoadAreasAsync()
        {
            Areas = (await _areaRepo.GetAllAreas())
                .Where(a => a.IsActive && a.AreaType != OutletRules.AreaType && _areaAccess.CanAccessRequiredArea(User, a.Id))
                .OrderBy(a => a.Name)
                .ToList();
        }

        private async Task<List<Polyhouse>> PolyhousesForAsync(int areaId)
        {
            if (areaId <= 0 || !_areaAccess.CanAccessRequiredArea(User, areaId))
                return new List<Polyhouse>();
            var area = await _areaRepo.GetAreaById(areaId);
            if (area == null || !area.IsActive || area.AreaType == OutletRules.AreaType)
                return new List<Polyhouse>();
            return (await _polyhouseRepo.GetByAreaIdAsync(areaId)).OrderBy(p => p.Name).ToList();
        }
    }
}
