using System.Security.Claims;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using PlantStockManager.Authorization;
using PlantStockManager.Data;
using PlantStockManager.Services;
using PlantStockManager.Services.FieldWork;

namespace PlantStockManager.Tests
{
    // Field Work Phase 4 (2026-11-03): expenses end-to-end against a SCRATCH COPY (PSM_SCRATCH_CONNECTION,
    // PlantsIMS2_Scratch_*; SKIPPED otherwise or without 2026-11-03_FieldWorkExpenses.sql). Real service, repository, CHECKs
    // and triggers. A and B are field employees, M a manager. Each test starts from no expense rows for them.
    [Collection("ScratchDb")]
    public class FieldExpenseE2ETests
    {
        // Used by every Field Work test cleanup (FieldCustomerE2ETests.Phase2CleanupSql): expenses reference photos.
        public const string ExpenseCleanupSql = @"
IF OBJECT_ID('dbo.FieldExpenses', 'U') IS NOT NULL
BEGIN
    DELETE ev FROM dbo.FieldExpenseEvents ev JOIN dbo.FieldExpenses e ON e.Id = ev.ExpenseId WHERE e.UserId IN (@A, @B, @C);
    DELETE FROM dbo.FieldExpenses WHERE UserId IN (@A, @B, @C);
    DELETE FROM dbo.FieldPhotos WHERE OwnerUserId IN (@A, @B, @C) AND Purpose = 'ExpenseBill';
END;";

        private sealed record Env(string Cs, FieldExpenseService Service, FieldExpenseRepository Repo, string PhotoRoot, int A, int B, int M);

        private static ClaimsPrincipal P(int id, params string[] perms)
        {
            var claims = new List<Claim> { new("UserId", id.ToString()) };
            foreach (var p in perms)
                claims.Add(p == "FULL" ? new Claim(ClaimsPrincipalSecurityExtensions.FullAccessClaimType, "true")
                                       : new Claim(MinimumAuthorizationLevelHandler.PermissionClaimType, p));
            return new ClaimsPrincipal(new ClaimsIdentity(claims, "T"));
        }

        private static ClaimsPrincipal Employee(int id) => P(id, FieldDayRules.DayPermission);
        private static ClaimsPrincipal Manager(int id) => P(id, FieldDayRules.ManagePermission);

        private static async Task<Env> OpenAsync()
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
                using (var probe = new SqlCommand(FieldExpenseSchema.ProbeSql, conn))
                    Skip.If((int)(await probe.ExecuteScalarAsync())! != 1, "2026-11-03_FieldWorkExpenses.sql is not applied to this scratch copy.");
                using (var cmd = new SqlCommand("SELECT TOP 3 Id FROM dbo.IMSUsers WHERE IsActive = 1 ORDER BY Id", conn))
                using (var r = await cmd.ExecuteReaderAsync())
                    while (await r.ReadAsync()) users.Add(r.GetInt32(0));
                using var setup = new SqlCommand(ExpenseCleanupSql, conn);
                setup.Parameters.AddWithValue("@A", users[0]);
                setup.Parameters.AddWithValue("@B", users[1]);
                setup.Parameters.AddWithValue("@C", users[2]);
                await setup.ExecuteNonQueryAsync();
            }
            var db = new DatabaseHelper(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:DefaultConnection"] = cs }).Build());
            var root = Path.Combine(Path.GetTempPath(), "psm-fw-exp-" + Guid.NewGuid().ToString("N")[..8]);
            var options = new FieldWorkOptions { PhotoRoot = root };
            var repo = new FieldExpenseRepository(db);
            return new Env(cs!, new FieldExpenseService(repo, new FieldPhotoStore(options), options), repo, root, users[0], users[1], users[2]);
        }

        private static byte[] Jpeg(int size = 2048)
        {
            var b = new byte[size];
            Random.Shared.NextBytes(b);
            b[0] = 0xFF; b[1] = 0xD8; b[2] = 0xFF; b[3] = 0xE0; b[^2] = 0xFF; b[^1] = 0xD9;
            return b;
        }

        private static FieldExpenseService.CreateRequest Req(string category = "Food", string amount = "250.50", DateTime? date = null, string? note = null,
            string? token = null, bool photo = true)
            => new(category, amount, date, note, photo ? Jpeg() : null, 1, token ?? Guid.NewGuid().ToString());

        private static async Task<T> Scalar<T>(Env env, string sql, params (string, object)[] p)
        {
            using var conn = new SqlConnection(env.Cs);
            await conn.OpenAsync();
            using var cmd = new SqlCommand(sql, conn);
            foreach (var (k, v) in p) cmd.Parameters.AddWithValue(k, v);
            return (T)(await cmd.ExecuteScalarAsync())!;
        }

        private static async Task Exec(Env env, string sql)
        {
            using var conn = new SqlConnection(env.Cs);
            await conn.OpenAsync();
            using var cmd = new SqlCommand(sql, conn);
            await cmd.ExecuteNonQueryAsync();
        }

        private static int Files(Env env) => Directory.Exists(env.PhotoRoot) ? Directory.GetFiles(env.PhotoRoot).Length : 0;

        private static Task<int> Rows(Env env, int user) => Scalar<int>(env, "SELECT COUNT(*) FROM dbo.FieldExpenses WHERE UserId = @U", ("@U", user));

        private static async Task<int> AddAsync(Env env, int user, FieldExpenseService.CreateRequest? req = null)
        {
            var r = await env.Service.CreateAsync(Employee(user), req ?? Req());
            Assert.True(r.Ok, string.Join(" ", r.Errors));
            return r.Id!.Value;
        }

        [SkippableFact]
        public async Task Create_SavesBillPhotoExpenseAndEvent_TodayByDefault_ListedInMyMonth()
        {
            var env = await OpenAsync();
            var id = await AddAsync(env, env.A, Req("Other", "1,200.75", note: "Courier for samples"));

            var e = await env.Service.GetVisibleAsync(Employee(env.A), id);
            Assert.NotNull(e);
            Assert.Equal(AppClock.Today, e!.ExpenseDate);
            Assert.Equal(1200.75m, e.Amount);
            Assert.Equal("Other", e.Category);
            Assert.Equal(FieldExpenseRules.StatusSubmitted, e.Status);
            Assert.Equal(1, Files(env));
            Assert.Equal(1, await Scalar<int>(env, @"SELECT COUNT(*) FROM dbo.FieldPhotos p JOIN dbo.FieldExpenses e ON e.BillPhotoId = p.Id
WHERE e.Id = @E AND p.Purpose = 'ExpenseBill' AND p.ConsentReminderVersion = 1 AND p.OwnerUserId = @U", ("@E", id), ("@U", env.A)));
            var events = await env.Service.ListEventsAsync(id);
            Assert.Equal("Submitted", Assert.Single(events).Action);
            Assert.NotNull(await env.Service.GetBillPhotoAsync(Employee(env.A), id));

            var mine = await env.Service.ListMineAsync(Employee(env.A), null);
            Assert.Contains(mine.Expenses, x => x.Id == id);
            Assert.Equal(1200.75m, mine.TotalByStatus[FieldExpenseRules.StatusSubmitted]);

            // back-dated within the window is fine; one day further is refused, and so is a missing photo
            Assert.True((await env.Service.CreateAsync(Employee(env.A), Req(date: AppClock.Today.AddDays(-7)))).Ok);
            var old = await env.Service.CreateAsync(Employee(env.A), Req(date: AppClock.Today.AddDays(-8)));
            Assert.False(old.Ok);
            var noPhoto = await env.Service.CreateAsync(Employee(env.A), Req(photo: false));
            Assert.Contains("Take a photo of the bill.", noPhoto.Errors);
            Assert.Equal(2, await Rows(env, env.A));
            Assert.Equal(2, Files(env));
        }

        [SkippableFact]
        public async Task OnlyFieldEmployees_Add_ManagerWithoutDayPermission_Refused()
        {
            var env = await OpenAsync();
            var r = await env.Service.CreateAsync(Manager(env.M), Req());
            Assert.False(r.Ok);
            r = await env.Service.CreateAsync(P(env.M, "FULL"), Req());
            Assert.False(r.Ok);
            Assert.Equal(0, await Rows(env, env.M));
            Assert.Equal(0, Files(env));
        }

        [SkippableFact]
        public async Task Cancel_OwnPendingWithReason_OthersAndDecidedRefused_EventWritten()
        {
            var env = await OpenAsync();
            var id = await AddAsync(env, env.A);

            Assert.False((await env.Service.CancelAsync(Employee(env.A), id, "  ")).Ok);                // reason required
            Assert.False((await env.Service.CancelAsync(Employee(env.B), id, "mine?")).Ok);             // not found for B
            Assert.False((await env.Service.CancelAsync(Manager(env.M), id, "manager")).Ok);           // only the owner cancels
            var ok = await env.Service.CancelAsync(Employee(env.A), id, "Wrong amount, adding again");
            Assert.True(ok.Ok, string.Join(" ", ok.Errors));

            var e = (await env.Service.GetVisibleAsync(Employee(env.A), id))!;
            Assert.Equal(FieldExpenseRules.StatusCancelled, e.Status);
            Assert.Equal("Wrong amount, adding again", e.CancelReason);
            Assert.False((await env.Service.CancelAsync(Employee(env.A), id, "again")).Ok);
            Assert.False((await env.Service.DecideAsync(Manager(env.M), id, true, null)).Ok);
            Assert.Equal(new[] { "Submitted", "Cancelled" }, (await env.Service.ListEventsAsync(id)).Select(x => x.Action));

            // after a decision the owner can no longer cancel
            var id2 = await AddAsync(env, env.A);
            Assert.True((await env.Service.DecideAsync(Manager(env.M), id2, true, null)).Ok);
            Assert.False((await env.Service.CancelAsync(Employee(env.A), id2, "too late")).Ok);
        }

        [SkippableFact]
        public async Task ApproveAndReject_RejectNeedsComment_DecisionIsFinal_EventsRecordWho()
        {
            var env = await OpenAsync();
            var a = await AddAsync(env, env.A);
            var b = await AddAsync(env, env.B);

            Assert.False((await env.Service.DecideAsync(Employee(env.B), a, true, null)).Ok);         // not a manager
            Assert.True((await env.Service.DecideAsync(Manager(env.M), a, true, "ok")).Ok);
            var rejectNoComment = await env.Service.DecideAsync(Manager(env.M), b, false, " ");
            Assert.Contains("Write why the expense is rejected.", rejectNoComment.Errors);
            Assert.True((await env.Service.DecideAsync(Manager(env.M), b, false, "No bill date visible")).Ok);

            Assert.False((await env.Service.DecideAsync(Manager(env.M), a, false, "changed my mind")).Ok);   // final
            Assert.False((await env.Service.DecideAsync(Manager(env.M), b, true, null)).Ok);

            var ea = (await env.Service.GetVisibleAsync(Manager(env.M), a))!;
            Assert.Equal(FieldExpenseRules.StatusApproved, ea.Status);
            Assert.Equal(env.M, ea.DecidedByUserId);
            var evB = (await env.Service.ListEventsAsync(b)).Last();
            Assert.Equal(("Rejected", "Submitted", env.M, "No bill date visible"), (evB.Action, evB.FromStatus!, evB.ByUserId, evB.Comment!));

            // the database refuses a direct change of a decided expense too
            var ex = await Assert.ThrowsAsync<SqlException>(() => Exec(env, $"UPDATE dbo.FieldExpenses SET Status = 'Submitted', DecidedByUserId = NULL, DecidedAtUtc = NULL, DecisionComment = NULL WHERE Id = {a}"));
            Assert.Equal(54454, ex.Number);
            ex = await Assert.ThrowsAsync<SqlException>(() => Exec(env, $"UPDATE dbo.FieldExpenses SET Amount = 1 WHERE Id = {b}"));
            Assert.Equal(54453, ex.Number);
        }

        [SkippableFact]
        public async Task OwnExpense_ManagerCannotDecide_AnotherManagerAndAdministratorCan()
        {
            var env = await OpenAsync();
            var managerEmployee = P(env.A, FieldDayRules.DayPermission, FieldDayRules.ManagePermission);
            var id = (await env.Service.CreateAsync(managerEmployee, Req())).Id!.Value;
            var id2 = (await env.Service.CreateAsync(managerEmployee, Req())).Id!.Value;

            var own = await env.Service.DecideAsync(managerEmployee, id, true, null);
            Assert.False(own.Ok);
            Assert.Contains("own expense", string.Join(" ", own.Errors));
            Assert.False((await env.Service.DecideAsync(managerEmployee, id, false, "self reject")).Ok);
            Assert.Equal(FieldExpenseRules.StatusSubmitted, (await env.Service.GetVisibleAsync(managerEmployee, id))!.Status);

            Assert.True((await env.Service.DecideAsync(Manager(env.M), id, true, null)).Ok);
            // an administrator may decide their own expense
            var admin = P(env.A, FieldDayRules.DayPermission, "FULL");
            Assert.True((await env.Service.DecideAsync(admin, id2, true, null)).Ok);
        }

        [SkippableFact]
        public async Task OtherEmployee_GetsNotFound_ForExpenseAndPhoto_ManagerSeesBoth()
        {
            var env = await OpenAsync();
            var id = await AddAsync(env, env.A);
            Assert.Null(await env.Service.GetVisibleAsync(Employee(env.B), id));
            Assert.Null(await env.Service.GetBillPhotoAsync(Employee(env.B), id));
            Assert.Null(await env.Service.GetVisibleAsync(P(env.B, "Dashboard.View"), id));
            Assert.NotNull(await env.Service.GetVisibleAsync(Manager(env.M), id));
            Assert.NotNull(await env.Service.GetBillPhotoAsync(Manager(env.M), id));
            Assert.DoesNotContain((await env.Service.ListMineAsync(Employee(env.B), null)).Expenses, e => e.Id == id);
        }

        [SkippableFact]
        public async Task DoubleSubmit_Sequential_SameExpenseOneFile()
        {
            var env = await OpenAsync();
            var token = Guid.NewGuid().ToString();
            var first = await env.Service.CreateAsync(Employee(env.A), Req(token: token));
            var second = await env.Service.CreateAsync(Employee(env.A), Req(token: token));
            Assert.True(first.Ok && second.Ok);
            Assert.False(first.Replay);
            Assert.True(second.Replay);
            Assert.Equal(first.Id, second.Id);
            Assert.Equal(1, await Rows(env, env.A));
            Assert.Equal(1, Files(env));
            // another user cannot reuse the token
            Assert.False((await env.Service.CreateAsync(Employee(env.B), Req(token: token))).Ok);
            Assert.Equal(0, await Rows(env, env.B));
        }

        [SkippableFact]
        public async Task DoubleSubmit_Concurrent_OneExpenseOneFile()
        {
            var env = await OpenAsync();
            var token = Guid.NewGuid().ToString();
            var results = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Task.Run(() => env.Service.CreateAsync(Employee(env.A), Req(token: token)))));
            Assert.All(results, r => Assert.True(r.Ok, string.Join(" ", r.Errors)));
            Assert.Single(results.Select(r => r.Id).Distinct());
            Assert.Equal(1, results.Count(r => !r.Replay));
            Assert.Equal(1, await Rows(env, env.A));
            Assert.Equal(1, await Scalar<int>(env, "SELECT COUNT(*) FROM dbo.FieldPhotos WHERE OwnerUserId = @U AND Purpose = 'ExpenseBill'", ("@U", env.A)));
            Assert.Equal(1, Files(env));
        }

        [SkippableFact]
        public async Task ForcedFailure_LeavesNoRowsAndNoFiles()
        {
            var env = await OpenAsync();
            // the last step of the save (the 'Submitted' event) fails: photo row and expense must roll back, the file is deleted
            await Exec(env, "IF OBJECT_ID('dbo.CK_ZZTEST_FailExpenseEvent', 'C') IS NULL ALTER TABLE dbo.FieldExpenseEvents WITH NOCHECK ADD CONSTRAINT CK_ZZTEST_FailExpenseEvent CHECK (Action <> 'Submitted');");
            try
            {
                var r = await env.Service.CreateAsync(Employee(env.A), Req());
                Assert.False(r.Ok);
            }
            finally
            {
                await Exec(env, "IF OBJECT_ID('dbo.CK_ZZTEST_FailExpenseEvent', 'C') IS NOT NULL ALTER TABLE dbo.FieldExpenseEvents DROP CONSTRAINT CK_ZZTEST_FailExpenseEvent;");
            }
            Assert.Equal(0, await Rows(env, env.A));
            Assert.Equal(0, await Scalar<int>(env, "SELECT COUNT(*) FROM dbo.FieldPhotos WHERE OwnerUserId = @U AND Purpose = 'ExpenseBill'", ("@U", env.A)));
            Assert.Equal(0, Files(env));
            // and the same form works afterwards
            Assert.True((await env.Service.CreateAsync(Employee(env.A), Req())).Ok);
        }

        [SkippableFact]
        public async Task ManagerList_FiltersTotalsAndExport_MatchTheFilteredRows()
        {
            var env = await OpenAsync();
            var a1 = await AddAsync(env, env.A, Req("Petrol", "100"));
            var a2 = await AddAsync(env, env.A, Req("Food", "40.50"));
            var a3 = await AddAsync(env, env.A, Req("Petrol", "60"));
            var b1 = await AddAsync(env, env.B, Req("Petrol", "500"));
            Assert.True((await env.Service.DecideAsync(Manager(env.M), a1, true, null)).Ok);
            Assert.True((await env.Service.CancelAsync(Employee(env.A), a3, "duplicate")).Ok);

            var ids = new[] { env.A, env.B };
            var today = AppClock.Today;
            var all = await env.Service.ListForManagerAsync(FieldExpenseRules.NormalizeFilter(today, today, null, null, null, ids, today));
            var totalA = all.Totals.Single(t => t.UserId == env.A);
            Assert.Equal((100m, 1, 40.50m, 1), (totalA.Approved, totalA.ApprovedCount, totalA.Pending, totalA.PendingCount));   // cancelled not counted
            Assert.Equal(500m, all.Totals.Single(t => t.UserId == env.B).Pending);

            var petrolA = await env.Service.ListForManagerAsync(FieldExpenseRules.NormalizeFilter(today, today, env.A, "Petrol", null, ids, today));
            Assert.Equal(new[] { a3, a1 }.OrderBy(x => x), petrolA.Expenses.Select(e => e.Id).OrderBy(x => x));
            var pending = await env.Service.ListForManagerAsync(FieldExpenseRules.NormalizeFilter(today, today, null, null, "Submitted", ids, today));
            Assert.Contains(pending.Expenses, e => e.Id == a2);
            Assert.Contains(pending.Expenses, e => e.Id == b1);
            Assert.DoesNotContain(pending.Expenses, e => e.Id == a1 || e.Id == a3);

            // an unknown employee id is dropped (all employees), never widened beyond the list
            Assert.Null(FieldExpenseRules.NormalizeFilter(today, today, 987654, "Bogus", "Bogus", ids, today).UserId);

            // the export is built from exactly the listed rows
            var rows = FieldExpenseService.ExportRows(petrolA.Expenses).ToList();
            Assert.Equal(petrolA.Expenses.Count, rows.Count);
            Assert.All(rows, r => Assert.Equal("Petrol", r[2]));
            var xlsx = ExportHelper.BuildExcel("Expenses", FieldExpenseService.ExportHeaders, rows);
            using var wb = new ClosedXML.Excel.XLWorkbook(new MemoryStream(xlsx));
            Assert.Equal(petrolA.Expenses.Count + 1, wb.Worksheet(1).LastRowUsed()!.RowNumber());
        }
    }
}
