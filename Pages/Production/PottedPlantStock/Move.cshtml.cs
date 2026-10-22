using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PlantStockManager.Authorization;
using PlantStockManager.Data;
using PlantStockManager.Models;
using InternalTransferModel = PlantStockManager.Models.InternalTransfer;
using PottedPlantStockModel = PlantStockManager.Models.PottedPlantStock;
using PlantStockManager.Services;

namespace PlantStockManager.Pages.Production.PottedPlantStock
{
    // One place to send READY potted plants:
    //   Main Office / Outlet -> an Area-to-Area stock movement (InternalTransfers, 'PottedPlant')
    //   Customer            -> a direct sale (booking + dispatch at once), from wherever the
    //                          stock is held -- gated by the Outlet.Sell permission and Area
    //                          access, never by the Area's type. Never more than READY/available.
    // Every movement is recorded in the potted-plant ledger with date, from,
    // to, variety, pot size, quantity, user, reference and remarks.
    public class MoveModel : PageModel
    {
        private readonly PottedPlantStockRepository _stockRepo;
        private readonly InternalTransferRepository _transferRepo;
        private readonly DispatchRepository _dispatchRepo;
        private readonly AreaRepository _areaRepo;
        private readonly AreaAccessService _areaAccess;
        private readonly IAuthorizationService _authorization;

        // Each destination needs the permission of the person who does it:
        // a customer sale is an Outlet sale; Main Office / Outlet is a stock transfer.
        public const string SellPolicy = "Outlet.Sell";
        public const string TransferPolicy = "InternalTransfer.Enter|MainOffice.Confirm";

        public MoveModel(PottedPlantStockRepository stockRepo, InternalTransferRepository transferRepo, DispatchRepository dispatchRepo,
            AreaRepository areaRepo, AreaAccessService areaAccess, IAuthorizationService authorization)
        {
            _authorization = authorization;
            _stockRepo = stockRepo;
            _transferRepo = transferRepo;
            _dispatchRepo = dispatchRepo;
            _areaRepo = areaRepo;
            _areaAccess = areaAccess;
        }

        public const string ToMainOffice = "MainOffice";
        public const string ToOutlet = "Outlet";
        public const string ToCustomer = "Customer";

        public PottedPlantStockModel Stock { get; set; } = new();
        public List<Area> MainOfficeAreas { get; set; } = new();
        public List<Area> OutletAreas { get; set; } = new();
        public bool CanSell { get; set; }
        public bool CanTransfer { get; set; }

        [BindProperty] public string? Destination { get; set; }
        [BindProperty] public int? DestinationAreaId { get; set; }
        [BindProperty] public decimal Quantity { get; set; }
        [BindProperty] public string? CustomerName { get; set; }
        [BindProperty] public string? CustomerContact { get; set; }
        [BindProperty] public string? Remarks { get; set; }
        // 2026-10-17: the form's one-time token -- a repeated submit sells / sends nothing twice.
        [BindProperty] public Guid SubmissionToken { get; set; }
        // 2026-10-17: opened from Mother Plant & Cutting > Ready Stock -- return there.
        [BindProperty(SupportsGet = true)] public string? From { get; set; }
        private IActionResult Done() => From == "mp" ? RedirectToPage("/Production/MotherPlant/ReadyStock") : RedirectToPage("/Production/PottedPlantStock/Index");

        public async Task<IActionResult> OnGetAsync(int id)
        {
            if (!await LoadAsync(id)) return Denied();
            SubmissionToken = Guid.NewGuid();
            return Page();
        }

        // 2026-10-21 security review: a post without the form's token (missing / empty / not a GUID -- all bind as
        // Guid.Empty) used to be processed WITHOUT replay protection. It is now refused before anything is sold or sent.
        public const string MissingTokenMessage = "This form is out of date -- nothing was sold or sent. Check the stock and submit the form again.";

        public async Task<IActionResult> OnPostAsync(int id)
        {
            if (!await LoadAsync(id))
                return Denied();
            if (SubmissionToken == Guid.Empty)
            {
                ModelState.AddModelError(string.Empty, MissingTokenMessage);
                ModelState.Remove(nameof(SubmissionToken));
                SubmissionToken = Guid.NewGuid();
                return Page();
            }
            var userId = User.GetUserId();
            var createdBy = User.Identity?.Name ?? "System";

            if (Quantity <= 0 || !PlantStockManager.Services.DirectSowingRules.IsWholeNumber(Quantity))
                ModelState.AddModelError(string.Empty, "Quantity must be a whole number greater than zero.");
            else if (Quantity > Stock.AvailableQuantity - Stock.InTransitQuantity)
                ModelState.AddModelError(string.Empty, $"Only {QuantityFormat.Qty(Stock.AvailableQuantity - Stock.InTransitQuantity)} plants are available to send.");

            if (Destination == ToCustomer)
            {
                if (!CanSell)
                    ModelState.AddModelError(string.Empty, "You are not authorized to sell to a customer.");
                if (string.IsNullOrWhiteSpace(CustomerName))
                    ModelState.AddModelError(string.Empty, "Customer name is required.");
                if (!ModelState.IsValid)
                    return Page();
                var (ok, message, code) = await _dispatchRepo.DirectSaleAsync(id, Quantity, CustomerName!, CustomerContact, Remarks, userId, createdBy,
                    SubmissionToken);
                if (!ok)
                {
                    ModelState.AddModelError(string.Empty, message ?? "Failed to record the sale.");
                    return Page();
                }
                TempData["Success"] = message == DispatchRepository.AlreadySoldMessage
                    ? $"{message} Dispatch {code}."
                    : $"Sold {QuantityFormat.Qty(Quantity)} x {Stock.SpeciesName?.Trim()} ({Stock.PotSize}) to {CustomerName} - dispatch {code}.";
                return Done();
            }

            if (!CanTransfer)
            {
                ModelState.AddModelError(string.Empty, "You are not allowed to transfer stock.");
                return Page();
            }
            var allowed = Destination == ToMainOffice ? MainOfficeAreas : Destination == ToOutlet ? OutletAreas : new List<Area>();
            if (!DestinationAreaId.HasValue || allowed.All(a => a.Id != DestinationAreaId.Value))
                ModelState.AddModelError(string.Empty, "Choose where the plants are going.");
            else if (DestinationAreaId == Stock.AreaId)
                ModelState.AddModelError(string.Empty, "The plants are already in that Area.");
            if (!ModelState.IsValid)
                return Page();

            var entry = new InternalTransferModel
            {
                StockType = "PottedPlant",
                SourcePottedPlantStockId = id,
                DestinationAreaId = DestinationAreaId,
                Quantity = Quantity,
                Remarks = Remarks,
                CreatedBy = createdBy,
                SubmissionToken = SubmissionToken
            };
            var (success, msg, _) = await _transferRepo.InsertAsync(entry, userId);
            if (success && msg == InternalTransferRepository.AlreadySentMessage)
            {
                TempData["Success"] = msg;
                return Done();
            }
            if (!success)
            {
                ModelState.AddModelError(string.Empty, msg ?? "Failed to send the plants.");
                return Page();
            }
            var destName = allowed.First(a => a.Id == DestinationAreaId).Name;
            TempData["Success"] = $"Sent {QuantityFormat.Qty(Quantity)} x {Stock.SpeciesName?.Trim()} ({Stock.PotSize}) to {destName} ({entry.TransferCode}).";
            return Done();
        }

        private async Task<bool> LoadAsync(int id)
        {
            var stock = await _stockRepo.GetByIdAsync(id);
            if (stock == null || !stock.AreaId.HasValue || !_areaAccess.CanAccessArea(User, stock.AreaId))
                return false;
            Stock = stock;
            var areas = (await _areaRepo.GetAllAreas()).Where(a => a.IsActive && a.Id != stock.AreaId).ToList();
            MainOfficeAreas = areas.Where(a => a.AreaType == "MainOffice").OrderBy(a => a.Name).ToList();
            OutletAreas = areas.Where(a => a.AreaType == "Outlet").OrderBy(a => a.Name).ToList();
            CanSell = (await _authorization.AuthorizeAsync(User, SellPolicy)).Succeeded;
            CanTransfer = (await _authorization.AuthorizeAsync(User, TransferPolicy)).Succeeded;
            return true;
        }

        private IActionResult Denied()
        {
            TempData["Error"] = "Stock not found, or it belongs to an Area you cannot access.";
            return RedirectToPage("/Production/PottedPlantStock/Index");
        }
    }
}
