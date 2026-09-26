using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PlantStockManager.Authorization;
using PlantStockManager.Data;
using PlantStockManager.Models;
using InternalTransferModel = PlantStockManager.Models.InternalTransfer;
using ReadyStockModel = PlantStockManager.Models.ReadyStock;

namespace PlantStockManager.Pages.Production.ReadyStock
{
    // Send an approved Ready Stock batch (seedling trays) to Main Office or
    // an Outlet -- the same immediate, no-confirmation Area-to-Area
    // movement Potted Plant Stock already has (InternalTransfers,
    // 'ReadyStock'). A batch is one whole sowing (one row per
    // SeedSowingId), so the whole batch moves together, and only before
    // anything has been reserved for a booking or dispatched.
    public class MoveModel : PageModel
    {
        private readonly ReadyStockRepository _readyStockRepo;
        private readonly InternalTransferRepository _transferRepo;
        private readonly AreaRepository _areaRepo;
        private readonly SeedlingAreaScope _areaAccess;
        private readonly IAuthorizationService _authorization;

        public const string TransferPolicy = "InternalTransfer.Enter|MainOffice.Confirm";

        public MoveModel(ReadyStockRepository readyStockRepo, InternalTransferRepository transferRepo,
            AreaRepository areaRepo, SeedlingAreaScope areaAccess, IAuthorizationService authorization)
        {
            _readyStockRepo = readyStockRepo;
            _transferRepo = transferRepo;
            _areaRepo = areaRepo;
            _areaAccess = areaAccess;
            _authorization = authorization;
        }

        public const string ToMainOffice = "MainOffice";
        public const string ToOutlet = "Outlet";

        public ReadyStockModel Batch { get; set; } = new();
        public List<Area> MainOfficeAreas { get; set; } = new();
        public List<Area> OutletAreas { get; set; } = new();
        public bool CanTransfer { get; set; }

        [BindProperty] public string? Destination { get; set; }
        [BindProperty] public int? DestinationAreaId { get; set; }
        [BindProperty] public decimal Quantity { get; set; }
        [BindProperty] public string? Remarks { get; set; }

        public async Task<IActionResult> OnGetAsync(int id) => await LoadAsync(id) ? Page() : Denied();

        public async Task<IActionResult> OnPostAsync(int id)
        {
            if (!await LoadAsync(id))
                return Denied();
            if (!CanTransfer)
            {
                ModelState.AddModelError(string.Empty, "You are not allowed to transfer stock.");
                return Page();
            }

            var allowed = Destination == ToMainOffice ? MainOfficeAreas : Destination == ToOutlet ? OutletAreas : new List<Area>();
            if (!DestinationAreaId.HasValue || allowed.All(a => a.Id != DestinationAreaId.Value))
                ModelState.AddModelError(string.Empty, "Choose where the trays are going.");
            if (!ModelState.IsValid)
                return Page();

            var userId = User.GetUserId();
            var createdBy = User.Identity?.Name ?? "System";
            var entry = new InternalTransferModel
            {
                StockType = "ReadyStock",
                SourceReadyStockId = id,
                DestinationAreaId = DestinationAreaId,
                Quantity = Quantity,
                Remarks = Remarks,
                CreatedBy = createdBy
            };
            var (success, msg, _) = await _transferRepo.InsertAsync(entry, userId);
            if (!success)
            {
                ModelState.AddModelError(string.Empty, msg ?? "Failed to send the trays.");
                return Page();
            }
            var destName = allowed.First(a => a.Id == DestinationAreaId).Name;
            TempData["Success"] = $"Sent {Batch.Quantity:N0} x {Batch.SpeciesName?.Trim()} ({Batch.CavityType}) trays ({Batch.SowingCode}) to {destName} ({entry.TransferCode}).";
            return RedirectToPage("/Production/ReadyStock/Index");
        }

        private async Task<bool> LoadAsync(int id)
        {
            var batch = await _readyStockRepo.GetByIdAsync(id);
            if (batch == null || !_areaAccess.CanAccessArea(User, batch.AreaId))
                return false;
            Batch = batch;
            Quantity = batch.AvailableQuantity;
            var areas = (await _areaRepo.GetAllAreas()).Where(a => a.IsActive && a.Id != batch.AreaId).ToList();
            MainOfficeAreas = areas.Where(a => a.AreaType == "MainOffice").OrderBy(a => a.Name).ToList();
            OutletAreas = areas.Where(a => a.AreaType == "Outlet").OrderBy(a => a.Name).ToList();
            CanTransfer = (await _authorization.AuthorizeAsync(User, TransferPolicy)).Succeeded;
            return true;
        }

        private IActionResult Denied()
        {
            TempData["Error"] = "Ready Stock batch not found, or it belongs to an Area you cannot access.";
            return RedirectToPage("/Production/ReadyStock/Index");
        }
    }
}
