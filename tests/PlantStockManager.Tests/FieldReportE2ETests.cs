using System.Security.Claims;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using PlantStockManager.Authorization;
using PlantStockManager.Data;
using PlantStockManager.Services;
using PlantStockManager.Services.FieldWork;

namespace PlantStockManager.Tests
{
    // Field Work Phase 5 (2026-11-03): reports end-to-end on a SCRATCH COPY (PSM_SCRATCH_CONNECTION; SKIPPED otherwise or
    // without the Phase 2 and Phase 4 migrations). A small data set is seeded TODAY through the real services, so every
    // number can be checked by hand:
    //   A: Start Day 100 km at (12.97, 77.59) -> New Visit "Alpha" at 12.98 (status Won) -> New Visit "Beta" at 12.99
    //      (Interested, follow-up tomorrow) -> New Visit "Gamma" (archived afterwards) -> End Day 130 km back at 12.97.
    //      Route = 12.97 -> 12.98 -> 12.99 -> 12.99 -> 12.97 = 1.112 + 1.112 + 0 + 2.224 = 4.45 km; odometer 30 km.
    //      Expenses: Petrol 100 approved, Food 50 pending, Toll 70 cancelled (not counted).
    //   B: Start Day 10 km -> New Visit "Delta" (Negotiation, no follow-up) -> day left open. Expense: Parking 20 rejected.
    //   M: manager (no field day).
    [Collection("ScratchDb")]
    public class FieldReportE2ETests
    {
        private sealed record Env(string Cs, FieldReportService Reports, int A, int B, int M);

        private static ClaimsPrincipal P(int id, params string[] perms)
            => new(new ClaimsIdentity(new[] { new Claim("UserId", id.ToString()) }
                .Concat(perms.Select(p => new Claim(MinimumAuthorizationLevelHandler.PermissionClaimType, p))), "T"));

        private static byte[] Jpeg()
        {
            var b = new byte[1024];
            Random.Shared.NextBytes(b);
            b[0] = 0xFF; b[1] = 0xD8; b[2] = 0xFF; b[3] = 0xE0; b[^2] = 0xFF; b[^1] = 0xD9;
            return b;
        }

        private static string Phone() => "9" + Random.Shared.NextInt64(100_000_000, 999_999_999);

        private static async Task<Env> SeedAsync()
        {
            var cs = Environment.GetEnvironmentVariable("PSM_SCRATCH_CONNECTION");
            Skip.If(string.IsNullOrWhiteSpace(cs), "Set PSM_SCRATCH_CONNECTION to a PlantsIMS2_Scratch_* database connection string to run these tests.");
            var users = new List<int>();
            using (var conn = new SqlConnection(cs))
            {
                await conn.OpenAsync();
                using (var cmd = new SqlCommand("SELECT DB_NAME()", conn))
                    if (!((string)(await cmd.ExecuteScalarAsync())!).StartsWith("PlantsIMS2_Scratch_", StringComparison.Ordinal))
                        throw new InvalidOperationException("Only PlantsIMS2_Scratch_* databases are allowed.");
                using (var probe = new SqlCommand(FieldCustomerSchema.ProbeSql, conn))
                    Skip.If((int)(await probe.ExecuteScalarAsync())! != 1, "2026-11-03_FieldWorkCustomers.sql is not applied to this scratch copy.");
                using (var probe = new SqlCommand(FieldExpenseSchema.ProbeSql, conn))
                    Skip.If((int)(await probe.ExecuteScalarAsync())! != 1, "2026-11-03_FieldWorkExpenses.sql is not applied to this scratch copy.");
                using (var cmd = new SqlCommand("SELECT TOP 3 Id FROM dbo.IMSUsers WHERE IsActive = 1 ORDER BY Id", conn))
                using (var r = await cmd.ExecuteReaderAsync())
                    while (await r.ReadAsync()) users.Add(r.GetInt32(0));
                using var setup = new SqlCommand(FieldCustomerE2ETests.Phase2CleanupSql + @"
DELETE e FROM dbo.FieldDayEvents e JOIN dbo.FieldDays d ON d.Id = e.FieldDayId WHERE d.UserId IN (@A, @B, @C);
DELETE FROM dbo.FieldDays WHERE UserId IN (@A, @B, @C);
DELETE FROM dbo.FieldPhotos WHERE OwnerUserId IN (@A, @B, @C);
INSERT INTO dbo.UserRoles (UserId, RoleId, CreatedDate)
SELECT u.Id, r.Id, SYSUTCDATETIME() FROM dbo.IMSUsers u CROSS JOIN dbo.Roles r
WHERE u.Id IN (@A, @B) AND r.Name = N'Field Employee' AND NOT EXISTS (SELECT 1 FROM dbo.UserRoles x WHERE x.UserId = u.Id AND x.RoleId = r.Id);", conn);
                setup.Parameters.AddWithValue("@A", users[0]);
                setup.Parameters.AddWithValue("@B", users[1]);
                setup.Parameters.AddWithValue("@C", users[2]);
                await setup.ExecuteNonQueryAsync();
            }
            var (a, b, m) = (users[0], users[1], users[2]);
            var db = new DatabaseHelper(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:DefaultConnection"] = cs }).Build());
            var options = new FieldWorkOptions { PhotoRoot = Path.Combine(Path.GetTempPath(), "psm-fw-rep-" + Guid.NewGuid().ToString("N")[..8]) };
            var photos = new FieldPhotoStore(options);
            var days = new FieldDayService(new FieldDayRepository(db), photos, options);
            var customers = new FieldCustomerService(new FieldCustomerRepository(db), photos, options);
            var visits = new FieldVisitService(new FieldVisitRepository(db), customers, photos, options);
            var expenses = new FieldExpenseService(new FieldExpenseRepository(db), photos, options);
            var empA = P(a, FieldDayRules.DayPermission);
            var empB = P(b, FieldDayRules.DayPermission);
            var mgr = P(m, FieldDayRules.ManagePermission);

            async Task Start(int u, decimal km)
            {
                await days.RecordConsentAsync(u);
                var r = await days.StartDayAsync(u, "OwnBike", new FieldDayService.ReadingRequest(km, 12.97m, 77.59m, 10m, null, Jpeg(), Guid.NewGuid().ToString()));
                Assert.True(r.Ok, string.Join(" ", r.Errors));
            }
            async Task<int> Visit(ClaimsPrincipal who, string name, decimal lat, string? status, DateTime? next = null, string? district = "Kolar")
            {
                var r = await visits.SaveVisitAsync(who, new FieldVisitService.NewVisitRequest(FieldVisitService.ModeNew, null,
                    new FieldCustomerRules.CustomerInput(name, null, Phone(), null, "Nursery", "Hoskote", district, null), true,
                    new FieldVisitService.VisitRequest(0, lat, 77.59m, 10m, null, "Talked", null, Array.Empty<string?>(), Array.Empty<string?>(), new List<byte[]>(),
                        null, status, next, null, Guid.NewGuid().ToString())));
                Assert.True(r.Ok, string.Join(" ", r.Errors));
                return r.CustomerId!.Value;
            }
            async Task<int> Expense(ClaimsPrincipal who, string category, string amount)
            {
                var r = await expenses.CreateAsync(who, new FieldExpenseService.CreateRequest(category, amount, null, null, Jpeg(), 1, Guid.NewGuid().ToString()));
                Assert.True(r.Ok, string.Join(" ", r.Errors));
                return r.Id!.Value;
            }

            await Start(a, 100m);
            await Visit(empA, "ZZTEST REP Alpha", 12.98m, "Won");
            await Visit(empA, "ZZTEST REP Beta", 12.99m, "Interested", AppClock.Today.AddDays(1), district: " KOLAR ");
            var gamma = await Visit(empA, "ZZTEST REP Gamma", 12.99m, null, district: null);
            Assert.True((await customers.ArchiveAsync(mgr, gamma, "Duplicate entry")).Ok);
            var end = await days.EndDayAsync(a, new FieldDayService.ReadingRequest(130m, 12.97m, 77.59m, 10m, null, Jpeg(), Guid.NewGuid().ToString()));
            Assert.True(end.Ok, string.Join(" ", end.Errors));

            await Start(b, 10m);
            await Visit(empB, "ZZTEST REP Delta", 12.97m, "Negotiation", district: "Mandya");

            var petrol = await Expense(empA, "Petrol", "100");
            Assert.True((await expenses.DecideAsync(mgr, petrol, true, null)).Ok);
            await Expense(empA, "Food", "50");
            var toll = await Expense(empA, "Toll", "70");
            Assert.True((await expenses.CancelAsync(empA, toll, "typed twice")).Ok);
            var parking = await Expense(empB, "Parking", "20");
            Assert.True((await expenses.DecideAsync(mgr, parking, false, "no bill date")).Ok);

            return new Env(cs!, new FieldReportService(new FieldReportRepository(db)), a, b, m);
        }

        private static FieldReportService.Filter Today(int? user = null) => new(new FieldReportRules.DateRange(AppClock.Today, AppClock.Today), user);

        [SkippableFact]
        public async Task SeededDay_EveryReportShowsTheHandCheckedNumbers()
        {
            var env = await SeedAsync();
            var mgr = P(env.M, FieldDayRules.ManagePermission);

            // a. daily
            var (daily, _) = await env.Reports.DailyAsync(mgr, Today());
            var da = daily.Rows.Single(r => r.UserId == env.A);
            Assert.Equal(("Closed", 3, 2, (decimal?)30m, 4.45m, false), (da.Status, da.Visits, da.NewCustomers, da.OdometerKm, da.RouteKm, da.Flagged));
            var db = daily.Rows.Single(r => r.UserId == env.B);
            Assert.Equal(("Open", 1, 1, (decimal?)null), (db.Status, db.Visits, db.NewCustomers, db.OdometerKm));
            Assert.Equal(0m, db.RouteKm);   // start and the one visit are at the same point

            // b. summary
            var (summary, expensesOn) = await env.Reports.SummaryAsync(mgr, Today());
            Assert.True(expensesOn);
            var sa = summary.Rows.Single(r => r.UserId == env.A);
            Assert.Equal((1, 0, 3, 2, 30m, 4.45m, 0, 100m, 50m),
                (sa.DaysWorked, sa.AutoClosedDays, sa.Visits, sa.NewCustomers, sa.OdometerKm, sa.RouteKm, sa.FlaggedDays, sa.ExpensesApproved, sa.ExpensesPending));
            var sb = summary.Rows.Single(r => r.UserId == env.B);
            Assert.Equal((1, 1, 0m, 0m, 0m), (sb.DaysWorked, sb.Visits, sb.OdometerKm, sb.ExpensesApproved, sb.ExpensesPending));   // rejected not counted

            // c. leads (archived Gamma left out); scoped to the seeded employees -- other users' data on the copy must not matter
            var leads = await env.Reports.LeadsAsync(mgr, Today(env.A));
            Assert.Equal((1, 0), (leads.MovedToWon, leads.MovedToLost));
            var la = leads.ByEmployee.Single(g => g.Key == env.A.ToString());
            Assert.Equal((2, 1, 1, 0), (la.CurrentTotal, la.Current["Won"], la.Current["Interested"], la.Current["New"]));
            Assert.Equal(1, la.MovedToWon);
            var kolar = leads.ByDistrict.Single(g => g.Key == "KOLAR");           // "Kolar" and " KOLAR " are one district
            Assert.Equal((2, 1), (kolar.CurrentTotal, kolar.MovedToWon));
            var leadsB = await env.Reports.LeadsAsync(mgr, Today(env.B));
            Assert.Equal(1, leadsB.ByDistrict.Single(g => g.Key == "MANDYA").Current["Negotiation"]);
            Assert.DoesNotContain(leadsB.ByEmployee, g => g.Key == env.A.ToString());

            // d. discipline (as of today)
            var discipline = await env.Reports.DisciplineAsync(mgr, Today());
            var xa = discipline.Rows.Single(r => r.UserId == env.A);
            Assert.Equal((2, 0, 0, 0), (xa.ActiveCustomers, xa.NoRecentVisit, xa.OverdueFollowUps, xa.HotWithoutFollowUp));   // Beta has its follow-up
            var xb = discipline.Rows.Single(r => r.UserId == env.B);
            Assert.Equal((1, 1), (xb.ActiveCustomers, xb.HotWithoutFollowUp));

            // e. expenses
            var exp = await env.Reports.ExpensesAsync(mgr, Today());
            var ea = exp.Rows.Single(r => r.UserId == env.A);
            Assert.Equal((100m, 50m, (100m, 0m), (0m, 50m), (0m, 0m)), (ea.Approved, ea.Pending, ea.ByCategory["Petrol"], ea.ByCategory["Food"], ea.ByCategory["Toll"]));
            Assert.DoesNotContain(exp.Rows, r => r.UserId == env.B);

            // employee filter narrows every report to that employee
            Assert.All((await env.Reports.DailyAsync(mgr, Today(env.B))).Rows.Rows, r => Assert.Equal(env.B, r.UserId));
            Assert.All((await env.Reports.SummaryAsync(mgr, Today(env.B))).Rows.Rows, r => Assert.Equal(env.B, r.UserId));
        }

        [SkippableFact]
        public async Task Dashboard_Today_StartedEndedVisitsNewCustomersQueue()
        {
            var env = await SeedAsync();
            var mgr = P(env.M, FieldDayRules.ManagePermission);
            var d = await env.Reports.DashboardAsync(mgr, null);
            Assert.Equal(AppClock.Today, d.Date);
            var rowA = d.Employees.Single(e => e.UserId == env.A);
            var rowB = d.Employees.Single(e => e.UserId == env.B);
            Assert.Equal(("Closed", 3), (rowA.Status, rowA.Visits));
            Assert.Equal(("Open", 1), (rowB.Status, rowB.Visits));
            Assert.True(d.Started >= 2 && d.Ended >= 1);
            Assert.True(d.Visits >= 4 && d.NewCustomers >= 3);

            using var conn = new SqlConnection(env.Cs);
            await conn.OpenAsync();
            using var q = new SqlCommand("SELECT COUNT(*), ISNULL(SUM(Amount), 0) FROM dbo.FieldExpenses WHERE Status = 'Submitted'", conn);
            using (var r = await q.ExecuteReaderAsync())
            {
                await r.ReadAsync();
                Assert.Equal((r.GetInt32(0), r.GetDecimal(1)), (d.PendingExpenses, d.PendingAmount));
            }
            Assert.True(d.PendingExpenses >= 1);   // A's Food 50

            // a future date is shown as today; a past date with no activity is empty for A and B
            Assert.Equal(AppClock.Today, (await env.Reports.DashboardAsync(mgr, AppClock.Today.AddDays(5))).Date);
            var past = await env.Reports.DashboardAsync(mgr, AppClock.Today.AddDays(-400));
            Assert.Null(past.Employees.Single(e => e.UserId == env.A).Status);
        }

        [SkippableFact]
        public async Task MySummary_IsMyDataOnly_AndManagersOnlyForTheReports()
        {
            var env = await SeedAsync();
            var mineA = (await env.Reports.MySummaryAsync(P(env.A, FieldDayRules.DayPermission), null))!;
            Assert.Equal((1, 3, 2, 30m, 100m, 50m), (mineA.Totals.DaysWorked, mineA.Totals.Visits, mineA.Totals.NewCustomers, mineA.Totals.OdometerKm,
                mineA.Totals.ExpensesApproved, mineA.Totals.ExpensesPending));
            Assert.All(mineA.Days, r => Assert.Equal(env.A, r.UserId));
            var mineB = (await env.Reports.MySummaryAsync(P(env.B, FieldDayRules.DayPermission), null))!;
            Assert.Equal((1, 1, 0m), (mineB.Totals.Visits, mineB.Totals.NewCustomers, mineB.Totals.ExpensesApproved));
            Assert.All(mineB.Days, r => Assert.Equal(env.B, r.UserId));

            // the report methods refuse anyone without FieldWork.Manage (a direct call, not only the page rule)
            var emp = P(env.A, FieldDayRules.DayPermission);
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => env.Reports.SummaryAsync(emp, Today()));
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => env.Reports.DashboardAsync(emp, null));
            // an unknown employee id in a filter is dropped, never widened
            Assert.Null(FieldReportService.NormalizeFilter(null, null, 987654, new[] { env.A }, AppClock.Today).UserId);
        }
    }
}
