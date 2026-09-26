using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PlantStockManager.Authorization;
using PlantStockManager.Data;
using PlantStockManager.Models;
using PlantStockManager.Services;
using PottedPlantStockModel = PlantStockManager.Models.PottedPlantStock;
using ReadyStockModel = PlantStockManager.Models.ReadyStock;

namespace PlantStockManager.Pages.Production.OutletBooking
{
    // A customer order across any mix of potted-plant and ready-tray items,
    // reserved against Outlet stock now and collected later (fully or
    // partially).
    public class CreateModel : PageModel
    {
        private readonly OutletBookingRepository _bookingRepo;
        private readonly PottedPlantStockRepository _pottedRepo;
        private readonly ReadyStockRepository _readyRepo;
        private readonly AreaRepository _areaRepo;
        private readonly AreaAccessService _areaAccess;

        public CreateModel(OutletBookingRepository bookingRepo, PottedPlantStockRepository pottedRepo, ReadyStockRepository readyRepo,
            AreaRepository areaRepo, AreaAccessService areaAccess)
        {
            _bookingRepo = bookingRepo;
            _pottedRepo = pottedRepo;
            _readyRepo = readyRepo;
            _areaRepo = areaRepo;
            _areaAccess = areaAccess;
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
        [BindProperty] public DateTime BookingDate { get; set; } = DateTime.Today;
        [BindProperty] public DateTime? RequiredDate { get; set; }
        [BindProperty] public string? Remarks { get; set; }
        [BindProperty] public List<ItemLine> Items { get; set; } = new();

        public List<Area> Outlets { get; set; } = new();
        public List<PottedPlantStockModel> PottedOptions { get; set; } = new();
        public List<ReadyStockModel> TrayOptions { get; set; } = new();

        public async Task OnGetAsync(int? outletAreaId)
        {
            await LoadAsync();
            OutletAreaId = outletAreaId ?? (Outlets.Count == 1 ? Outlets[0].Id : 0);
            if (OutletAreaId > 0)
                await LoadStockAsync(OutletAreaId);
        }

        public async Task<JsonResult> OnGetStockOptionsAsync(int outletAreaId)
        {
            if (!_areaAccess.CanAccessArea(User, outletAreaId))
                return new JsonResult(new { potted = Array.Empty<object>(), trays = Array.Empty<object>() });
            var potted = (await _pottedRepo.GetAllAsync())
                .Where(s => s.AreaId == outletAreaId && s.AvailableQuantity - s.InTransitQuantity > 0)
                .OrderBy(s => s.SpeciesName)
                .Select(s => new { id = s.Id, label = $"{s.SpeciesName?.Trim()}{(string.IsNullOrEmpty(s.SpeciesColor) ? "" : " (" + s.SpeciesColor + ")")} - {s.PotSize} - available {s.AvailableQuantity - s.InTransitQuantity:N0}", available = s.AvailableQuantity - s.InTransitQuantity });
            var trays = (await _readyRepo.GetAllAsync())
                .Where(s => s.AreaId == outletAreaId)
                .Select(s => new { s, cavity = DirectSowingRules.CavityCount(s.CavityType) })
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

            var header = new PlantStockManager.Models.OutletBooking
            {
                CustomerName = CustomerName ?? "",
                CustomerContact = CustomerContact,
                BookingDate = BookingDate,
                RequiredDate = RequiredDate,
                Remarks = Remarks,
                CreatedBy = User.Identity?.Name ?? "System"
            };
            var lines = Items.Select(i => new OutletBookingRepository.BookingItemInput(i.StockType, i.StockId, i.Quantity)).ToList();
            var (success, message, id) = await _bookingRepo.InsertAsync(header, lines, OutletAreaId, User.GetUserId());
            if (!success)
            {
                ModelState.AddModelError(string.Empty, message ?? "Failed to create the booking.");
                await LoadAsync();
                if (OutletAreaId > 0) await LoadStockAsync(OutletAreaId);
                return Page();
            }
            TempData["Success"] = $"Booking {header.BookingCode}: {Items.Count} item(s) reserved for {CustomerName}.";
            return RedirectToPage("/Production/OutletBooking/Details", new { id });
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
