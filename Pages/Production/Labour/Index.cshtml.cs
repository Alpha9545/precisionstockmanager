using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PlantStockManager.Authorization;
using PlantStockManager.Data;
using PlantStockManager.Models;
using PlantStockManager.Services;

namespace PlantStockManager.Pages.Production.Labour
{
    // Labour Records (Labour.View): daily labour headcounts, filtered in SQL
    // and limited to the Areas the user is assigned to (a full-access user sees
    // every Area). Outlet is not part of Daily Labour. The summary covers every
    // matching record.
    public class IndexModel : PageModel
    {
        private readonly DailyLabourCountRepository _labourRepo;
        private readonly AreaRepository _areaRepo;
        private readonly PolyhouseRepository _polyhouseRepo;
        private readonly AreaAccessService _areaAccess;

        public IndexModel(DailyLabourCountRepository labourRepo, AreaRepository areaRepo, PolyhouseRepository polyhouseRepo, AreaAccessService areaAccess)
        {
            _labourRepo = labourRepo;
            _areaRepo = areaRepo;
            _polyhouseRepo = polyhouseRepo;
            _areaAccess = areaAccess;
        }

        [BindProperty(SupportsGet = true)] public DateTime? FromDate { get; set; }
        [BindProperty(SupportsGet = true)] public DateTime? ToDate { get; set; }
        [BindProperty(SupportsGet = true)] public int? AreaId { get; set; }
        [BindProperty(SupportsGet = true)] public int? PolyhouseId { get; set; }

        public List<Area> AreaOptions { get; set; } = new();
        public List<Polyhouse> PolyhouseOptions { get; set; } = new();
        public bool ShowPolyhouseFilter { get; set; }
        public List<DailyLabourCount> Records { get; set; } = new();
        public DailyLabourSummary Summary { get; set; } = new();
        public bool Truncated => Summary.Records > Records.Count;

        public async Task OnGetAsync()
        {
            // Default: the last 7 days, today included.
            if (!FromDate.HasValue && !ToDate.HasValue)
            {
                ToDate = DateTime.Today;
                FromDate = DateTime.Today.AddDays(-6);
            }
            if (FromDate > ToDate)
                (FromDate, ToDate) = (ToDate, FromDate);

            AreaOptions = (await _areaRepo.GetAllAreas())
                .Where(a => DailyLabourRules.IsLabourArea(a.AreaType) && _areaAccess.CanAccessRequiredArea(User, a.Id))
                .OrderBy(a => a.Name)
                .ToList();

            // An Area the user may not see, or an Outlet, is ignored, never trusted.
            if (AreaId.HasValue && !_areaAccess.CanAccessRequiredArea(User, AreaId.Value))
                AreaId = null;

            var selectedArea = AreaId.HasValue ? await _areaRepo.GetAreaById(AreaId.Value) : null;
            if (selectedArea != null && !DailyLabourRules.IsLabourArea(selectedArea.AreaType))
            {
                AreaId = null;
                selectedArea = null;
            }
            ShowPolyhouseFilter = selectedArea != null && DailyLabourRules.RequiresPolyhouse(selectedArea.AreaType);
            if (ShowPolyhouseFilter)
                PolyhouseOptions = (await _polyhouseRepo.GetByAreaIdAsync(selectedArea!.Id)).OrderBy(p => p.Name).ToList();
            if (!ShowPolyhouseFilter || !PolyhouseOptions.Any(p => p.Id == PolyhouseId))
                PolyhouseId = null;

            var scope = _areaAccess.HasFullAreaAccess(User) ? null : _areaAccess.GetAccessibleAreaIds(User);
            (Records, Summary) = await _labourRepo.SearchAsync(new DailyLabourFilter
            {
                FromDate = FromDate,
                ToDate = ToDate,
                AreaId = AreaId,
                PolyhouseId = PolyhouseId,
            }, scope);
        }
    }
}
