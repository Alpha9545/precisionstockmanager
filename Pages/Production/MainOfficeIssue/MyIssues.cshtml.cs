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

namespace PlantStockManager.Pages.Production.MainOfficeIssue
{
    // Main Office's own sent-history view -- mirrors CuttingStock's
    // "My Transactions" idea, but for MainOfficeIssue transfers. Reuses
    // InternalTransferRepository.GetByAreaAsync (already fully generic --
    // no change needed there) and filters to this StockType in memory,
    // exactly the same reuse pattern the redesign has used elsewhere
    // rather than adding a StockType parameter to a shared method.
    public class MyIssuesModel : PageModel
    {
        private readonly InternalTransferRepository _internalTransferRepo;
        private readonly AreaRepository _areaRepo;
        private readonly AreaAccessService _areaAccessService;

        public MyIssuesModel(
            InternalTransferRepository internalTransferRepo,
            AreaRepository areaRepo,
            AreaAccessService areaAccessService)
        {
            _internalTransferRepo = internalTransferRepo;
            _areaRepo = areaRepo;
            _areaAccessService = areaAccessService;
        }

        public List<Area> MainOfficeAreas { get; set; } = new();
        public List<InternalTransferModel> Issues { get; set; } = new();
        public int? SelectedAreaId { get; set; }

        public async Task<IActionResult> OnGetAsync(int? areaId)
        {
            if (areaId.HasValue && !_areaAccessService.CanAccessArea(User, areaId))
            {
                TempData["Error"] = "You are not authorized to view issues for the selected Area.";
                return RedirectToPage("/Production/MainOfficeIssue/MyIssues");
            }

            SelectedAreaId = areaId;

            var allMainOffice = await _areaRepo.GetByAreaTypesAsync("MainOffice");
            MainOfficeAreas = _areaAccessService.HasFullAreaAccess(User)
                ? allMainOffice
                : allMainOffice.Where(a => _areaAccessService.CanAccessArea(User, a.Id)).ToList();

            if (areaId.HasValue)
            {
                Issues = (await _internalTransferRepo.GetByAreaAsync(areaId.Value))
                    .Where(t => t.StockType == "MainOfficeIssue")
                    .ToList();
            }

            return Page();
        }
    }
}
