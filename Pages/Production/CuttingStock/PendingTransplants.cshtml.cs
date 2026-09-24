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
    // "Pending Transplants" -- Cutting transfers whose receipt Main
    // Office has confirmed but which have not yet been routed to a
    // Destination Polyhouse. A transfer sits here for however long it
    // takes; it is never dropped and never silently counted as done.
    public class PendingTransplantsModel : PageModel
    {
        private readonly InternalTransferRepository _internalTransferRepo;
        private readonly AreaRepository _areaRepo;
        private readonly AreaAccessService _areaAccessService;

        public PendingTransplantsModel(InternalTransferRepository internalTransferRepo, AreaRepository areaRepo, AreaAccessService areaAccessService)
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
            // F1: Area-scoped like every other pending queue.
            if (areaId.HasValue && !_areaAccessService.CanAccessArea(User, areaId))
            {
                TempData["Error"] = "You are not authorized to view the queue for the selected Area.";
                return RedirectToPage("/Production/CuttingStock/PendingTransplants");
            }

            SelectedAreaId = areaId;
            MainOfficeAreas = _areaAccessService.FilterByArea(User, await _areaRepo.GetByAreaTypesAsync("MainOffice"), a => (int?)a.Id);
            Pending = FilterToAccessible(await _internalTransferRepo.GetPendingTransplantsAsync(areaId));
            return Page();
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
