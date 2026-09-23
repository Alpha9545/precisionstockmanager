using Microsoft.AspNetCore.Mvc.RazorPages;
using PlantStockManager.Data;
using ActualCuttingModel = PlantStockManager.Models.ActualCutting;

namespace PlantStockManager.Pages.Production.ActualCutting
{
    public class IndexModel : PageModel
    {
        private readonly ActualCuttingRepository _actualCuttingRepo;

        public IndexModel(ActualCuttingRepository actualCuttingRepo)
        {
            _actualCuttingRepo = actualCuttingRepo;
        }

        public List<ActualCuttingModel> ActualCuttings { get; set; } = new();

        public decimal TotalGood => ActualCuttings.Sum(a => a.GoodQuantity);
        public decimal TotalDamaged => ActualCuttings.Sum(a => a.DamagedQuantity);
        public decimal TotalRejected => ActualCuttings.Sum(a => a.RejectedQuantity);

        public async Task OnGetAsync()
        {
            ActualCuttings = await _actualCuttingRepo.GetAllAsync();
        }
    }
}
