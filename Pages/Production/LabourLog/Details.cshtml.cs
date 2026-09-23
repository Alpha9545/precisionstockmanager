using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PlantStockManager.Data;
using LabourLogModel = PlantStockManager.Models.LabourLog;

namespace PlantStockManager.Pages.Production.LabourLog
{
    public class DetailsModel : PageModel
    {
        private readonly LabourLogRepository _labourLogRepo;

        public DetailsModel(LabourLogRepository labourLogRepo)
        {
            _labourLogRepo = labourLogRepo;
        }

        public LabourLogModel? Log { get; set; }

        public async Task<IActionResult> OnGetAsync(int id)
        {
            Log = await _labourLogRepo.GetByIdAsync(id);
            if (Log == null)
                return RedirectToPage("/Production/LabourLog/Index");
            return Page();
        }
    }
}
