using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PlantStockManager.Data;
using CuttingPlanModel = PlantStockManager.Models.CuttingPlan;

namespace PlantStockManager.Pages.Production.CuttingPlan
{
    public class IndexModel : PageModel
    {
        private readonly CuttingPlanRepository _cuttingPlanRepo;

        public IndexModel(CuttingPlanRepository cuttingPlanRepo)
        {
            _cuttingPlanRepo = cuttingPlanRepo;
        }

        public List<CuttingPlanModel> CuttingPlans { get; set; } = new();

        [BindProperty(SupportsGet = true)] public string? Status { get; set; }

        public int PlannedCount => CuttingPlans.Count(c => c.Status == "Planned");
        public decimal TotalPlannedQuantity => CuttingPlans.Sum(c => c.PlannedQuantity);

        public async Task OnGetAsync()
        {
            CuttingPlans = await _cuttingPlanRepo.GetAllAsync(status: Status);
        }
    }
}
