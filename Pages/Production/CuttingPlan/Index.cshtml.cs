using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PlantStockManager.Authorization;
using PlantStockManager.Data;
using CuttingPlanModel = PlantStockManager.Models.CuttingPlan;

namespace PlantStockManager.Pages.Production.CuttingPlan
{
    public class IndexModel : PageModel
    {
        private readonly CuttingPlanRepository _cuttingPlanRepo;
        private readonly MotherPlantAreaScope _areaScope;

        public IndexModel(CuttingPlanRepository cuttingPlanRepo, MotherPlantAreaScope areaScope)
        {
            _areaScope = areaScope;
            _cuttingPlanRepo = cuttingPlanRepo;
        }

        public List<CuttingPlanModel> CuttingPlans { get; set; } = new();

        [BindProperty(SupportsGet = true)] public string? Status { get; set; }

        public int PlannedCount => CuttingPlans.Count(c => c.Status == "Planned");
        public decimal TotalPlannedQuantity => CuttingPlans.Sum(c => c.PlannedQuantity);

        public async Task OnGetAsync()
        {
            // F1: Area scope via the record's Mother Plant (MotherPlantAreaScope): previously every Area's
            // records were listed to every user.
            CuttingPlans = await _areaScope.FilterAsync(User, await _cuttingPlanRepo.GetAllAsync(status: Status), r => (int?)r.MotherPlantId);
        }
    }
}
