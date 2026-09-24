using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PlantStockManager.Authorization;
using PlantStockManager.Data;
using LabourLogModel = PlantStockManager.Models.LabourLog;

namespace PlantStockManager.Pages.Production.LabourLog
{
    public class DetailsModel : PageModel
    {
        private readonly LabourLogRepository _labourLogRepo;
        private readonly AreaAccessService _areaAccessService;

        public DetailsModel(LabourLogRepository labourLogRepo, AreaAccessService areaAccessService)
        {
            _areaAccessService = areaAccessService;
            _labourLogRepo = labourLogRepo;
        }

        public LabourLogModel? Log { get; set; }

        public async Task<IActionResult> OnGetAsync(int id)
        {
            Log = await _labourLogRepo.GetByIdAsync(id);
            if (Log == null)
                return RedirectToPage("/Production/LabourLog/Index");
            // F1: block viewing another Area's Labour Log by id.
            if (!_areaAccessService.CanAccessArea(User, Log.AreaId))
            {
                TempData["Error"] = "You are not authorized to view this Labour Log.";
                return RedirectToPage("/Production/LabourLog/Index");
            }
            return Page();
        }
    }
}
