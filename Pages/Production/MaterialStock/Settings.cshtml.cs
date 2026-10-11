using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PlantStockManager.Authorization;
using PlantStockManager.Data;
using PlantStockManager.Models;
using PlantStockManager.Services;

namespace PlantStockManager.Pages.Production.MaterialStock
{
    // Material Stock settings (MaterialStock.Manage -- no role holds it by default,
    // so only full-access users): the approximate business values as DATA --
    // conversion factor per material, kg per tray per cavity and material -- and
    // physical-count adjustments with a required reason. A changed value applies to
    // stock added / sowings recorded from then on; every ledger row keeps the value
    // it was calculated with.
    public class SettingsModel : PageModel
    {
        private readonly MaterialStockRepository _repo;
        private readonly AreaAccessService _areaAccess;

        public SettingsModel(MaterialStockRepository repo, AreaAccessService areaAccess)
        {
            _repo = repo;
            _areaAccess = areaAccess;
        }

        public List<MaterialType> Materials { get; set; } = new();
        public List<MaterialConsumptionRule> Rules { get; set; } = new();
        public List<(int AreaId, string AreaName, int PolyhouseId, string PolyhouseName)> Locations { get; set; } = new();

        [BindProperty] public int AdjustAreaId { get; set; }
        [BindProperty] public int AdjustPolyhouseId { get; set; }
        [BindProperty] public int AdjustMaterialTypeId { get; set; }
        [BindProperty] public int AdjustDirection { get; set; }
        [BindProperty] public decimal? AdjustQuantity { get; set; }
        [BindProperty] public string? AdjustReason { get; set; }
        [BindProperty] public Guid SubmissionToken { get; set; }

        public async Task OnGetAsync()
        {
            SubmissionToken = Guid.NewGuid();
            await LoadAsync();
        }

        public async Task<IActionResult> OnPostFactorAsync(int materialTypeId, decimal conversionFactor)
        {
            var error = await _repo.UpdateConversionFactorAsync(materialTypeId, conversionFactor, User.GetUserId());
            TempData[error == null ? "Success" : "Error"] = error ?? $"Conversion factor saved: {conversionFactor:0.####}. It applies to stock added from now on.";
            return RedirectToPage();
        }

        public async Task<IActionResult> OnPostRuleAsync(int ruleId, decimal quantityPerTray)
        {
            var error = await _repo.UpdateQuantityPerTrayAsync(ruleId, quantityPerTray, User.GetUserId());
            TempData[error == null ? "Success" : "Error"] = error ?? $"Consumption saved: {MaterialStockRules.Kg(quantityPerTray)} per tray. It applies to sowings recorded from now on.";
            return RedirectToPage();
        }

        public async Task<IActionResult> OnPostAdjustAsync()
        {
            var (ok, message, balance) = await _repo.AdjustAsync(AdjustAreaId, AdjustPolyhouseId, AdjustMaterialTypeId, AdjustDirection,
                AdjustQuantity ?? 0, AdjustReason, SubmissionToken, User.GetUserId(), areaId => _areaAccess.CanAccessRequiredArea(User, areaId));
            if (!ok)
            {
                ModelState.AddModelError(string.Empty, message ?? "Failed to record the adjustment.");
                await LoadAsync();
                return Page();
            }
            TempData["Success"] = message ?? $"Adjustment recorded. Stock now: {MaterialStockRules.Kg(balance)}.";
            return RedirectToPage();
        }

        private async Task LoadAsync()
        {
            Materials = await _repo.GetTypesAsync(activeOnly: false);
            Rules = await _repo.GetRulesAsync();
            Locations = await _repo.GetLocationsAsync(a => _areaAccess.CanAccessRequiredArea(User, a));
        }
    }
}
