using System.Globalization;
using PlantStockManager.Data;

namespace PlantStockManager.Services.FieldWork
{
    // Field Work Phase 5 (2026-11-03): the Excel rows of each report -- exactly the rows the page shows (same filter, same
    // cap). Built with the existing ExportHelper.BuildExcel. A capped export starts with a line that says so.
    public static class FieldReportExport
    {
        public const string Daily = "daily", Summary = "summary", Leads = "leads", Discipline = "discipline", Expenses = "expenses";
        public static readonly IReadOnlyList<string> Reports = new[] { Daily, Summary, Leads, Discipline, Expenses };

        public static string Title(string report) => report switch
        {
            Daily => "Daily visits",
            Summary => "Employee summary",
            Leads => "Lead conversion",
            Discipline => "Visit and follow-up discipline",
            Expenses => "Expenses by category",
            _ => report,
        };

        public static string CappedNote => $"Only the first {FieldReportRules.MaxRows} rows are included. Narrow the filter to see the rest.";

        private static string Ist(DateTime? utc) => utc.HasValue ? AppClock.ToIndiaTime(utc.Value).ToString("hh:mm tt", CultureInfo.InvariantCulture) : "";

        private static List<IReadOnlyList<object?>> WithNote(IEnumerable<IReadOnlyList<object?>> rows, bool truncated)
        {
            var list = new List<IReadOnlyList<object?>>();
            if (truncated)
                list.Add(new object?[] { CappedNote });
            list.AddRange(rows);
            return list;
        }

        public static (IReadOnlyList<string> Headers, List<IReadOnlyList<object?>> Rows) DailyRows(IEnumerable<FieldReportRules.DailyRow> rows, bool truncated)
            => (new[] { "Date", "Employee", "Day status", "Start (IST)", "End (IST)", "Visits", "New customers", "Odometer km", "Route km", "Flagged" },
                WithNote(rows.Select(r => (IReadOnlyList<object?>)new object?[]
                {
                    r.Date, r.Name, FieldDayRules.StatusLabel(r.Status), Ist(r.StartedAtUtc), Ist(r.EndedAtUtc), r.Visits, r.NewCustomers,
                    r.OdometerKm, r.RouteKm, r.Flagged ? "Yes" : "",
                }), truncated));

        public static (IReadOnlyList<string> Headers, List<IReadOnlyList<object?>> Rows) SummaryRows(IEnumerable<FieldReportRules.SummaryRow> rows, bool truncated,
            bool expenses)
            => (new[] { "Employee", "Days worked", "Auto-closed days", "Visits", "New customers", "Odometer km", "Route km", "Flagged days",
                        "Expenses approved (Rs)", "Expenses pending (Rs)" },
                WithNote(rows.Select(r => (IReadOnlyList<object?>)new object?[]
                {
                    r.Name, r.DaysWorked, r.AutoClosedDays, r.Visits, r.NewCustomers, r.OdometerKm, r.RouteKm, r.FlaggedDays,
                    expenses ? r.ExpensesApproved : null, expenses ? r.ExpensesPending : null,
                }), truncated));

        public static (IReadOnlyList<string> Headers, List<IReadOnlyList<object?>> Rows) LeadRows(FieldReportRules.LeadReport report)
        {
            var statuses = FieldCustomerRules.LeadStatuses.Keys.ToList();
            var headers = new List<string> { "Grouping", "Name" };
            headers.AddRange(statuses.Select(FieldCustomerRules.LeadLabel));
            headers.AddRange(new[] { "Total now", "Moved to Won in range", "Moved to Lost in range" });
            IEnumerable<IReadOnlyList<object?>> Section(string grouping, IEnumerable<FieldReportRules.LeadGroup> groups)
                => groups.Select(g =>
                {
                    var row = new List<object?> { grouping, g.Label };
                    row.AddRange(statuses.Select(s => (object?)g.Current[s]));
                    row.AddRange(new object?[] { g.CurrentTotal, g.MovedToWon, g.MovedToLost });
                    return (IReadOnlyList<object?>)row;
                });
            var rows = Section("Employee (assigned now / recorded the move)", report.ByEmployee)
                .Concat(Section("District", report.ByDistrict))
                .ToList();
            return (headers, WithNote(rows.Take(FieldReportRules.MaxRows), rows.Count > FieldReportRules.MaxRows));
        }

        public static (IReadOnlyList<string> Headers, List<IReadOnlyList<object?>> Rows) DisciplineRows(IEnumerable<FieldReportRepository.DisciplineRow> rows,
            bool truncated)
            => (new[] { "Employee", "Active customers", $"No visit in {FieldReportRules.NoVisitDays}+ days", "Overdue follow-ups",
                        "Interested / Sample Given / Negotiation without a follow-up" },
                WithNote(rows.Select(r => (IReadOnlyList<object?>)new object?[] { r.Name, r.ActiveCustomers, r.NoRecentVisit, r.OverdueFollowUps, r.HotWithoutFollowUp }),
                    truncated));

        public static (IReadOnlyList<string> Headers, List<IReadOnlyList<object?>> Rows) ExpenseRows(IEnumerable<FieldReportRules.ExpenseRow> rows, bool truncated)
        {
            var headers = new List<string> { "Employee" };
            foreach (var c in FieldExpenseRules.Categories)
                headers.AddRange(new[] { $"{c} approved (Rs)", $"{c} pending (Rs)" });
            headers.AddRange(new[] { "Total approved (Rs)", "Total pending (Rs)" });
            return (headers, WithNote(rows.Select(r =>
            {
                var row = new List<object?> { r.Name };
                foreach (var c in FieldExpenseRules.Categories)
                    row.AddRange(new object?[] { r.ByCategory[c].Approved, r.ByCategory[c].Pending });
                row.AddRange(new object?[] { r.Approved, r.Pending });
                return (IReadOnlyList<object?>)row;
            }), truncated));
        }
    }
}
