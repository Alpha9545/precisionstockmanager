using Microsoft.AspNetCore.Mvc.RazorPages;
using PlantStockManager.Authorization;
using PlantStockManager.Data;
using ActualCuttingModel = PlantStockManager.Models.ActualCutting;

namespace PlantStockManager.Pages.Production.ActualCutting
{
    public class IndexModel : PageModel
    {
        private readonly ActualCuttingRepository _actualCuttingRepo;
        private readonly MotherPlantAreaScope _areaScope;

        public IndexModel(ActualCuttingRepository actualCuttingRepo, MotherPlantAreaScope areaScope)
        {
            _areaScope = areaScope;
            _actualCuttingRepo = actualCuttingRepo;
        }

        public List<ActualCuttingModel> ActualCuttings { get; set; } = new();

        public decimal TotalGood => ActualCuttings.Sum(a => a.GoodQuantity);
        public decimal TotalDamaged => ActualCuttings.Sum(a => a.DamagedQuantity);
        public decimal TotalRejected => ActualCuttings.Sum(a => a.RejectedQuantity);

        public async Task OnGetAsync()
        {
            // F1: Area scope via the record's Mother Plant (MotherPlantAreaScope): previously every Area's
            // records were listed to every user.
            ActualCuttings = await _areaScope.FilterAsync(User, await _actualCuttingRepo.GetAllAsync(), r => (int?)r.MotherPlantId);
        }
    }
}
