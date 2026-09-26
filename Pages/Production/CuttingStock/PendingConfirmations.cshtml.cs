using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PlantStockManager.Authorization;
using PlantStockManager.Data;
using PlantStockManager.Models;
// Alias required: Pages/Production/InternalTransfer/ makes "InternalTransfer"
// a sibling namespace under PlantStockManager.Pages.Production, which
// shadows the bare Models.InternalTransfer type (CS0118). Same fix already
// used elsewhere in this codebase (see PotProduction/CreateFromCutting.cshtml.cs).
using InternalTransferModel = PlantStockManager.Models.InternalTransfer;

namespace PlantStockManager.Pages.Production.CuttingStock
{
    // "Main Office Pending Cutting" -- the confirmation queue. Main Office
    // either confirms receipt (-> ConfirmReceipt.cshtml, which immediately
    // moves the received cuttings into Main Office Cutting Stock) or rejects it here.
    public class PendingConfirmationsModel : PageModel
    {
        private readonly InternalTransferRepository _internalTransferRepo;
        private readonly AreaRepository _areaRepo;
        private readonly AreaAccessService _areaAccessService;

        public PendingConfirmationsModel(InternalTransferRepository internalTransferRepo, AreaRepository areaRepo, AreaAccessService areaAccessService)
        {
            _areaAccessService = areaAccessService;
            _internalTransferRepo = internalTransferRepo;
            _areaRepo = areaRepo;
        }

        public List<Area> MainOfficeAreas { get; set; } = new();
        public List<InternalTransferModel> Pending { get; set; } = new();
        public int? SelectedAreaId { get; set; }

        public async Task<IActionResult> OnGetAsync(int? areaId)
        {
            if (areaId.HasValue && !_areaAccessService.CanAccessArea(User, areaId))
            {
                TempData["Error"] = "You are not authorized to view the queue for the selected Area.";
                return RedirectToPage("/Production/CuttingStock/PendingConfirmations");
            }

            SelectedAreaId = areaId;
            MainOfficeAreas = _areaAccessService.FilterByArea(User, await _areaRepo.GetByAreaTypesAsync("MainOffice"), a => (int?)a.Id);
            Pending = FilterToAccessible(await _internalTransferRepo.GetPendingConfirmationsAsync(areaId));
            return Page();
        }

        public async Task<IActionResult> OnPostRejectAsync(int id, string reason, int? areaId)
        {
            // F1: re-load the transfer and verify it is (a) a CUTTING transfer
            // -- this handler previously rejected ANY pending transfer id,
            // including Main Office Issue / Growing Partner -> Outlet ones,
            // bypassing their own Area-checked pages -- (b) still pending,
            // and (c) waiting at an Area this user may act for.
            var transfer = await _internalTransferRepo.GetByIdAsync(id);
            if (transfer == null
                || transfer.StockType != "Cutting"
                || transfer.Status != "PendingConfirmation"
                || !_areaAccessService.CanAccessRequiredArea(User, transfer.PendingConfirmationAreaId))
            {
                TempData["Error"] = "You are not authorized to reject that transfer.";
                return RedirectToPage("/Production/CuttingStock/PendingConfirmations", new { areaId });
            }

            var (success, message) = await _internalTransferRepo.RejectAsync(id, reason, User.Identity?.Name ?? "System");
            TempData[success ? "Success" : "Error"] = success ? "Transfer rejected." : (message ?? "Failed to reject.");
            return RedirectToPage("/Production/CuttingStock/PendingConfirmations", new { areaId });
        }

        // F1: shared Area filter for this Main Office queue: an explicit
        // areaId must be accessible; without one, a user without cross-Area
        // access only sees transfers waiting at their own Area(s).
        private List<InternalTransferModel> FilterToAccessible(List<InternalTransferModel> items)
            => _areaAccessService.HasFullAreaAccess(User)
                ? items
                : items.Where(t => _areaAccessService.CanAccessRequiredArea(User, t.PendingConfirmationAreaId)).ToList();
    }
}
