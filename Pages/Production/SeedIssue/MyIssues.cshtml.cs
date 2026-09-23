using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PlantStockManager.Authorization;
using PlantStockManager.Data;
using PlantStockManager.Models;

namespace PlantStockManager.Pages.Production.SeedIssue
{
    // Main Office's own sent-history view -- mirrors MainOfficeIssue/
    // MyIssues exactly, but reads from the dedicated SeedIssueRepository
    // rather than filtering InternalTransferRepository by StockType.
    public class MyIssuesModel : PageModel
    {
        private readonly SeedIssueRepository _seedIssueRepo;
        private readonly AreaRepository _areaRepo;
        private readonly AreaAccessService _areaAccessService;

        public MyIssuesModel(
            SeedIssueRepository seedIssueRepo,
            AreaRepository areaRepo,
            AreaAccessService areaAccessService)
        {
            _seedIssueRepo = seedIssueRepo;
            _areaRepo = areaRepo;
            _areaAccessService = areaAccessService;
        }

        public List<Area> MainOfficeAreas { get; set; } = new();
        public List<PlantStockManager.Models.SeedIssue> Issues { get; set; } = new();
        public int? SelectedAreaId { get; set; }

        public async Task<IActionResult> OnGetAsync(int? areaId)
        {
            if (areaId.HasValue && !_areaAccessService.CanAccessArea(User, areaId))
            {
                TempData["Error"] = "You are not authorized to view issues for the selected Area.";
                return RedirectToPage("/Production/SeedIssue/MyIssues");
            }

            SelectedAreaId = areaId;

            var allMainOffice = await _areaRepo.GetByAreaTypesAsync("MainOffice");
            MainOfficeAreas = _areaAccessService.HasFullAreaAccess(User)
                ? allMainOffice
                : allMainOffice.Where(a => _areaAccessService.CanAccessArea(User, a.Id)).ToList();

            if (areaId.HasValue)
            {
                Issues = await _seedIssueRepo.GetByAreaAsync(areaId.Value);
            }

            return Page();
        }
    }
}
