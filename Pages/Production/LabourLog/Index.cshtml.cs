using Microsoft.AspNetCore.Mvc.RazorPages;
using PlantStockManager.Authorization;
using PlantStockManager.Data;
using LabourLogModel = PlantStockManager.Models.LabourLog;

namespace PlantStockManager.Pages.Production.LabourLog
{
    public class IndexModel : PageModel
    {
        private readonly LabourLogRepository _labourLogRepo;
        private readonly AreaAccessService _areaAccessService;

        public IndexModel(LabourLogRepository labourLogRepo, AreaAccessService areaAccessService)
        {
            _areaAccessService = areaAccessService;
            _labourLogRepo = labourLogRepo;
        }

        public List<LabourLogModel> Logs { get; set; } = new();

        public decimal TotalWage => Logs.Where(l => l.Status == "Recorded").Sum(l => l.TotalWage);

        public async Task OnGetAsync()
        {
            // F1: only Labour Logs of the user's Area(s) (AreaAccessService).
            Logs = _areaAccessService.FilterByArea(User, await _labourLogRepo.GetAllAsync(), l => l.AreaId);
        }
    }
}
