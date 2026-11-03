using PlantStockManager.Data;

namespace PlantStockManager.Services.FieldWork
{
    // Field Work Phase 5 (2026-11-03): the pure report rules -- no database, no Razor (unit-tested). Owner decisions:
    //   worked day   = a day that was started (Open, Closed or AutoClosed); auto-closed days are also counted on their own.
    //   odometer km  = only days with an end reading; route km = start -> visits -> end, as the route map (FieldRouteRules).
    //   flagged day  = an automatic flag (start / end) or a manager's Flagged review, and not Approved since.
    //   Won / Lost   = status changes to Won / Lost recorded in the range, each customer once per outcome.
    public static class FieldReportRules
    {
        public const int MaxRangeDays = 366;
        public const int MaxRows = 5000;
        public const int NoVisitDays = 30;
        public static readonly IReadOnlyList<string> HotStatuses = new[] { "Interested", "SampleGiven", "Negotiation" };

        public sealed record DateRange(DateTime From, DateTime To);

        // Default: the current month so far (1st -> today, India). Swapped when reversed; capped at MaxRangeDays.
        public static DateRange NormalizeRange(DateTime? from, DateTime? to, DateTime todayIst)
        {
            var t = (to ?? todayIst).Date;
            var f = (from ?? new DateTime(t.Year, t.Month, 1)).Date;
            if (f > t)
                (f, t) = (t, f);
            if ((t - f).TotalDays > MaxRangeDays)
                f = t.AddDays(-MaxRangeDays);
            return new DateRange(f, t);
        }

        // A whole month (My Summary): "yyyy-MM"; invalid / future = the current month; never past today.
        public static DateRange MonthSoFar(string? month, DateTime todayIst)
        {
            var (from, to) = FieldExpenseRules.MonthRange(month, todayIst);
            return new DateRange(from, to > todayIst.Date ? todayIst.Date : to);
        }

        // UTC bounds of India dates [from 00:00 IST, (to + 1) 00:00 IST) -- for SYSUTCDATETIME() columns.
        public static (DateTime FromUtc, DateTime ToUtcExclusive) UtcBounds(DateRange r)
            => (AppClock.IndiaToUtc(r.From.Date), AppClock.IndiaToUtc(r.To.Date.AddDays(1)));

        public static bool IsFlagged(string? startFlags, string? endFlags, string reviewStatus)
            => reviewStatus != FieldDayRules.ReviewApproved
               && (!string.IsNullOrWhiteSpace(startFlags) || !string.IsNullOrWhiteSpace(endFlags) || reviewStatus == FieldDayRules.ReviewFlagged);

        public static decimal? OdometerKm(decimal startKm, decimal? endKm) => endKm.HasValue ? endKm.Value - startKm : null;

        public static decimal RouteKm(FieldReportRepository.DayRow d, IEnumerable<FieldReportRepository.VisitPoint> visits)
        {
            var points = new List<FieldRouteRules.PointInput>
            {
                new(FieldRouteRules.PointKind.Start, d.StartedAtUtc, d.StartLat, d.StartLng, d.StartAcc, null),
            };
            points.AddRange(visits.Select(v => new FieldRouteRules.PointInput(FieldRouteRules.PointKind.Visit, v.AtUtc, v.Lat, v.Lng, v.Acc, null, v.VisitId)));
            if (d.EndedAtUtc.HasValue)
                points.Add(new(FieldRouteRules.PointKind.End, d.EndedAtUtc.Value, d.EndLat, d.EndLng, d.EndAcc, null));
            return FieldRouteRules.RouteKm(points);
        }

        // ---- a. daily visits ----------------------------------------------------------------------------------------

        public sealed record DailyRow(DateTime Date, int UserId, string Name, string Status, DateTime StartedAtUtc, DateTime? EndedAtUtc, int Visits,
            int NewCustomers, decimal? OdometerKm, decimal RouteKm, bool Flagged);

        public static List<DailyRow> BuildDaily(FieldReportRepository.Activity a)
        {
            var visits = a.Visits.ToLookup(v => v.DayId);
            var created = a.NewCustomers.ToDictionary(n => (n.UserId, n.Date.Date), n => n.Count);
            return a.Days
                .Select(d => new DailyRow(d.WorkDate.Date, d.UserId, d.Name, d.Status, d.StartedAtUtc, d.EndedAtUtc, visits[d.DayId].Count(),
                    created.TryGetValue((d.UserId, d.WorkDate.Date), out var n) ? n : 0, OdometerKm(d.StartKm, d.EndKm), RouteKm(d, visits[d.DayId]),
                    IsFlagged(d.StartFlags, d.EndFlags, d.ReviewStatus)))
                .OrderByDescending(r => r.Date).ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        // ---- b. monthly employee summary (also My Summary) -----------------------------------------------------------

        public sealed record SummaryRow(int UserId, string Name, int DaysWorked, int AutoClosedDays, int Visits, int NewCustomers, decimal OdometerKm,
            decimal RouteKm, int FlaggedDays, decimal ExpensesApproved, decimal ExpensesPending);

        public static List<SummaryRow> BuildSummary(FieldReportRepository.Activity a)
        {
            var daily = BuildDaily(a);
            var names = new Dictionary<int, string>();
            foreach (var d in a.Days) names[d.UserId] = d.Name;
            foreach (var n in a.NewCustomers) names.TryAdd(n.UserId, n.Name);
            foreach (var e in a.Expenses) names.TryAdd(e.UserId, e.Name);
            return names
                .Select(kv =>
                {
                    var days = daily.Where(d => d.UserId == kv.Key).ToList();
                    decimal Exp(string status) => a.Expenses.Where(e => e.UserId == kv.Key && e.Status == status).Sum(e => e.Amount);
                    return new SummaryRow(kv.Key, kv.Value, days.Count, days.Count(d => d.Status == FieldDayRules.StatusAutoClosed), days.Sum(d => d.Visits),
                        a.NewCustomers.Where(n => n.UserId == kv.Key).Sum(n => n.Count), days.Sum(d => d.OdometerKm ?? 0), days.Sum(d => d.RouteKm),
                        days.Count(d => d.Flagged), Exp(FieldExpenseRules.StatusApproved), Exp(FieldExpenseRules.StatusSubmitted));
                })
                .OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        // ---- c. lead conversion -------------------------------------------------------------------------------------

        public sealed record LeadGroup(string Key, string Label, IReadOnlyDictionary<string, int> Current, int CurrentTotal, int MovedToWon, int MovedToLost);

        public sealed record LeadReport(IReadOnlyList<LeadGroup> ByStatus, IReadOnlyList<LeadGroup> ByEmployee, IReadOnlyList<LeadGroup> ByDistrict,
            int MovedToWon, int MovedToLost);

        public const string NoDistrict = "(none)";

        public static string DistrictKey(string? district) => (district ?? "").Trim().ToUpperInvariant();

        // Each customer counts once per outcome (a customer moved Won -> Negotiation -> Won in the range is one "Won").
        public static (int Won, int Lost) CountMoves(IEnumerable<FieldReportRepository.StatusMove> moves)
        {
            var distinct = moves.Select(m => (m.CustomerId, m.ToStatus)).Distinct().ToList();
            return (distinct.Count(m => m.ToStatus == "Won"), distinct.Count(m => m.ToStatus == "Lost"));
        }

        public static LeadReport BuildLeads(IReadOnlyList<FieldReportRepository.StatusCount> current, IReadOnlyList<FieldReportRepository.StatusMove> moves)
        {
            var statuses = FieldCustomerRules.LeadStatuses.Keys.ToList();
            IReadOnlyDictionary<string, int> Counts(IEnumerable<FieldReportRepository.StatusCount> rows)
                => statuses.ToDictionary(s => s, s => rows.Where(r => r.Status == s).Sum(r => r.Count));

            var byStatus = statuses.Select(s =>
            {
                var m = CountMoves(moves.Where(x => x.ToStatus == s));
                return new LeadGroup(s, FieldCustomerRules.LeadLabel(s), Counts(current.Where(c => c.Status == s)), current.Where(c => c.Status == s).Sum(c => c.Count),
                    m.Won, m.Lost);
            }).ToList();

            var employeeNames = new Dictionary<int, string>();
            foreach (var c in current) employeeNames[c.UserId] = c.Name;
            foreach (var m in moves) employeeNames.TryAdd(m.ByUserId, m.ByName);
            var byEmployee = employeeNames.Select(kv =>
            {
                var rows = current.Where(c => c.UserId == kv.Key).ToList();
                var m = CountMoves(moves.Where(x => x.ByUserId == kv.Key));
                return new LeadGroup(kv.Key.ToString(), kv.Value, Counts(rows), rows.Sum(r => r.Count), m.Won, m.Lost);
            }).OrderBy(g => g.Label, StringComparer.OrdinalIgnoreCase).ToList();

            var districtLabels = new Dictionary<string, string>();
            foreach (var c in current) districtLabels.TryAdd(DistrictKey(c.District), c.District?.Trim() is { Length: > 0 } l ? l : NoDistrict);
            foreach (var m in moves) districtLabels.TryAdd(DistrictKey(m.District), m.District?.Trim() is { Length: > 0 } l ? l : NoDistrict);
            var byDistrict = districtLabels.Select(kv =>
            {
                var rows = current.Where(c => DistrictKey(c.District) == kv.Key).ToList();
                var m = CountMoves(moves.Where(x => DistrictKey(x.District) == kv.Key));
                return new LeadGroup(kv.Key, kv.Value, Counts(rows), rows.Sum(r => r.Count), m.Won, m.Lost);
            }).OrderBy(g => g.Key.Length == 0 ? 1 : 0).ThenBy(g => g.Label, StringComparer.OrdinalIgnoreCase).ToList();

            var all = CountMoves(moves);
            return new LeadReport(byStatus, byEmployee, byDistrict, all.Won, all.Lost);
        }

        // ---- e. expenses by employee and category -------------------------------------------------------------------

        public sealed record ExpenseRow(int UserId, string Name, IReadOnlyDictionary<string, (decimal Approved, decimal Pending)> ByCategory,
            decimal Approved, decimal Pending);

        public static List<ExpenseRow> BuildExpenses(IEnumerable<FieldReportRepository.CategoryAmount> rows)
            => rows.GroupBy(r => (r.UserId, r.Name))
                .Select(g => new ExpenseRow(g.Key.UserId, g.Key.Name,
                    FieldExpenseRules.Categories.ToDictionary(c => c, c => (g.Where(r => r.Category == c).Sum(r => r.Approved), g.Where(r => r.Category == c).Sum(r => r.Pending))),
                    g.Sum(r => r.Approved), g.Sum(r => r.Pending)))
                .OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();

        // ---- dashboard ------------------------------------------------------------------------------------------------

        public sealed record EmployeeDay(int UserId, string Name, string? Status, DateTime? StartedAtUtc, DateTime? EndedAtUtc, int Visits, int DayId);

        // Active field employees for one date: not started first, then started (still open), then ended; visits per employee.
        public static List<EmployeeDay> BuildEmployeeDays(IEnumerable<Models.FieldEmployeeOption> employees, FieldReportRepository.Activity a)
        {
            var days = a.Days.ToDictionary(d => d.UserId);
            var visits = a.Visits.ToLookup(v => v.DayId);
            var rows = employees.Select(e => days.TryGetValue(e.UserId, out var d)
                    ? new EmployeeDay(e.UserId, e.Name, d.Status, d.StartedAtUtc, d.EndedAtUtc, visits[d.DayId].Count(), d.DayId)
                    : new EmployeeDay(e.UserId, e.Name, null, null, null, 0, 0))
                .ToList();
            // someone who worked that day but is no longer an active field employee still shows
            foreach (var d in a.Days.Where(d => rows.All(r => r.UserId != d.UserId)))
                rows.Add(new EmployeeDay(d.UserId, d.Name, d.Status, d.StartedAtUtc, d.EndedAtUtc, visits[d.DayId].Count(), d.DayId));
            int Order(EmployeeDay r) => r.Status == null ? 0 : r.Status == FieldDayRules.StatusOpen ? 1 : 2;
            return rows.OrderBy(Order).ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase).ToList();
        }
    }
}
