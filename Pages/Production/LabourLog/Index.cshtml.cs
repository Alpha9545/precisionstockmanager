using Microsoft.AspNetCore.Mvc.RazorPages;
using PlantStockManager.Data;
using LabourLogModel = PlantStockManager.Models.LabourLog;

namespace PlantStockManager.Pages.Production.LabourLog
{
    public class IndexModel : PageModel
    {
        private readonly LabourLogRepository _labourLogRepo;

        public IndexModel(LabourLogRepository labourLogRepo)
        {
            _labourLogRepo = labourLogRepo;
        }

        public List<LabourLogModel> Logs { get; set; } = new();

        public decimal TotalWage => Logs.Where(l => l.Status == "Recorded").Sum(l => l.TotalWage);

        public async Task OnGetAsync()
        {
            Logs = await _labourLogRepo.GetAllAsync();
        }
    }
}
