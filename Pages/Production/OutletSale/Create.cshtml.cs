using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PlantStockManager.Authorization;
using PlantStockManager.Data;
using PlantStockManager.Models;
using PlantStockManager.Services;
using PottedPlantStockModel = PlantStockManager.Models.PottedPlantStock;
using ReadyStockModel = PlantStockManager.Models.ReadyStock;

namespace PlantStockManager.Pages.Production.OutletSale
{
    // A single customer buying any mix of potted-plant and ready-tray items
    // from one Outlet at once. One header + several items, all-or-nothing:
    // the first item that cannot be fulfilled fails the whole sale
    // (OutletSaleRepository), whichever stock type it is. 2026-10-21: a double
    // click / browser retry / replay of the same form is stopped by
    // SubmissionTokenGuard (it used to sell -- and deduct the stock -- again).
    public class CreateModel : PageModel
    {
        public const string TokenScope = "OutletSale";

        private readonly OutletSaleRepository _saleRepo;
        private readonly PottedPlantStockRepository _pottedRepo;
        private readonly ReadyStockRepository _readyRepo;
        private readonly AreaRepository _areaRepo;
        private readonly AreaAccessService _areaAccess;
        private readonly SubmissionTokenGuard _tokens;

        public CreateModel(OutletSaleRepository saleRepo, PottedPlantStockRepository pottedRepo, ReadyStockRepository readyRepo,
            AreaRepository areaRepo, AreaAccessService areaAccess, SubmissionTokenGuard tokens)
        {
            _saleRepo = saleRepo;
            _pottedRepo = pottedRepo;
            _readyRepo = readyRepo;
            _areaRepo = areaRepo;
            _areaAccess = areaAccess;
            _tokens = tokens;
        }

        public class ItemLine
        {
            public string StockType { get; set; } = OutletStockType.Potted;
            public int StockId { get; set; }
            public decimal Quantity { get; set; }
        }

        [BindProperty] public int OutletAreaId { get; set; }
        [BindProperty] public string? CustomerName { get; set; }
        [BindProperty] public string? CustomerContact { get; set; }
        [BindProperty] public DateTime SaleDate { get; set; } = DateTime.Today;
        [BindProperty] public string? Remarks { get; set; }
        [BindProperty] public List<ItemLine> Items { get; set; } = new();
        [BindProperty] public string? SubmitToken { get; set; }

        public List<Area> Outlets { get; set; } = new();
        public List<PottedPlantStockModel> PottedOptions { get; set; } = new();
        public List<ReadyStockModel> TrayOptions { get; set; } = new();

        public async Task OnGetAsync(int? outletAreaId)
        {
            await LoadAsync();
            SubmitToken = SubmissionTokenGuard.NewToken();
            OutletAreaId = outletAreaId ?? (Outlets.Count == 1 ? Outlets[0].Id : 0);
            if (OutletAreaId > 0)
                await LoadStockAsync(OutletAreaId);
        }

        // The Outlet's own available stock (both kinds), for the item picker.
        public async Task<JsonResult> OnGetStockOptionsAsync(int outletAreaId)
        {
            if (!_areaAccess.CanAccessArea(User, outletAreaId))
                return new JsonResult(new { potted = Array.Empty<object>(), trays = Array.Empty<object>() });
            var potted = (await _pottedRepo.GetAllAsync())
                .Where(s => s.AreaId == outletAreaId && s.AvailableQuantity - s.InTransitQuantity > 0)
                .OrderBy(s => s.SpeciesName)
                .Select(s => new { id = s.Id, label = $"{s.SpeciesName?.Trim()}{(string.IsNullOrEmpty(s.SpeciesColor) ? "" : " (" + s.SpeciesColor + ")")} - {s.PotSize} - available {QuantityFormat.Qty(s.AvailableQuantity - s.InTransitQuantity)}", available = s.AvailableQuantity - s.InTransitQuantity });
            var trays = (await _readyRepo.GetAllAsync())
                .Where(s => s.AreaId == outletAreaId)
                .Select(s => new { s, cavity = s.TraySize })   // a partial-tray batch: 1 tray = its whole quantity (2026-10-31)
                .Where(x => x.cavity is > 0 && x.s.AvailableQuantity > 0)
                .Select(x => new { id = x.s.Id, label = $"{x.s.SpeciesName?.Trim()} - {x.s.CavityType} - available {(x.s.AvailableQuantity / x.cavity!.Value):N0} trays", available = Math.Floor(x.s.AvailableQuantity / x.cavity!.Value) })
                .OrderBy(o => o.label);
            return new JsonResult(new { potted, trays });
        }

        public async Task<IActionResult> OnPostAsync()
        {
            if (!_areaAccess.CanAccessArea(User, OutletAreaId))
                ModelState.AddModelError(string.Empty, "You are not authorized for the selected Outlet.");
            Items = Items.Where(i => i.StockId > 0 && i.Quantity != 0).ToList();
            if (Items.Count == 0)
                ModelState.AddModelError(string.Empty, "Add at least one item.");
            if (!ModelState.IsValid)
            {
                await LoadAsync();
                if (OutletAreaId > 0) await LoadStockAsync(OutletAreaId);
                return Page();
            }

            if (!_tokens.TryConsume(TokenScope, SubmitToken))
            {
                TempData["Error"] = "This sale form was already submitted. Check the sales below before selling again.";
                return RedirectToPage("/Production/OutletSale/Index");
            }

            var header = new PlantStockManager.Models.OutletSale
            {
                CustomerName = CustomerName ?? "",
                CustomerContact = CustomerContact,
                SaleDate = SaleDate,
                Remarks = Remarks,
                CreatedBy = User.Identity?.Name ?? "System"
            };
            var lines = Items.Select(i => new OutletSaleRepository.SaleItemInput(i.StockType, i.StockId, i.Quantity)).ToList();
            var (success, message, id) = await _saleRepo.InsertAsync(header, lines, OutletAreaId, User.GetUserId());
            if (!success)
            {
                ModelState.AddModelError(string.Empty, message ?? "Failed to complete the sale.");
                // The token was used up; issue a fresh one so the corrected form can be sent again.
                ModelState.Remove(nameof(SubmitToken));
                SubmitToken = SubmissionTokenGuard.NewToken();
                await LoadAsync();
                if (OutletAreaId > 0) await LoadStockAsync(OutletAreaId);
                return Page();
            }
            TempData["Success"] = $"Sale {header.SaleCode}: {Items.Count} item(s) sold to {CustomerName}.";
            return RedirectToPage("/Production/OutletSale/Details", new { id });
        }

        private async Task LoadAsync()
        {
            Outlets = _areaAccess.FilterByArea(User, await _areaRepo.GetByAreaTypesAsync(OutletRules.AreaType), a => (int?)a.Id)
                .Where(a => a.IsActive).ToList();
        }

        private async Task LoadStockAsync(int outletAreaId)
        {
            PottedOptions = (await _pottedRepo.GetAllAsync())
                .Where(s => s.AreaId == outletAreaId && s.AvailableQuantity - s.InTransitQuantity > 0)
                .OrderBy(s => s.SpeciesName).ToList();
            TrayOptions = (await _readyRepo.GetAllAsync())
                .Where(s => s.AreaId == outletAreaId && s.AvailableQuantity > 0)
                .OrderBy(s => s.SpeciesName).ToList();
        }
    }
}
