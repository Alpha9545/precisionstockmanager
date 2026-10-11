using System.Globalization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PlantStockManager.Authorization;
using PlantStockManager.Data;
using PlantStockManager.Models;
using PlantStockManager.Services;

namespace PlantStockManager.Pages.Production.Labour
{
    // Overtime History (Labour.View): every overtime entry matching the filters (one date, or
    // a date range; Area; Polyhouse), with totals and a summary by Date / Month / Area /
    // Polyhouse (male and female worker-hours in each). Filtered in SQL and limited to the
    // Areas the user is assigned to (a full-access user sees every Area); Outlet is not part
    // of Daily Labour. Totals cover every matching entry; at most MaxListRows are listed.
    public class OvertimeHistoryModel : PageModel
    {
        public const int MaxListRows = 1000;
        public static readonly string[] Groupings = { "Date", "Month", "Area", "Polyhouse" };

        private readonly LabourOvertimeRepository _overtimeRepo;
        private readonly AreaRepository _areaRepo;
        private readonly PolyhouseRepository _polyhouseRepo;
        private readonly AreaAccessService _areaAccess;

        public OvertimeHistoryModel(LabourOvertimeRepository overtimeRepo, AreaRepository areaRepo, PolyhouseRepository polyhouseRepo, AreaAccessService areaAccess)
        {
            _overtimeRepo = overtimeRepo;
            _areaRepo = areaRepo;
            _polyhouseRepo = polyhouseRepo;
            _areaAccess = areaAccess;
        }

        // Date = one day (overrides From/To); otherwise From/To (default: this month, India time).
        [BindProperty(SupportsGet = true)] public DateTime? Date { get; set; }
        [BindProperty(SupportsGet = true)] public DateTime? FromDate { get; set; }
        [BindProperty(SupportsGet = true)] public DateTime? ToDate { get; set; }
        [BindProperty(SupportsGet = true)] public int? AreaId { get; set; }
        [BindProperty(SupportsGet = true)] public int? PolyhouseId { get; set; }
        [BindProperty(SupportsGet = true)] public string? GroupBy { get; set; }

        public List<Area> AreaOptions { get; set; } = new();
        public List<Polyhouse> PolyhouseOptions { get; set; } = new();
        public bool ShowPolyhouseFilter { get; set; }
        public List<LabourOvertimeEntry> Entries { get; set; } = new();
        public int MatchingEntries { get; set; }
        public LabourOvertimeSummary Total { get; set; } = new();
        public List<LabourOvertimeSummary> Summary { get; set; } = new();

        public async Task OnGetAsync()
        {
            if (Date.HasValue)
            {
                FromDate = Date.Value.Date;
                ToDate = Date.Value.Date;
            }
            else if (!FromDate.HasValue && !ToDate.HasValue)
            {
                var today = AppClock.Today;
                FromDate = new DateTime(today.Year, today.Month, 1);
                ToDate = today;
            }
            if (FromDate > ToDate)
                (FromDate, ToDate) = (ToDate, FromDate);
            if (!Groupings.Contains(GroupBy))
                GroupBy = "Date";

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
            var all = await _overtimeRepo.SearchAsync(new LabourOvertimeFilter { FromDate = FromDate, ToDate = ToDate, AreaId = AreaId, PolyhouseId = PolyhouseId }, scope);

            MatchingEntries = all.Count;
            foreach (var e in all) Total.Add(e);
            Entries = all.Take(MaxListRows).ToList();
            Summary = GroupBy switch
            {
                "Month" => LabourOvertimeSummary.GroupBy(all, e => e.LabourDate.ToString("yyyy-MM", CultureInfo.InvariantCulture)).OrderByDescending(s => s.Key)
                    .Select(s => { s.Key = DateTime.ParseExact(s.Key + "-01", "yyyy-MM-dd", CultureInfo.InvariantCulture).ToString("MMMM yyyy", CultureInfo.InvariantCulture); return s; }).ToList(),
                "Area" => LabourOvertimeSummary.GroupBy(all, e => e.AreaName ?? "").OrderBy(s => s.Key).ToList(),
                "Polyhouse" => LabourOvertimeSummary.GroupBy(all, e => e.PolyhouseId.HasValue ? $"{e.AreaName} / {e.PolyhouseName}" : $"{e.AreaName} (Area-wise)").OrderBy(s => s.Key).ToList(),
                _ => LabourOvertimeSummary.GroupBy(all, e => e.LabourDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)).OrderByDescending(s => s.Key)
                    .Select(s => { s.Key = DateTime.ParseExact(s.Key, "yyyy-MM-dd", CultureInfo.InvariantCulture).ToString("dd-MM-yyyy", CultureInfo.InvariantCulture); return s; }).ToList(),
            };
        }
    }
}
