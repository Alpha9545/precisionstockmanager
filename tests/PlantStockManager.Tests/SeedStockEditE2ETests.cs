using System.Net;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using PlantStockManager.Authorization;
using PlantStockManager.Data;
using PlantStockManager.Services;
using EditPage = PlantStockManager.Pages.Production.SeedStock.EditModel;

namespace PlantStockManager.Tests
{
    // 2026-10-20, against a SCRATCH COPY only (PSM_SCRATCH_CONNECTION, database name PlantsIMS2_Scratch_*; SKIPPED otherwise):
    //   * an administrator corrects a Seed Stock receipt's date and quantity on the Edit page -- the SAME lot is updated
    //     (no new lot), the original receipt stays in the ledger with its reversal, the balance equals the ledger, and
    //     seeds already used stay covered (the history never goes negative);
    //   * invalid / stale / no-op posts change nothing;
    //   * the administrator opens Reports > Attendance History / Overtime History over HTTP (non-administrators are
    //     refused before any page code runs -- AdminOnlySeedStockEditAndLabourReportsTests).
    [Collection("ScratchDb")]
    public class SeedStockEditE2ETests : IClassFixture<DailyOperationsReportAuthorizationTests.ScratchAppFactory>
    {
        private const string ConnectionVariable = "PSM_SCRATCH_CONNECTION";
        private const string RequiredPrefix = "PlantsIMS2_Scratch_";

        private readonly DailyOperationsReportAuthorizationTests.ScratchAppFactory _scratchApp;

        public SeedStockEditE2ETests(DailyOperationsReportAuthorizationTests.ScratchAppFactory scratchApp) => _scratchApp = scratchApp;

        private sealed class NullTempData : ITempDataProvider
        {
            public IDictionary<string, object> LoadTempData(HttpContext context) => new Dictionary<string, object>();
            public void SaveTempData(HttpContext context, IDictionary<string, object> values) { }
        }

        private sealed class StaticMonitor<T> : IOptionsMonitor<T>
        {
            public StaticMonitor(T value) => CurrentValue = value;
            public T CurrentValue { get; }
            public T Get(string? name) => CurrentValue;
            public IDisposable? OnChange(Action<T, string?> listener) => null;
        }

        private sealed class Env
        {
            public string Cs = "";
            public ServiceProvider Provider = null!;
            public T Get<T>() where T : notnull => Provider.GetRequiredService<T>();

            public async Task<T> ScalarAsync<T>(string sql, params (string Name, object? Value)[] args)
            {
                using var conn = new SqlConnection(Cs);
                await conn.OpenAsync();
                using var cmd = new SqlCommand(sql, conn);
                foreach (var (n, v) in args) cmd.Parameters.AddWithValue(n, v ?? DBNull.Value);
                var r = await cmd.ExecuteScalarAsync();
                return r == null || r == DBNull.Value ? default! : (T)Convert.ChangeType(r, Nullable.GetUnderlyingType(typeof(T)) ?? typeof(T));
            }

            public async Task<ClaimsPrincipal> LoginAsync(int userId)
            {
                var factory = Get<UserClaimsFactory>();
                var user = await factory.GetActiveUserAsync(userId);
                Assert.NotNull(user);
                return await factory.CreatePrincipalAsync(user!);
            }

            public T Page<T>(ClaimsPrincipal principal) where T : PageModel
            {
                var http = new DefaultHttpContext { User = principal, RequestServices = Provider };
                var page = ActivatorUtilities.CreateInstance<T>(Provider);
                page.PageContext = new PageContext(new ActionContext(http, new Microsoft.AspNetCore.Routing.RouteData(), new CompiledPageActionDescriptor()));
                page.TempData = new TempDataDictionary(http, new NullTempData());
                return page;
            }

            public Task<int> LedgerRowsAsync(int lot) => ScalarAsync<int>("SELECT COUNT(*) FROM dbo.SeedStockTransactions WHERE SeedStockId = @L", ("@L", lot));
            public Task<decimal> PhysicalAsync(int lot) => ScalarAsync<decimal>("SELECT PhysicalQuantity FROM dbo.SeedStock WHERE Id = @L", ("@L", lot));
            public Task<decimal> LedgerSumAsync(int lot) => ScalarAsync<decimal>("SELECT ISNULL(SUM(Quantity), 0) FROM dbo.SeedStockTransactions WHERE SeedStockId = @L", ("@L", lot));
        }

        private static string ScratchConnectionOrSkip()
        {
            var cs = Environment.GetEnvironmentVariable(ConnectionVariable);
            Skip.If(string.IsNullOrWhiteSpace(cs), $"Set {ConnectionVariable} to a {RequiredPrefix}* database connection string to run these tests.");
            return cs!;
        }

        private static async Task<Env> OpenAsync()
        {
            var cs = ScratchConnectionOrSkip();
            using (var conn = new SqlConnection(cs))
            {
                await conn.OpenAsync();
                using var cmd = new SqlCommand("SELECT DB_NAME()", conn);
                var name = (string)(await cmd.ExecuteScalarAsync())!;
                if (!name.StartsWith(RequiredPrefix, StringComparison.Ordinal))
                    throw new InvalidOperationException($"Refusing to run these tests against '{name}'. Only databases named {RequiredPrefix}* are allowed.");
            }
            var services = new ServiceCollection();
            services.AddSingleton<IConfiguration>(new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:DefaultConnection"] = cs }).Build());
            services.AddLogging();
            services.AddSingleton<DatabaseHelper>();
            services.AddSingleton<AreaAccessService>();
            services.AddSingleton<IOptions<SecurityOptions>>(Options.Create(new SecurityOptions { FullAccessRoleNames = new[] { "System Administrator" } }));
            services.AddSingleton<IOptionsMonitor<SeedlingWorkflowOptions>>(new StaticMonitor<SeedlingWorkflowOptions>(new SeedlingWorkflowOptions()));
            services.AddSingleton<SeedlingAreaScope>();
            services.AddScoped<UserClaimsFactory>();
            services.AddSingleton<IAuthorizationPolicyProvider, PermissionAuthorizationPolicyProvider>();
            services.AddScoped<IAuthorizationHandler, MinimumAuthorizationLevelHandler>();
            services.AddAuthorization();
            services.AddScoped<FeatureAccessService>();
            foreach (var t in typeof(DatabaseHelper).Assembly.GetTypes().Where(t => t.Namespace == "PlantStockManager.Data" && t.IsClass && t.Name.EndsWith("Repository")))
                services.AddScoped(t);
            return new Env { Cs = cs, Provider = services.BuildServiceProvider() };
        }

        private sealed record Fixture(int Admin, int SpeciesId, int Lot, int Receipt, DateTime ReceivedOnLocal);

        // A new variety with ONE receipt of 1000 seeds, ten days ago, at the Main Office -- through the real receive path --
        // then 600 of them sown five days ago (a 'Sown' deduction, as Direct / Bed Sowing record it).
        private static async Task<Fixture> NewLotAsync(Env env)
        {
            var admin = await env.ScalarAsync<int>(@"SELECT TOP 1 ur.UserId FROM dbo.UserRoles ur JOIN dbo.Roles r ON r.Id = ur.RoleId
JOIN dbo.IMSUsers u ON u.Id = ur.UserId AND u.IsActive = 1 WHERE r.Name = N'System Administrator' ORDER BY ur.UserId");
            Skip.If(admin == 0, "No active System Administrator in this copy.");
            var mainOffice = await env.ScalarAsync<int>("SELECT TOP 1 Id FROM dbo.Area WHERE AreaType = N'MainOffice' AND IsActive = 1 ORDER BY Id");
            Skip.If(mainOffice == 0, "No active Main Office Area in this copy.");

            var name = "ZZTEST SeedEdit " + Guid.NewGuid().ToString("N")[..10];
            var species = await env.ScalarAsync<int>(@"INSERT INTO dbo.PlantSpecies (PlantTypeId, Name) VALUES ((SELECT MIN(Id) FROM dbo.PlantTypes), @N);
SELECT CAST(SCOPE_IDENTITY() AS INT)", ("@N", name));
            var receivedOn = SeedStockEditRules.ToMinute(DateTime.Now.AddDays(-10));
            var (ok, msg, lot) = await env.Get<SeedStockRepository>().ReceiveAsync(species, mainOffice, "LOT-" + name[^10..], null, "Seeds",
                1000m, receivedOn, "first count", admin, "seed-edit-test");
            Assert.True(ok, msg);

            using (var conn = new SqlConnection(env.Cs))
            {
                await conn.OpenAsync();
                using var tx = conn.BeginTransaction();
                var (sOk, sMsg) = await env.Get<SeedStockRepository>().RecordTransactionAsync(conn, tx, lot, -600m, "Sown", "SeedSowing", null, admin,
                    "seed-edit-test sowing", DateTime.UtcNow.AddDays(-5));
                Assert.True(sOk, sMsg);
                tx.Commit();
            }
            var receipt = await env.ScalarAsync<int>("SELECT Id FROM dbo.SeedStockTransactions WHERE SeedStockId = @L AND TransactionType = N'StockIn'", ("@L", lot));
            return new Fixture(admin, species, lot, receipt, receivedOn);
        }

        private static async Task CleanupAsync(Env env, Fixture f)
        {
            using var conn = new SqlConnection(env.Cs);
            await conn.OpenAsync();
            using var cmd = new SqlCommand(@"DELETE FROM dbo.SeedStockTransactions WHERE SeedStockId = @L;
DELETE FROM dbo.SeedStock WHERE Id = @L; DELETE FROM dbo.PlantSpecies WHERE Id = @S;", conn);
            cmd.Parameters.AddWithValue("@L", f.Lot);
            cmd.Parameters.AddWithValue("@S", f.SpeciesId);
            await cmd.ExecuteNonQueryAsync();
        }

        private static EditPage Post(Env env, ClaimsPrincipal user, int receiptId, decimal? qty, DateTime? on, string? notes, string? reason)
        {
            var page = env.Page<EditPage>(user);
            (page.ReceiptId, page.Quantity, page.ReceivedOn, page.Notes, page.Reason) = (receiptId, qty, on, notes, reason);
            return page;
        }

        [SkippableFact]
        public async Task Administrator_CorrectsTheReceiptDateAndQuantity_InPlace_WithTheLedgerReconciled()
        {
            var env = await OpenAsync();
            var f = await NewLotAsync(env);
            try
            {
                var admin = await env.LoginAsync(f.Admin);
                Assert.True(await env.Get<FeatureAccessService>().CanAccessPageAsync(admin, SeedStockEditRules.EditPage));
                Assert.Equal(400m, await env.PhysicalAsync(f.Lot));
                var lotsOfVariety = await env.ScalarAsync<int>("SELECT COUNT(*) FROM dbo.SeedStock WHERE SpeciesId = @S", ("@S", f.SpeciesId));

                // GET: the only receipt is pre-selected and the form holds its values
                var get = env.Page<EditPage>(admin);
                Assert.IsType<PageResult>(await get.OnGetAsync(f.Lot, null));
                Assert.Equal(f.Receipt, Assert.Single(get.Receipts).Id);
                Assert.Equal((f.Receipt, 1000m, f.ReceivedOnLocal, "first count"), (get.ReceiptId, get.Quantity, get.ReceivedOn, get.Notes));

                // invalid posts: the form again with the reason, nothing written
                var rows = await env.LedgerRowsAsync(f.Lot);
                foreach (var (qty, on, reason, field) in new (decimal?, DateTime?, string?, string)[]
                {
                    (0m, f.ReceivedOnLocal, "r", "Quantity"), (-5m, f.ReceivedOnLocal, "r", "Quantity"), (12.5m, f.ReceivedOnLocal, "r", "Quantity"),
                    (null, f.ReceivedOnLocal, "r", "Quantity"), (1000m, DateTime.Today.AddDays(2), "r", "ReceivedOn"), (1000m, null, "r", "ReceivedOn"),
                    (1000m, new DateTime(1990, 1, 1), "r", "ReceivedOn"), (1200m, f.ReceivedOnLocal, null, "Reason"),
                })
                {
                    var bad = Post(env, admin, f.Receipt, qty, on, "x", reason);
                    Assert.IsType<PageResult>(await bad.OnPostAsync(f.Lot));
                    Assert.True(bad.ModelState[field]?.Errors.Count > 0, $"{field} error expected for {qty} / {on} / {reason}");
                }
                Assert.Equal((rows, 400m), (await env.LedgerRowsAsync(f.Lot), await env.PhysicalAsync(f.Lot)));

                // too few seeds for what was already sown (600 used five days ago) -- refused, nothing written
                var tooFew = Post(env, admin, f.Receipt, 500m, f.ReceivedOnLocal, "first count", "recount");
                Assert.IsType<PageResult>(await tooFew.OnPostAsync(f.Lot));
                Assert.Contains(tooFew.ModelState[string.Empty]!.Errors, e => e.ErrorMessage.Contains("already used"));
                // received AFTER the seeds were sown -- refused, nothing written
                var tooLate = Post(env, admin, f.Receipt, 1000m, DateTime.Now.AddDays(-2), "first count", "date");
                Assert.IsType<PageResult>(await tooLate.OnPostAsync(f.Lot));
                Assert.Contains(tooLate.ModelState[string.Empty]!.Errors, e => e.ErrorMessage.Contains("already used"));
                Assert.Equal((rows, 400m), (await env.LedgerRowsAsync(f.Lot), await env.PhysicalAsync(f.Lot)));

                // the correction: 1000 -> 1200 seeds, received two days earlier
                var newOn = f.ReceivedOnLocal.AddDays(-2);
                var save = Post(env, admin, f.Receipt, 1200m, newOn, "recounted", "wrong count and date");
                var redirect = Assert.IsType<RedirectToPageResult>(await save.OnPostAsync(f.Lot));
                Assert.Equal("/Production/SeedStock/Details", redirect.PageName);
                Assert.Contains("Receipt corrected", save.TempData["Success"] as string);

                // the SAME lot, no new lot; balance 1200 - 600 = 600 and equal to the ledger
                Assert.Equal(lotsOfVariety, await env.ScalarAsync<int>("SELECT COUNT(*) FROM dbo.SeedStock WHERE SpeciesId = @S", ("@S", f.SpeciesId)));
                Assert.Equal(600m, await env.PhysicalAsync(f.Lot));
                Assert.Equal(600m, await env.LedgerSumAsync(f.Lot));
                Assert.Equal(rows + 2, await env.LedgerRowsAsync(f.Lot));
                Assert.False(string.IsNullOrEmpty(await env.ScalarAsync<string>("SELECT ModifiedBy FROM dbo.SeedStock WHERE Id = @L", ("@L", f.Lot))));

                // the original receipt is untouched; its reversal is on the ORIGINAL date with the audit text
                Assert.Equal(1000m, await env.ScalarAsync<decimal>("SELECT Quantity FROM dbo.SeedStockTransactions WHERE Id = @R", ("@R", f.Receipt)));
                Assert.Equal(-1000m, await env.ScalarAsync<decimal>(
                    "SELECT Quantity FROM dbo.SeedStockTransactions WHERE ReferenceType = N'SeedStockEdit' AND ReferenceId = @R AND SeedStockId = @L", ("@R", f.Receipt), ("@L", f.Lot)));
                Assert.Equal(0, await env.ScalarAsync<int>(@"SELECT DATEDIFF(MINUTE, o.TransactionDate, r.TransactionDate) FROM dbo.SeedStockTransactions o
JOIN dbo.SeedStockTransactions r ON r.ReferenceType = N'SeedStockEdit' AND r.ReferenceId = o.Id WHERE o.Id = @R", ("@R", f.Receipt)));
                var audit = await env.ScalarAsync<string>("SELECT Remarks FROM dbo.SeedStockTransactions WHERE ReferenceType = N'SeedStockEdit' AND ReferenceId = @R", ("@R", f.Receipt));
                Assert.Contains($"receipt #{f.Receipt}", audit);
                Assert.Contains("Reason: wrong count and date", audit);

                // the corrected receipt: an ordinary receipt on the new date -- now the lot's only editable receipt
                var receipts = await env.Get<SeedStockRepository>().GetEditableReceiptsAsync(f.Lot);
                var corrected = Assert.Single(receipts);
                Assert.NotEqual(f.Receipt, corrected.Id);
                Assert.Equal((1200m, "recounted", newOn), (corrected.Quantity, corrected.Remarks, SeedStockEditRules.ToMinute(corrected.TransactionDate.ToLocalTime())));

                // the historical balance never goes negative (stock available from the new date onward >= 0)
                using (var conn = new SqlConnection(env.Cs))
                {
                    await conn.OpenAsync();
                    using var tx = conn.BeginTransaction();
                    Assert.True(await SeedStockRepository.AvailableFromAsync(conn, tx, f.Lot, newOn.ToUniversalTime().AddTicks(-1)) >= 0);
                    tx.Rollback();
                }

                // the same (now stale) form posted again: refused, nothing applied twice
                var again = Post(env, admin, f.Receipt, 1200m, newOn, "recounted", "wrong count and date");
                Assert.IsType<RedirectToPageResult>(await again.OnPostAsync(f.Lot));
                Assert.Equal(SeedStockEditRules.StaleMessage, again.TempData["Error"]);
                Assert.Equal((rows + 2, 600m), (await env.LedgerRowsAsync(f.Lot), await env.PhysicalAsync(f.Lot)));

                // no change: nothing written, says so
                var same = Post(env, admin, corrected.Id, 1200m, newOn, "recounted", "check");
                Assert.IsType<RedirectToPageResult>(await same.OnPostAsync(f.Lot));
                Assert.Equal(SeedStockEditRules.NoChangeMessage, same.TempData["Success"]);
                Assert.Equal(rows + 2, await env.LedgerRowsAsync(f.Lot));

                // the repository re-reads the receipt under the lot lock: a superseded receipt, or one id that is not a
                // receipt of THIS lot (here: the reversal row), is refused even when called directly
                var reversalId = await env.ScalarAsync<int>("SELECT Id FROM dbo.SeedStockTransactions WHERE ReferenceType = N'SeedStockEdit' AND ReferenceId = @R", ("@R", f.Receipt));
                foreach (var id in new[] { f.Receipt, reversalId, corrected.Id + 1_000_000 })
                {
                    var direct = await env.Get<SeedStockRepository>().EditReceiptAsync(new SeedStockRepository.ReceiptEdit(f.Lot, id, 1300m, newOn, null, "x"), f.Admin, "seed-edit-test");
                    Assert.Equal((false, SeedStockEditRules.StaleMessage), (direct.Success, direct.Message));
                }
                Assert.Equal((rows + 2, 600m), (await env.LedgerRowsAsync(f.Lot), await env.PhysicalAsync(f.Lot)));
            }
            finally { await CleanupAsync(env, f); }
        }

        // A receipt stored with full datetime2 precision (data-entry scripts write such rows): its reversal must carry EXACTLY
        // the same instant -- if it sorted even a millisecond before the receipt, the running balance would dip by the whole
        // receipt and a later back-dated deduction (Bed Sowing) would be refused although the seeds were there.
        [SkippableFact]
        public async Task Reversal_HasTheOriginalsExactInstant_SoTheHistoryNeverDips()
        {
            var env = await OpenAsync();
            var f = await NewLotAsync(env);
            try
            {
                // the receipt at 10:00:00.0006 UTC ten days ago -- rounds DOWN to 10:00:00.000 as SQL datetime
                var precise = DateTime.UtcNow.Date.AddDays(-10).AddHours(4).AddTicks(6_000);
                await env.ScalarAsync<int>("UPDATE dbo.SeedStockTransactions SET TransactionDate = DATEADD(MICROSECOND, 600, CAST(@D AS datetime2)) WHERE Id = @R; SELECT 0",
                    ("@D", precise.AddTicks(-6_000)), ("@R", f.Receipt));   // set in SQL: a datetime parameter would round it
                var exact = await env.ScalarAsync<DateTime>("SELECT TransactionDate FROM dbo.SeedStockTransactions WHERE Id = @R", ("@R", f.Receipt));
                Assert.Equal(precise, exact);

                // quantity-only correction (same minute): 1000 -> 1100
                var receivedLocal = DateTime.SpecifyKind(exact, DateTimeKind.Utc).ToLocalTime();
                var (ok, msg, changed, _) = await env.Get<SeedStockRepository>().EditReceiptAsync(
                    new SeedStockRepository.ReceiptEdit(f.Lot, f.Receipt, 1100m, SeedStockEditRules.ToMinute(receivedLocal), "first count", "recount"), f.Admin, "seed-edit-test");
                Assert.True(ok, msg);
                Assert.True(changed);

                Assert.Equal(exact, await env.ScalarAsync<DateTime>(
                    "SELECT TransactionDate FROM dbo.SeedStockTransactions WHERE ReferenceType = N'SeedStockEdit' AND ReferenceId = @R", ("@R", f.Receipt)));
                Assert.Equal(500m, await env.PhysicalAsync(f.Lot));
                Assert.Equal(500m, await env.LedgerSumAsync(f.Lot));

                // seeds available to a deduction dated just before the original instant (after the corrected receipt at
                // 04:00:00.000): the lowest balance from then on is today's 500 -- a reversal mis-ordered before the
                // original would make it 100 (1100 - 1000). Before the corrected receipt the lot was empty: 0.
                using var conn = new SqlConnection(env.Cs);
                await conn.OpenAsync();
                using var tx = conn.BeginTransaction();
                Assert.Equal(500m, await SeedStockRepository.AvailableFromAsync(conn, tx, f.Lot, exact.AddTicks(-1)));
                Assert.Equal(0m, await SeedStockRepository.AvailableFromAsync(conn, tx, f.Lot, exact.AddDays(-1)));
                tx.Rollback();
            }
            finally { await CleanupAsync(env, f); }
        }

        // Two administrators correct the SAME receipt at the same moment: the lot lock lets exactly one through; the other
        // is refused as stale and writes nothing.
        [SkippableFact]
        public async Task SimultaneousCorrections_OfOneReceipt_ExactlyOneIsApplied()
        {
            var env = await OpenAsync();
            var f = await NewLotAsync(env);
            try
            {
                var rows = await env.LedgerRowsAsync(f.Lot);
                var results = await Task.WhenAll(Enumerable.Range(0, 6).Select(i => Task.Run(() => env.Get<SeedStockRepository>().EditReceiptAsync(
                    new SeedStockRepository.ReceiptEdit(f.Lot, f.Receipt, 1000m + 10 * (i + 1), f.ReceivedOnLocal, "first count", "race " + i), f.Admin, "seed-edit-test"))));
                Assert.Equal(1, results.Count(r => r.Success && r.Changed));
                Assert.All(results.Where(r => !r.Success), r => Assert.Equal(SeedStockEditRules.StaleMessage, r.Message));
                Assert.Equal(rows + 2, await env.LedgerRowsAsync(f.Lot));
                Assert.Equal(await env.LedgerSumAsync(f.Lot), await env.PhysicalAsync(f.Lot));
                Assert.Single(await env.Get<SeedStockRepository>().GetEditableReceiptsAsync(f.Lot));
            }
            finally { await CleanupAsync(env, f); }
        }

        // Reserved seeds (a pending Seed Issue: InTransitQuantity) stay covered by the corrected receipt.
        [SkippableFact]
        public async Task Correction_CannotUncoverSeedsReservedForAPendingSeedIssue()
        {
            var env = await OpenAsync();
            var f = await NewLotAsync(env);
            try
            {
                using (var conn = new SqlConnection(env.Cs))
                {
                    await conn.OpenAsync();
                    using var tx = conn.BeginTransaction();
                    var (ok, msg, _, _, _, _) = await env.Get<SeedStockRepository>().ReserveInTransitAsync(conn, tx, f.Lot, 300m);
                    Assert.True(ok, msg);
                    tx.Commit();
                }
                var rows = await env.LedgerRowsAsync(f.Lot);
                // 400 physical, 300 reserved: 1000 -> 650 would leave 50 < 300
                var (success, message, changed, _) = await env.Get<SeedStockRepository>().EditReceiptAsync(
                    new SeedStockRepository.ReceiptEdit(f.Lot, f.Receipt, 650m, f.ReceivedOnLocal, "first count", "recount"), f.Admin, "seed-edit-test");
                Assert.False(success);
                Assert.False(changed);
                Assert.Contains("reserved for a pending Seed Issue", message);
                Assert.Equal((rows, 400m), (await env.LedgerRowsAsync(f.Lot), await env.PhysicalAsync(f.Lot)));

                // 1000 -> 950 leaves 350 >= 300: allowed
                (success, message, changed, _) = await env.Get<SeedStockRepository>().EditReceiptAsync(
                    new SeedStockRepository.ReceiptEdit(f.Lot, f.Receipt, 950m, f.ReceivedOnLocal, "first count", "recount"), f.Admin, "seed-edit-test");
                Assert.True(success, message);
                Assert.True(changed);
                Assert.Equal(350m, await env.PhysicalAsync(f.Lot));
                Assert.Equal(350m, await env.LedgerSumAsync(f.Lot));
            }
            finally
            {
                await env.ScalarAsync<int>("UPDATE dbo.SeedStock SET InTransitQuantity = 0 WHERE Id = @L; SELECT 0", ("@L", f.Lot));
                await CleanupAsync(env, f);
            }
        }

        // ---- Reports > Attendance History / Overtime History: the administrator over HTTP ----------------

        [SkippableTheory]
        [InlineData("/Production/Labour/AttendanceHistory", "Attendance History")]
        [InlineData("/Production/Labour/AttendanceHistory?fromDate=2026-10-01&toDate=2026-10-20", "Attendance History")]
        [InlineData("/Production/Labour/OvertimeHistory", "Overtime History")]
        [InlineData("/Production/Labour/OvertimeHistory?fromDate=2026-10-01&toDate=2026-10-20&groupBy=Area", "Overtime History")]
        public async Task Administrator_OpensTheLabourReports_UnderReports(string url, string title)
        {
            ScratchConnectionOrSkip();
            var client = _scratchApp.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
            var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Add(DeleteAuthorizationTests.HeaderAuthHandler.Header, "FULL");
            var response = await client.SendAsync(request);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var html = await response.Content.ReadAsStringAsync();
            Assert.Contains(title, html);
            // the rendered menu: both reports inside the Reports section, not in Labour
            string Section(string name)
            {
                // rendered spans carry the layout's CSS-isolation attribute: <span b-xxxx class="link_name">
                var start = html.IndexOf($"class=\"link_name\">{name}</span>", StringComparison.Ordinal);
                Assert.True(start > 0, $"menu section {name} not rendered");
                var next = System.Text.RegularExpressions.Regex.Match(html[(start + 20)..], "<span[^>]*class=\"link_name\">");
                return next.Success ? html.Substring(start, next.Index + 20) : html[start..];
            }
            var reports = Section("Reports");
            Assert.Contains(">Attendance History</a></li>", reports);
            Assert.Contains(">Overtime History</a></li>", reports);
            var labour = Section("Labour");
            Assert.Contains(">Daily Attendance</a></li>", labour);
            Assert.DoesNotContain("History</a></li>", labour);
        }

        // A Labour recorder (not an administrator) is shown no report link in the rendered menu or on the recording page.
        [SkippableFact]
        public async Task Recorder_SeesNoReportLinks_OnTheRecordingPage()
        {
            ScratchConnectionOrSkip();
            var client = _scratchApp.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
            var request = new HttpRequestMessage(HttpMethod.Get, "/Production/Labour/Attendance");
            request.Headers.Add(DeleteAuthorizationTests.HeaderAuthHandler.Header, "Labour.View,Labour.Enter,Dashboard.View");
            var response = await client.SendAsync(request);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var html = await response.Content.ReadAsStringAsync();
            Assert.Contains("Daily Attendance", html);
            Assert.DoesNotContain("/Production/Labour/AttendanceHistory", html);
            Assert.DoesNotContain("/Production/Labour/OvertimeHistory", html);
        }
    }
}
