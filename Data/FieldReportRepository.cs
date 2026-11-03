using System.Data;
using Microsoft.Data.SqlClient;
using PlantStockManager.Models;
using static PlantStockManager.Data.FieldSql;

namespace PlantStockManager.Data
{
    // Field Work Phase 5 (2026-11-03): read-only report data. Every report is ONE SQL batch (several result sets, no per-row
    // queries) over the existing Field Work tables -- no migration. Day dates are dbo.FieldDays.WorkDate (already the India
    // date); timestamps are compared with UTC bounds of India days so the indexes stay usable. Archived customers and
    // cancelled / rejected expenses are left out here, as the pages say. userId = null: every employee (managers only --
    // FieldReportService decides who may ask for what).
    public class FieldReportRepository
    {
        private readonly DatabaseHelper _db;

        public FieldReportRepository(DatabaseHelper db) => _db = db;

        public async Task<(bool Customers, bool Expenses)> SchemaAsync()
        {
            using var conn = _db.GetConnection();
            await conn.OpenAsync();
            return (await FieldCustomerSchema.ReadyAsync(conn), await FieldExpenseSchema.ReadyAsync(conn));
        }

        private static void Range(SqlCommand cmd, DateTime from, DateTime to, DateTime fromUtc, DateTime toUtcExclusive, int? userId)
        {
            cmd.Parameters.Add(Date("@From", from));
            cmd.Parameters.Add(Date("@To", to));
            cmd.Parameters.Add(new SqlParameter("@FromUtc", SqlDbType.DateTime2) { Value = fromUtc });
            cmd.Parameters.Add(new SqlParameter("@ToUtc", SqlDbType.DateTime2) { Value = toUtcExclusive });
            cmd.Parameters.Add(Int("@U", userId));
            cmd.Parameters.Add(Int("@Off", Services.AppClock.IndiaOffsetMinutes));
        }

        // ---- activity: days + their route points + new customers + expenses (reports a, b, My Summary, dashboard) -------

        public sealed record DayRow(int DayId, int UserId, string Name, DateTime WorkDate, string Status, DateTime StartedAtUtc, DateTime? EndedAtUtc,
            decimal StartKm, decimal? EndKm, string? StartFlags, string? EndFlags, string ReviewStatus,
            decimal? StartLat, decimal? StartLng, decimal? StartAcc, decimal? EndLat, decimal? EndLng, decimal? EndAcc);

        public sealed record VisitPoint(int DayId, int VisitId, DateTime AtUtc, decimal? Lat, decimal? Lng, decimal? Acc);

        public sealed record UserDateCount(int UserId, string Name, DateTime Date, int Count);

        public sealed record UserAmount(int UserId, string Name, string Status, decimal Amount, int Count);

        public sealed record Activity(List<DayRow> Days, List<VisitPoint> Visits, List<UserDateCount> NewCustomers, List<UserAmount> Expenses,
            bool ExpensesAvailable);

        public async Task<Activity> ActivityAsync(DateTime from, DateTime to, DateTime fromUtc, DateTime toUtcExclusive, int? userId)
        {
            using var conn = _db.GetConnection();
            await conn.OpenAsync();
            var expenses = await FieldExpenseSchema.ReadyAsync(conn);
            var sql = @"
SELECT d.Id, d.UserId, " + NameOf("u") + @", d.WorkDate, d.Status, d.StartedAtUtc, d.EndedAtUtc, d.StartKm, d.EndKm,
       d.StartFlagReasons, d.EndFlagReasons, d.ReviewStatus, d.StartLatitude, d.StartLongitude, d.StartAccuracyM,
       d.EndLatitude, d.EndLongitude, d.EndAccuracyM
FROM dbo.FieldDays d JOIN dbo.IMSUsers u ON u.Id = d.UserId
WHERE d.WorkDate BETWEEN @From AND @To AND (@U IS NULL OR d.UserId = @U)
ORDER BY d.WorkDate, d.UserId;

SELECT v.FieldDayId, v.Id, v.CheckInAtUtc, v.Latitude, v.Longitude, v.AccuracyM
FROM dbo.FieldVisits v JOIN dbo.FieldDays d ON d.Id = v.FieldDayId
WHERE d.WorkDate BETWEEN @From AND @To AND (@U IS NULL OR d.UserId = @U);

SELECT c.CreatedByUserId, " + NameOf("u") + @", CAST(DATEADD(MINUTE, @Off, c.CreatedAtUtc) AS DATE) AS D, COUNT(*)
FROM dbo.FieldCustomers c JOIN dbo.IMSUsers u ON u.Id = c.CreatedByUserId
WHERE c.CreatedAtUtc >= @FromUtc AND c.CreatedAtUtc < @ToUtc AND c.IsActive = 1 AND (@U IS NULL OR c.CreatedByUserId = @U)
GROUP BY c.CreatedByUserId, u.Name, u.Username, CAST(DATEADD(MINUTE, @Off, c.CreatedAtUtc) AS DATE);"
                + (expenses ? @"

SELECT e.UserId, " + NameOf("u") + @", e.Status, SUM(e.Amount), COUNT(*)
FROM dbo.FieldExpenses e JOIN dbo.IMSUsers u ON u.Id = e.UserId
WHERE e.ExpenseDate BETWEEN @From AND @To AND e.Status IN ('Approved', 'Submitted') AND (@U IS NULL OR e.UserId = @U)
GROUP BY e.UserId, u.Name, u.Username, e.Status;" : "");
            using var cmd = new SqlCommand(sql, conn);
            Range(cmd, from, to, fromUtc, toUtcExclusive, userId);
            var result = new Activity(new(), new(), new(), new(), expenses);
            using var r = await cmd.ExecuteReaderAsync();
            while (await r.ReadAsync())
                result.Days.Add(new DayRow(r.GetInt32(0), r.GetInt32(1), r.GetString(2), r.GetDateTime(3), r.GetString(4), r.GetDateTime(5), Dt(r, 6),
                    r.GetDecimal(7), Dec(r, 8), Str(r, 9), Str(r, 10), r.GetString(11), Dec(r, 12), Dec(r, 13), Dec(r, 14), Dec(r, 15), Dec(r, 16), Dec(r, 17)));
            await r.NextResultAsync();
            while (await r.ReadAsync())
                result.Visits.Add(new VisitPoint(r.GetInt32(0), r.GetInt32(1), r.GetDateTime(2), Dec(r, 3), Dec(r, 4), Dec(r, 5)));
            await r.NextResultAsync();
            while (await r.ReadAsync())
                result.NewCustomers.Add(new UserDateCount(r.GetInt32(0), r.GetString(1), r.GetDateTime(2), r.GetInt32(3)));
            if (expenses && await r.NextResultAsync())
                while (await r.ReadAsync())
                    result.Expenses.Add(new UserAmount(r.GetInt32(0), r.GetString(1), r.GetString(2), r.GetDecimal(3), r.GetInt32(4)));
            return result;
        }

        // ---- c. lead conversion -------------------------------------------------------------------------------------

        // Active customers now, per assigned employee / status / district (district grouped trimmed and case-insensitive).
        public sealed record StatusCount(int UserId, string Name, string Status, string? District, int Count);

        // A Won / Lost status change in the range (the employee who recorded it), active customers only.
        public sealed record StatusMove(int CustomerId, string ToStatus, int ByUserId, string ByName, string? District);

        public async Task<(List<StatusCount> Current, List<StatusMove> Moves)> LeadsAsync(DateTime fromUtc, DateTime toUtcExclusive, int? userId)
        {
            using var conn = _db.GetConnection();
            await conn.OpenAsync();
            const string districtKey = "UPPER(LTRIM(RTRIM(ISNULL(c.District, N''))))";
            var sql = @"
SELECT c.AssignedUserId, " + NameOf("u") + @", c.LeadStatus, MIN(NULLIF(LTRIM(RTRIM(c.District)), N'')), COUNT(*)
FROM dbo.FieldCustomers c JOIN dbo.IMSUsers u ON u.Id = c.AssignedUserId
WHERE c.IsActive = 1 AND (@U IS NULL OR c.AssignedUserId = @U)
GROUP BY c.AssignedUserId, u.Name, u.Username, c.LeadStatus, " + districtKey + @";

SELECT DISTINCT ev.CustomerId, ev.ToStatus, ev.ByUserId, " + NameOf("b") + @", NULLIF(LTRIM(RTRIM(c.District)), N'')
FROM dbo.FieldCustomerEvents ev
JOIN dbo.FieldCustomers c ON c.Id = ev.CustomerId
JOIN dbo.IMSUsers b ON b.Id = ev.ByUserId
WHERE ev.EventType IN ('StatusChanged', 'Created') AND ev.ToStatus IN ('Won', 'Lost')
  AND ev.AtUtc >= @FromUtc AND ev.AtUtc < @ToUtc AND c.IsActive = 1 AND (@U IS NULL OR ev.ByUserId = @U);";
            using var cmd = new SqlCommand(sql, conn);
            Range(cmd, DateTime.Today, DateTime.Today, fromUtc, toUtcExclusive, userId);
            var current = new List<StatusCount>();
            var moves = new List<StatusMove>();
            using var r = await cmd.ExecuteReaderAsync();
            while (await r.ReadAsync())
                current.Add(new StatusCount(r.GetInt32(0), r.GetString(1), r.GetString(2), Str(r, 3), r.GetInt32(4)));
            await r.NextResultAsync();
            while (await r.ReadAsync())
                moves.Add(new StatusMove(r.GetInt32(0), r.GetString(1), r.GetInt32(2), r.GetString(3), Str(r, 4)));
            return (current, moves);
        }

        // ---- d. discipline (as of today) ------------------------------------------------------------------------------

        public sealed record DisciplineRow(int UserId, string Name, int ActiveCustomers, int NoRecentVisit, int OverdueFollowUps, int HotWithoutFollowUp);

        // staleBeforeUtc: a customer whose last visit (or creation, if never visited) is before this counts as "no recent visit".
        public async Task<List<DisciplineRow>> DisciplineAsync(DateTime staleBeforeUtc, DateTime todayIst, int? userId)
        {
            using var conn = _db.GetConnection();
            await conn.OpenAsync();
            const string sql = @"
WITH c AS (
    SELECT c.Id, c.AssignedUserId, c.LeadStatus, c.CreatedAtUtc,
           (SELECT MAX(v.CheckInAtUtc) FROM dbo.FieldVisits v WHERE v.CustomerId = c.Id) AS LastVisitUtc,
           (SELECT TOP 1 f.DueDate FROM dbo.FieldFollowUps f WHERE f.CustomerId = c.Id AND f.Status = 'Pending') AS PendingDue
    FROM dbo.FieldCustomers c
    WHERE c.IsActive = 1 AND (@U IS NULL OR c.AssignedUserId = @U)
)
SELECT c.AssignedUserId, " + "ISNULL(NULLIF(LTRIM(RTRIM(u.Name)), N''), u.Username)" + @", COUNT(*),
       SUM(CASE WHEN ISNULL(c.LastVisitUtc, c.CreatedAtUtc) < @Stale THEN 1 ELSE 0 END),
       SUM(CASE WHEN c.PendingDue < @Today THEN 1 ELSE 0 END),
       SUM(CASE WHEN c.LeadStatus IN ('Interested', 'SampleGiven', 'Negotiation') AND c.PendingDue IS NULL THEN 1 ELSE 0 END)
FROM c JOIN dbo.IMSUsers u ON u.Id = c.AssignedUserId
GROUP BY c.AssignedUserId, u.Name, u.Username
ORDER BY 2;";
            using var cmd = new SqlCommand(sql, conn);
            cmd.Parameters.Add(new SqlParameter("@Stale", SqlDbType.DateTime2) { Value = staleBeforeUtc });
            cmd.Parameters.Add(Date("@Today", todayIst));
            cmd.Parameters.Add(Int("@U", userId));
            var list = new List<DisciplineRow>();
            using var r = await cmd.ExecuteReaderAsync();
            while (await r.ReadAsync())
                list.Add(new DisciplineRow(r.GetInt32(0), r.GetString(1), r.GetInt32(2), r.GetInt32(3), r.GetInt32(4), r.GetInt32(5)));
            return list;
        }

        // ---- e. expenses by employee and category ---------------------------------------------------------------------

        public sealed record CategoryAmount(int UserId, string Name, string Category, decimal Approved, decimal Pending);

        public async Task<List<CategoryAmount>> ExpensesByCategoryAsync(DateTime from, DateTime to, int? userId)
        {
            using var conn = _db.GetConnection();
            await conn.OpenAsync();
            if (!await FieldExpenseSchema.ReadyAsync(conn))
                return new List<CategoryAmount>();
            using var cmd = new SqlCommand(@"
SELECT e.UserId, " + NameOf("u") + @", e.Category,
       SUM(CASE WHEN e.Status = 'Approved' THEN e.Amount ELSE 0 END), SUM(CASE WHEN e.Status = 'Submitted' THEN e.Amount ELSE 0 END)
FROM dbo.FieldExpenses e JOIN dbo.IMSUsers u ON u.Id = e.UserId
WHERE e.ExpenseDate BETWEEN @From AND @To AND e.Status IN ('Approved', 'Submitted') AND (@U IS NULL OR e.UserId = @U)
GROUP BY e.UserId, u.Name, u.Username, e.Category
ORDER BY 2, e.Category;", conn);
            Range(cmd, from, to, DateTime.UtcNow, DateTime.UtcNow, userId);
            var list = new List<CategoryAmount>();
            using var r = await cmd.ExecuteReaderAsync();
            while (await r.ReadAsync())
                list.Add(new CategoryAmount(r.GetInt32(0), r.GetString(1), r.GetString(2), r.GetDecimal(3), r.GetDecimal(4)));
            return list;
        }

        // ---- dashboard extras (one date) ------------------------------------------------------------------------------

        public sealed record DashboardExtras(List<FieldEmployeeOption> ActiveEmployees, int FollowUpsDue, int FollowUpsOverdue,
            int PendingExpenses, decimal PendingAmount, int PendingExpensesForDate, bool ExpensesAvailable);

        public async Task<DashboardExtras> DashboardExtrasAsync(DateTime dateIst)
        {
            using var conn = _db.GetConnection();
            await conn.OpenAsync();
            var expenses = await FieldExpenseSchema.ReadyAsync(conn);
            var sql = @"
SELECT u.Id, " + NameOf("u") + @"
FROM dbo.IMSUsers u
WHERE u.IsActive = 1 AND EXISTS (SELECT 1 FROM dbo.UserRoles ur
                                 JOIN dbo.RolePermissions rp ON rp.RoleId = ur.RoleId
                                 JOIN dbo.Permissions p ON p.Id = rp.PermissionId
                                 WHERE ur.UserId = u.Id AND p.Code = N'FieldWork.Day')
ORDER BY 2;

SELECT SUM(CASE WHEN f.DueDate = @D THEN 1 ELSE 0 END), SUM(CASE WHEN f.DueDate < @D THEN 1 ELSE 0 END)
FROM dbo.FieldFollowUps f JOIN dbo.FieldCustomers c ON c.Id = f.CustomerId
WHERE f.Status = 'Pending' AND f.DueDate <= @D AND c.IsActive = 1;"
                + (expenses ? @"

SELECT COUNT(*), ISNULL(SUM(Amount), 0), SUM(CASE WHEN ExpenseDate = @D THEN 1 ELSE 0 END)
FROM dbo.FieldExpenses WHERE Status = 'Submitted';" : "");
            using var cmd = new SqlCommand(sql, conn);
            cmd.Parameters.Add(Date("@D", dateIst));
            var employees = new List<FieldEmployeeOption>();
            int due = 0, overdue = 0, pending = 0, pendingForDate = 0;
            decimal pendingAmount = 0;
            using var r = await cmd.ExecuteReaderAsync();
            while (await r.ReadAsync())
                employees.Add(new FieldEmployeeOption(r.GetInt32(0), r.GetString(1)));
            await r.NextResultAsync();
            if (await r.ReadAsync())
            {
                due = IntOrNull(r, 0) ?? 0;
                overdue = IntOrNull(r, 1) ?? 0;
            }
            if (expenses && await r.NextResultAsync() && await r.ReadAsync())
            {
                pending = r.GetInt32(0);
                pendingAmount = r.GetDecimal(1);
                pendingForDate = IntOrNull(r, 2) ?? 0;
            }
            return new DashboardExtras(employees, due, overdue, pending, pendingAmount, pendingForDate, expenses);
        }

        // The employee filter of the reports: active FieldWork.Day holders plus anyone with a day, customer or expense.
        public async Task<List<FieldEmployeeOption>> ListEmployeesAsync()
        {
            using var conn = _db.GetConnection();
            await conn.OpenAsync();
            var expenses = await FieldExpenseSchema.ReadyAsync(conn);
            using var cmd = new SqlCommand(@"
SELECT u.Id, " + NameOf("u") + @" AS Name
FROM dbo.IMSUsers u
WHERE (u.IsActive = 1 AND EXISTS (SELECT 1 FROM dbo.UserRoles ur
                                  JOIN dbo.RolePermissions rp ON rp.RoleId = ur.RoleId
                                  JOIN dbo.Permissions p ON p.Id = rp.PermissionId
                                  WHERE ur.UserId = u.Id AND p.Code = N'FieldWork.Day'))
   OR EXISTS (SELECT 1 FROM dbo.FieldDays d WHERE d.UserId = u.Id)
   OR EXISTS (SELECT 1 FROM dbo.FieldCustomers c WHERE c.AssignedUserId = u.Id)"
                + (expenses ? @"
   OR EXISTS (SELECT 1 FROM dbo.FieldExpenses e WHERE e.UserId = u.Id)" : "") + @"
ORDER BY Name;", conn);
            var list = new List<FieldEmployeeOption>();
            using var r = await cmd.ExecuteReaderAsync();
            while (await r.ReadAsync())
                list.Add(new FieldEmployeeOption(r.GetInt32(0), r.GetString(1)));
            return list;
        }
    }
}
