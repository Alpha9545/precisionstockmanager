using PlantStockManager.Services;

namespace PlantStockManager.Models
{
    // dbo.MaintenanceCategories: a trade and the worker ROLE that may take its tickets.
    public class MaintenanceCategory
    {
        public int Id { get; set; }
        public string Name { get; set; } = string.Empty;
        public int WorkerRoleId { get; set; }
        public string WorkerRoleName { get; set; } = string.Empty;
        public bool IsActive { get; set; }
    }

    // One row of dbo.MaintenanceTickets. Every *At is UTC, written by the database (SYSUTCDATETIME()).
    public class MaintenanceTicket
    {
        public int Id { get; set; }
        public string TicketNo { get; set; } = string.Empty;
        public int CategoryId { get; set; }
        public string CategoryName { get; set; } = string.Empty;
        public string WorkerRoleName { get; set; } = string.Empty;
        public int AreaId { get; set; }
        public string? AreaName { get; set; }
        public int? PolyhouseId { get; set; }
        public string? PolyhouseName { get; set; }
        public string Title { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public string Priority { get; set; } = "Normal";
        public string Status { get; set; } = MaintenanceRules.Open;
        public int RequestedById { get; set; }
        public string? RequestedByName { get; set; }
        public DateTime RequestedAt { get; set; }
        public int? AssignedWorkerId { get; set; }
        public string? AssignedWorkerName { get; set; }
        public DateTime? AcceptedAt { get; set; }
        public DateTime? ExpectedVisitDate { get; set; }
        public string? ExpectedVisitRemarks { get; set; }
        public DateTime? ArrivedAt { get; set; }
        public int? ArrivalConfirmedById { get; set; }
        public string? ArrivalConfirmedByName { get; set; }
        public DateTime? ArrivalConfirmedAt { get; set; }
        public DateTime? WorkStartedAt { get; set; }
        public string? ProblemFound { get; set; }
        public string? WorkPerformed { get; set; }
        public DateTime? WorkEndedAt { get; set; }
        public string? WorkDone { get; set; }
        public string? Resolution { get; set; }
        public string? MaterialsUsed { get; set; }
        public string? WorkerRemarks { get; set; }
        public int? CompletionConfirmedById { get; set; }
        public string? CompletionConfirmedByName { get; set; }
        public DateTime? CompletionConfirmedAt { get; set; }
        public DateTime? ClosedAt { get; set; }
        public int? CancelledById { get; set; }
        public string? CancelledByName { get; set; }
        public DateTime? CancelledAt { get; set; }
        public string? CancelReason { get; set; }

        public string Location => PolyhouseId.HasValue ? $"{AreaName} / {PolyhouseName}" : AreaName ?? string.Empty;
        public TimeSpan? ResponseTime => MaintenanceRules.Response(RequestedAt, ArrivedAt);
        public TimeSpan? WorkTime => MaintenanceRules.Work(WorkStartedAt, WorkEndedAt);
        public TimeSpan? OnSiteTime => MaintenanceRules.OnSite(ArrivedAt, WorkEndedAt);
        public TimeSpan? ResolutionTime => MaintenanceRules.Resolution(RequestedAt, ClosedAt);

        public string? OverdueReason(DateTime nowUtc, DateTime todayIndia)
            => MaintenanceRules.OverdueReason(Status, Priority, RequestedAt, ExpectedVisitDate, nowUtc, todayIndia);
    }

    public class MaintenanceEvent
    {
        public int Id { get; set; }
        public string EventType { get; set; } = string.Empty;
        public string? FromStatus { get; set; }
        public string? ToStatus { get; set; }
        public int? ActorId { get; set; }
        public string? ActorName { get; set; }
        public DateTime EventAt { get; set; }       // UTC
        public string? Note { get; set; }
    }

    public class MaintenancePhoto
    {
        public int Id { get; set; }
        public int TicketId { get; set; }
        public string Stage { get; set; } = "Problem";
        public string StoredFileName { get; set; } = string.Empty;
        public string OriginalFileName { get; set; } = string.Empty;
        public string ContentType { get; set; } = string.Empty;
        public int SizeBytes { get; set; }
        public string? UploadedByName { get; set; }
        public DateTime UploadedAt { get; set; }    // UTC
    }

    public class MaintenanceTicketFilter
    {
        public DateTime? FromDate { get; set; }     // India date of RequestedAt
        public DateTime? ToDate { get; set; }
        public int? CategoryId { get; set; }
        public int? WorkerId { get; set; }
        public int? AreaId { get; set; }
        public int? PolyhouseId { get; set; }
        public string? Status { get; set; }
        public string? Priority { get; set; }
        public int? RequestedById { get; set; }
        public IReadOnlyCollection<int>? CategoryIds { get; set; }
        public bool ActiveOnly { get; set; }
    }

    // Report row: one worker / category / Area / Polyhouse.
    public class MaintenanceReportRow
    {
        public string Key { get; set; } = string.Empty;
        public int Tickets { get; set; }
        public int Closed { get; set; }
        public int Open { get; set; }
        public int Overdue { get; set; }
        public TimeSpan WorkTime { get; set; }
        public TimeSpan? AverageResponse { get; set; }
        public TimeSpan? AverageOnSite { get; set; }
        public TimeSpan? AverageResolution { get; set; }

        public static List<MaintenanceReportRow> Build(IEnumerable<MaintenanceTicket> tickets, Func<MaintenanceTicket, string> key, DateTime nowUtc, DateTime todayIndia)
            => tickets.GroupBy(key).Select(g =>
            {
                static TimeSpan? Avg(IEnumerable<TimeSpan?> spans)
                {
                    var list = spans.Where(s => s.HasValue).Select(s => s!.Value.Ticks).ToList();
                    return list.Count == 0 ? null : TimeSpan.FromTicks((long)list.Average());
                }
                return new MaintenanceReportRow
                {
                    Key = g.Key,
                    Tickets = g.Count(),
                    Closed = g.Count(t => t.Status == MaintenanceRules.Closed),
                    Open = g.Count(t => MaintenanceRules.IsActive(t.Status)),
                    Overdue = g.Count(t => t.OverdueReason(nowUtc, todayIndia) != null),
                    WorkTime = TimeSpan.FromTicks(g.Sum(t => t.WorkTime?.Ticks ?? 0)),
                    AverageResponse = Avg(g.Select(t => t.ResponseTime)),
                    AverageOnSite = Avg(g.Select(t => t.OnSiteTime)),
                    AverageResolution = Avg(g.Select(t => t.ResolutionTime)),
                };
            }).OrderByDescending(r => r.Tickets).ThenBy(r => r.Key).ToList();
    }

    public sealed record MaintenanceWorker(int Id, string Name);
}
