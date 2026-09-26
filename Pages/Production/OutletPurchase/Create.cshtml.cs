using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PlantStockManager.Authorization;
using PlantStockManager.Data;
using PlantStockManager.Models;
using PlantStockManager.Services;
using OutletPurchaseModel = PlantStockManager.Models.OutletPurchase;

namespace PlantStockManager.Pages.Production.OutletPurchase
{
    // Potted plants an Outlet buys directly from an outside supplier --
    // distinct from a Main Office purchase order. Credits the Outlet's own
    // Potted Plant Stock.
    public class CreateModel : PageModel
    {
        private readonly OutletPurchaseRepository _purchaseRepo;
        private readonly PlantSpeciesRepository _speciesRepo;
        private readonly PotSizeRepository _potSizeRepo;
        private readonly AreaRepository _areaRepo;
        private readonly AreaAccessService _areaAccess;

        public CreateModel(OutletPurchaseRepository purchaseRepo, PlantSpeciesRepository speciesRepo,
            PotSizeRepository potSizeRepo, AreaRepository areaRepo, AreaAccessService areaAccess)
        {
            _purchaseRepo = purchaseRepo;
            _speciesRepo = speciesRepo;
            _potSizeRepo = potSizeRepo;
            _areaRepo = areaRepo;
            _areaAccess = areaAccess;
        }

        [BindProperty] public DateTime PurchaseDate { get; set; } = DateTime.Today;
        [BindProperty] public string? SupplierName { get; set; }
        [BindProperty] public int OutletAreaId { get; set; }
        [BindProperty] public int SpeciesId { get; set; }
        [BindProperty] public string? PotSize { get; set; }
        [BindProperty] public decimal Quantity { get; set; }
        [BindProperty] public string? Remarks { get; set; }

        public List<Area> Outlets { get; set; } = new();
        public List<PlantSpecies> Species { get; set; } = new();
        public List<PotSize> PotSizes { get; set; } = new();
        public List<OutletPurchaseModel> Recent { get; set; } = new();

        public async Task OnGetAsync()
        {
            await LoadAsync();
            if (Outlets.Count == 1)
                OutletAreaId = Outlets[0].Id;
        }

        public async Task<IActionResult> OnPostAsync()
        {
            if (!_areaAccess.CanAccessArea(User, OutletAreaId))
                ModelState.AddModelError(string.Empty, "You are not authorized for the selected Outlet.");
            if (!ModelState.IsValid)
            {
                await LoadAsync();
                return Page();
            }

            var entry = new PlantStockManager.Models.OutletPurchase
            {
                PurchaseDate = PurchaseDate,
                SupplierName = SupplierName ?? "",
                OutletAreaId = OutletAreaId,
                SpeciesId = SpeciesId,
                PotSize = PotSize ?? "",
                Quantity = Quantity,
                Remarks = Remarks,
                CreatedBy = User.Identity?.Name ?? "System"
            };
            var (success, message, _) = await _purchaseRepo.InsertAsync(entry, User.GetUserId());
            if (!success)
            {
                ModelState.AddModelError(string.Empty, message ?? "Failed to record the purchase.");
                await LoadAsync();
                return Page();
            }
            TempData["Success"] = $"Purchase {entry.PurchaseCode}: {Quantity:N0} pots added to Outlet stock.";
            return RedirectToPage();
        }

        private async Task LoadAsync()
        {
            Outlets = _areaAccess.FilterByArea(User, await _areaRepo.GetByAreaTypesAsync(OutletRules.AreaType), a => (int?)a.Id)
                .Where(a => a.IsActive).ToList();
            Species = await _speciesRepo.GetAllAsync();
            PotSizes = await _potSizeRepo.GetAllAsync(activeOnly: true);
            var recent = await _purchaseRepo.GetAllAsync();
            Recent = _areaAccess.FilterByArea(User, recent, r => (int?)r.OutletAreaId).Take(25).ToList();
        }
    }
}
