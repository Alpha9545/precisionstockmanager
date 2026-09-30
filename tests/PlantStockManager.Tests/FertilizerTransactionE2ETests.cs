using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using PlantStockManager.Authorization;
using PlantStockManager.Data;
using PlantStockManager.Services;
using HistoryPage = PlantStockManager.Pages.Fertilizer.TransactionsModel;
using UsagePage = PlantStockManager.Pages.Fertilizer.UsageModel;

namespace PlantStockManager.Tests
{
    // CORRECTION #8 -- end-to-end: the REAL Fertilizer Usage page (issue), Fertilizer Transactions page and repository against a
    // SCRATCH COPY of the test database that already has 2026-09-28_FertilizerUsageReceiverAndEnteredBy.sql applied (same safety
    // rules as the other E2E classes: PSM_SCRATCH_CONNECTION, database name must start with PlantsIMS2_Scratch_).
    //
    // Every test creates its OWN stock batches (unique BatchNumber) and issues dated January 2020, so the shared scratch copy and
    // repeated runs never interfere; checks look at THAT data (found by its batch ids) or at data-driven SQL, never at totals.
    // The real older issues (UsageId <= 154, typed receiver names, no user links) are fingerprinted before and after.
    [Collection("ScratchDb")]
    public class FertilizerTransactionE2ETests
    {
        private const string ConnectionVariable = "PSM_SCRATCH_CONNECTION";
        private const string RequiredPrefix = "PlantsIMS2_Scratch_";
        private const int Mahadev = 11, Prajwal = 21, Kiran = 115, Akshay = 6, Achyut = 101, InactiveSomnath = 5;
        private const int OriginalRows = 154;

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
            public string ConnectionString { get; init; } = "";
            public ServiceProvider Provider { get; init; } = null!;
            public T Get<T>() where T : notnull => Provider.GetRequiredService<T>();

            public async Task<T> ScalarAsync<T>(string sql, params (string Name, object? Value)[] args)
            {
                using var conn = new SqlConnection(ConnectionString);
                await conn.OpenAsync();
                using var cmd = new SqlCommand(sql, conn);
                foreach (var (n, v) in args) cmd.Parameters.AddWithValue(n, v ?? DBNull.Value);
                var r = await cmd.ExecuteScalarAsync();
                return r == null || r == DBNull.Value ? default! : (T)Convert.ChangeType(r, Nullable.GetUnderlyingType(typeof(T)) ?? typeof(T));
            }

            public async Task ExecAsync(string sql, params (string Name, object? Value)[] args)
            {
                using var conn = new SqlConnection(ConnectionString);
                await conn.OpenAsync();
                using var cmd = new SqlCommand(sql, conn);
                foreach (var (n, v) in args) cmd.Parameters.AddWithValue(n, v ?? DBNull.Value);
                await cmd.ExecuteNonQueryAsync();
            }

            public async Task<ClaimsPrincipal> LoginAsync(int userId)
            {
                var factory = Get<UserClaimsFactory>();
                var user = await factory.GetActiveUserAsync(userId);
                Assert.NotNull(user);
                return await factory.CreatePrincipalAsync(user!);
            }

            private PageContext Context(ClaimsPrincipal principal)
            {
                var http = new DefaultHttpContext { User = principal };
                return new PageContext(new ActionContext(http, new Microsoft.AspNetCore.Routing.RouteData(), new CompiledPageActionDescriptor()));
            }

            public HistoryPage HistoryPage(ClaimsPrincipal principal)
            {
                var page = ActivatorUtilities.CreateInstance<HistoryPage>(Provider);
                page.PageContext = Context(principal);
                page.TempData = new TempDataDictionary(page.PageContext.HttpContext, new NullTempData());
                return page;
            }

            public UsagePage UsagePage(ClaimsPrincipal principal)
            {
                var page = ActivatorUtilities.CreateInstance<UsagePage>(Provider);
                page.PageContext = Context(principal);
                page.TempData = new TempDataDictionary(page.PageContext.HttpContext, new NullTempData());
                return page;
            }
        }

        private static async Task<Env> OpenAsync()
        {
            var cs = Environment.GetEnvironmentVariable(ConnectionVariable);
            Skip.If(string.IsNullOrWhiteSpace(cs), $"Set {ConnectionVariable} to a {RequiredPrefix}* database connection string to run the end-to-end tests.");
            string name;
            using (var conn = new SqlConnection(cs))
            {
                await conn.OpenAsync();
                using var cmd = new SqlCommand("SELECT DB_NAME()", conn);
                name = (string)(await cmd.ExecuteScalarAsync())!;
            }
            if (!name.StartsWith(RequiredPrefix, StringComparison.Ordinal))
                throw new InvalidOperationException($"Refusing to run end-to-end tests against '{name}'. Only databases named {RequiredPrefix}* are allowed.");

            var services = new ServiceCollection();
            services.AddSingleton<IConfiguration>(new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:DefaultConnection"] = cs }).Build());
            services.AddSingleton<DatabaseHelper>();
            services.AddSingleton<AreaAccessService>();
            services.AddSingleton<IOptions<SecurityOptions>>(Options.Create(new SecurityOptions { FullAccessRoleNames = new[] { "System Administrator" } }));
            services.AddSingleton<IOptionsMonitor<SeedlingWorkflowOptions>>(new StaticMonitor<SeedlingWorkflowOptions>(new SeedlingWorkflowOptions()));
            services.AddSingleton<SeedlingAreaScope>();
            services.AddScoped<UserClaimsFactory>();
            foreach (var t in typeof(DatabaseHelper).Assembly.GetTypes().Where(t => t.Namespace == "PlantStockManager.Data" && t.IsClass && t.Name.EndsWith("Repository")))
                services.AddScoped(t);
            var env = new Env { ConnectionString = cs!, Provider = services.BuildServiceProvider() };

            // the migration must be on the scratch copy (it is applied by the test setup, never by a test)
            Assert.Equal(2, await env.ScalarAsync<int>("SELECT COUNT(*) FROM sys.columns WHERE object_id = OBJECT_ID('dbo.FertilizerUsage') AND name IN ('ReceivedById','EnteredById')"));
            return env;
        }

        private sealed class Data
        {
            public int F1, F2, SrcA, SrcB, Unit;
            public int S1, S2;                                   // two batches, different fertilizer and source
            public int I1, I2, I3, I4;                           // issued through the repository (receiver + entered-by recorded)
            public int L1, L2, L3;                               // older-style rows: typed name / nothing / an inactive receiver
            public int[] Stocks => new[] { S1, S2 };
        }

        private static async Task<int> NewStockAsync(Env env, int fertilizer, int source, int unit, decimal quantity)
            => await env.ScalarAsync<int>(@"
INSERT INTO dbo.FertilizerStock (FertilizerId, Quantity, UnitId, PurchaseDate, SourceId, BatchNumber, LatestAvailableQuantity, IsUtilized)
OUTPUT INSERTED.StockId
VALUES (@F, @Q, @U, '2020-01-01', @S, @B, @Q, 0)",
                ("@F", fertilizer), ("@Q", quantity), ("@U", unit), ("@S", source), ("@B", "E2E-F8-" + Guid.NewGuid().ToString("N")[..8]));

        private static async Task<int> LegacyRowAsync(Env env, int stock, decimal qty, string date, string typed, int? receiverId, int? enteredId)
            => await env.ScalarAsync<int>(@"
INSERT INTO dbo.FertilizerUsage (StockId, UsedQuantity, IssueDate, ReceivedBy, ReceivedById, EnteredById, Remarks)
OUTPUT INSERTED.UsageId VALUES (@S, @Q, @D, @T, @R, @E, N'legacy e2e')",
                ("@S", stock), ("@Q", qty), ("@D", date), ("@T", typed), ("@R", receiverId), ("@E", enteredId));

        private static async Task<Data> SeedAsync(Env env)
        {
            var repo = env.Get<FertilizerTransactionRepository>();
            var d = new Data();
            var f = new List<int>();
            using (var conn = new SqlConnection(env.ConnectionString))
            {
                await conn.OpenAsync();
                using var cmd = new SqlCommand("SELECT TOP 2 FertilizerId FROM dbo.FertilizerMaster ORDER BY FertilizerId", conn);
                using var r = await cmd.ExecuteReaderAsync();
                while (await r.ReadAsync()) f.Add(r.GetInt32(0));
            }
            (d.F1, d.F2) = (f[0], f[1]);
            var sources = new List<int>();
            using (var conn = new SqlConnection(env.ConnectionString))
            {
                await conn.OpenAsync();
                using var cmd = new SqlCommand("SELECT TOP 2 SourceId FROM dbo.FertilizerSource ORDER BY SourceId", conn);
                using var r = await cmd.ExecuteReaderAsync();
                while (await r.ReadAsync()) sources.Add(r.GetInt32(0));
            }
            (d.SrcA, d.SrcB) = (sources[0], sources[1]);
            d.Unit = await env.ScalarAsync<int>("SELECT TOP 1 UnitId FROM dbo.UnitMaster ORDER BY UnitId");
            d.S1 = await NewStockAsync(env, d.F1, d.SrcA, d.Unit, 100);
            d.S2 = await NewStockAsync(env, d.F2, d.SrcB, d.Unit, 100);

            async Task<int> Issue(int stock, decimal qty, string date, int receiver, int enteredBy)
            {
                var res = await repo.IssueAsync(stock, qty, DateTime.Parse(date), receiver, "e2e issue", enteredBy);
                Assert.True(res.Success, res.Message);
                return res.UsageId;
            }
            d.I1 = await Issue(d.S1, 2, "2020-01-05", Akshay, Mahadev);
            d.I2 = await Issue(d.S1, 3, "2020-01-10", Achyut, Mahadev);
            d.I3 = await Issue(d.S2, 4, "2020-01-15", Akshay, Prajwal);
            d.I4 = await Issue(d.S2, 5, "2020-01-20", Achyut, Prajwal);
            d.L1 = await LegacyRowAsync(env, d.S1, 1, "2020-01-07", "Patel Sir ", null, null);                 // typed name, nothing else (like the older issues)
            d.L2 = await LegacyRowAsync(env, d.S2, 1, "2020-01-17", "   ", null, null);                        // no receiver at all
            d.L3 = await LegacyRowAsync(env, d.S1, 1, "2020-01-25", "Somnath", InactiveSomnath, null);         // a receiver who is no longer active
            return d;
        }

        private static int[] Set(params int[] ids) => ids.OrderBy(i => i).ToArray();
        private static int[] Mine(Data d, IEnumerable<FertilizerTransactionRow> rows) => rows.Where(r => d.Stocks.Contains(r.StockId)).Select(r => r.UsageId).OrderBy(i => i).ToArray();
        private static FertilizerTransactionRow Row(IEnumerable<FertilizerTransactionRow> rows, int id) => rows.Single(r => r.UsageId == id);

        private static FertilizerTransactionFilter Window(Action<FertilizerTransactionFilter>? set = null)
        {
            var f = new FertilizerTransactionFilter { From = new DateTime(2020, 1, 1), To = new DateTime(2020, 1, 31) };
            set?.Invoke(f);
            f.Normalize();
            return f;
        }

        private static async Task<int[]> With(Env env, Data d, Action<FertilizerTransactionFilter> set)
            => Mine(d, await env.Get<FertilizerTransactionRepository>().SearchAsync(Window(set)));

        private static async Task<string> OriginalFingerprintAsync(Env env)
            => await env.ScalarAsync<string>($"SELECT CONCAT(COUNT(*), '/', CHECKSUM_AGG(CHECKSUM(UsageId, StockId, UsedQuantity, IssueDate, ReceivedBy, Remarks, CreatedAt))) FROM dbo.FertilizerUsage WHERE UsageId <= {OriginalRows}");

        // ---- Received By: selectable, saved, shown ----

        [SkippableFact]
        public async Task TheReceiverChoices_AreExactlyTheActiveUsers()
        {
            var env = await OpenAsync();
            var choices = await env.Get<FertilizerTransactionRepository>().GetReceiverChoicesAsync();
            var expected = new List<int>();
            using (var conn = new SqlConnection(env.ConnectionString))
            {
                await conn.OpenAsync();
                using var cmd = new SqlCommand("SELECT Id FROM dbo.IMSUsers WHERE IsActive = 1 ORDER BY Id", conn);
                using var r = await cmd.ExecuteReaderAsync();
                while (await r.ReadAsync()) expected.Add(r.GetInt32(0));
            }
            Assert.Equal(expected, choices.Select(c => int.Parse(c.Value)).OrderBy(i => i).ToList());
            Assert.DoesNotContain(choices, c => c.Value == InactiveSomnath.ToString());                    // an inactive user cannot be picked
            Assert.All(choices, c => Assert.Equal(c.Label, env.ScalarAsync<string>("SELECT LTRIM(RTRIM(Name)) FROM dbo.IMSUsers WHERE Id = @I", ("@I", int.Parse(c.Value))).Result));

            // the page offers the same list
            var page = env.UsagePage(await env.LoginAsync(Mahadev));
            await page.OnGetAsync();
            Assert.Equal(choices.Select(c => c.Value), page.Receivers.Select(c => c.Value));
        }

        [SkippableFact]
        public async Task AnIssueMadeOnTheRealPage_SavesTheReceiverAndTheEnteringUser_AndReducesTheStock()
        {
            var env = await OpenAsync();
            var stock = await NewStockAsync(env, await env.ScalarAsync<int>("SELECT MIN(FertilizerId) FROM dbo.FertilizerMaster"),
                await env.ScalarAsync<int>("SELECT MIN(SourceId) FROM dbo.FertilizerSource"), await env.ScalarAsync<int>("SELECT MIN(UnitId) FROM dbo.UnitMaster"), 10);

            var page = env.UsagePage(await env.LoginAsync(Mahadev));
            page.Usage = new FertilizerUsage { StockId = stock, UsedQuantity = 4, IssueDate = new DateTime(2020, 1, 3), ReceivedById = Akshay, Remarks = " field 3 " };
            var result = await page.OnPostSaveAsync();
            Assert.IsType<RedirectToPageResult>(result);
            Assert.Equal("Fertilizer issued.", page.TempData["Success"]);
            Assert.Null(page.TempData["Error"]);

            var row = (await env.Get<FertilizerTransactionRepository>().SearchAsync(Window())).Single(r => r.StockId == stock);
            Assert.Equal((Akshay, Mahadev, 4m), (row.ReceivedById, row.EnteredById, row.Quantity));
            Assert.Equal(await env.ScalarAsync<string>("SELECT LTRIM(RTRIM(Name)) FROM dbo.IMSUsers WHERE Id = @I", ("@I", Akshay)), row.ReceiverLabel);
            Assert.Equal(await env.ScalarAsync<string>("SELECT LTRIM(RTRIM(Name)) FROM dbo.IMSUsers WHERE Id = @I", ("@I", Mahadev)), row.EnteredByLabel);
            Assert.Equal("field 3", row.Remarks);
            Assert.Equal("Issue", row.TransactionType);
            Assert.Equal(new DateTime(2020, 1, 3), row.IssueDate);
            // the name is also kept in the original ReceivedBy column (what anything that already reads it sees)
            Assert.Equal(row.ReceiverLabel, (await env.ScalarAsync<string>("SELECT ReceivedBy FROM dbo.FertilizerUsage WHERE UsageId = @I", ("@I", row.UsageId))).Trim());

            // the stock rule is the one that was there: balance down by the quantity, "utilized" only when it reaches zero
            Assert.Equal(6m, await env.ScalarAsync<decimal>("SELECT LatestAvailableQuantity FROM dbo.FertilizerStock WHERE StockId = @S", ("@S", stock)));
            Assert.False(await env.ScalarAsync<bool>("SELECT IsUtilized FROM dbo.FertilizerStock WHERE StockId = @S", ("@S", stock)));
            var page2 = env.UsagePage(await env.LoginAsync(Mahadev));
            page2.Usage = new FertilizerUsage { StockId = stock, UsedQuantity = 6, IssueDate = new DateTime(2020, 1, 4), ReceivedById = Achyut };
            await page2.OnPostSaveAsync();
            Assert.Equal(0m, await env.ScalarAsync<decimal>("SELECT LatestAvailableQuantity FROM dbo.FertilizerStock WHERE StockId = @S", ("@S", stock)));
            Assert.True(await env.ScalarAsync<bool>("SELECT IsUtilized FROM dbo.FertilizerStock WHERE StockId = @S", ("@S", stock)));
            Assert.Equal(10m, await env.ScalarAsync<decimal>("SELECT SUM(UsedQuantity) FROM dbo.FertilizerUsage WHERE StockId = @S", ("@S", stock)));   // used + available = purchased
        }

        [SkippableFact]
        public async Task TheEnteringUser_IsAlwaysTheLoggedInUser()
        {
            var env = await OpenAsync();
            var d = await SeedAsync(env);
            var repoRows = await env.Get<FertilizerTransactionRepository>().SearchAsync(Window());
            Assert.Equal((Mahadev, Mahadev, Prajwal, Prajwal), (Row(repoRows, d.I1).EnteredById, Row(repoRows, d.I2).EnteredById, Row(repoRows, d.I3).EnteredById, Row(repoRows, d.I4).EnteredById));
            // the page model has nothing a caller could post to name somebody else as the enterer
            Assert.DoesNotContain(typeof(UsagePage).GetProperties(), p => p.Name.Contains("EnteredBy", StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(typeof(FertilizerUsage).GetProperties(), p => p.Name.Contains("EnteredBy", StringComparison.OrdinalIgnoreCase));
        }

        [SkippableFact]
        public async Task InvalidIssues_AreRejected_AndWriteNothing()
        {
            var env = await OpenAsync();
            var d = await SeedAsync(env);
            var repo = env.Get<FertilizerTransactionRepository>();
            async Task<(int Rows, decimal Balance)> State() => (await env.ScalarAsync<int>("SELECT COUNT(*) FROM dbo.FertilizerUsage WHERE StockId = @S", ("@S", d.S1)),
                await env.ScalarAsync<decimal>("SELECT LatestAvailableQuantity FROM dbo.FertilizerStock WHERE StockId = @S", ("@S", d.S1)));
            var before = await State();

            foreach (var (label, result) in new[]
            {
                ("no receiver", await repo.IssueAsync(d.S1, 1, new DateTime(2020, 1, 2), null, null, Mahadev)),
                ("receiver 0", await repo.IssueAsync(d.S1, 1, new DateTime(2020, 1, 2), 0, null, Mahadev)),
                ("inactive receiver", await repo.IssueAsync(d.S1, 1, new DateTime(2020, 1, 2), InactiveSomnath, null, Mahadev)),
                ("receiver that does not exist", await repo.IssueAsync(d.S1, 1, new DateTime(2020, 1, 2), 987654, null, Mahadev)),
                ("zero quantity", await repo.IssueAsync(d.S1, 0, new DateTime(2020, 1, 2), Akshay, null, Mahadev)),
                ("negative quantity", await repo.IssueAsync(d.S1, -2, new DateTime(2020, 1, 2), Akshay, null, Mahadev)),
                ("more than is available", await repo.IssueAsync(d.S1, 1000, new DateTime(2020, 1, 2), Akshay, null, Mahadev)),
                ("no date", await repo.IssueAsync(d.S1, 1, default, Akshay, null, Mahadev)),
                ("stock that does not exist", await repo.IssueAsync(987654, 1, new DateTime(2020, 1, 2), Akshay, null, Mahadev)),
            })
            {
                Assert.False(result.Success, label);
                Assert.False(string.IsNullOrWhiteSpace(result.Message), label);
            }
            Assert.Equal(before, await State());                                              // nothing written, nothing deducted

            // and through the page: the person sees the reason, nothing is saved
            var page = env.UsagePage(await env.LoginAsync(Mahadev));
            page.Usage = new FertilizerUsage { StockId = d.S1, UsedQuantity = 1, IssueDate = new DateTime(2020, 1, 2), ReceivedById = null };
            await page.OnPostSaveAsync();
            Assert.Equal("Choose who received the fertilizer.", page.TempData["Error"]);
            Assert.Equal(before, await State());
        }

        // ---- duplicate / concurrent submission ----

        [SkippableFact]
        public async Task TheSameFormSubmittedTwice_IsSavedOnce()
        {
            var env = await OpenAsync();
            var d = await SeedAsync(env);
            var repo = env.Get<FertilizerTransactionRepository>();
            var before = await env.ScalarAsync<decimal>("SELECT LatestAvailableQuantity FROM dbo.FertilizerStock WHERE StockId = @S", ("@S", d.S2));

            var first = await repo.IssueAsync(d.S2, 7, new DateTime(2020, 1, 28), Akshay, "twice", Mahadev);
            var second = await repo.IssueAsync(d.S2, 7, new DateTime(2020, 1, 28), Akshay, "twice", Mahadev);
            Assert.True(first.Success); Assert.False(first.Duplicate);
            Assert.True(second.Success); Assert.True(second.Duplicate);
            Assert.Equal(first.UsageId, second.UsageId);
            Assert.Equal(1, await env.ScalarAsync<int>("SELECT COUNT(*) FROM dbo.FertilizerUsage WHERE StockId = @S AND UsedQuantity = 7 AND IssueDate = '2020-01-28'", ("@S", d.S2)));
            Assert.Equal(before - 7, await env.ScalarAsync<decimal>("SELECT LatestAvailableQuantity FROM dbo.FertilizerStock WHERE StockId = @S", ("@S", d.S2)));   // deducted once

            // a DIFFERENT issue right after is not a duplicate (another receiver / quantity)
            Assert.False((await repo.IssueAsync(d.S2, 7, new DateTime(2020, 1, 28), Achyut, "other person", Mahadev)).Duplicate);
            Assert.False((await repo.IssueAsync(d.S2, 8, new DateTime(2020, 1, 28), Akshay, "other quantity", Mahadev)).Duplicate);
        }

        [SkippableFact]
        public async Task EightSimultaneousIdenticalSubmissions_CreateExactlyOneIssue()
        {
            var env = await OpenAsync();
            var d = await SeedAsync(env);
            var before = await env.ScalarAsync<decimal>("SELECT LatestAvailableQuantity FROM dbo.FertilizerStock WHERE StockId = @S", ("@S", d.S1));
            var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ =>
                Task.Run(() => env.Get<FertilizerTransactionRepository>().IssueAsync(d.S1, 9, new DateTime(2020, 1, 29), Achyut, "race", Mahadev))));
            Assert.All(results, r => Assert.True(r.Success, r.Message));
            Assert.Equal(1, results.Count(r => !r.Duplicate));
            Assert.Equal(1, await env.ScalarAsync<int>("SELECT COUNT(*) FROM dbo.FertilizerUsage WHERE StockId = @S AND UsedQuantity = 9 AND IssueDate = '2020-01-29'", ("@S", d.S1)));
            Assert.Equal(before - 9, await env.ScalarAsync<decimal>("SELECT LatestAvailableQuantity FROM dbo.FertilizerStock WHERE StockId = @S", ("@S", d.S1)));
        }

        [SkippableFact]
        public async Task SimultaneousDifferentIssues_CanNeverIssueMoreThanTheStockHolds()
        {
            var env = await OpenAsync();
            var stock = await NewStockAsync(env, await env.ScalarAsync<int>("SELECT MIN(FertilizerId) FROM dbo.FertilizerMaster"),
                await env.ScalarAsync<int>("SELECT MIN(SourceId) FROM dbo.FertilizerSource"), await env.ScalarAsync<int>("SELECT MIN(UnitId) FROM dbo.UnitMaster"), 10);
            // 12 people each ask for 1..12 -- together far more than the 10 available
            var results = await Task.WhenAll(Enumerable.Range(1, 12).Select(i =>
                Task.Run(() => env.Get<FertilizerTransactionRepository>().IssueAsync(stock, i, new DateTime(2020, 1, 2), i % 2 == 0 ? Akshay : Achyut, "race " + i, Mahadev))));
            var used = await env.ScalarAsync<decimal>("SELECT ISNULL(SUM(UsedQuantity), 0) FROM dbo.FertilizerUsage WHERE StockId = @S", ("@S", stock));
            var balance = await env.ScalarAsync<decimal>("SELECT LatestAvailableQuantity FROM dbo.FertilizerStock WHERE StockId = @S", ("@S", stock));
            Assert.True(used <= 10m, $"issued {used} of 10");
            Assert.True(balance >= 0m);
            Assert.Equal(10m, used + balance);                                                 // purchased = used + available, always
            Assert.Equal(results.Count(r => r.Success && !r.Duplicate), await env.ScalarAsync<int>("SELECT COUNT(*) FROM dbo.FertilizerUsage WHERE StockId = @S", ("@S", stock)));
            Assert.Contains(results, r => !r.Success);                                         // and the surplus requests were refused with a reason
        }

        // ---- the history and each filter ----

        [SkippableFact]
        public async Task EveryColumn_ShowsTheDatabaseRelationships()
        {
            var env = await OpenAsync();
            var d = await SeedAsync(env);
            var rows = await env.Get<FertilizerTransactionRepository>().SearchAsync(Window());
            async Task<string> Str(string sql, params (string, object?)[] a) => (await env.ScalarAsync<string>(sql, a))?.Trim() ?? "";

            foreach (var (id, stock, fertilizer, source, receiver, enteredBy, qty, date) in new[]
            {
                (d.I1, d.S1, d.F1, d.SrcA, Akshay, Mahadev, 2m, new DateTime(2020, 1, 5)), (d.I4, d.S2, d.F2, d.SrcB, Achyut, Prajwal, 5m, new DateTime(2020, 1, 20))
            })
            {
                var r = Row(rows, id);
                Assert.Equal(await Str("SELECT FertilizerName FROM dbo.FertilizerMaster WHERE FertilizerId = @I", ("@I", fertilizer)), r.FertilizerName);
                Assert.Equal(await Str("SELECT ft.TypeName FROM dbo.FertilizerMaster fm JOIN dbo.FertilizerType ft ON ft.FertilizerTypeId = fm.FertilizerTypeId WHERE fm.FertilizerId = @I", ("@I", fertilizer)), r.FertilizerType ?? "");
                Assert.Equal(await Str("SELECT BatchNumber FROM dbo.FertilizerStock WHERE StockId = @I", ("@I", stock)), r.BatchNumber);
                Assert.Equal(await Str("SELECT um.UnitName FROM dbo.FertilizerStock fs JOIN dbo.UnitMaster um ON um.UnitId = fs.UnitId WHERE fs.StockId = @I", ("@I", stock)), r.UnitName);
                Assert.Equal(await Str("SELECT SourceName FROM dbo.FertilizerSource WHERE SourceId = @I", ("@I", source)), r.SourceName);     // Source = the supplier of the batch
                Assert.Equal(await Str("SELECT Name FROM dbo.IMSUsers WHERE Id = @I", ("@I", receiver)), r.ReceiverLabel);
                Assert.Equal(await Str("SELECT Name FROM dbo.IMSUsers WHERE Id = @I", ("@I", enteredBy)), r.EnteredByLabel);
                Assert.Equal((qty, date, "e2e issue", "Issue"), (r.Quantity, r.IssueDate, r.Remarks, r.TransactionType));
            }
            Assert.Equal("Patel Sir", Row(rows, d.L1).ReceiverLabel);              // an older typed name is shown as typed
            Assert.True(Row(rows, d.L1).ReceiverIsTypedName);
            Assert.Equal("Not recorded", Row(rows, d.L1).EnteredByLabel);          // nobody was recorded as the enterer
            Assert.Equal("Not recorded", Row(rows, d.L2).ReceiverLabel);           // nothing to show: never guessed
            Assert.Equal("Somnath (inactive)", Row(rows, d.L3).ReceiverLabel);     // an inactive receiver is kept and marked, not dropped
            Assert.Equal(d.Unit, await env.ScalarAsync<int>("SELECT UnitId FROM dbo.FertilizerStock WHERE StockId = @S", ("@S", d.S1)));
        }

        [SkippableFact]
        public async Task EveryFilter_OnItsOwn_ReturnsExactlyTheMatchingRecords()
        {
            var env = await OpenAsync();
            var d = await SeedAsync(env);
            var all = Set(d.I1, d.I2, d.I3, d.I4, d.L1, d.L2, d.L3);
            Assert.Equal(all, await With(env, d, _ => { }));                                                                                        // no filter: everything

            // date range (from / to / both, inclusive)
            Assert.Equal(Set(d.I2, d.I3, d.L2), await With(env, d, f => { f.From = new DateTime(2020, 1, 10); f.To = new DateTime(2020, 1, 17); }));
            Assert.Equal(Set(d.I4, d.L3), await With(env, d, f => f.From = new DateTime(2020, 1, 20)));
            Assert.Equal(Set(d.I1, d.L1), await With(env, d, f => f.To = new DateTime(2020, 1, 7)));
            Assert.Empty(await With(env, d, f => { f.From = new DateTime(2019, 1, 1); f.To = new DateTime(2019, 12, 31); }));
            // fertilizer and source
            Assert.Equal(Set(d.I1, d.I2, d.L1, d.L3), await With(env, d, f => f.FertilizerId = d.F1));
            Assert.Equal(Set(d.I3, d.I4, d.L2), await With(env, d, f => f.FertilizerId = d.F2));
            Assert.Equal(Set(d.I1, d.I2, d.L1, d.L3), await With(env, d, f => f.SourceId = d.SrcA));
            Assert.Equal(Set(d.I3, d.I4, d.L2), await With(env, d, f => f.SourceId = d.SrcB));
            // received by: a selected user, an older typed name (any case / spacing), an inactive user, and "Not recorded"
            Assert.Equal(Set(d.I1, d.I3), await With(env, d, f => f.Receiver = "u:" + Akshay));
            Assert.Equal(Set(d.I2, d.I4), await With(env, d, f => f.Receiver = "u:" + Achyut));
            Assert.Equal(Set(d.L3), await With(env, d, f => f.Receiver = "u:" + InactiveSomnath));
            Assert.Equal(Set(d.L1), await With(env, d, f => f.Receiver = "t:patel sir"));
            Assert.Equal(Set(d.L2), await With(env, d, f => f.Receiver = "none"));
            // entered by
            Assert.Equal(Set(d.I1, d.I2), await With(env, d, f => f.EnteredBy = Mahadev.ToString()));
            Assert.Equal(Set(d.I3, d.I4), await With(env, d, f => f.EnteredBy = Prajwal.ToString()));
            Assert.Equal(Set(d.L1, d.L2, d.L3), await With(env, d, f => f.EnteredBy = "none"));
            Assert.Empty(await With(env, d, f => f.EnteredBy = Kiran.ToString()));                                                              // valid user, entered nothing
        }

        [SkippableFact]
        public async Task Filters_CombineWithAnd_AndOnlyEverNarrow()
        {
            var env = await OpenAsync();
            var d = await SeedAsync(env);
            Assert.Equal(Set(d.I1), await With(env, d, f => { f.FertilizerId = d.F1; f.Receiver = "u:" + Akshay; }));
            Assert.Equal(Set(d.I1), await With(env, d, f => { f.FertilizerId = d.F1; f.Receiver = "u:" + Akshay; f.EnteredBy = Mahadev.ToString(); f.SourceId = d.SrcA; f.To = new DateTime(2020, 1, 8); }));
            Assert.Empty(await With(env, d, f => { f.FertilizerId = d.F2; f.Receiver = "u:" + Akshay; f.EnteredBy = Mahadev.ToString(); }));  // Akshay + F2 exists (I3) but not entered by Mahadev
            Assert.Empty(await With(env, d, f => { f.FertilizerId = d.F1; f.SourceId = d.SrcB; }));                                             // a fertilizer bought from the other source
            Assert.Equal(Set(d.L3), await With(env, d, f => { f.Receiver = "u:" + InactiveSomnath; f.EnteredBy = "none"; f.From = new DateTime(2020, 1, 25); }));
            Assert.Equal(Set(d.L2), await With(env, d, f => { f.Receiver = "none"; f.FertilizerId = d.F2; }));
            Assert.Empty(await With(env, d, f => { f.Receiver = "none"; f.FertilizerId = d.F1; }));

            // adding a filter never adds a row
            var repo = env.Get<FertilizerTransactionRepository>();
            var wide = Mine(d, await repo.SearchAsync(Window(f => f.FertilizerId = d.F1)));
            var narrow = Mine(d, await repo.SearchAsync(Window(f => { f.FertilizerId = d.F1; f.EnteredBy = Mahadev.ToString(); })));
            Assert.All(narrow, id => Assert.Contains(id, wide));
            Assert.True(narrow.Length <= wide.Length);
        }

        [SkippableFact]
        public async Task TheReceiverOptions_AreAllNamesThatOccur_PlusNotRecorded()
        {
            var env = await OpenAsync();
            var d = await SeedAsync(env);
            var options = await env.Get<FertilizerTransactionRepository>().GetFilterOptionsAsync();
            var values = options.Receivers.Select(o => o.Value).ToList();
            Assert.Equal("none", values[0]);                                                     // "Not recorded" first, always there
            Assert.Equal("Not recorded", options.Receivers[0].Label);
            Assert.Contains("u:" + Akshay, values);
            Assert.Contains("u:" + Achyut, values);
            Assert.Contains("u:" + InactiveSomnath, values);                                     // inactive receivers are not dropped...
            Assert.Contains(options.Receivers, o => o.Value == "u:" + InactiveSomnath && o.Label.Contains("(inactive)"));   // ...and are marked
            Assert.Contains(values, v => string.Equals(v, "t:Patel Sir", StringComparison.OrdinalIgnoreCase));            // older typed names
            Assert.Equal(values.Count, values.Distinct(StringComparer.OrdinalIgnoreCase).Count());                         // nothing twice
            // every option leads to at least one row (nothing that can only return an empty page), except "Not recorded"
            var repo = env.Get<FertilizerTransactionRepository>();
            foreach (var o in options.Receivers.Where(o => o.Value != "none"))
            {
                var f = new FertilizerTransactionFilter { Receiver = o.Value };
                f.Normalize();
                Assert.NotEmpty(await repo.SearchAsync(f));
            }
            Assert.Contains(options.EnteredBy, o => o.Value == Mahadev.ToString());
            Assert.Contains(options.EnteredBy, o => o.Value == "none");
            Assert.Contains(options.Fertilizers, o => o.Value == d.F1.ToString());
            Assert.Contains(options.Sources, o => o.Value == d.SrcB.ToString());
        }

        // ---- the real page: retained filters, notices, count, access ----

        private static async Task<HistoryPage> LoadAsync(Env env, ClaimsPrincipal user, Action<HistoryPage>? set = null)
        {
            var page = env.HistoryPage(user);
            set?.Invoke(page);
            await page.OnGetAsync();
            return page;
        }

        [SkippableFact]
        public async Task ThePage_KeepsTheSelectedFilters_AndCountsTheRows()
        {
            var env = await OpenAsync();
            var d = await SeedAsync(env);
            var user = await env.LoginAsync(Mahadev);
            var page = await LoadAsync(env, user, p =>
            {
                p.From = new DateTime(2020, 1, 1); p.To = new DateTime(2020, 1, 31); p.FertilizerId = d.F1; p.SourceId = d.SrcA;
                p.Receiver = "u:" + Akshay; p.EnteredBy = Mahadev.ToString();
            });
            Assert.Empty(page.Notices);
            Assert.True(page.FilterActive);
            Assert.Equal((new DateTime(2020, 1, 1), new DateTime(2020, 1, 31), d.F1, d.SrcA, "u:" + Akshay, Mahadev.ToString()),
                (page.From, page.To, page.FertilizerId, page.SourceId, page.Receiver, page.EnteredBy));
            Assert.Equal(Set(d.I1), Mine(d, page.Rows));
            Assert.Contains(page.Rows, r => r.UsageId == d.I1);
            Assert.All(page.Rows, r => Assert.Equal((d.F1, d.SrcA, Akshay, Mahadev), (r.FertilizerId, r.SourceId, r.ReceivedById, r.EnteredById)));   // every row shown satisfies every filter

            var none = await LoadAsync(env, user, p => { p.From = new DateTime(2019, 1, 1); p.To = new DateTime(2019, 1, 2); });
            Assert.Empty(none.Rows);                                                              // the page then shows "No fertilizer transactions match"
            Assert.True(none.FilterActive);

            var clear = await LoadAsync(env, user);                                                // "Clear filters" = the page with no query string
            Assert.False(clear.FilterActive);
            Assert.Null(clear.Receiver);
            Assert.True(clear.Rows.Count >= OriginalRows);                                         // all the older issues are there
        }

        [SkippableFact]
        public async Task InvalidFilterValues_AreIgnoredWithANotice_AndNeverBreakOrWidenAnything()
        {
            var env = await OpenAsync();
            var d = await SeedAsync(env);
            var user = await env.LoginAsync(Mahadev);
            var baseline = (await LoadAsync(env, user)).Rows.Select(r => r.UsageId).OrderBy(i => i).ToArray();

            foreach (var set in new Action<HistoryPage>[]
            {
                p => p.Receiver = "garbage",
                p => p.Receiver = "u:abc",
                p => p.Receiver = "u:987654",                              // a well-formed id nobody has
                p => p.Receiver = "t:Nobody Ever",                         // a name that never received anything
                p => p.Receiver = "'; DROP TABLE dbo.FertilizerUsage;--",
                p => p.Receiver = "t:' OR 1=1 --",
                p => p.EnteredBy = "abc",
                p => p.EnteredBy = "987654",
                p => p.FertilizerId = -1,
                p => p.FertilizerId = 987654,
                p => p.SourceId = 987654,
            })
            {
                var page = await LoadAsync(env, user, set);
                Assert.Equal(baseline, page.Rows.Select(r => r.UsageId).OrderBy(i => i).ToArray());   // ignored: the same rows as with no filter
                if (page.FertilizerId != null || page.SourceId != null || page.Receiver != null || page.EnteredBy != null)
                    Assert.Fail("an unusable value was kept as a filter");
            }
            // the table survived every one of them
            Assert.True(await env.ScalarAsync<int>("SELECT COUNT(*) FROM dbo.FertilizerUsage") >= OriginalRows);

            // some notices are worded (the page shows them); a negative id is simply "no filter"
            Assert.NotEmpty((await LoadAsync(env, user, p => p.Receiver = "garbage")).Notices);
            Assert.NotEmpty((await LoadAsync(env, user, p => p.EnteredBy = "abc")).Notices);
            Assert.NotEmpty((await LoadAsync(env, user, p => p.SourceId = 987654)).Notices);
            var swapped = await LoadAsync(env, user, p => { p.From = new DateTime(2020, 1, 31); p.To = new DateTime(2020, 1, 1); });
            Assert.NotEmpty(swapped.Notices);
            Assert.Equal((new DateTime(2020, 1, 1), new DateTime(2020, 1, 31)), (swapped.From, swapped.To));
        }

        [SkippableFact]
        public async Task Access_IsByPermission_TheHistoryHasNoAreaToBypass_AndFullAccessKeepsEverything()
        {
            var env = await OpenAsync();
            var d = await SeedAsync(env);
            var supervisor = await env.LoginAsync(Mahadev);     // Fertilizer Supervisor, assigned to Area 2
            var admin = await env.LoginAsync(Prajwal);          // System Administrator (full access)
            var other = await env.LoginAsync(Kiran);            // Mother Plant Supervisor: no fertilizer permission at all

            // the permission claims decide who may open the pages (the server-side page rule is Fertilizer.View / Fertilizer.Enter)
            bool Has(ClaimsPrincipal p, string code) => p.HasClaim(MinimumAuthorizationLevelHandler.PermissionClaimType, code);
            Assert.True(Has(supervisor, "Fertilizer.View") && Has(supervisor, "Fertilizer.Enter"));
            Assert.False(Has(other, "Fertilizer.View") || Has(other, "Fertilizer.Enter"));
            Assert.True(admin.HasClaim(ClaimsPrincipalSecurityExtensions.FullAccessClaimType, "true") || Has(admin, "Fertilizer.View"));

            // there is no Area in the fertilizer data and no Area filter on the page: an Area-assigned user and a full-access user see the same records
            var forSupervisor = Mine(d, (await LoadAsync(env, supervisor)).Rows);
            var forAdmin = Mine(d, (await LoadAsync(env, admin)).Rows);
            Assert.Equal(forAdmin, forSupervisor);
            Assert.Equal(Set(d.I1, d.I2, d.I3, d.I4, d.L1, d.L2, d.L3), forAdmin);
            Assert.DoesNotContain(typeof(HistoryPage).GetProperties(), p => p.Name.Contains("Area", StringComparison.OrdinalIgnoreCase));

            // the filters only narrow: with any combination, the result is a subset of the unfiltered result for the same user
            var everything = (await LoadAsync(env, supervisor)).Rows.Select(r => r.UsageId).ToHashSet();
            foreach (var set in new Action<HistoryPage>[] { p => p.Receiver = "u:" + Akshay, p => p.EnteredBy = "none", p => p.FertilizerId = d.F1, p => p.SourceId = d.SrcB, p => p.Receiver = "none" })
                Assert.All((await LoadAsync(env, supervisor, set)).Rows, r => Assert.Contains(r.UsageId, everything));
        }

        // ---- existing data is untouched ----

        [SkippableFact]
        public async Task ExistingIssues_AreUnchanged_ShowTheirTypedName_AndAreNotGuessedIntoUsers()
        {
            var env = await OpenAsync();
            var before = await OriginalFingerprintAsync(env);
            var permissionsBefore = await env.ScalarAsync<string>("SELECT CONCAT(COUNT(*), '/', CHECKSUM_AGG(CHECKSUM(RoleId, PermissionId))) FROM dbo.RolePermissions");
            var d = await SeedAsync(env);                                                // all the activity of this class
            Assert.Equal(before, await OriginalFingerprintAsync(env));                  // the original 154 issues: not one column changed
            Assert.Equal(permissionsBefore, await env.ScalarAsync<string>("SELECT CONCAT(COUNT(*), '/', CHECKSUM_AGG(CHECKSUM(RoleId, PermissionId))) FROM dbo.RolePermissions"));

            Assert.Equal(0, await env.ScalarAsync<int>($"SELECT COUNT(*) FROM dbo.FertilizerUsage WHERE UsageId <= {OriginalRows} AND (ReceivedById IS NOT NULL OR EnteredById IS NOT NULL)"));   // nothing was linked by guessing
            var rows = (await env.Get<FertilizerTransactionRepository>().SearchAsync(new FertilizerTransactionFilter())).Where(r => r.UsageId <= OriginalRows).ToList();
            Assert.Equal(OriginalRows, rows.Count);
            Assert.All(rows, r =>
            {
                Assert.Equal("Not recorded", r.EnteredByLabel);                          // never stored for these
                Assert.Null(r.ReceivedById);
                Assert.Equal(r.ReceivedByText!.Trim(), r.ReceiverLabel);                 // exactly the name that was typed
                Assert.True(r.ReceiverIsTypedName);
            });
            // the "typed name" filter finds exactly the older rows with that text, whatever its case
            var f = new FertilizerTransactionFilter { Receiver = "t:akshay shinde" };
            f.Normalize();
            var found = (await env.Get<FertilizerTransactionRepository>().SearchAsync(f)).Where(r => r.UsageId <= OriginalRows).Select(r => r.UsageId).OrderBy(i => i).ToArray();
            var expected = new List<int>();
            using (var conn = new SqlConnection(env.ConnectionString))
            {
                await conn.OpenAsync();
                using var cmd = new SqlCommand($"SELECT UsageId FROM dbo.FertilizerUsage WHERE UsageId <= {OriginalRows} AND LTRIM(RTRIM(ReceivedBy)) = N'akshay shinde' ORDER BY UsageId", conn);
                using var r = await cmd.ExecuteReaderAsync();
                while (await r.ReadAsync()) expected.Add(r.GetInt32(0));
            }
            Assert.NotEmpty(expected);
            Assert.Equal(expected, found.ToList());
            // "Not recorded" never picks up an older row that HAS a typed name
            var none = new FertilizerTransactionFilter { Receiver = "none" };
            none.Normalize();
            Assert.DoesNotContain((await env.Get<FertilizerTransactionRepository>().SearchAsync(none)), r => r.UsageId <= OriginalRows);
        }

        [SkippableFact]
        public async Task StockBalances_OfTheOlderBatches_AreNotTouched_ByIssuingFromNewOnes()
        {
            var env = await OpenAsync();
            var before = await env.ScalarAsync<string>("SELECT CONCAT(COUNT(*), '/', CHECKSUM_AGG(CHECKSUM(StockId, Quantity, LatestAvailableQuantity, IsUtilized))) FROM dbo.FertilizerStock WHERE StockId <= 67");
            var d = await SeedAsync(env);
            Assert.Equal(before, await env.ScalarAsync<string>("SELECT CONCAT(COUNT(*), '/', CHECKSUM_AGG(CHECKSUM(StockId, Quantity, LatestAvailableQuantity, IsUtilized))) FROM dbo.FertilizerStock WHERE StockId <= 67"));
            // for the batches this test made: purchased = used + available (the issues made through the repository)
            Assert.Equal(100m - 2 - 3, await env.ScalarAsync<decimal>("SELECT LatestAvailableQuantity FROM dbo.FertilizerStock WHERE StockId = @S", ("@S", d.S1)));
            Assert.Equal(100m - 4 - 5, await env.ScalarAsync<decimal>("SELECT LatestAvailableQuantity FROM dbo.FertilizerStock WHERE StockId = @S", ("@S", d.S2)));
        }
    }
}
