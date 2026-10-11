using System.Globalization;

namespace PlantStockManager.Services
{
    // Maintenance Tickets (Database/Migrations/2026-10-06_MaintenanceTickets.sql) -- pure rules
    // shared by the pages, the repository and the tests: statuses and the allowed transitions,
    // input validation, durations and overdue detection. WHO may perform a step is
    // MaintenanceAuthorization; the database repeats the data rules (CHECK constraints).
    public static class MaintenanceRules
    {
        public const string Open = "Open";
        public const string Accepted = "Accepted";
        public const string Arrived = "Arrived";
        public const string ArrivalConfirmed = "ArrivalConfirmed";
        public const string InProgress = "InProgress";
        public const string Resolved = "Resolved";
        public const string Closed = "Closed";
        public const string Cancelled = "Cancelled";

        public static readonly IReadOnlyList<string> Statuses = new[] { Open, Accepted, Arrived, ArrivalConfirmed, InProgress, Resolved, Closed, Cancelled };
        public static readonly IReadOnlyList<string> Priorities = new[] { "Normal", "Urgent", "Critical" };

        // Workflow actions and the status each one requires -> produces.
        public enum Action { Accept, Assign, Reassign, Arrive, ConfirmArrival, StartWork, Resolve, ConfirmCompletion, RejectCompletion, Cancel }

        public static readonly IReadOnlyDictionary<Action, (string[] From, string To)> Transitions = new Dictionary<Action, (string[], string)>
        {
            [Action.Accept] = (new[] { Open }, Accepted),
            [Action.Assign] = (new[] { Open }, Accepted),                    // administrator assigns an Open ticket
            [Action.Reassign] = (new[] { Accepted }, Accepted),              // administrator changes the worker before arrival
            [Action.Arrive] = (new[] { Accepted }, Arrived),
            [Action.ConfirmArrival] = (new[] { Arrived }, ArrivalConfirmed),
            [Action.StartWork] = (new[] { ArrivalConfirmed }, InProgress),
            [Action.Resolve] = (new[] { InProgress }, Resolved),
            [Action.ConfirmCompletion] = (new[] { Resolved }, Closed),
            [Action.RejectCompletion] = (new[] { Resolved }, InProgress),
            [Action.Cancel] = (new[] { Open, Accepted }, Cancelled),
        };

        public static bool IsAllowed(Action action, string? status) => status != null && Transitions[action].From.Contains(status);

        public static bool IsActive(string? status) => status is not (Closed or Cancelled);

        public static string StatusLabel(string? status) => status switch
        {
            ArrivalConfirmed => "Arrival Confirmed",
            InProgress => "In Progress",
            _ => status ?? string.Empty,
        };

        public static string StatusBadge(string? status) => status switch
        {
            Open => "bg-primary",
            Accepted => "bg-info text-dark",
            Arrived or ArrivalConfirmed => "bg-warning text-dark",
            InProgress => "bg-warning text-dark",
            Resolved => "bg-success",
            Closed => "bg-secondary",
            Cancelled => "bg-dark",
            _ => "bg-light text-dark",
        };

        public static string PriorityBadge(string? priority) => priority switch
        {
            "Critical" => "bg-danger",
            "Urgent" => "bg-warning text-dark",
            _ => "bg-light text-dark border",
        };

        // ---- limits and messages -----------------------------------------------------------
        public const int MaxTitleLength = 150;
        public const int MaxDescriptionLength = 2000;
        public const int MaxWorkTextLength = 1000;
        public const int MaxRemarksLength = 500;
        public const int MaxPhotosPerStage = 5;

        public const string ChangedMessage = "This ticket was changed by someone else a moment ago. Please check its current status.";
        public const string AlreadySubmittedMessage = "This form was already submitted.";
        public const string ExpectedDateRequiredMessage = "Expected visit date is required.";
        public const string ExpectedDatePastMessage = "Expected visit date cannot be in the past.";
        public const string NotYourTradeMessage = "This ticket is not in your trade.";
        public const string ReasonRequiredMessage = "A reason is required.";

        public static string? Required(string? value, string label, int max)
        {
            if (string.IsNullOrWhiteSpace(value))
                return $"{label} is required.";
            return value.Trim().Length > max ? $"{label} cannot be longer than {max} characters." : null;
        }

        public static string? Optional(string? value, string label, int max)
            => value != null && value.Trim().Length > max ? $"{label} cannot be longer than {max} characters." : null;

        public static string? ValidateExpectedVisitDate(DateTime? date, DateTime todayIndia)
        {
            if (!date.HasValue || date.Value == default)
                return ExpectedDateRequiredMessage;
            return date.Value.Date < todayIndia.Date ? ExpectedDatePastMessage : null;
        }

        public static string? Trimmed(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

        // ---- durations (all from server timestamps) -----------------------------------------
        public static TimeSpan? Between(DateTime? from, DateTime? to)
            => from.HasValue && to.HasValue && to.Value >= from.Value ? to.Value - from.Value : null;

        // Response = requested -> worker arrived; Work = work started -> ended;
        // On-site = arrived -> work ended; Resolution = requested -> closed.
        public static TimeSpan? Response(DateTime requestedAt, DateTime? arrivedAt) => Between(requestedAt, arrivedAt);
        public static TimeSpan? Work(DateTime? startedAt, DateTime? endedAt) => Between(startedAt, endedAt);
        public static TimeSpan? OnSite(DateTime? arrivedAt, DateTime? endedAt) => Between(arrivedAt, endedAt);
        public static TimeSpan? Resolution(DateTime requestedAt, DateTime? closedAt) => Between(requestedAt, closedAt);

        // 105 min -> "1h 45m", 2d 3h 0m -> "2d 3h", 40 s -> "0m".
        public static string Format(TimeSpan? span)
        {
            if (!span.HasValue) return "—";
            var total = (int)Math.Floor(span.Value.TotalMinutes);
            var d = total / 1440;
            var h = total % 1440 / 60;
            var m = total % 60;
            var parts = new List<string>();
            if (d > 0) parts.Add($"{d}d");
            if (h > 0) parts.Add($"{h}h");
            if (m > 0 || parts.Count == 0) parts.Add($"{m}m");
            return string.Join(" ", parts.Take(2));
        }

        public static string Hours(TimeSpan span) => span.TotalHours.ToString("0.##", CultureInfo.InvariantCulture);

        // ---- overdue ------------------------------------------------------------------------
        // A ticket is overdue while nobody has arrived yet and either
        //   * it is Open (not accepted) longer than its priority allows
        //     (Critical 4 h, Urgent 24 h, Normal 72 h), or
        //   * it is Accepted and the expected visit date (India) has passed.
        public static TimeSpan AcceptanceLimit(string? priority) => priority switch
        {
            "Critical" => TimeSpan.FromHours(4),
            "Urgent" => TimeSpan.FromHours(24),
            _ => TimeSpan.FromHours(72),
        };

        public static string? OverdueReason(string? status, string? priority, DateTime requestedAtUtc, DateTime? expectedVisitDate, DateTime nowUtc, DateTime todayIndia)
        {
            if (status == Open && nowUtc - requestedAtUtc > AcceptanceLimit(priority))
                return $"Not accepted within {AcceptanceLimit(priority).TotalHours:0} h";
            if (status == Accepted && expectedVisitDate.HasValue && expectedVisitDate.Value.Date < todayIndia.Date)
                return "Expected visit date passed";
            return null;
        }
    }
}
