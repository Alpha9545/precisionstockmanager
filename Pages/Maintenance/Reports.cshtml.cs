using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PlantStockManager.Authorization;
using PlantStockManager.Data;
using PlantStockManager.Models;
using PlantStockManager.Services;

namespace PlantStockManager.Pages.Maintenance
{
    // Maintenance Reports (Maintenance.View / Maintenance.Manage): ticket counts by status and
    // priority, overdue tickets, and worker- / category- / Area- / Polyhouse-wise tickets, work
    // hours and average response / on-site / resolution times. Filters: requested date range
    // (India dates), category, worker, Area, Polyhouse, status, priority. Supervisors see their
    // own Areas only; a manager / full-access user sees every Area.
    public class ReportsModel : PageModel
    {
        private readonly MaintenanceRepository _repo;
        private readonly AreaRepository _areaRepo;
        private readonly PolyhouseRepository _polyhouseRepo;
        private readonly AreaAccessService _areaAccess;
        private readonly MaintenanceAuthorization _auth;

        public ReportsModel(MaintenanceRepository repo, AreaRepository areaRepo, PolyhouseRepository polyhouseRepo, AreaAccessService areaAccess, MaintenanceAuthorization auth)
        {
            _repo = repo;
            _areaRepo = areaRepo;
            _polyhouseRepo = polyhouseRepo;
            _areaAccess = areaAccess;
            _auth = auth;
        }

        [BindProperty(SupportsGet = true)] public DateTime? FromDate { get; set; }
        [BindProperty(SupportsGet = true)] public DateTime? ToDate { get; set; }
        [BindProperty(SupportsGet = true)] public int? CategoryId { get; set; }
        [BindProperty(SupportsGet = true)] public int? WorkerId { get; set; }
        [BindProperty(SupportsGet = true)] public int? AreaId { get; set; }
        [BindProperty(SupportsGet = true)] public int? PolyhouseId { get; set; }
        [BindProperty(SupportsGet = true)] public string? Status { get; set; }
        [BindProperty(SupportsGet = true)] public string? Priority { get; set; }

        public List<MaintenanceCategory> Categories { get; set; } = new();
        public List<MaintenanceWorker> Workers { get; set; } = new();
        public List<Area> AreaOptions { get; set; } = new();
        public List<Polyhouse> PolyhouseOptions { get; set; } = new();
        public List<MaintenanceTicket> Tickets { get; set; } = new();
        public Dictionary<string, int> Counts { get; set; } = new();
        public List<MaintenanceTicket> OverdueTickets { get; set; } = new();
        public List<MaintenanceReportRow> ByWorker { get; set; } = new();
        public List<MaintenanceReportRow> ByCategory { get; set; } = new();
        public List<MaintenanceReportRow> ByArea { get; set; } = new();
        public List<MaintenanceReportRow> ByPolyhouse { get; set; } = new();
        public MaintenanceReportRow Overall { get; set; } = new();

        public async Task OnGetAsync()
        {
            if (!FromDate.HasValue && !ToDate.HasValue)
            {
                var today = AppClock.Today;
                FromDate = new DateTime(today.Year, today.Month, 1);
                ToDate = today;
            }
            if (FromDate > ToDate)
                (FromDate, ToDate) = (ToDate, FromDate);
            if (!MaintenanceRules.Statuses.Contains(Status)) Status = null;
            if (!MaintenanceRules.Priorities.Contains(Priority)) Priority = null;

            var allAreas = _auth.IsManager(User) || _areaAccess.HasFullAreaAccess(User);
            AreaOptions = (await _areaRepo.GetAllAreas()).Where(a => allAreas || _areaAccess.CanAccessRequiredArea(User, a.Id)).OrderBy(a => a.Name).ToList();
            if (AreaId.HasValue && !AreaOptions.Any(a => a.Id == AreaId))
                AreaId = null;   // another Area's id is ignored, never trusted
            if (AreaId.HasValue)
                PolyhouseOptions = (await _polyhouseRepo.GetByAreaIdAsync(AreaId.Value)).OrderBy(p => p.Name).ToList();
            if (!PolyhouseOptions.Any(p => p.Id == PolyhouseId))
                PolyhouseId = null;

            Categories = await _repo.GetCategoriesAsync(activeOnly: false);
            foreach (var c in Categories)
                foreach (var w in await _repo.GetWorkersForCategoryAsync(c.Id))
                    if (!Workers.Any(x => x.Id == w.Id)) Workers.Add(w);
            Workers = Workers.OrderBy(w => w.Name).ToList();

            var scope = allAreas ? null : _areaAccess.GetAccessibleAreaIds(User);
            Tickets = await _repo.SearchAsync(new MaintenanceTicketFilter
            {
                FromDate = FromDate, ToDate = ToDate, CategoryId = CategoryId, WorkerId = WorkerId,
                AreaId = AreaId, PolyhouseId = PolyhouseId, Status = Status, Priority = Priority,
            }, scope);

            var now = DateTime.UtcNow;
            var todayIndia = AppClock.Today;
            OverdueTickets = Tickets.Where(t => t.OverdueReason(now, todayIndia) != null).ToList();
            Counts = new Dictionary<string, int>
            {
                ["Total"] = Tickets.Count,
                ["Open"] = Tickets.Count(t => t.Status == MaintenanceRules.Open),
                ["Accepted"] = Tickets.Count(t => t.Status == MaintenanceRules.Accepted),
                ["Pending Visit"] = Tickets.Count(t => t.Status is MaintenanceRules.Accepted or MaintenanceRules.Arrived),
                ["In Progress"] = Tickets.Count(t => t.Status is MaintenanceRules.ArrivalConfirmed or MaintenanceRules.InProgress),
                ["Resolved"] = Tickets.Count(t => t.Status == MaintenanceRules.Resolved),
                ["Closed"] = Tickets.Count(t => t.Status == MaintenanceRules.Closed),
                ["Cancelled"] = Tickets.Count(t => t.Status == MaintenanceRules.Cancelled),
                ["Overdue"] = OverdueTickets.Count,
                ["Urgent"] = Tickets.Count(t => t.Priority == "Urgent"),
                ["Critical"] = Tickets.Count(t => t.Priority == "Critical"),
            };
            ByWorker = MaintenanceReportRow.Build(Tickets.Where(t => t.AssignedWorkerId.HasValue), t => t.AssignedWorkerName ?? $"User {t.AssignedWorkerId}", now, todayIndia);
            ByCategory = MaintenanceReportRow.Build(Tickets, t => t.CategoryName, now, todayIndia);
            ByArea = MaintenanceReportRow.Build(Tickets, t => t.AreaName ?? "", now, todayIndia);
            ByPolyhouse = MaintenanceReportRow.Build(Tickets, t => t.PolyhouseId.HasValue ? $"{t.AreaName} / {t.PolyhouseName}" : $"{t.AreaName} (whole Area)", now, todayIndia);
            Overall = MaintenanceReportRow.Build(Tickets, _ => "All", now, todayIndia).FirstOrDefault() ?? new MaintenanceReportRow { Key = "All" };
        }
    }
}
