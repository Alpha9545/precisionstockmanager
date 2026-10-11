using System.Security.Claims;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using PlantStockManager.Authorization;
using PlantStockManager.Data;
using PlantStockManager.Models;
using PlantStockManager.Services;

namespace PlantStockManager.Pages.Production.MaterialStock
{
    // The Tray part of Add Material (formerly the Add Tray Stock page, moved unchanged
    // 2026-10-08): the Sowing Supervisor (TrayStock.Enter) adds empty trays to an
    // AREA + POLYHOUSE + CAVITY pool for an Area they are authorized for -- any active,
    // non-Outlet Area (the existing Tray Stock rule; NOT limited to the Main Office like
    // Cocopeat / Pitmos). Area authorization is AreaAccessService's regular rule, checked
    // for both dropdowns, on POST, and again inside TrayStockRepository.AddStockAsync,
    // which also verifies (under its transaction) that the Polyhouse belongs to that
    // Area -- a posted AreaId / PolyhouseId pair is never trusted. A tray is a COUNT:
    // no conversion factor and no kg ever apply.
    // 2026-10-09: TrayStock.EnterMotherPlant adds every active Mother Plant Area to the
    // allowed Areas (Authorization/TrayAreaScope.cs -- the one per-Area rule, used for the
    // dropdowns, on POST and inside AddStockAsync alike).
    public sealed class TrayAddInput
    {
        public int AreaId { get; set; }
        public int PolyhouseId { get; set; }
        public string? TraySize { get; set; }
        public decimal Quantity { get; set; }
        public DateTime TransactionDate { get; set; } = DateTime.Now;
        public string? Remarks { get; set; }
        // One-time token per form: a repeated submit of the same form adds nothing twice.
        public int SubmissionToken { get; set; }

        public static TrayAddInput New() => new() { TransactionDate = DateTime.Now, SubmissionToken = RandomNumberGenerator.GetInt32(1, int.MaxValue) };
    }

    public sealed class TrayAddForm
    {
        public const string Prefix = "Tray";

        private readonly TrayStockRepository _trayStockRepo;
        private readonly AreaRepository _areaRepo;
        private readonly PolyhouseRepository _polyhouseRepo;
        private readonly AreaAccessService _areaAccess;
        private readonly TrayAreaScope _scope;

        public TrayAddForm(TrayStockRepository trayStockRepo, AreaRepository areaRepo, PolyhouseRepository polyhouseRepo, AreaAccessService areaAccess)
        {
            _trayStockRepo = trayStockRepo;
            _areaRepo = areaRepo;
            _polyhouseRepo = polyhouseRepo;
            _areaAccess = areaAccess;
            _scope = new TrayAreaScope(areaRepo, areaAccess);
        }

        public IReadOnlyList<string> TraySizes => DirectSowingRules.CavityTypes;

        // Active, non-Outlet Areas this user may add trays to (a full-access user sees every one).
        public Task<List<Area>> AreasAsync(ClaimsPrincipal user) => _scope.AddableAreasAsync(user);

        // Dependent dropdown: only the Polyhouses of the chosen Area, and only when the
        // user may add Tray Stock for that Area.
        public async Task<List<Polyhouse>> PolyhousesForAsync(ClaimsPrincipal user, int areaId)
        {
            if (areaId <= 0)
                return new List<Polyhouse>();
            var area = await _areaRepo.GetAreaById(areaId);
            if (area == null || !_scope.CanAddIn(user, area))
                return new List<Polyhouse>();
            return (await _polyhouseRepo.GetByAreaIdAsync(areaId)).OrderBy(p => p.Name).ToList();
        }

        public void Validate(ClaimsPrincipal user, TrayAddInput t, List<Area> areas, List<Polyhouse> polyhouses, ModelStateDictionary state)
        {
            string Key(string field) => $"{Prefix}.{field}";
            if (t.AreaId <= 0)
                state.AddModelError(Key(nameof(t.AreaId)), "Area is required.");
            else if (!areas.Any(a => a.Id == t.AreaId))
                state.AddModelError(Key(nameof(t.AreaId)), "You are not authorized to add Tray Stock for the selected Area.");
            if (t.PolyhouseId <= 0)
                state.AddModelError(Key(nameof(t.PolyhouseId)), "Polyhouse is required.");
            else if (t.AreaId > 0 && !polyhouses.Any(p => p.Id == t.PolyhouseId))
                state.AddModelError(Key(nameof(t.PolyhouseId)), "Selected Polyhouse does not belong to the selected Area.");
            if (!DirectSowingRules.IsValidCavityType(t.TraySize))
                state.AddModelError(Key(nameof(t.TraySize)), "Cavity is required.");
            if (t.Quantity <= 0 || !DirectSowingRules.IsWholeNumber(t.Quantity))
                state.AddModelError(Key(nameof(t.Quantity)), "Tray Quantity must be a whole number greater than zero.");
            if (t.TransactionDate == default)
                state.AddModelError(Key(nameof(t.TransactionDate)), "Date / Time is required.");
            else if (t.TransactionDate > DateTime.Now.AddMinutes(5))
                state.AddModelError(Key(nameof(t.TransactionDate)), "Date / Time cannot be in the future.");
        }

        public async Task<(bool Success, string? Message, bool Duplicate, decimal Balance)> SaveAsync(ClaimsPrincipal user, TrayAddInput t)
        {
            // datetime-local is the employee's local business time; the ledger stores UTC.
            var transactionDateUtc = t.TransactionDate.Kind == DateTimeKind.Utc ? t.TransactionDate : t.TransactionDate.ToUniversalTime();
            var allowed = (await _scope.AddableAreasAsync(user)).Select(a => a.Id).ToHashSet();
            return await _trayStockRepo.AddStockAsync(
                t.AreaId, t.PolyhouseId, t.TraySize!, t.Quantity, transactionDateUtc, t.SubmissionToken, user.GetUserId(), user.Identity?.Name, t.Remarks,
                areaId => allowed.Contains(areaId));
        }
    }
}
