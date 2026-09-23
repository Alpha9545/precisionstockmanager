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

        // Area count and Area Size / Capacity totals per Polyhouse,
        // computed live from dbo.Area (requirement: Polyhouse
        // administration must display its Areas and calculate totals
        // from the Area records rather than storing duplicate totals on
        // Polyhouse itself).
        public Dictionary<int, PolyhouseAreaSummary> AreaSummaries { get; set; } = new();

        [BindProperty]
        public string NewPolyhouseName { get; set; }

        [BindProperty]
        public int EditId { get; set; }

        [BindProperty]
        public string EditPolyhouseName { get; set; }

        public async Task OnGetAsync()
        {
            Polyhouses = await _polyhouseRepo.GetAllPolyhouses();
            await BuildAreaSummariesAsync();
        }

        public async Task<IActionResult> OnPostAddAsync()
        {
            if (!string.IsNullOrWhiteSpace(NewPolyhouseName))
            {
                await _polyhouseRepo.AddPolyhouse(NewPolyhouseName);
            }
            return RedirectToPage();
        }

        public async Task<IActionResult> OnPostEditAsync()
        {
            if (EditId > 0 && !string.IsNullOrWhiteSpace(EditPolyhouseName))
            {
                await _polyhouseRepo.UpdatePolyhouse(EditId, EditPolyhouseName);
            }
            return RedirectToPage();
        }

        private async Task BuildAreaSummariesAsync()
        {
            foreach (var polyhouse in Polyhouses)
            {
                var areas = await _areaRepo.GetAreasByPolyhouseId(polyhouse.Id);

                // Areas of the same Polyhouse can use different units (e.g.
                // some in sq.ft, some in sq.m), so totals are grouped by unit
                // instead of being summed together blindly across units.
                var summary = new PolyhouseAreaSummary
                {
                    AreaCount = areas.Count,
                    SizeTotals = areas
                        .Where(a => a.AreaSize.HasValue)
                        .GroupBy(a => string.IsNullOrWhiteSpace(a.AreaUnit) ? "(no unit)" : a.AreaUnit)
                        .Select(g => $"{g.Sum(a => a.AreaSize!.Value):N2} {g.Key}")
                        .ToList(),
                    CapacityTotals = areas
                        .Where(a => a.Capacity.HasValue)
                        .GroupBy(a => string.IsNullOrWhiteSpace(a.CapacityUnit) ? "(no unit)" : a.CapacityUnit)
                        .Select(g => $"{g.Sum(a => a.Capacity!.Value):N2} {g.Key}")
                        .ToList()
                };

                AreaSummaries[polyhouse.Id] = summary;
            }
        }
    }

    // Display-only aggregate computed on the fly from dbo.Area -- never
    // persisted on Polyhouse itself.
    public class PolyhouseAreaSummary
    {
        public int AreaCount { get; set; }
        public List<string> SizeTotals { get; set; } = new();
        public List<string> CapacityTotals { get; set; } = new();
    }
}
