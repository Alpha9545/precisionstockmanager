using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
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
using AddStockPage = PlantStockManager.Pages.Production.SeedStock.AddStockModel;
using AddSeedStockPage = PlantStockManager.Pages.Production.SeedStock.AddSeedStockModel;

namespace PlantStockManager.Tests
{
    // 2026-10-21 security review (Attack 4 -- duplicate submission): Seed Stock > Add Stock and Add Seed Stock credited the
    // lot AGAIN for every repeat of the same form (double click, browser retry, replayed request -- sequential or
    // simultaneous). Both pages now take a one-time SubmissionTokenGuard token, like Empty Pot Add Stock and Overtime.
    // Also (Attack 5): an absurd quantity / a Received On before 2000 is refused by validation instead of reaching SQL
    // Server, whose raw overflow text used to be shown on the page.
    // Against a SCRATCH COPY only (PSM_SCRATCH_CONNECTION, database name PlantsIMS2_Scratch_*; SKIPPED otherwise).
    [Collection("ScratchDb")]
    public class SeedStockReceiptReplayE2ETests
    {
        private const string ConnectionVariable = "PSM_SCRATCH_CONNECTION";
        private const string RequiredPrefix = "PlantsIMS2_Scratch_";

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
                using var scope = Provider.CreateScope();
                var factory = scope.ServiceProvider.GetRequiredService<UserClaimsFactory>();
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
            public Task<int> LotOfSpeciesAsync(int species) => ScalarAsync<int>("SELECT ISNULL(MAX(Id), 0) FROM dbo.SeedStock WHERE SpeciesId = @S", ("@S", species));
        }

        private static async Task<Env> OpenAsync()
        {
            var cs = Environment.GetEnvironmentVariable(ConnectionVariable);
            Skip.If(string.IsNullOrWhiteSpace(cs), $"Set {ConnectionVariable} to a {RequiredPrefix}* database connection string to run these tests.");
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
            services.AddSingleton<SubmissionTokenGuard>();
            services.AddScoped<UserClaimsFactory>();
            services.AddSingleton<IAuthorizationPolicyProvider, PermissionAuthorizationPolicyProvider>();
            services.AddScoped<IAuthorizationHandler, MinimumAuthorizationLevelHandler>();
            services.AddAuthorization();
            foreach (var t in typeof(DatabaseHelper).Assembly.GetTypes().Where(t => t.Namespace == "PlantStockManager.Data" && t.IsClass && t.Name.EndsWith("Repository")))
                services.AddScoped(t);
            return new Env { Cs = cs!, Provider = services.BuildServiceProvider() };
        }

        private sealed record Fixture(int User, int MainOffice, int PlantTypeId, int SpeciesId, int Lot);

        // A new variety with ONE receipt of 1000 seeds at the Main Office (the real receive path). The user is a
        // System Administrator -- these tests are about replay, not about who may add stock.
        private static async Task<Fixture> NewLotAsync(Env env, bool withLot = true)
        {
            var user = await env.ScalarAsync<int>(@"SELECT TOP 1 ur.UserId FROM dbo.UserRoles ur JOIN dbo.Roles r ON r.Id = ur.RoleId
JOIN dbo.IMSUsers u ON u.Id = ur.UserId AND u.IsActive = 1 WHERE r.Name = N'System Administrator' ORDER BY ur.UserId");
            Skip.If(user == 0, "No active System Administrator in this copy.");
            var mainOffice = await env.ScalarAsync<int>("SELECT TOP 1 Id FROM dbo.Area WHERE AreaType = N'MainOffice' AND IsActive = 1 ORDER BY Id");
            Skip.If(mainOffice == 0, "No active Main Office Area in this copy.");

            var name = "ZZTEST SeedReplay " + Guid.NewGuid().ToString("N")[..10];
            var plantType = await env.ScalarAsync<int>("SELECT MIN(Id) FROM dbo.PlantTypes");
            var species = await env.ScalarAsync<int>(@"INSERT INTO dbo.PlantSpecies (PlantTypeId, Name) VALUES (@T, @N);
SELECT CAST(SCOPE_IDENTITY() AS INT)", ("@T", plantType), ("@N", name));
            var lot = 0;
            if (withLot)
            {
                var (ok, msg, id) = await env.Get<SeedStockRepository>().ReceiveAsync(species, mainOffice, "LOT-" + name[^10..], null, "Seeds",
                    1000m, DateTime.Now.AddDays(-1), "first count", user, "seed-replay-test");
                Assert.True(ok, msg);
                lot = id;
            }
            return new Fixture(user, mainOffice, plantType, species, lot);
        }

        private static async Task CleanupAsync(Env env, Fixture f)
        {
            using var conn = new SqlConnection(env.Cs);
            await conn.OpenAsync();
            using var cmd = new SqlCommand(@"DELETE t FROM dbo.SeedStockTransactions t JOIN dbo.SeedStock s ON s.Id = t.SeedStockId WHERE s.SpeciesId = @S;
DELETE FROM dbo.SeedStock WHERE SpeciesId = @S; DELETE FROM dbo.PlantSpecies WHERE Id = @S;", conn);
            cmd.Parameters.AddWithValue("@S", f.SpeciesId);
            await cmd.ExecuteNonQueryAsync();
        }

        // The token the page hands out on GET (exactly what the browser posts back).
        private static async Task<string> AddStockFormTokenAsync(Env env, ClaimsPrincipal user, int lot)
        {
            var page = env.Page<AddStockPage>(user);
            Assert.IsType<PageResult>(await page.OnGetAsync(lot));
            Assert.False(string.IsNullOrWhiteSpace(page.SubmitToken));
            return page.SubmitToken!;
        }

        private static Task<IActionResult> PostAddStockAsync(Env env, ClaimsPrincipal user, int lot, string? token, decimal qty = 50m)
        {
            var page = env.Page<AddStockPage>(user);
            (page.Quantity, page.Remarks, page.SubmitToken) = (qty, "seed-replay-test", token);
            return page.OnPostAsync(lot);
        }

        [SkippableFact]
        public async Task AddStock_SameFormPostedTwice_AddsTheReceiptOnce()
        {
            var env = await OpenAsync();
            var f = await NewLotAsync(env);
            try
            {
                var user = await env.LoginAsync(f.User);
                var rows = await env.LedgerRowsAsync(f.Lot);
                var token = await AddStockFormTokenAsync(env, user, f.Lot);

                var first = await PostAddStockAsync(env, user, f.Lot, token);
                var replay = await PostAddStockAsync(env, user, f.Lot, token);

                Assert.IsType<RedirectToPageResult>(first);
                Assert.Equal("/Production/SeedStock/Details", Assert.IsType<RedirectToPageResult>(replay).PageName);
                Assert.Equal(rows + 1, await env.LedgerRowsAsync(f.Lot));
                Assert.Equal(1050m, await env.PhysicalAsync(f.Lot));
                Assert.Equal(await env.LedgerSumAsync(f.Lot), await env.PhysicalAsync(f.Lot));
            }
            finally { await CleanupAsync(env, f); }
        }

        [SkippableFact]
        public async Task AddStock_SameFormPostedSimultaneously_AddsTheReceiptOnce()
        {
            var env = await OpenAsync();
            var f = await NewLotAsync(env);
            try
            {
                var user = await env.LoginAsync(f.User);
                var rows = await env.LedgerRowsAsync(f.Lot);
                var token = await AddStockFormTokenAsync(env, user, f.Lot);

                await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Task.Run(() => PostAddStockAsync(env, user, f.Lot, token))));

                Assert.Equal(rows + 1, await env.LedgerRowsAsync(f.Lot));
                Assert.Equal(1050m, await env.PhysicalAsync(f.Lot));
            }
            finally { await CleanupAsync(env, f); }
        }

        [SkippableFact]
        public async Task AddStock_WithoutAToken_OrAfterAFailedValidation_IsHandledSafely()
        {
            var env = await OpenAsync();
            var f = await NewLotAsync(env);
            try
            {
                var user = await env.LoginAsync(f.User);
                var rows = await env.LedgerRowsAsync(f.Lot);

                // a forged request without the form's token adds nothing
                Assert.IsType<RedirectToPageResult>(await PostAddStockAsync(env, user, f.Lot, null));
                Assert.Equal(rows, await env.LedgerRowsAsync(f.Lot));

                // an invalid post does not use the token up: the corrected form still goes through, once
                var token = await AddStockFormTokenAsync(env, user, f.Lot);
                Assert.IsType<PageResult>(await PostAddStockAsync(env, user, f.Lot, token, SeedStockEditRules.MaxQuantity + 1));
                Assert.IsType<RedirectToPageResult>(await PostAddStockAsync(env, user, f.Lot, token, 5m));
                Assert.Equal(rows + 1, await env.LedgerRowsAsync(f.Lot));
                Assert.Equal(1005m, await env.PhysicalAsync(f.Lot));
            }
            finally { await CleanupAsync(env, f); }
        }

        private static AddSeedStockPage ReceiveForm(Env env, ClaimsPrincipal user, Fixture f, string? token, decimal qty, DateTime receivedOn)
        {
            var page = env.Page<AddSeedStockPage>(user);
            (page.PlantTypeId, page.SpeciesId, page.AreaId, page.BatchNo, page.Unit) = (f.PlantTypeId, f.SpeciesId, f.MainOffice, "ZZTEST-REPLAY", "Seeds");
            (page.Quantity, page.ReceivedOn, page.Notes, page.SubmitToken) = (qty, receivedOn, "seed-replay-test", token);
            return page;
        }

        [SkippableFact]
        public async Task AddSeedStock_SameFormPostedTwice_ReceivesOnce()
        {
            var env = await OpenAsync();
            var f = await NewLotAsync(env, withLot: false);
            try
            {
                var user = await env.LoginAsync(f.User);
                var get = env.Page<AddSeedStockPage>(user);
                await get.OnGetAsync();
                var token = get.SubmitToken;
                Assert.False(string.IsNullOrWhiteSpace(token));

                var on = DateTime.Now.AddHours(-1);
                Assert.IsType<RedirectToPageResult>(await ReceiveForm(env, user, f, token, 10m, on).OnPostAsync());
                Assert.Equal("/Production/SeedStock/Index",
                    Assert.IsType<RedirectToPageResult>(await ReceiveForm(env, user, f, token, 10m, on).OnPostAsync()).PageName);
                // and a request without the form's token receives nothing
                Assert.IsType<RedirectToPageResult>(await ReceiveForm(env, user, f, null, 10m, on).OnPostAsync());

                var lot = await env.LotOfSpeciesAsync(f.SpeciesId);
                Assert.Equal(1, await env.LedgerRowsAsync(lot));
                Assert.Equal(10m, await env.PhysicalAsync(lot));
            }
            finally { await CleanupAsync(env, f); }
        }

        [SkippableFact]
        public async Task AddSeedStock_AbsurdQuantityOrAncientDate_IsRefusedByValidation_NotBySqlServer()
        {
            var env = await OpenAsync();
            var f = await NewLotAsync(env, withLot: false);
            try
            {
                var user = await env.LoginAsync(f.User);

                var huge = ReceiveForm(env, user, f, SubmissionTokenGuard.NewToken(), SeedStockEditRules.MaxQuantity + 1, DateTime.Now.AddHours(-1));
                Assert.IsType<PageResult>(await huge.OnPostAsync());
                Assert.Contains(SeedStockEditRules.MaxQuantityMessage, huge.ModelState[nameof(AddSeedStockPage.Quantity)]!.Errors.Select(e => e.ErrorMessage));

                var ancient = ReceiveForm(env, user, f, SubmissionTokenGuard.NewToken(), 10m, new DateTime(1, 1, 1));
                Assert.IsType<PageResult>(await ancient.OnPostAsync());
                Assert.Contains(SeedStockEditRules.EarliestDateMessage, ancient.ModelState[nameof(AddSeedStockPage.ReceivedOn)]!.Errors.Select(e => e.ErrorMessage));

                var messages = huge.ModelState.Values.Concat(ancient.ModelState.Values).SelectMany(v => v.Errors).Select(e => e.ErrorMessage);
                Assert.DoesNotContain(messages, m => m.Contains("overflow", StringComparison.OrdinalIgnoreCase));
                Assert.Equal(0, await env.LotOfSpeciesAsync(f.SpeciesId));
            }
            finally { await CleanupAsync(env, f); }
        }
    }

    // The same validation without a database (the Edit page and its repository call share SeedStockEditRules.Validate).
    public class SeedStockReceiptLimitsTests
    {
        [Fact]
        public void Validate_RefusesAQuantityAboveTheColumnLimit_AndAcceptsTheLimit()
        {
            var on = DateTime.Today.AddDays(-1);
            Assert.Contains(SeedStockEditRules.Validate(SeedStockEditRules.MaxQuantity + 1, on, null, "r", DateTime.Today),
                e => e.Field == "Quantity" && e.Message == SeedStockEditRules.MaxQuantityMessage);
            Assert.Empty(SeedStockEditRules.Validate(SeedStockEditRules.MaxQuantity, on, null, "r", DateTime.Today));
        }

        [Fact]
        public void ReceiptPages_CarryAOneTimeSubmitToken()
        {
            Assert.NotNull(typeof(AddStockPage).GetProperty(nameof(AddStockPage.SubmitToken)));
            Assert.NotNull(typeof(AddSeedStockPage).GetProperty(nameof(AddSeedStockPage.SubmitToken)));
            Assert.NotEqual(AddStockPage.TokenScope, AddSeedStockPage.TokenScope);
        }
    }
}
