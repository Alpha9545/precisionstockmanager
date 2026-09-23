using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PlantStockManager.Authorization;
using PlantStockManager.Data;
using DispatchModel = PlantStockManager.Models.Dispatch;

namespace PlantStockManager.Pages.Production.Dispatch
{
    public class DetailsModel : PageModel
    {
        private readonly DispatchRepository _dispatchRepo;
        private readonly AreaAccessService _areaAccessService;

        public DetailsModel(DispatchRepository dispatchRepo, AreaAccessService areaAccessService)
        {
            _dispatchRepo = dispatchRepo;
            _areaAccessService = areaAccessService;
        }

        public DispatchModel? Dispatch { get; set; }

        public async Task<IActionResult> OnGetAsync(int id)
        {
            Dispatch = await _dispatchRepo.GetByIdAsync(id);
            if (Dispatch == null)
                return RedirectToPage("/Production/Dispatch/Index");

            if (!_areaAccessService.CanAccessArea(User, Dispatch.AreaId))
            {
                TempData["Error"] = "You are not authorized to view this Dispatch.";
                return RedirectToPage("/Production/Dispatch/Index");
            }

            return Page();
        }
    }
}
