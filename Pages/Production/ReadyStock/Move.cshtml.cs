using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PlantStockManager.Authorization;
using PlantStockManager.Data;
using PlantStockManager.Models;
using InternalTransferModel = PlantStockManager.Models.InternalTransfer;
using ReadyStockModel = PlantStockManager.Models.ReadyStock;
using PlantStockManager.Services;

namespace PlantStockManager.Pages.Production.ReadyStock
{
    // Send an approved Ready Stock batch (seedling trays) to Main Office or
    // an Outlet -- the same immediate, no-confirmation Area-to-Area
    // movement Potted Plant Stock already has (InternalTransfers,
    // 'ReadyStock'). A batch is one whole sowing (one row per
    // SeedSowingId), so the whole batch moves together, and only before
    // anything has been reserved for a booking or dispatched.
    // 2026-10-19: a third destination, Customer -- whole trays dispatched straight to a customer by a user holding
    // ReadyStock.DispatchToCustomer (Office Coordinator) in the batch's own Area (AreaAccessService, always enforced).
    // It is the existing direct tray sale (SeedlingFulfilmentRepository.DirectTraySaleAsync): booking + allocation +
    // reservation + Seedling Dispatch in one transaction, re-checked under the batch's lock, once per form token.
    // The Main Office / Outlet transfer is unchanged and still needs TransferPolicy.
    public class MoveModel : PageModel
    {
        private readonly ReadyStockRepository _readyStockRepo;
        private readonly InternalTransferRepository _transferRepo;
        private readonly AreaRepository _areaRepo;
        private readonly SeedlingAreaScope _areaAccess;
        private readonly IAuthorizationService _authorization;
        private readonly AreaAccessService _strictArea;   // 2026-10-17: Mother Plant Area stock -- always Area-scoped
        private readonly SeedlingFulfilmentRepository _fulfilment;
        private readonly MotherPlantTransferRepository _mpTransferRepo;
        private readonly DatabaseHelper _db;

        public const string TransferPolicy = "InternalTransfer.Enter|MainOffice.Confirm";
        public const string CustomerDispatchPolicy = "ReadyStock.DispatchToCustomer";

        public MoveModel(ReadyStockRepository readyStockRepo, InternalTransferRepository transferRepo,
            AreaRepository areaRepo, SeedlingAreaScope areaAccess, IAuthorizationService authorization, AreaAccessService strictArea,
            SeedlingFulfilmentRepository fulfilment, MotherPlantTransferRepository mpTransferRepo, DatabaseHelper db)
        {
            _strictArea = strictArea;
            _fulfilment = fulfilment;
            _mpTransferRepo = mpTransferRepo;
            _db = db;
            _readyStockRepo = readyStockRepo;
            _transferRepo = transferRepo;
            _areaRepo = areaRepo;
            _areaAccess = areaAccess;
            _authorization = authorization;
        }

        public const string ToMainOffice = "MainOffice";
        public const string ToOutlet = "Outlet";
        public const string ToCustomer = "Customer";

        public ReadyStockModel Batch { get; set; } = new();
        public List<Area> MainOfficeAreas { get; set; } = new();
        public List<Area> OutletAreas { get; set; } = new();
        public bool CanTransfer { get; set; }
        // 2026-10-19: the Customer destination is offered (permission + the batch's own Area + a Main Office / Mother Plant Area).
        public bool CanDispatchToCustomer { get; set; }
        public AreaReadyStockActionRules.Availability TransferEligibility { get; set; } = AreaReadyStockActionRules.Availability.Yes;
        public AreaReadyStockActionRules.Availability CustomerEligibility { get; set; } = AreaReadyStockActionRules.Availability.No("Dispatching to a customer is not part of your role.");
        public int AvailableTrays => AreaReadyStockActionRules.AvailableCompleteTrays(Batch);

        [BindProperty] public string? Destination { get; set; }
        [BindProperty] public int? DestinationAreaId { get; set; }
        [BindProperty] public decimal Quantity { get; set; }
        [BindProperty] public string? Remarks { get; set; }
        // 2026-10-17: the form's one-time token -- a repeated submit sends the batch once.
        [BindProperty] public Guid SubmissionToken { get; set; }
        // 2026-10-19: Customer destination -- whole trays and the customer (name + contact required, as for every direct sale).
        [BindProperty] public int Trays { get; set; }
        [BindProperty] public string? CustomerName { get; set; }
        [BindProperty] public string? CustomerContact { get; set; }
        [BindProperty] public string? CustomerAddress { get; set; }
        // 2026-10-17: opened from Mother Plant & Cutting > Ready Stock -- return there.
        [BindProperty(SupportsGet = true)] public string? From { get; set; }
        public bool FromMotherPlant => From == "mp";

        public async Task<IActionResult> OnGetAsync(int id)
        {
            if (!await LoadAsync(id)) return Denied();
            SubmissionToken = Guid.NewGuid();
            // 2026-10-19: opened from Mother Plant & Cutting > Ready Stock "Send to Outlet" -- Outlet preselected (Main Office stays allowed).
            if (FromMotherPlant && Destination == null && OutletAreas.Any())
            {
                Destination = ToOutlet;
                DestinationAreaId = OutletAreas.Count == 1 ? OutletAreas[0].Id : null;
            }
            // 2026-10-19: a user who may only dispatch to customers starts on that destination.
            if (Destination == null && !CanTransfer && CanDispatchToCustomer)
                Destination = ToCustomer;
            return Page();
        }

        // 2026-10-21 security review: a post without the form's token (missing / empty / not a GUID -- all bind as
        // Guid.Empty) used to send trays WITHOUT replay protection. It is now refused before anything is sent or sold
        // (the customer dispatch already refused it in SeedlingFulfilmentRepository).
        public const string MissingTokenMessage = "This form is out of date -- nothing was sent. Check the batch and submit the form again.";

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
            if (Destination == ToCustomer)
                return await DispatchToCustomerAsync(id);
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
                CreatedBy = createdBy,
                SubmissionToken = SubmissionToken
            };
            var (success, msg, _) = await _transferRepo.InsertAsync(entry, userId);
            if (success && msg == InternalTransferRepository.AlreadySentMessage)
            {
                TempData["Success"] = msg;
                return FromMotherPlant ? RedirectToPage("/Production/MotherPlant/ReadyStock") : RedirectToPage("/Production/ReadyStock/Index");
            }
            if (!success)
            {
                ModelState.AddModelError(string.Empty, msg ?? "Failed to send the trays.");
                return Page();
            }
            var destName = allowed.First(a => a.Id == DestinationAreaId).Name;
            TempData["Success"] = $"Sent batch {Batch.BatchNo} ({Batch.SowingCode}): {QuantityFormat.Qty(Batch.Quantity)} {Batch.SpeciesName?.Trim()} plants"
                + (Batch.PhysicalTrays.HasValue ? $" in {Batch.PhysicalTrays:0} x {Batch.CavityType} trays" : $" ({Batch.CavityType})") + $" to {destName} ({entry.TransferCode}).";
            return FromMotherPlant ? RedirectToPage("/Production/MotherPlant/ReadyStock") : RedirectToPage("/Production/ReadyStock/Index");
        }

        // 2026-10-19: Customer destination. The page checks are for the message only -- DirectTraySaleAsync re-reads the batch
        // under its lock and re-checks the Area (strict AreaAccessService), the Area type, the cavity and the available trays.
        private async Task<IActionResult> DispatchToCustomerAsync(int id)
        {
            if (!CanDispatchToCustomer)
            {
                ModelState.AddModelError(string.Empty, "You are not allowed to dispatch this stock to a customer.");
                return Page();
            }
            if (!CustomerEligibility.Available)
            {
                ModelState.AddModelError(string.Empty, CustomerEligibility.Reason ?? "This batch cannot be dispatched to a customer.");
                return Page();
            }
            var (ok, msg, code) = await _fulfilment.DirectTraySaleAsync(
                new SeedlingFulfilmentRepository.DirectTraySaleRequest(id, Trays, CustomerName, CustomerContact, CustomerAddress, Remarks,
                    SubmissionToken, FromReadySeedlingStock: true),
                new SeedlingFulfilmentRepository.Actor(User.Identity?.Name, User.GetUserId()),
                areaId => _strictArea.CanAccessArea(User, areaId));
            if (!ok)
            {
                ModelState.AddModelError(string.Empty, msg ?? "The dispatch could not be recorded.");
                return Page();
            }
            TempData["Success"] = msg == SeedlingFulfilmentRepository.AlreadySoldMessage
                ? $"{msg} (Dispatch {code})"
                : $"Dispatched to customer {CustomerName!.Trim()} ({CustomerContact!.Trim()}): {Trays} x {Batch.CavityType} trays "
                  + $"({QuantityFormat.Qty(Trays * (decimal)Batch.TraySize!.Value)} plants) of {Batch.SpeciesName?.Trim()}, batch {Batch.BatchNo} ({Batch.SowingCode}). "
                  + $"Dispatch reference {code}.";
            return FromMotherPlant ? RedirectToPage("/Production/MotherPlant/ReadyStock") : RedirectToPage("/Production/ReadyStock/Index");
        }

        private async Task<bool> LoadAsync(int id)
        {
            var batch = await _readyStockRepo.GetByIdAsync(id);
            if (batch == null || !_areaAccess.CanAccessArea(User, batch.AreaId))
                return false;
            // 2026-10-17: stock held in a Mother Plant Area is sent only by a user of THAT Area (or full access) -- the regular
            // Area rule, whatever the seedling-scope switch says; a forged batch id of another Area is refused.
            if (batch.InMotherPlantArea && !_strictArea.CanAccessArea(User, batch.AreaId))
                return false;
            Batch = batch;
            Quantity = batch.AvailableQuantity;
            var areas = (await _areaRepo.GetAllAreas()).Where(a => a.IsActive && a.Id != batch.AreaId).ToList();
            MainOfficeAreas = areas.Where(a => a.AreaType == "MainOffice").OrderBy(a => a.Name).ToList();
            OutletAreas = areas.Where(a => a.AreaType == "Outlet").OrderBy(a => a.Name).ToList();
            CanTransfer = (await _authorization.AuthorizeAsync(User, TransferPolicy)).Succeeded;
            TransferEligibility = AreaReadyStockActionRules.SendToOutlet(batch, true, false);
            // Customer: the permission AND the batch's own Area under the regular Area rule (never the relaxed seedling scope).
            CanDispatchToCustomer = (batch.InMainArea || batch.InMotherPlantArea)
                && _strictArea.CanAccessArea(User, batch.AreaId)
                && (await _authorization.AuthorizeAsync(User, CustomerDispatchPolicy)).Succeeded;
            if (CanDispatchToCustomer)
            {
                var forPots = batch.InMotherPlantArea && (await _mpTransferRepo.PotProductionBatchIdsAsync()).Contains(id);
                CustomerEligibility = AreaReadyStockActionRules.SellToCustomer(batch, true, forPots,
                    await WorkflowCorrectionsSchema.SeedlingSaleTokenAsync(_db));
            }
            return true;
        }

        private IActionResult Denied()
        {
            TempData["Error"] = "Ready Stock batch not found, or it belongs to an Area you cannot access.";
            return FromMotherPlant ? RedirectToPage("/Production/MotherPlant/ReadyStock") : RedirectToPage("/Production/ReadyStock/Index");
        }
    }
}
