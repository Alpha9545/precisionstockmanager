using Microsoft.AspNetCore.Mvc.RazorPages;
using PlantStockManager.Authorization;
using PlantStockManager.Data;
using LabRequestModel = PlantStockManager.Models.LabRequest;

namespace PlantStockManager.Pages.Production.LabRequest
{
    public class IndexModel : PageModel
    {
        private readonly LabRequestRepository _labRequestRepo;
        private readonly AreaAccessService _areaAccessService;

        public IndexModel(LabRequestRepository labRequestRepo, AreaAccessService areaAccessService)
        {
            _areaAccessService = areaAccessService;
            _labRequestRepo = labRequestRepo;
        }

        // F1: a Lab Request belongs to the Area of the stock pool its sample
        // came from (Request.AreaId, server-derived). Previously every page
        // here trusted the id alone. Access = that Area (AreaAccessService),
        // OR the existing Phase 14 lab permissions -- the LabWorker role is
        // deliberately NOT Area-scoped (Phase 17/B: "assignments with no
        // AreaId, e.g. Admin/Management/LabWorker"), so lab staff keep
        // working across Areas exactly as designed.
        private bool CanAccessLabRequest(int? areaId, bool write)
            => _areaAccessService.CanAccessArea(User, areaId)
               || User.HasPermission("Lab.Enter")
               || (!write && User.HasPermission("Lab.View"));

        public List<LabRequestModel> Requests { get; set; } = new();

        public decimal TotalOutstanding => Requests.Where(r => r.Status == "Sent").Sum(r => r.SentQuantity);

        public async Task OnGetAsync()
        {
            Requests = (await _labRequestRepo.GetAllAsync()).Where(r => CanAccessLabRequest(r.AreaId, write: false)).ToList(); // F1
        }
    }
}
