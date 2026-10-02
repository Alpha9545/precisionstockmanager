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
using PlantStockManager.Models;
using PlantStockManager.Services;
using CuttingIndex = PlantStockManager.Pages.Production.CuttingStock.IndexModel;
using PotCreate = PlantStockManager.Pages.Production.PotBatch.CreateModel;
using SowingCreate = PlantStockManager.Pages.Production.SeedSowing.CreateFromCuttingModel;

namespace PlantStockManager.Tests
{
    // CORRECTION #3 -- end-to-end: REAL users (dbo.UserRoles -> UserClaimsFactory -> claims, exactly as at login)
    // against the REAL Cutting Stock page, Cutting Tray Sowing and Pot Batch pickers and repositories, on a SCRATCH COPY
    // of the test database (same safety rules as the other E2E classes: PSM_SCRATCH_CONNECTION, database name must start
    // with PlantsIMS2_Scratch_, otherwise refused / skipped).
    //
    // Users (real rows): 110 Vasant Shinde = Mother Plant Supervisor of Green Bless Nursery (Area 127);
    // 9 / 6 / 11 / 2 = Main Office (Area 2); 101 = Ashirawad Cutting (122); 8 = Tilekarwadi (128); 113 / 115 = Shree Swami
    // Samarth Agro (1); 111 = Samarth Ropvatika (123, no pool); 21 = System Administrator (full access).
    // Pools: 166 Green Bless (127); 160 / 163 / 165 Main Office (2); 157 (122); 161 (128); 162 / 164 (1).
    [Collection("ScratchDb")]
    public class CuttingStockAreaVisibilityE2ETests
    {
        private const string ConnectionVariable = "PSM_SCRATCH_CONNECTION";
        private const string RequiredPrefix = "PlantsIMS2_Scratch_";
        private const int GreenBlessUser = 110, GreenBlessArea = 127, GreenBlessPool = 166;
        private const int MainOfficeArea = 2;

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

            public Task ExecAsync(string sql, params (string Name, object? Value)[] args) => ScalarAsync<int>(sql + "; SELECT 1", args);

            // The pools that BELONG to the given Areas, straight from dbo.CuttingStock.AreaId (independent of the code under test).
            // The scratch copy is shared with the other E2E classes, which legitimately create pools, so expectations are read
            // from the database instead of being hard-coded.
            public async Task<int[]> PoolsOfAsync(bool availableOnly, params int[] areas)
            {
                var list = new List<int>();
                using var conn = new SqlConnection(ConnectionString);
                await conn.OpenAsync();
                using var cmd = new SqlCommand($"SELECT Id FROM dbo.CuttingStock WHERE AreaId IN ({string.Join(",", areas.Select(a => a.ToString()))}) AND (@All = 1 OR AvailableQuantity > 0) ORDER BY Id", conn);
                cmd.Parameters.AddWithValue("@All", availableOnly ? 0 : 1);
                using var r = await cmd.ExecuteReaderAsync();
                while (await r.ReadAsync()) list.Add(r.GetInt32(0));
                return list.ToArray();
            }

            public async Task<int[]> AllPoolsAsync(bool availableOnly)
            {
                var list = new List<int>();
                using var conn = new SqlConnection(ConnectionString);
                await conn.OpenAsync();
                using var cmd = new SqlCommand("SELECT Id FROM dbo.CuttingStock WHERE (@All = 1 OR AvailableQuantity > 0) ORDER BY Id", conn);
                cmd.Parameters.AddWithValue("@All", availableOnly ? 0 : 1);
                using var r = await cmd.ExecuteReaderAsync();
                while (await r.ReadAsync()) list.Add(r.GetInt32(0));
                return list.ToArray();
            }

            // as at login: the user's principal built from dbo.UserRoles by the real claims factory
            public async Task<ClaimsPrincipal> LoginAsync(int userId)
            {
                var factory = Get<UserClaimsFactory>();
                var user = await factory.GetActiveUserAsync(userId);
                Assert.NotNull(user);
                return await factory.CreatePrincipalAsync(user!);
            }

            public T Page<T>(ClaimsPrincipal principal) where T : PageModel
            {
                var http = new DefaultHttpContext { User = principal };
                var page = ActivatorUtilities.CreateInstance<T>(Provider);
                page.PageContext = new PageContext(new ActionContext(http, new Microsoft.AspNetCore.Routing.RouteData(), new CompiledPageActionDescriptor()));
                page.TempData = new TempDataDictionary(http, new NullTempData());
                return page;
            }

            // a comparable fingerprint of every stock pool / ledger / sowing / pot batch (what a rejected attempt must not change)
            public Task<string> StateAsync() => ScalarAsync<string>(@"SELECT CONCAT(
                (SELECT CONCAT(COUNT(*), '/', ISNULL(SUM(PhysicalQuantity), 0), '/', ISNULL(SUM(InTransitQuantity), 0)) FROM dbo.CuttingStock), '|',
                (SELECT COUNT(*) FROM dbo.CuttingStockTransactions), '|',
                (SELECT COUNT(*) FROM dbo.SeedSowings), '|',
                (SELECT COUNT(*) FROM dbo.PotProductionBatches))");
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
            // scratch-copy setup: species of the pools used for sowing need growing days
            await env.ExecAsync("UPDATE dbo.PlantSpecies SET ReadyStockDays = 60 WHERE ReadyStockDays IS NULL AND Id IN (SELECT SpeciesId FROM dbo.CuttingStock)");
            return env;
        }

        private static int[] Ids(IEnumerable<PlantStockManager.Models.CuttingStock> pools) => pools.Select(p => p.Id).OrderBy(i => i).ToArray();

        private sealed record Seen(int[] Index, int[] SowingPicker, int[] PotPicker, int[] SendAreas, int[] DeliveryAreas);

        private static async Task<Seen> SeenByAsync(Env env, int userId)
        {
            var p = await env.LoginAsync(userId);
            var index = env.Page<CuttingIndex>(p);
            await index.OnGetAsync(null, showEmpty: true);
            var sow = env.Page<SowingCreate>(p);
            await sow.OnGetAsync(null);
            var pot = env.Page<PotCreate>(p);
            await pot.OnGetAsync(null);
            var give = env.Page<PlantStockManager.Pages.Production.CuttingStock.GiveToMainOfficeModel>(p);
            await give.OnGetAsync(null);
            var deliveries = env.Page<PlantStockManager.Pages.Production.CuttingStock.MyTransactionsModel>(p);
            await deliveries.OnGetAsync(null);
            return new Seen(Ids(index.Stock), Ids(sow.StockPools), Ids(pot.StockPools),
                give.Areas.Select(a => a.Id).OrderBy(i => i).ToArray(), deliveries.Areas.Select(a => a.Id).OrderBy(i => i).ToArray());
        }

        // ---- Green Bless Nursery user (the reported case) ----

        [SkippableFact]
        public async Task GreenBlessUser_SeesOnlyGreenBlessStock_OnEveryCuttingStockScreen_NeverMainOfficeStock()
        {
            var env = await OpenAsync();
            var login = await env.LoginAsync(GreenBlessUser);
            Assert.Equal(new[] { GreenBlessArea.ToString() }, login.FindAll(AreaAccessService.AreaAccessClaimType).Select(c => c.Value).ToArray());
            Assert.False(env.Get<AreaAccessService>().HasFullAreaAccess(login));

            var seen = await SeenByAsync(env, GreenBlessUser);
            Assert.Equal(new[] { GreenBlessPool }, seen.Index);                 // Cutting Stock > Index
            Assert.Equal(new[] { GreenBlessPool }, seen.SowingPicker);          // Cutting Tray Sowing "Cutting Stock" picker
            Assert.Equal(new[] { GreenBlessPool }, seen.PotPicker);             // Pot Batch "Cutting Stock" picker (was: + Main Office pools 160/163/165)
            Assert.Equal(new[] { GreenBlessArea }, seen.SendAreas);
            Assert.Equal(new[] { GreenBlessArea }, seen.DeliveryAreas);
            var mainOfficePools = await env.PoolsOfAsync(false, MainOfficeArea);
            Assert.NotEmpty(mainOfficePools);                                   // there IS Main Office stock that must stay out of sight
            Assert.Empty(seen.Index.Intersect(mainOfficePools));
            Assert.Empty(seen.SowingPicker.Intersect(mainOfficePools));
            Assert.Empty(seen.PotPicker.Intersect(mainOfficePools));
        }

        // ---- Main Office users ----

        [SkippableTheory]
        [InlineData(9)]    // Sowing Operator + Sowing Supervisor
        [InlineData(6)]    // Sowing Supervisor
        [InlineData(11)]   // Main Office Store Keeper (+ Fertilizer Supervisor, Purchase Officer)
        [InlineData(2)]    // Dispatch Executive
        public async Task MainOfficeUsers_SeeAndUseMainOfficeStock_AndNoProductionAreasStock(int userId)
        {
            var env = await OpenAsync();
            var seen = await SeenByAsync(env, userId);
            Assert.Equal(await env.PoolsOfAsync(false, MainOfficeArea), seen.Index);
            Assert.Equal(await env.PoolsOfAsync(true, MainOfficeArea), seen.SowingPicker);
            Assert.Equal(await env.PoolsOfAsync(true, MainOfficeArea), seen.PotPicker);
            Assert.Empty(seen.Index.Except(await env.PoolsOfAsync(false, MainOfficeArea)));   // nothing of any other Area
            Assert.DoesNotContain(GreenBlessPool, seen.Index);
        }

        // ---- other Areas stay isolated ----

        [SkippableTheory]
        [InlineData(101, 122)]   // Ashirawad Cutting
        [InlineData(8, 128)]     // Tilekarwadi
        public async Task AnotherAreasUser_SeesOnlyThatAreasStock(int userId, int area)
        {
            var env = await OpenAsync();
            var seen = await SeenByAsync(env, userId);
            var own = await env.PoolsOfAsync(false, area);
            Assert.NotEmpty(own);
            Assert.Equal(own, seen.Index);
            Assert.Equal(await env.PoolsOfAsync(true, area), seen.SowingPicker);
            Assert.Equal(await env.PoolsOfAsync(true, area), seen.PotPicker);
            Assert.Empty(seen.Index.Intersect(await env.PoolsOfAsync(false, MainOfficeArea, GreenBlessArea)));
        }

        [SkippableFact]
        public async Task AreaWithoutPools_AndAreaWithEmptyPools_ShowNoMainOfficeStock()
        {
            var env = await OpenAsync();
            var noPool = await SeenByAsync(env, 111);      // Samarth Ropvatika: no pool at all
            Assert.Empty(noPool.Index);
            Assert.Empty(noPool.SowingPicker);
            Assert.Empty(noPool.PotPicker);
            var swami = await SeenByAsync(env, 113);       // Shree Swami (Area 1): its own pools only -- never the Main Office pools
            Assert.Equal(await env.PoolsOfAsync(false, 1), swami.Index);
            Assert.Equal(await env.PoolsOfAsync(true, 1), swami.SowingPicker);
            Assert.Equal(await env.PoolsOfAsync(true, 1), swami.PotPicker);
            Assert.Empty(swami.SowingPicker.Intersect(await env.PoolsOfAsync(false, MainOfficeArea)));
        }

        [SkippableFact]
        public async Task SystemAdministrator_SeesEveryAreasStock()
        {
            var env = await OpenAsync();
            var seen = await SeenByAsync(env, 21);
            Assert.Equal(await env.AllPoolsAsync(false), seen.Index);                  // every pool of every Area
            Assert.Equal(await env.AllPoolsAsync(true), seen.SowingPicker);            // every pool that holds available cuttings
        }

        // ---- cross-area access is refused, on the page AND in the repository; nothing changes ----

        [SkippableFact]
        public async Task CrossAreaUse_IsRefused_OnThePages_AndByTheRepositories_NothingChanges()
        {
            var env = await OpenAsync();
            var vasant = await env.LoginAsync(GreenBlessUser);
            var before = await env.StateAsync();
            var access = env.Get<AreaAccessService>();
            Func<int, bool> canUse = areaId => CuttingRules.CanUseAsSource(access.CanAccessArea(vasant, areaId));

            // Tray Sowing page: a Main Office pool (165) and another Area's pool (157)
            foreach (var pool in new[] { 165, 157 })
            {
                var page = env.Page<SowingCreate>(vasant);
                page.CuttingStockId = pool; page.CuttingQuantity = 240; page.CavityType = "24 Cavity"; page.SowingDate = DateTime.Today;
                page.SupervisorId = GreenBlessUser;
                Assert.IsType<PageResult>(await page.OnPostAsync());
                Assert.Contains(page.ModelState.Values.SelectMany(v => v.Errors), e => e.ErrorMessage.Contains("Choose a Cutting Stock you can use"));
            }
            // Pot Batch page
            foreach (var pool in new[] { 163, 157 })
            {
                var page = env.Page<PotCreate>(vasant);
                page.SourceCuttingStockId = pool; page.CuttingAllocated = 50;
                Assert.IsType<PageResult>(await page.OnPostAsync());
                Assert.Contains(page.ModelState.Values.SelectMany(v => v.Errors), e => e.ErrorMessage.Contains("Choose a Cutting Stock you can use"));
            }

            // the repositories refuse on their own (a forged request that skips the page)
            var sowingRepo = env.Get<SeedSowingRepository>();
            foreach (var pool in new[] { 165, 157 })
            {
                var sowing = new SeedSowing
                {
                    SourceCuttingStockId = pool, SeedQuantity = 240, CavityType = "24 Cavity", SowingDate = DateTime.Today, AreaId = GreenBlessArea,
                    SupervisorId = GreenBlessUser, CreatedBy = "e2e", CreatedById = GreenBlessUser
                };
                var (ok, message, _) = await sowingRepo.InsertFromCuttingAsync(sowing, GreenBlessUser, id => access.CanAccessArea(vasant, id), canUse);
                Assert.False(ok);
                Assert.Contains("not authorized", message);
            }
            var potRepo = env.Get<PotBatchRepository>();
            foreach (var pool in new[] { 160, 157 })
            {
                var batch = new PotProductionBatch
                {
                    SourceCuttingStockId = pool, CuttingAllocated = 50, AreaId = GreenBlessArea, PotSize = "4 inch", ProductionStartDate = DateTime.Today,
                    ExpectedReadyDate = DateTime.Today.AddDays(30), SupervisorId = GreenBlessUser, CreatedBy = "e2e"
                };
                var (ok, message, _) = await potRepo.CreateAsync(batch, GreenBlessUser, canUse);
                Assert.False(ok);
                Assert.Equal("You are not authorized to use cuttings from this Area.", message);
            }
            Assert.Equal(before, await env.StateAsync());
        }

        [SkippableFact]
        public async Task MainOfficeUser_CannotUseAProductionAreasStock_EitherWay()
        {
            var env = await OpenAsync();
            var maya = await env.LoginAsync(9);
            var access = env.Get<AreaAccessService>();
            var before = await env.StateAsync();
            var sowing = new SeedSowing
            {
                SourceCuttingStockId = GreenBlessPool, SeedQuantity = 240, CavityType = "24 Cavity", SowingDate = DateTime.Today, AreaId = 2,
                SupervisorId = 6, CreatedBy = "e2e", CreatedById = 9
            };
            var (ok, message, _) = await env.Get<SeedSowingRepository>().InsertFromCuttingAsync(sowing, 9,
                id => access.CanAccessArea(maya, id), id => CuttingRules.CanUseAsSource(access.CanAccessArea(maya, id)));
            Assert.False(ok);
            Assert.Contains("not authorized", message);
            Assert.Equal(before, await env.StateAsync());
        }

        // ---- authorized access still works ----

        [SkippableFact]
        public async Task AuthorizedUsers_CanStillUseGreenBlessStock_ButNotSowItAtMainOfficeItself()
        {
            var env = await OpenAsync();
            var access = env.Get<AreaAccessService>();
            var sowingRepo = env.Get<SeedSowingRepository>();

            // Green Bless supervisor sows from Green Bless's own pool (166): 240 of the 500,
            // destination = Green Bless itself (a real growing Area) -- still works exactly as before.
            var vasant = await env.LoginAsync(GreenBlessUser);
            var poolBefore = await env.ScalarAsync<decimal>("SELECT PhysicalQuantity FROM dbo.CuttingStock WHERE Id = @P", ("@P", GreenBlessPool));
            var own = new SeedSowing
            {
                SourceCuttingStockId = GreenBlessPool, SeedQuantity = 240, CavityType = "24 Cavity", SowingDate = DateTime.Today, AreaId = GreenBlessArea,
                SupervisorId = GreenBlessUser, CreatedBy = "e2e", CreatedById = GreenBlessUser
            };
            var (ok, message, _) = await sowingRepo.InsertFromCuttingAsync(own, GreenBlessUser, id => access.CanAccessArea(vasant, id),
                id => CuttingRules.CanUseAsSource(access.CanAccessArea(vasant, id)));
            Assert.True(ok, message);
            Assert.Equal(poolBefore - 240, await env.ScalarAsync<decimal>("SELECT PhysicalQuantity FROM dbo.CuttingStock WHERE Id = @P", ("@P", GreenBlessPool)));

            // Business rule (this change): Main Office stock (pool 165) MAY be
            // sown from, but the destination is now a REQUIRED real growing
            // Polyhouse, never a bare AreaId -- a sowing that still only sets
            // AreaId (the old shape, defaulting to Main Office itself) is
            // refused atomically (no partial Sown/Wastage deduction left
            // behind). See CuttingSowingDestinationRulesTests for the Main
            // Office / Outlet exclusion rule itself (pure, DB-free).
            var maya = await env.LoginAsync(9);
            var mainBefore = await env.ScalarAsync<decimal>("SELECT PhysicalQuantity FROM dbo.CuttingStock WHERE Id = 165");
            var office = new SeedSowing
            {
                SourceCuttingStockId = 165, SeedQuantity = 240, CavityType = "24 Cavity", SowingDate = DateTime.Today, AreaId = 2,
                SupervisorId = 6, CreatedBy = "e2e", CreatedById = 9
            };
            var (officeOk, officeMessage, _) = await sowingRepo.InsertFromCuttingAsync(office, 9, id => access.CanAccessArea(maya, id),
                id => CuttingRules.CanUseAsSource(access.CanAccessArea(maya, id)));
            Assert.False(officeOk);
            Assert.Contains("Main Area / Polyhouse", officeMessage, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(mainBefore, await env.ScalarAsync<decimal>("SELECT PhysicalQuantity FROM dbo.CuttingStock WHERE Id = 165"));
        }

        [SkippableFact]
        public async Task AnExplicitMainOfficeAssignment_IsAuthorizedByTheOrdinaryAreaRules()
        {
            var env = await OpenAsync();
            // give the Green Bless supervisor a second role row for the Main Office Area (scratch copy only) ...
            await env.ExecAsync("INSERT INTO dbo.UserRoles (UserId, RoleId, AreaId, CreatedDate) VALUES (@U, 11, 2, SYSUTCDATETIME())", ("@U", GreenBlessUser));
            try
            {
                var seen = await SeenByAsync(env, GreenBlessUser);
                Assert.Equal(await env.PoolsOfAsync(false, MainOfficeArea, GreenBlessArea), seen.Index);   // ... and BOTH Areas' stock is now theirs
                Assert.Equal(await env.PoolsOfAsync(true, MainOfficeArea, GreenBlessArea), seen.PotPicker);
            }
            finally
            {
                await env.ExecAsync("DELETE FROM dbo.UserRoles WHERE UserId = @U AND AreaId = 2", ("@U", GreenBlessUser));
            }
            var after = await SeenByAsync(env, GreenBlessUser);
            Assert.Equal(new[] { GreenBlessPool }, after.Index);
        }

        // ---- historical records: made under the old rule, still valid and untouched ----

        [SkippableFact]
        public async Task HistoricalRecords_MadeFromMainOfficePools_AreUntouched_AndStillWork()
        {
            var env = await OpenAsync();
            // pot batches 208 / 288 and cutting sowings 653 / 936 drew Main Office pools for production Areas
            var history = await env.ScalarAsync<string>(@"SELECT CONCAT(
                (SELECT CHECKSUM_AGG(CHECKSUM(*)) FROM dbo.PotProductionBatches WHERE Id IN (208, 286, 287, 288)), '/',
                (SELECT CHECKSUM_AGG(CHECKSUM(*)) FROM dbo.SeedSowings WHERE Id IN (653, 936)))");
            await SeenByAsync(env, GreenBlessUser);
            await SeenByAsync(env, 115);
            foreach (var id in new[] { 208, 286, 287, 288 })
                Assert.NotNull(await env.Get<PotBatchRepository>().GetByIdAsync(id));
            foreach (var id in new[] { 653, 936 })
                Assert.NotNull(await env.Get<SeedSowingRepository>().GetByIdAsync(id));
            Assert.Equal(160, (await env.Get<SeedSowingRepository>().GetByIdAsync(936))!.SourceCuttingStockId);

            // an existing Main Office-sourced sowing is still approvable by its assigned Main Office supervisor
            var maya = await env.LoginAsync(9);
            var access = env.Get<AreaAccessService>();
            var (approved, message, confirmationId) = await env.Get<ReadyConfirmationRepository>().ConfirmAsync(
                936, 44, null, null, null, "e2e", 9, id => CuttingRules.CanUseAsSource(access.CanAccessArea(maya, id)));
            Assert.True(approved, message);
            var (cancelled, cancelMessage) = await env.Get<ReadyConfirmationRepository>().CancelAsync(confirmationId, "e2e", 9);
            Assert.True(cancelled, cancelMessage);

            Assert.Equal(await env.ScalarAsync<string>("SELECT CONCAT((SELECT CHECKSUM_AGG(CHECKSUM(*)) FROM dbo.PotProductionBatches WHERE Id IN (208, 286, 287, 288)), '/', (SELECT COUNT(*) FROM dbo.SeedSowings WHERE Id IN (653, 936) AND Status = N'Sown'))"),
                         history.Split('/')[0] + "/2");
        }
    }
}
