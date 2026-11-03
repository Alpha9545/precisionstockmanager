using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PlantStockManager.Services;
using PlantStockManager.Services.FieldWork;

namespace PlantStockManager.Pages.FieldWork.Manage
{
    // Field Work reports (managers): a. daily visits, b. employee summary, c. lead conversion, d. visit and follow-up
    // discipline (as of today), e. expenses by category. Filters: From / To (default this month so far) and employee,
    // normalised on the server. The table and the Excel export are built from the SAME rows (FieldReportExport), capped at
    // FieldReportRules.MaxRows with a visible note. The export re-checks FieldWork.Manage inside the handler.
    public class ReportsModel : PageModel
    {
        private readonly FieldReportService _service;

        public ReportsModel(FieldReportService service) => _service = service;

        [BindProperty(SupportsGet = true)] public string? Report { get; set; }
        [BindProperty(SupportsGet = true)] public DateTime? From { get; set; }
        [BindProperty(SupportsGet = true)] public DateTime? To { get; set; }
        [BindProperty(SupportsGet = true)] public int? EmployeeId { get; set; }

        public bool SchemaReady { get; private set; } = true;
        public string Selected { get; private set; } = FieldReportExport.Daily;
        public FieldReportService.Filter? Filter { get; private set; }
        public List<PlantStockManager.Models.FieldEmployeeOption> Employees { get; private set; } = new();
        public List<ReportTable> Tables { get; } = new();
        public bool Truncated { get; private set; }
        public bool ExpensesAvailable { get; private set; } = true;
        public string? Note { get; private set; }

        public sealed record ReportTable(string? Title, IReadOnlyList<string> Headers, List<IReadOnlyList<object?>> Rows);

        public Dictionary<string, string> RouteFor(string report) => new()
        {
            ["Report"] = report,
            ["From"] = Filter!.Range.From.ToString("yyyy-MM-dd"),
            ["To"] = Filter.Range.To.ToString("yyyy-MM-dd"),
            ["EmployeeId"] = Filter.UserId?.ToString() ?? "",
        };

        private async Task<bool> LoadAsync()
        {
            SchemaReady = await _service.SchemaReadyAsync();
            if (!SchemaReady)
                return false;
            Selected = Report != null && FieldReportExport.Reports.Contains(Report) ? Report : FieldReportExport.Daily;
            Employees = await _service.ListEmployeesAsync();
            Filter = FieldReportService.NormalizeFilter(From, To, EmployeeId, Employees.Select(e => e.UserId).ToList(), AppClock.Today);
            return true;
        }

        // Fills Tables for the selected report; returns the rows for the export (with the capped note when capped).
        private async Task<(IReadOnlyList<string> Headers, List<IReadOnlyList<object?>> Rows)> BuildAsync()
        {
            switch (Selected)
            {
                case FieldReportExport.Summary:
                {
                    var (rows, expenses) = await _service.SummaryAsync(User, Filter!);
                    (Truncated, ExpensesAvailable) = (rows.Truncated, expenses);
                    Note = "Days worked = days started (open, closed or auto-closed). Odometer km only from days with an end reading. "
                         + "Flagged = automatic flag or manager \"Flagged\", not approved since. Expenses: approved and pending only.";
                    var t = FieldReportExport.SummaryRows(rows.Rows, false, expenses);
                    Tables.Add(new ReportTable(null, t.Headers, t.Rows));
                    return FieldReportExport.SummaryRows(rows.Rows, rows.Truncated, expenses);
                }
                case FieldReportExport.Leads:
                {
                    var r = await _service.LeadsAsync(User, Filter!);
                    Note = $"Customers now: active (not archived) customers by their current status and assigned employee. "
                         + $"Moved to Won / Lost: status changes recorded between {Filter!.Range.From:dd MMM} and {Filter.Range.To:dd MMM}, each customer once, "
                         + "counted for the employee who recorded the change. In range: " + r.MovedToWon + " won, " + r.MovedToLost + " lost.";
                    Tables.Add(new ReportTable("By status", new[] { "Status", "Customers now", "Moved to Won in range", "Moved to Lost in range" },
                        r.ByStatus.Select(g => (IReadOnlyList<object?>)new object?[] { g.Label, g.CurrentTotal, g.Key == "Won" ? g.MovedToWon : null, g.Key == "Lost" ? g.MovedToLost : null }).ToList()));
                    var all = FieldReportExport.LeadRows(r);
                    var headers = all.Headers.Skip(1).ToList();
                    Tables.Add(new ReportTable("By employee", headers, all.Rows.Where(x => (x[0] as string)?.StartsWith("Employee") == true).Select(x => (IReadOnlyList<object?>)x.Skip(1).ToList()).ToList()));
                    Tables.Add(new ReportTable("By district", headers, all.Rows.Where(x => (x[0] as string) == "District").Select(x => (IReadOnlyList<object?>)x.Skip(1).ToList()).ToList()));
                    Truncated = all.Rows.Count > 0 && all.Rows[0].Count == 1;
                    return all;
                }
                case FieldReportExport.Discipline:
                {
                    var rows = await _service.DisciplineAsync(User, Filter!);
                    Truncated = rows.Truncated;
                    Note = $"As of today ({AppClock.Today:dd MMM yyyy}) -- the date range does not apply. Active customers by assigned employee. "
                         + $"\"No visit\" counts customers whose last visit (or creation, if never visited) is more than {FieldReportRules.NoVisitDays} days ago.";
                    var t = FieldReportExport.DisciplineRows(rows.Rows, false);
                    Tables.Add(new ReportTable(null, t.Headers, t.Rows));
                    return FieldReportExport.DisciplineRows(rows.Rows, rows.Truncated);
                }
                case FieldReportExport.Expenses:
                {
                    var rows = await _service.ExpensesAsync(User, Filter!);
                    Truncated = rows.Truncated;
                    Note = "Approved and pending only (cancelled and rejected expenses are not counted), by expense date.";
                    var t = FieldReportExport.ExpenseRows(rows.Rows, false);
                    Tables.Add(new ReportTable(null, t.Headers, t.Rows));
                    return FieldReportExport.ExpenseRows(rows.Rows, rows.Truncated);
                }
                default:
                {
                    var (rows, _) = await _service.DailyAsync(User, Filter!);
                    Truncated = rows.Truncated;
                    Note = "One row per employee per day started. New customers: created by that employee that day (archived ones left out). "
                         + "Odometer km only with an end reading; route km = start -> visits -> end (straight lines).";
                    var t = FieldReportExport.DailyRows(rows.Rows, false);
                    Tables.Add(new ReportTable(null, t.Headers, t.Rows));
                    return FieldReportExport.DailyRows(rows.Rows, rows.Truncated);
                }
            }
        }

        public async Task<IActionResult> OnGetAsync()
        {
            if (await LoadAsync())
                await BuildAsync();
            return Page();
        }

        public async Task<IActionResult> OnGetExportExcelAsync()
        {
            if (!FieldReportService.MayManage(User) || !await LoadAsync())
                return NotFound();
            var (headers, rows) = await BuildAsync();
            var bytes = ExportHelper.BuildExcel(FieldReportExport.Title(Selected), headers, rows);
            return File(bytes, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
                $"Field_{Selected}_{Filter!.Range.From:yyyyMMdd}_{Filter.Range.To:yyyyMMdd}.xlsx");
        }
    }
}
