using System.Security.Claims;
using PlantStockManager.Authorization;
using PlantStockManager.Data;
using PlantStockManager.Models;

namespace PlantStockManager.Services.FieldWork
{
    // Field Work Phase 5 (2026-11-03): manager dashboard, the five reports and the employee's own My Summary. Read-only.
    // Managers (FieldWork.Manage) see every employee; My Summary always reads the signed-in user only -- there is no
    // parameter that could widen it. Filters are normalised here: an unknown employee id is dropped (never widened), the
    // range is capped. No Razor dependency.
    public class FieldReportService
    {
        private readonly FieldReportRepository _repo;

        public FieldReportService(FieldReportRepository repo) => _repo = repo;

        public static bool MayManage(ClaimsPrincipal user) => FieldDayService.MayManage(user);

        // Reports need Phases 1 and 2 (days, customers); the expense columns also need Phase 4 (otherwise shown as "—").
        public async Task<bool> SchemaReadyAsync() => (await _repo.SchemaAsync()).Customers;

        public Task<List<FieldEmployeeOption>> ListEmployeesAsync() => _repo.ListEmployeesAsync();

        public sealed record Filter(FieldReportRules.DateRange Range, int? UserId);

        public static Filter NormalizeFilter(DateTime? from, DateTime? to, int? employeeId, IReadOnlyCollection<int> knownEmployeeIds, DateTime todayIst)
            => new(FieldReportRules.NormalizeRange(from, to, todayIst),
                   employeeId.HasValue && knownEmployeeIds.Contains(employeeId.Value) ? employeeId : null);

        private static void RequireManager(ClaimsPrincipal user)
        {
            if (!MayManage(user))
                throw new UnauthorizedAccessException("Field Work reports are for field managers.");
        }

        public sealed record Capped<T>(IReadOnlyList<T> Rows, bool Truncated);

        private static Capped<T> Cap<T>(List<T> rows)
            => rows.Count > FieldReportRules.MaxRows ? new Capped<T>(rows.Take(FieldReportRules.MaxRows).ToList(), true) : new Capped<T>(rows, false);

        private Task<FieldReportRepository.Activity> ActivityAsync(FieldReportRules.DateRange range, int? userId)
        {
            var (fromUtc, toUtc) = FieldReportRules.UtcBounds(range);
            return _repo.ActivityAsync(range.From, range.To, fromUtc, toUtc, userId);
        }

        // ---- reports (managers) ---------------------------------------------------------------------------------------

        public async Task<(Capped<FieldReportRules.DailyRow> Rows, bool ExpensesAvailable)> DailyAsync(ClaimsPrincipal user, Filter f)
        {
            RequireManager(user);
            var a = await ActivityAsync(f.Range, f.UserId);
            return (Cap(FieldReportRules.BuildDaily(a)), a.ExpensesAvailable);
        }

        public async Task<(Capped<FieldReportRules.SummaryRow> Rows, bool ExpensesAvailable)> SummaryAsync(ClaimsPrincipal user, Filter f)
        {
            RequireManager(user);
            var a = await ActivityAsync(f.Range, f.UserId);
            return (Cap(FieldReportRules.BuildSummary(a)), a.ExpensesAvailable);
        }

        public async Task<FieldReportRules.LeadReport> LeadsAsync(ClaimsPrincipal user, Filter f)
        {
            RequireManager(user);
            var (fromUtc, toUtc) = FieldReportRules.UtcBounds(f.Range);
            var (current, moves) = await _repo.LeadsAsync(fromUtc, toUtc, f.UserId);
            return FieldReportRules.BuildLeads(current, moves);
        }

        // "As of today" -- the date range does not apply.
        public async Task<Capped<FieldReportRepository.DisciplineRow>> DisciplineAsync(ClaimsPrincipal user, Filter f)
        {
            RequireManager(user);
            var today = AppClock.Today;
            var staleBefore = AppClock.IndiaToUtc(today.AddDays(-FieldReportRules.NoVisitDays));
            return Cap(await _repo.DisciplineAsync(staleBefore, today, f.UserId));
        }

        public async Task<Capped<FieldReportRules.ExpenseRow>> ExpensesAsync(ClaimsPrincipal user, Filter f)
        {
            RequireManager(user);
            return Cap(FieldReportRules.BuildExpenses(await _repo.ExpensesByCategoryAsync(f.Range.From, f.Range.To, f.UserId)));
        }

        // ---- dashboard (managers) -------------------------------------------------------------------------------------

        public sealed record Dashboard(DateTime Date, IReadOnlyList<FieldReportRules.EmployeeDay> Employees, int Started, int NotStarted, int Ended,
            int Visits, int NewCustomers, int FollowUpsDue, int FollowUpsOverdue, int PendingExpenses, decimal PendingAmount, int PendingExpensesForDate,
            bool ExpensesAvailable, int FlaggedDays);

        public async Task<Dashboard> DashboardAsync(ClaimsPrincipal user, DateTime? date)
        {
            RequireManager(user);
            var today = AppClock.Today;
            var d = (date ?? today).Date;
            if (d > today) d = today;
            var range = new FieldReportRules.DateRange(d, d);
            var a = await ActivityAsync(range, null);
            var extras = await _repo.DashboardExtrasAsync(d);
            var employees = FieldReportRules.BuildEmployeeDays(extras.ActiveEmployees, a);
            return new Dashboard(d, employees,
                Started: employees.Count(e => e.Status != null),
                NotStarted: employees.Count(e => e.Status == null),
                Ended: employees.Count(e => e.Status is FieldDayRules.StatusClosed or FieldDayRules.StatusAutoClosed),
                Visits: a.Visits.Count,
                NewCustomers: a.NewCustomers.Sum(n => n.Count),
                extras.FollowUpsDue, extras.FollowUpsOverdue, extras.PendingExpenses, extras.PendingAmount, extras.PendingExpensesForDate,
                extras.ExpensesAvailable,
                FlaggedDays: a.Days.Count(x => FieldReportRules.IsFlagged(x.StartFlags, x.EndFlags, x.ReviewStatus)));
        }

        // ---- My Summary (the signed-in employee only) -----------------------------------------------------------------

        public sealed record MySummary(FieldReportRules.DateRange Range, FieldReportRules.SummaryRow Totals, IReadOnlyList<FieldReportRules.DailyRow> Days,
            bool ExpensesAvailable);

        public async Task<MySummary?> MySummaryAsync(ClaimsPrincipal user, string? month)
        {
            var me = user.GetUserId();
            if (me == null)
                return null;
            var range = FieldReportRules.MonthSoFar(month, AppClock.Today);
            var a = await ActivityAsync(range, me.Value);
            var totals = FieldReportRules.BuildSummary(a).SingleOrDefault(r => r.UserId == me.Value)
                         ?? new FieldReportRules.SummaryRow(me.Value, "", 0, 0, 0, 0, 0, 0, 0, 0, 0);
            return new MySummary(range, totals, FieldReportRules.BuildDaily(a), a.ExpensesAvailable);
        }
    }
}
