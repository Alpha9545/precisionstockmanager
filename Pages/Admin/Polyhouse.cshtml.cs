using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PlantStockManager.Data;
using PlantStockManager.Models;

namespace PlantStockManager.Pages.Admin
{
    //[Authorize(Policy = "Admin")]

    public class PolyhouseModel : PageModel
    {
        private readonly PolyhouseRepository _polyhouseRepo;
        private readonly AreaRepository _areaRepo;

        public PolyhouseModel(PolyhouseRepository polyhouseRepo, AreaRepository areaRepo)
        {
            _polyhouseRepo = polyhouseRepo;
            _areaRepo = areaRepo;
        }

        public List<Polyhouse> Polyhouses { get; set; } = new();

        // Phase B: Area (site) choices for "this Polyhouse belongs to Area".
        public List<Area> Areas { get; set; } = new();

        // Area count and Area Size / Capacity totals per Polyhouse,
        // computed live from dbo.Area (requirement: Polyhouse
        // administration must display its Areas and calculate totals
        // from the Area records rather than storing duplicate totals on
        // Polyhouse itself).

        [BindProperty]
        public string NewPolyhouseName { get; set; }

        [BindProperty]
        public int? NewAreaId { get; set; }

        [BindProperty]
        public int? EditAreaId { get; set; }

        [BindProperty]
        public int EditId { get; set; }

        [BindProperty]
        public string EditPolyhouseName { get; set; }

        public async Task OnGetAsync()
        {
            Polyhouses = await _polyhouseRepo.GetAllPolyhouses();
            Areas = await _areaRepo.GetAllAreas();
        }

        public async Task<IActionResult> OnPostAddAsync()
        {
            if (!string.IsNullOrWhiteSpace(NewPolyhouseName))
            {
                await _polyhouseRepo.AddPolyhouse(NewPolyhouseName, await ValidAreaIdOrNullAsync(NewAreaId));
            }
            return RedirectToPage();
        }

        public async Task<IActionResult> OnPostEditAsync()
        {
            if (EditId > 0 && !string.IsNullOrWhiteSpace(EditPolyhouseName))
            {
                await _polyhouseRepo.UpdatePolyhouse(EditId, EditPolyhouseName, await ValidAreaIdOrNullAsync(EditAreaId));
            }
            return RedirectToPage();
        }

        // Only an existing Area id is stored (never a tampered value).
        private async Task<int?> ValidAreaIdOrNullAsync(int? areaId)
            => areaId.HasValue && areaId.Value > 0 && await _areaRepo.GetAreaById(areaId.Value) != null ? areaId : null;
    }
}
