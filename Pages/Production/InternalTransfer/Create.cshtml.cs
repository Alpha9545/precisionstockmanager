using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PlantStockManager.Authorization;
using PlantStockManager.Data;
using PlantStockManager.Models;
using PlantStockManager.Services;
using InternalTransferModel = PlantStockManager.Models.InternalTransfer;

namespace PlantStockManager.Pages.Production.InternalTransfer
{
    public class CreateModel : PageModel
    {
        private readonly InternalTransferRepository _internalTransferRepo;
        private readonly EmptyPotInventoryRepository _emptyPotInventoryRepo;
        private readonly PottedPlantStockRepository _pottedPlantStockRepo;
        private readonly AreaRepository _areaRepo;
        private readonly SupervisorSelectionService _supervisors;
        private readonly AreaAccessService _areaAccessService;

        public CreateModel(
            AreaAccessService areaAccessService,
            InternalTransferRepository internalTransferRepo,
            EmptyPotInventoryRepository emptyPotInventoryRepo,
            PottedPlantStockRepository pottedPlantStockRepo,
            AreaRepository areaRepo,
            SupervisorSelectionService supervisors)
        {
            _internalTransferRepo = internalTransferRepo;
            _emptyPotInventoryRepo = emptyPotInventoryRepo;
            _pottedPlantStockRepo = pottedPlantStockRepo;
            _areaRepo = areaRepo;
            _supervisors = supervisors;
            _areaAccessService = areaAccessService;
        }

        [BindProperty]
        public InternalTransferModel InternalTransfer { get; set; } = new();

        // Only pools that already have a real (non-null) Area and some
        // physical stock are offered as a Transfer source -- legacy
        // "unassigned location" pools must first receive stock directly
        // against a specific Area (Add Stock / Pot Production) before
        // they can be moved onward.
        public List<PlantStockManager.Models.EmptyPotInventory> EmptyPotPools { get; set; } = new();
        public List<PlantStockManager.Models.PottedPlantStock> PottedPlantPools { get; set; } = new();
        public List<Area> Areas { get; set; } = new();
        // Phase 1: supervisors of the SOURCE Area's type (Production Area /
        // Main Office / Outlet) for that Area; narrowed in the browser from the
        // chosen pool and re-checked on save.
        public List<KindedSupervisorOption> Supervisors { get; set; } = new();
        public string? AreaKindKey(int? areaId)
            => areaId.HasValue ? SupervisorRules.KindKeyForAreaType(Areas.FirstOrDefault(a => a.Id == areaId.Value)?.AreaType) : null;

        public async Task OnGetAsync()
        {
            InternalTransfer.StockType = "EmptyPot";
            await LoadDropdownsAsync();
        }

        public async Task<IActionResult> OnPostAsync()
        {
            ModelState.Remove("InternalTransfer.TransferCode");
            ModelState.Remove("InternalTransfer.CreatedBy");
            ModelState.Remove("InternalTransfer.Status");
            ModelState.Remove("InternalTransfer.SourceAreaId"); // server-derived from the selected pool
            ModelState.Remove("InternalTransfer.PotSize");      // server-derived from the selected pool

            if (InternalTransfer.StockType != "EmptyPot" && InternalTransfer.StockType != "PottedPlant")
                ModelState.AddModelError("InternalTransfer.StockType", "Stock Type is required.");
            if (InternalTransfer.StockType == "EmptyPot" && !InternalTransfer.SourceEmptyPotInventoryId.HasValue)
                ModelState.AddModelError("InternalTransfer.SourceEmptyPotInventoryId", "Source Pot Size / Area is required.");
            if (InternalTransfer.StockType == "PottedPlant" && !InternalTransfer.SourcePottedPlantStockId.HasValue)
                ModelState.AddModelError("InternalTransfer.SourcePottedPlantStockId", "Source Species / Pot Size / Area is required.");
            if (!InternalTransfer.DestinationAreaId.HasValue || InternalTransfer.DestinationAreaId <= 0)
                ModelState.AddModelError("InternalTransfer.DestinationAreaId", "Destination Area is required.");
            if (InternalTransfer.Quantity <= 0)
                ModelState.AddModelError("InternalTransfer.Quantity", "Quantity must be greater than zero.");

            // F1: the chosen source pool's ACTUAL Area (read from the
            // database, not the form) must be one the user is assigned to --
            // previously any user could move any Area's stock out.
            if (ModelState.IsValid)
            {
                int? sourceAreaId = null;
                bool sourceFound;
                if (InternalTransfer.StockType == "EmptyPot")
                {
                    var pool = await _emptyPotInventoryRepo.GetByIdAsync(InternalTransfer.SourceEmptyPotInventoryId!.Value);
                    sourceFound = pool != null;
                    sourceAreaId = pool?.AreaId;
                }
                else
                {
                    var pool = await _pottedPlantStockRepo.GetByIdAsync(InternalTransfer.SourcePottedPlantStockId!.Value);
                    sourceFound = pool != null;
                    sourceAreaId = pool?.AreaId;
                }
                if (!sourceFound || !_areaAccessService.CanAccessRequiredArea(User, sourceAreaId))
                    ModelState.AddModelError(string.Empty, "You are not authorized to transfer stock from the selected source.");
                else
                {
                    var kind = await _supervisors.KindForAreaAsync(sourceAreaId, SupervisorKind.ProductionArea);
                    var supervisorError = await _supervisors.ValidateAsync(kind, sourceAreaId, InternalTransfer.SupervisorId);
                    if (supervisorError != null)
                        ModelState.AddModelError("InternalTransfer.SupervisorId", supervisorError);
                }
            }

            if (!ModelState.IsValid)
            {
                await LoadDropdownsAsync();
                return Page();
            }

            // F1: over-posting guard. Status and PendingConfirmationAreaId
            // are server-controlled (InsertHeaderAsync uses a posted Status
            // verbatim, so "InternalTransfer.Status=PendingConfirmation"
            // could inject a row into another workflow's queue), and the
            // Cutting / confirmation fields never apply to this page.
            InternalTransfer.Status = "Completed"; // the model's own default for EmptyPot/PottedPlant
            InternalTransfer.PendingConfirmationAreaId = null;
            InternalTransfer.SourceCuttingStockId = null;

            // Only the field relevant to the chosen Stock Type is kept --
            // the repository itself also enforces "exactly one of the
            // two source Ids" via the DB CHECK constraint as a backstop.
            if (InternalTransfer.StockType == "EmptyPot")
                InternalTransfer.SourcePottedPlantStockId = null;
            else
                InternalTransfer.SourceEmptyPotInventoryId = null;

            InternalTransfer.ResponsiblePersonId = null;   // Phase 1: no longer entered
            InternalTransfer.CreatedBy = User.Identity?.Name ?? "System";
            var userIdClaim = User.FindFirst("UserId")?.Value;
            int? userId = int.TryParse(userIdClaim, out var parsedUserId) ? parsedUserId : null;

            var (success, message, _) = await _internalTransferRepo.InsertAsync(InternalTransfer, userId);
            if (!success)
            {
                ModelState.AddModelError(string.Empty, message ?? "Failed to record Internal Transfer.");
                await LoadDropdownsAsync();
                return Page();
            }

            TempData["Success"] = $"Internal Transfer {InternalTransfer.TransferCode} recorded successfully. Stock moved.";
            return RedirectToPage("/Production/InternalTransfer/Index");
        }

        private async Task LoadDropdownsAsync()
        {
            var allEmptyPots = await _emptyPotInventoryRepo.GetAllAsync(activeOnly: true);
            EmptyPotPools = allEmptyPots.Where(p => p.AreaId.HasValue && p.PhysicalQuantity > 0 && _areaAccessService.CanAccessArea(User, p.AreaId)).ToList();

            var allPotted = await _pottedPlantStockRepo.GetAllAsync();
            PottedPlantPools = allPotted.Where(p => p.AreaId.HasValue && p.PhysicalQuantity > 0 && _areaAccessService.CanAccessArea(User, p.AreaId)).ToList();

            Areas = await _areaRepo.GetAllAreas();
            Supervisors = await _supervisors.AllAreaKindOptionsAsync();
        }
    }
}
