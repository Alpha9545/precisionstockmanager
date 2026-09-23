using Microsoft.AspNetCore.Mvc.RazorPages;
using PlantStockManager.Data;
using LabRequestModel = PlantStockManager.Models.LabRequest;

namespace PlantStockManager.Pages.Production.LabRequest
{
    public class IndexModel : PageModel
    {
        private readonly LabRequestRepository _labRequestRepo;

        public IndexModel(LabRequestRepository labRequestRepo)
        {
            _labRequestRepo = labRequestRepo;
        }

        public List<LabRequestModel> Requests { get; set; } = new();

        public decimal TotalOutstanding => Requests.Where(r => r.Status == "Sent").Sum(r => r.SentQuantity);

        public async Task OnGetAsync()
        {
            Requests = await _labRequestRepo.GetAllAsync();
        }
    }
}
