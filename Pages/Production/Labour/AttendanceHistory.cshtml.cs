using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PlantStockManager.Authorization;
using PlantStockManager.Data;
using PlantStockManager.Models;
using PlantStockManager.Services;

namespace PlantStockManager.Pages.Production.Labour
{
    // Attendance History (Labour.View): one row per location and date with the
    // 9:00 AM / 2:00 PM headcounts and the ESTIMATED Full/Half Day split
    // (DailyLabourRules.Classify), plus the older manual records
    // (dbo.DailyLabourCounts, shown as "Manual"). Incomplete days (one time only)
    // are listed but left out of the Full/Half Day totals. Filtered in SQL and
    // limited to the Areas the user is assigned to (a full-access user sees every
    // Area); Outlet is not part of Daily Labour.
    public class AttendanceHistoryModel : PageModel
    {
        private readonly DailyLabourAttendanceRepository _attendanceRepo;
        private readonly AreaRepository _areaRepo;
        private readonly PolyhouseRepository _polyhouseRepo;
        private readonly AreaAccessService _areaAccess;
        private readonly DatabaseHelper _db;

        public AttendanceHistoryModel(DailyLabourAttendanceRepository attendanceRepo, AreaRepository areaRepo, PolyhouseRepository polyhouseRepo, AreaAccessService areaAccess,
            DatabaseHelper db)
        {
            _db = db;
            _attendanceRepo = attendanceRepo;
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
        public List<DailyLabourDay> Days { get; set; } = new();
        public DailyLabourDaySummary Summary { get; set; } = new();
        public bool Truncated => Summary.Days > Days.Count;
        public bool IsFullAccess => User.IsFullAccess();

        // Days grouped by date (newest first) with that date's totals.
        public IEnumerable<(DateTime Date, List<DailyLabourDay> Rows, DailyLabourDaySummary Total)> ByDate =>
            Days.GroupBy(d => d.LabourDate.Date)
                .OrderByDescending(g => g.Key)
                .Select(g =>
                {
                    var total = new DailyLabourDaySummary();
                    foreach (var d in g) total.Add(d);
                    return (g.Key, g.ToList(), total);
                });

        public async Task OnGetAsync()
        {
            // Default: the last 7 days (India time), today included.
            if (!FromDate.HasValue && !ToDate.HasValue)
            {
                ToDate = AppClock.Today;
                FromDate = AppClock.Today.AddDays(-6);
            }
            if (FromDate > ToDate)
                (FromDate, ToDate) = (ToDate, FromDate);

            var outletAllowed = await LabourAttendanceOutletSchema.ReadyAsync(_db);   // 2026-10-23: Outlet attendance
            AreaOptions = (await _areaRepo.GetAllAreas())
                .Where(a => DailyLabourRules.IsAttendanceArea(a.AreaType, outletAllowed) && _areaAccess.CanAccessRequiredArea(User, a.Id))
                .OrderBy(a => a.Name)
                .ToList();

            // An Area the user may not see, or an Outlet, is ignored, never trusted.
            if (AreaId.HasValue && !_areaAccess.CanAccessRequiredArea(User, AreaId.Value))
                AreaId = null;
            var selectedArea = AreaId.HasValue ? await _areaRepo.GetAreaById(AreaId.Value) : null;
            if (selectedArea != null && !DailyLabourRules.IsAttendanceArea(selectedArea.AreaType, outletAllowed))
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
            (Days, Summary) = await _attendanceRepo.SearchDaysAsync(new DailyLabourFilter
            {
                FromDate = FromDate,
                ToDate = ToDate,
                AreaId = AreaId,
                PolyhouseId = PolyhouseId,
            }, scope);
        }
    }
}
