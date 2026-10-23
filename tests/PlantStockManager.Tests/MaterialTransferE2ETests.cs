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
using PlantStockManager.Models;
using PlantStockManager.Services;
using HistoryPage = PlantStockManager.Pages.Production.MaterialStock.HistoryModel;
using TransfersPage = PlantStockManager.Pages.Production.MaterialStock.TransfersModel;

namespace PlantStockManager.Tests
{
    // 2026-10-23: Cocopeat / Pitmos transfers between Polyhouses (2026-10-25_MaterialStockTransfers.sql), end-to-end against a
    // SCRATCH COPY only (PSM_SCRATCH_CONNECTION, database name PlantsIMS2_Scratch_*; SKIPPED otherwise, and per test when the
    // migration is / is not applied). REAL users -- principals built from dbo.UserRoles by the real claims factory -- drive the
    // real page models and repositories. Each test resets the balances it uses (scratch-only clean-up switch) and checks the
    // ledger invariant: every balance = the sum of its ledger, never negative.
    [Collection("ScratchDb")]
    public class MaterialTransferE2ETests
    {
        private const string ConnectionVariable = "PSM_SCRATCH_CONNECTION";
        private const string RequiredPrefix = "PlantsIMS2_Scratch_";
        private const int MainArea = 2;

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
            public int Cocopeat, Pitmos;
            public int MainA, MainB, MainC;            // three Polyhouses of the Main Office Area
            public int MpArea, MpPolyhouse;            // a Mother Plant Area and one of its Polyhouses
            public int OutletArea, OutletPolyhouse;
            public int Supervisor, MpSupervisor, Admin, NoPermissionUser;
            public T Get<T>() where T : notnull => Provider.GetRequiredService<T>();
            public MaterialTransferRepository Repo => Get<MaterialTransferRepository>();

            public async Task<T> ScalarAsync<T>(string sql, params (string Name, object? Value)[] args)
            {
                using var conn = new SqlConnection(Cs);
                await conn.OpenAsync();
                using var cmd = new SqlCommand(sql, conn);
                foreach (var (n, v) in args) cmd.Parameters.AddWithValue(n, v ?? DBNull.Value);
                var r = await cmd.ExecuteScalarAsync();
                return r == null || r == DBNull.Value ? default! : (T)Convert.ChangeType(r, Nullable.GetUnderlyingType(typeof(T)) ?? typeof(T));
            }

            public Task ExecAsync(string sql, params (string Name, object? Value)[] args) => ScalarAsync<int>(sql + "; SELECT 1", args);

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

            // Empties the given Polyhouses' material (balances, ledger rows and the transfers touching them -- scratch-only
            // clean-up switch), then sets Main Office balances through the REAL ledger path (an adjustment).
            public async Task ResetAsync(params (int Area, int Polyhouse, decimal Cocopeat)[] balances)
            {
                foreach (var (area, polyhouse, _) in balances)
                    await ExecAsync(@"
EXEC sp_set_session_context N'MaterialTestCleanup', 1;
DELETE x FROM dbo.MaterialTransfers x WHERE x.SourcePolyhouseId = @P OR x.DestinationPolyhouseId = @P;
DELETE t FROM dbo.MaterialTransactions t JOIN dbo.MaterialStock s ON s.Id = t.MaterialStockId WHERE s.AreaId = @A AND s.PolyhouseId = @P;
UPDATE dbo.MaterialStock SET CurrentQuantity = 0 WHERE AreaId = @A AND PolyhouseId = @P;
EXEC sp_set_session_context N'MaterialTestCleanup', 0;", ("@A", area), ("@P", polyhouse));
                foreach (var (area, polyhouse, kg) in balances)
                    if (kg > 0)
                    {
                        var (ok, msg, _) = await Get<MaterialStockRepository>().AdjustAsync(area, polyhouse, Cocopeat, 1, kg, "transfer test setup", Guid.NewGuid(), Supervisor, _ => true,
                            LedgerDates.Instant(AppClock.Today.AddDays(-10)).TransactionUtc);
                        Assert.True(ok, msg);
                    }
            }

            public Task<decimal> BalanceAsync(int area, int polyhouse, int? material = null)
                => ScalarAsync<decimal>("SELECT ISNULL((SELECT CurrentQuantity FROM dbo.MaterialStock WHERE AreaId = @A AND PolyhouseId = @P AND MaterialTypeId = @M), 0)",
                    ("@A", area), ("@P", polyhouse), ("@M", material ?? Cocopeat));

            public Task<int> TransfersTouchingAsync(int polyhouse)
                => ScalarAsync<int>("SELECT COUNT(*) FROM dbo.MaterialTransfers WHERE SourcePolyhouseId = @P OR DestinationPolyhouseId = @P", ("@P", polyhouse));

            public Task<int> InvariantViolationsAsync() => ScalarAsync<int>(@"
SELECT COUNT(*) FROM dbo.MaterialStock s
WHERE s.CurrentQuantity < 0
   OR s.CurrentQuantity <> ISNULL((SELECT SUM(t.Direction * t.StockQuantity) FROM dbo.MaterialTransactions t WHERE t.MaterialStockId = s.Id), 0)");

            // Every transfer has exactly one OUT and one IN row of its quantity.
            public Task<int> HalfTransfersAsync() => ScalarAsync<int>(@"
SELECT COUNT(*) FROM dbo.MaterialTransfers x
WHERE (SELECT COUNT(*) FROM dbo.MaterialTransactions t WHERE t.ReferenceType = 'MaterialTransfer' AND t.ReferenceId = x.Id AND t.TransactionType = 'TRANSFER_OUT' AND t.StockQuantity = x.Quantity) <> 1
   OR (SELECT COUNT(*) FROM dbo.MaterialTransactions t WHERE t.ReferenceType = 'MaterialTransfer' AND t.ReferenceId = x.Id AND t.TransactionType = 'TRANSFER_IN' AND t.StockQuantity = x.Quantity) <> 1");

            public MaterialTransferRequest Request(int fromArea, int fromPolyhouse, int toArea, int toPolyhouse, decimal kg, Guid? token = null, DateTime? date = null) => new()
            {
                MaterialTypeId = Cocopeat, SourceAreaId = fromArea, SourcePolyhouseId = fromPolyhouse, DestinationAreaId = toArea, DestinationPolyhouseId = toPolyhouse,
                Quantity = kg, TransferDate = date ?? AppClock.Today, Remarks = "transfer test", SubmissionToken = token ?? Guid.NewGuid(),
            };

            public MaterialTransferRepository.Actor Actor(int user, bool full = false) => new(user, "mt-test-" + user, full);
        }

        private static async Task<Env> OpenAsync(bool requireMigration = true)
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
                using var probe = new SqlCommand(MaterialTransferSchema.ProbeSql, conn);
                var applied = (int)(await probe.ExecuteScalarAsync())! == 1;
                Skip.If(requireMigration && !applied, "2026-10-25_MaterialStockTransfers.sql is not applied to this scratch copy.");
                Skip.If(!requireMigration && applied, "2026-10-25_MaterialStockTransfers.sql is applied to this scratch copy.");
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
            var env = new Env { Cs = cs!, Provider = services.BuildServiceProvider() };

            env.Cocopeat = await env.ScalarAsync<int>("SELECT Id FROM dbo.MaterialTypes WHERE Code = 'COCOPEAT'");
            env.Pitmos = await env.ScalarAsync<int>("SELECT Id FROM dbo.MaterialTypes WHERE Code = 'PITMOS'");
            const string mainPolyhouse = "SELECT Id FROM dbo.Polyhouses WHERE AreaId = 2 ORDER BY Id OFFSET @N ROWS FETCH NEXT 1 ROWS ONLY";
            env.MainA = await env.ScalarAsync<int>(mainPolyhouse, ("@N", 0));
            env.MainB = await env.ScalarAsync<int>(mainPolyhouse, ("@N", 1));
            env.MainC = await env.ScalarAsync<int>(mainPolyhouse, ("@N", 2));
            env.MpArea = await env.ScalarAsync<int>(@"SELECT TOP 1 a.Id FROM dbo.Area a WHERE a.IsActive = 1 AND a.AreaType = N'MotherPlant'
  AND EXISTS (SELECT 1 FROM dbo.Polyhouses p WHERE p.AreaId = a.Id)
  AND EXISTS (SELECT 1 FROM dbo.UserRoles ur JOIN dbo.Roles r ON r.Id = ur.RoleId JOIN dbo.IMSUsers u ON u.Id = ur.UserId AND u.IsActive = 1
              WHERE ur.AreaId = a.Id AND COALESCE(NULLIF(LTRIM(RTRIM(r.Name)), N''), r.RoleName) = N'Mother Plant Supervisor') ORDER BY a.Id");
            env.MpPolyhouse = await env.ScalarAsync<int>("SELECT MIN(Id) FROM dbo.Polyhouses WHERE AreaId = @A", ("@A", env.MpArea));
            env.OutletArea = await env.ScalarAsync<int>("SELECT TOP 1 a.Id FROM dbo.Area a WHERE a.AreaType = N'Outlet' AND EXISTS (SELECT 1 FROM dbo.Polyhouses p WHERE p.AreaId = a.Id) ORDER BY a.Id");
            env.OutletPolyhouse = await env.ScalarAsync<int>("SELECT MIN(Id) FROM dbo.Polyhouses WHERE AreaId = @A", ("@A", env.OutletArea));
            env.Supervisor = await env.ScalarAsync<int>(FixtureUsers.MainOfficeSowingSupervisorSql);
            env.MpSupervisor = await env.ScalarAsync<int>(@"SELECT TOP 1 ur.UserId FROM dbo.UserRoles ur JOIN dbo.Roles r ON r.Id = ur.RoleId JOIN dbo.IMSUsers u ON u.Id = ur.UserId AND u.IsActive = 1
WHERE ur.AreaId = @A AND COALESCE(NULLIF(LTRIM(RTRIM(r.Name)), N''), r.RoleName) = N'Mother Plant Supervisor' ORDER BY ur.UserId", ("@A", env.MpArea));
            env.Admin = await env.ScalarAsync<int>(@"SELECT TOP 1 ur.UserId FROM dbo.UserRoles ur JOIN dbo.Roles r ON r.Id = ur.RoleId JOIN dbo.IMSUsers u ON u.Id = ur.UserId AND u.IsActive = 1
WHERE COALESCE(NULLIF(LTRIM(RTRIM(r.Name)), N''), r.RoleName) = N'System Administrator' ORDER BY ur.UserId");
            // An active Main Office user who can see material stock but may NOT transfer it (or, failing that, any non-holder in the Area).
            env.NoPermissionUser = await env.ScalarAsync<int>(@"SELECT TOP 1 u.Id FROM dbo.IMSUsers u JOIN dbo.UserRoles ur ON ur.UserId = u.Id AND ur.AreaId = 2
WHERE u.IsActive = 1 AND NOT EXISTS (SELECT 1 FROM dbo.UserRoles r2 JOIN dbo.RolePermissions rp ON rp.RoleId = r2.RoleId JOIN dbo.Permissions p ON p.Id = rp.PermissionId
                                     WHERE r2.UserId = u.Id AND p.Code = N'MaterialStock.Transfer')
  AND NOT EXISTS (SELECT 1 FROM dbo.UserRoles r3 JOIN dbo.Roles r ON r.Id = r3.RoleId WHERE r3.UserId = u.Id AND COALESCE(NULLIF(LTRIM(RTRIM(r.Name)), N''), r.RoleName) = N'System Administrator')
ORDER BY u.Id");
            Skip.If(env.MainC == 0 || env.MpPolyhouse == 0 || env.OutletPolyhouse == 0 || env.Supervisor == 0 || env.MpSupervisor == 0 || env.Admin == 0 || env.NoPermissionUser == 0,
                "This scratch copy lacks the Areas / Polyhouses / users these tests need.");
            return env;
        }

        private static Func<int, bool> Own(Env env, ClaimsPrincipal user) => id => env.Get<AreaAccessService>().CanAccessRequiredArea(user, id);

        // ---- 1. the move itself ------------------------------------------------------------------------------------------

        [SkippableFact]
        public async Task Transfer_BetweenMainOfficePolyhouses_MovesExactlyTheQuantity_AuditedOnBothSides()
        {
            var env = await OpenAsync();
            await env.ResetAsync((MainArea, env.MainA, 100m), (MainArea, env.MainB, 20m));
            var sup = await env.LoginAsync(env.Supervisor);

            var result = await env.Repo.CreateAsync(env.Request(MainArea, env.MainA, MainArea, env.MainB, 37.5m), env.Actor(env.Supervisor), Own(env, sup));
            Assert.True(result.Success, result.Message);
            Assert.False(result.Duplicate);
            Assert.StartsWith("MT-", result.Code);
            Assert.Equal((62.5m, 57.5m), (result.SourceBalance, result.DestinationBalance));
            Assert.Equal(62.5m, await env.BalanceAsync(MainArea, env.MainA));
            Assert.Equal(57.5m, await env.BalanceAsync(MainArea, env.MainB));

            // the audit record: material, quantity, source, destination, date and user, and each side's ledger row
            var (rows, _) = await env.Repo.GetTransfersAsync(new MaterialTransferFilter(), null);
            var t = rows.Single(r => r.Id == result.TransferId);
            Assert.Equal(("Cocopeat", 37.5m, env.MainA, env.MainB, AppClock.Today, env.Supervisor),
                (t.MaterialName, t.Quantity, t.SourcePolyhouseId, t.DestinationPolyhouseId, t.TransferDate.Date, t.CreatedById));
            Assert.Equal((62.5m, 57.5m), (t.SourceBalanceAfter, t.DestinationBalanceAfter));
            Assert.Equal(1, await env.ScalarAsync<int>(@"SELECT COUNT(*) FROM dbo.MaterialTransactions WHERE ReferenceType = 'MaterialTransfer' AND ReferenceId = @T
AND TransactionType = 'TRANSFER_OUT' AND Direction = -1 AND BalanceBefore = 100 AND BalanceAfter = 62.5 AND CreatedById = @U", ("@T", result.TransferId), ("@U", env.Supervisor)));
            Assert.Equal(1, await env.ScalarAsync<int>(@"SELECT COUNT(*) FROM dbo.MaterialTransactions WHERE ReferenceType = 'MaterialTransfer' AND ReferenceId = @T
AND TransactionType = 'TRANSFER_IN' AND Direction = 1 AND BalanceBefore = 20 AND BalanceAfter = 57.5 AND CreatedById = @U", ("@T", result.TransferId), ("@U", env.Supervisor)));
            // no conversion factor is applied to a transfer (Cocopeat's 5.2 is for Add Material only)
            Assert.Equal(0, await env.ScalarAsync<int>("SELECT COUNT(*) FROM dbo.MaterialTransactions WHERE ReferenceType = 'MaterialTransfer' AND ReferenceId = @T AND (InputQuantity IS NOT NULL OR ConversionFactor IS NOT NULL)", ("@T", result.TransferId)));
            Assert.Equal(0, await env.InvariantViolationsAsync());
            Assert.Equal(0, await env.HalfTransfersAsync());
        }

        [SkippableFact]
        public async Task Transfer_InsufficientStock_IsRefused_AndNothingChanges()
        {
            var env = await OpenAsync();
            await env.ResetAsync((MainArea, env.MainA, 10m), (MainArea, env.MainB, 0m));
            var sup = await env.LoginAsync(env.Supervisor);
            const string pitmosRowsSql = "SELECT COUNT(*) FROM dbo.MaterialStock WHERE AreaId = @A AND PolyhouseId = @P AND MaterialTypeId = @M";
            var pitmosRows = await env.ScalarAsync<int>(pitmosRowsSql, ("@A", MainArea), ("@P", env.MainB), ("@M", env.Pitmos));

            var result = await env.Repo.CreateAsync(env.Request(MainArea, env.MainA, MainArea, env.MainB, 10.001m), env.Actor(env.Supervisor), Own(env, sup));
            Assert.False(result.Success);
            Assert.Contains("Insufficient Cocopeat stock", result.Message);
            Assert.Contains("Available: 10.00 kg", result.Message);
            // nor from a Polyhouse that never held the material (a zero balance row is not left behind)
            var empty = await env.Repo.CreateAsync(env.Request(MainArea, env.MainB, MainArea, env.MainA, 1m), env.Actor(env.Supervisor), Own(env, sup));
            Assert.False(empty.Success);
            Assert.Contains("Insufficient", empty.Message);
            // a Pitmos transfer cannot take Cocopeat
            var pitmos = env.Request(MainArea, env.MainA, MainArea, env.MainB, 1m);
            pitmos.MaterialTypeId = env.Pitmos;
            Assert.False((await env.Repo.CreateAsync(pitmos, env.Actor(env.Supervisor), Own(env, sup))).Success);

            Assert.Equal((10m, 0m), (await env.BalanceAsync(MainArea, env.MainA), await env.BalanceAsync(MainArea, env.MainB)));
            Assert.Equal(0, await env.TransfersTouchingAsync(env.MainA));
            Assert.Equal(pitmosRows, await env.ScalarAsync<int>(pitmosRowsSql, ("@A", MainArea), ("@P", env.MainB), ("@M", env.Pitmos)));
            Assert.Equal(0, await env.InvariantViolationsAsync());
        }

        [SkippableFact]
        public async Task Transfer_InvalidRequests_AreRefused_NothingChanges()
        {
            var env = await OpenAsync();
            await env.ResetAsync((MainArea, env.MainA, 50m));
            var sup = await env.LoginAsync(env.Supervisor);
            var admin = env.Actor(env.Admin, full: true);
            async Task<string?> Refused(MaterialTransferRequest r, MaterialTransferRepository.Actor? a = null)
            {
                var res = await env.Repo.CreateAsync(r, a ?? env.Actor(env.Supervisor), a == null ? Own(env, sup) : _ => true);
                Assert.False(res.Success);
                return res.Message;
            }

            Assert.Equal(MaterialTransferRules.SamePolyhouseMessage, await Refused(env.Request(MainArea, env.MainA, MainArea, env.MainA, 1m)));
            // never an Outlet -- not even for an administrator
            Assert.Equal("The destination must be a Polyhouse of an active Main Office or Mother Plant Area.",
                await Refused(env.Request(MainArea, env.MainA, env.OutletArea, env.OutletPolyhouse, 1m), admin));
            Assert.Contains("does not belong", await Refused(env.Request(MainArea, env.MainA, env.MpArea, env.MainB, 1m), admin));
            Assert.Contains("greater than zero", await Refused(env.Request(MainArea, env.MainA, MainArea, env.MainB, 0m)));
            Assert.Contains("decimal places", await Refused(env.Request(MainArea, env.MainA, MainArea, env.MainB, 1.0001m)));
            Assert.Equal(LedgerDates.FutureDateMessage, await Refused(env.Request(MainArea, env.MainA, MainArea, env.MainB, 1m, date: AppClock.Today.AddDays(1))));
            Assert.Equal(MaterialTransferRules.ExpiredFormMessage, await Refused(env.Request(MainArea, env.MainA, MainArea, env.MainB, 1m, token: Guid.Empty)));
            // back-dated before the stock arrived (it arrived 10 days ago)
            Assert.Contains("was in", await Refused(env.Request(MainArea, env.MainA, MainArea, env.MainB, 1m, date: AppClock.Today.AddDays(-20))));

            Assert.Equal(50m, await env.BalanceAsync(MainArea, env.MainA));
            Assert.Equal(0, await env.TransfersTouchingAsync(env.MainA));
            Assert.Equal(0, await env.InvariantViolationsAsync());
        }

        // ---- 2. duplicates and concurrency --------------------------------------------------------------------------------

        [SkippableFact]
        public async Task Transfer_DuplicateSubmission_SequentialOrSimultaneous_MovesStockOnce()
        {
            var env = await OpenAsync();
            await env.ResetAsync((MainArea, env.MainA, 100m), (MainArea, env.MainB, 0m));
            var sup = await env.LoginAsync(env.Supervisor);

            var token = Guid.NewGuid();
            var first = await env.Repo.CreateAsync(env.Request(MainArea, env.MainA, MainArea, env.MainB, 10m, token), env.Actor(env.Supervisor), Own(env, sup));
            var again = await env.Repo.CreateAsync(env.Request(MainArea, env.MainA, MainArea, env.MainB, 10m, token), env.Actor(env.Supervisor), Own(env, sup));
            Assert.True(first.Success && !first.Duplicate, first.Message);
            Assert.True(again.Success && again.Duplicate);
            Assert.Equal(first.TransferId, again.TransferId);

            var burstToken = Guid.NewGuid();
            var burst = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ =>
                env.Repo.CreateAsync(env.Request(MainArea, env.MainA, MainArea, env.MainB, 5m, burstToken), env.Actor(env.Supervisor), Own(env, sup))));
            Assert.All(burst, r => Assert.True(r.Success, r.Message));
            Assert.Single(burst.Select(r => r.TransferId).Distinct());
            Assert.Equal(1, burst.Count(r => !r.Duplicate));

            Assert.Equal((85m, 15m), (await env.BalanceAsync(MainArea, env.MainA), await env.BalanceAsync(MainArea, env.MainB)));
            Assert.Equal(2, await env.TransfersTouchingAsync(env.MainA));
            Assert.Equal(0, await env.InvariantViolationsAsync());
            Assert.Equal(0, await env.HalfTransfersAsync());
        }

        [SkippableFact]
        public async Task Transfer_ConcurrentFromOneSource_NeverGoesNegative_OrMovesMoreThanItHeld()
        {
            var env = await OpenAsync();
            await env.ResetAsync((MainArea, env.MainA, 100m), (MainArea, env.MainB, 0m), (MainArea, env.MainC, 0m));
            var sup = await env.LoginAsync(env.Supervisor);

            var results = await Task.WhenAll(Enumerable.Range(0, 10).Select(i =>
                env.Repo.CreateAsync(env.Request(MainArea, env.MainA, MainArea, i % 2 == 0 ? env.MainB : env.MainC, 30m), env.Actor(env.Supervisor), Own(env, sup))));
            Assert.Equal(3, results.Count(r => r.Success));
            Assert.All(results.Where(r => !r.Success), r => Assert.Contains("Insufficient", r.Message));
            Assert.Equal(10m, await env.BalanceAsync(MainArea, env.MainA));
            Assert.Equal(90m, await env.BalanceAsync(MainArea, env.MainB) + await env.BalanceAsync(MainArea, env.MainC));
            Assert.Equal(3, await env.TransfersTouchingAsync(env.MainA));
            Assert.Equal(0, await env.InvariantViolationsAsync());
            Assert.Equal(0, await env.HalfTransfersAsync());
        }

        [SkippableFact]
        public async Task Transfer_OppositeDirectionsAtOnce_DoNotDeadlock_AndConserveStock()
        {
            var env = await OpenAsync();
            await env.ResetAsync((MainArea, env.MainA, 100m), (MainArea, env.MainB, 100m));
            var sup = await env.LoginAsync(env.Supervisor);

            var results = await Task.WhenAll(Enumerable.Range(0, 12).Select(i => i % 2 == 0
                ? env.Repo.CreateAsync(env.Request(MainArea, env.MainA, MainArea, env.MainB, 7m), env.Actor(env.Supervisor), Own(env, sup))
                : env.Repo.CreateAsync(env.Request(MainArea, env.MainB, MainArea, env.MainA, 3m), env.Actor(env.Supervisor), Own(env, sup))));
            Assert.All(results, r => Assert.True(r.Success, r.Message));
            Assert.Equal(100m - 6 * 7m + 6 * 3m, await env.BalanceAsync(MainArea, env.MainA));
            Assert.Equal(100m + 6 * 7m - 6 * 3m, await env.BalanceAsync(MainArea, env.MainB));
            Assert.Equal(0, await env.InvariantViolationsAsync());
            Assert.Equal(0, await env.HalfTransfersAsync());
        }

        // ---- 3. atomicity -------------------------------------------------------------------------------------------------

        [SkippableFact]
        public async Task Transfer_FailureAfterTheSourceWasDebited_RollsEverythingBack()
        {
            var env = await OpenAsync();
            await env.ResetAsync((MainArea, env.MainA, 40m), (MainArea, env.MainB, 5m));
            var sup = await env.LoginAsync(env.Supervisor);
            // A scratch-only trigger that fails the DESTINATION side of this test's transfer -- after the transfer row and the
            // source's TRANSFER_OUT row were written in the same transaction.
            await env.ExecAsync(@"EXEC (N'CREATE OR ALTER TRIGGER dbo.TR_ZZTest_FailTransferIn ON dbo.MaterialTransactions AFTER INSERT AS
BEGIN IF EXISTS (SELECT 1 FROM inserted WHERE TransactionType = ''TRANSFER_IN'' AND Remarks LIKE ''%ZZFAIL%'') THROW 50999, ''zz test: destination failed'', 1; END')");
            try
            {
                var request = env.Request(MainArea, env.MainA, MainArea, env.MainB, 15m);
                await env.ExecAsync("UPDATE dbo.Polyhouses SET Name = Name + N' ZZFAIL' WHERE Id = @P", ("@P", env.MainA));
                MaterialTransferRepository.Result result;
                try { result = await env.Repo.CreateAsync(request, env.Actor(env.Supervisor), Own(env, sup)); }
                finally { await env.ExecAsync("UPDATE dbo.Polyhouses SET Name = REPLACE(Name, N' ZZFAIL', N'') WHERE Id = @P", ("@P", env.MainA)); }
                Assert.False(result.Success);
                Assert.Contains("zz test", result.Message);
                Assert.Equal((40m, 5m), (await env.BalanceAsync(MainArea, env.MainA), await env.BalanceAsync(MainArea, env.MainB)));
                Assert.Equal(0, await env.TransfersTouchingAsync(env.MainA));
                Assert.Equal(0, await env.ScalarAsync<int>("SELECT COUNT(*) FROM dbo.MaterialTransfers WHERE SubmissionToken = @T", ("@T", request.SubmissionToken)));
                // the same form can then be submitted again and moves the stock once
                var retry = await env.Repo.CreateAsync(request, env.Actor(env.Supervisor), Own(env, sup));
                Assert.True(retry.Success && !retry.Duplicate, retry.Message);
                Assert.Equal((25m, 20m), (await env.BalanceAsync(MainArea, env.MainA), await env.BalanceAsync(MainArea, env.MainB)));
            }
            finally
            {
                await env.ExecAsync("IF OBJECT_ID('dbo.TR_ZZTest_FailTransferIn', 'TR') IS NOT NULL DROP TRIGGER dbo.TR_ZZTest_FailTransferIn");
            }
            Assert.Equal(0, await env.InvariantViolationsAsync());
            Assert.Equal(0, await env.HalfTransfersAsync());
        }

        [SkippableFact]
        public async Task Database_RefusesTransferRowsWithoutTheirTransfer_AndOtherMovementsOutsideTheMainOffice()
        {
            var env = await OpenAsync();
            await env.ResetAsync((MainArea, env.MainA, 30m), (env.MpArea, env.MpPolyhouse, 0m));
            var stock = await env.ScalarAsync<int>("SELECT Id FROM dbo.MaterialStock WHERE AreaId = @A AND PolyhouseId = @P AND MaterialTypeId = @M", ("@A", MainArea), ("@P", env.MainA), ("@M", env.Cocopeat));
            var orphan = await Assert.ThrowsAsync<SqlException>(() => env.ExecAsync(@"
INSERT INTO dbo.MaterialTransactions (MaterialStockId, TransactionType, Direction, StockQuantity, BalanceBefore, BalanceAfter, ReferenceType, ReferenceId, CreatedById)
VALUES (@S, 'TRANSFER_OUT', -1, 5, 30, 25, 'MaterialTransfer', -1, @U)", ("@S", stock), ("@U", env.Supervisor)));
            Assert.Contains("exactly its transfer", orphan.Message);
            Assert.Equal(30m, await env.BalanceAsync(MainArea, env.MainA));

            // a Mother Plant balance (opened by an administrator's transfer) cannot be added to or adjusted -- in the app or the database
            var res = await env.Repo.CreateAsync(env.Request(MainArea, env.MainA, env.MpArea, env.MpPolyhouse, 5m), env.Actor(env.Admin, full: true), _ => true);
            Assert.True(res.Success, res.Message);
            var (adjOk, adjMsg, _) = await env.Get<MaterialStockRepository>().AdjustAsync(env.MpArea, env.MpPolyhouse, env.Cocopeat, 1, 1m, "x", Guid.NewGuid(), env.Admin, _ => true);
            Assert.False(adjOk);
            Assert.Equal(MaterialStockRules.LocationMessage, adjMsg);
            var mpStock = await env.ScalarAsync<int>("SELECT Id FROM dbo.MaterialStock WHERE AreaId = @A AND PolyhouseId = @P AND MaterialTypeId = @M", ("@A", env.MpArea), ("@P", env.MpPolyhouse), ("@M", env.Cocopeat));
            var adjust = await Assert.ThrowsAsync<SqlException>(() => env.ExecAsync(@"
INSERT INTO dbo.MaterialTransactions (MaterialStockId, TransactionType, Direction, StockQuantity, BalanceBefore, BalanceAfter, Remarks, CreatedById)
VALUES (@S, 'ADJUSTMENT', 1, 1, 5, 6, N'direct', @U)", ("@S", mpStock), ("@U", env.Admin)));
            Assert.Contains("outside the Main Office", adjust.Message);
            // the transfer record is insert-only
            var edit = await Assert.ThrowsAsync<SqlException>(() => env.ExecAsync("UPDATE dbo.MaterialTransfers SET Quantity = 1 WHERE Id = @T", ("@T", res.TransferId)));
            Assert.Contains("cannot be changed", edit.Message);
            Assert.Equal(5m, await env.BalanceAsync(env.MpArea, env.MpPolyhouse));
            Assert.Equal(0, await env.InvariantViolationsAsync());
            await env.ResetAsync((env.MpArea, env.MpPolyhouse, 0m));
        }

        // ---- 4. permissions and Areas -------------------------------------------------------------------------------------

        [SkippableFact]
        public async Task Permissions_OnlyMaterialStockTransfer_MaySend_ThePageAndTheServerBothCheck()
        {
            var env = await OpenAsync();
            await env.ResetAsync((MainArea, env.MainA, 20m), (MainArea, env.MainB, 0m));
            var feature = env.Get<FeatureAccessService>();
            var sup = await env.LoginAsync(env.Supervisor);
            var mp = await env.LoginAsync(env.MpSupervisor);
            var other = await env.LoginAsync(env.NoPermissionUser);

            Assert.True(sup.CanTransferMaterials());
            Assert.True(await feature.CanAccessPageAsync(sup, "/Production/MaterialStock/Transfers"));
            Assert.False(mp.CanTransferMaterials());
            Assert.False(await feature.CanAccessPageAsync(mp, "/Production/MaterialStock/Transfers"));
            Assert.False(other.CanTransferMaterials());
            Assert.Equal(new FeatureRule(MaterialStockAccess.MaterialTransfersAny, MaterialStockAccess.MaterialTransfer),
                FeatureAuthorizationConventions.Rules["/Production/MaterialStock/Transfers"]);

            foreach (var user in new[] { mp, other })
            {
                var page = env.Page<TransfersPage>(user);
                page.Input = env.Request(MainArea, env.MainA, MainArea, env.MainB, 5m);
                Assert.IsType<ForbidResult>(await page.OnPostSendAsync());
            }

            // the Sowing Supervisor through the page: the transfer is made and recorded under their own user id
            var ok = env.Page<TransfersPage>(sup);
            ok.Input = env.Request(MainArea, env.MainA, MainArea, env.MainB, 5m);
            Assert.IsType<RedirectToPageResult>(await ok.OnPostSendAsync());
            Assert.Equal((15m, 5m), (await env.BalanceAsync(MainArea, env.MainA), await env.BalanceAsync(MainArea, env.MainB)));
            Assert.Equal(env.Supervisor, await env.ScalarAsync<int>("SELECT TOP 1 CreatedById FROM dbo.MaterialTransfers WHERE SourcePolyhouseId = @P ORDER BY Id DESC", ("@P", env.MainA)));
        }

        [SkippableFact]
        public async Task Areas_SourceMustBeTheUsersOwnArea_DestinationNeedsSomeoneWhoCanSeeMaterial_AdministratorsAnywhere()
        {
            var env = await OpenAsync();
            await env.ResetAsync((MainArea, env.MainA, 50m), (env.MpArea, env.MpPolyhouse, 0m));
            var sup = await env.LoginAsync(env.Supervisor);
            var admin = await env.LoginAsync(env.Admin);
            var viewable = await env.Repo.GetViewableAreaIdsAsync();
            Assert.Contains(MainArea, viewable);
            Skip.If(viewable.Contains(env.MpArea), "Someone in the Mother Plant Area can see material stock in this copy -- the no-viewer rule cannot be shown here.");

            // to a Mother Plant Area where nobody can see material: refused for the Sowing Supervisor (page lists it neither)
            var refused = await env.Repo.CreateAsync(env.Request(MainArea, env.MainA, env.MpArea, env.MpPolyhouse, 5m), env.Actor(env.Supervisor), Own(env, sup));
            Assert.Equal((false, MaterialTransferRules.NoViewerMessage), (refused.Success, refused.Message));
            var page = env.Page<TransfersPage>(sup);
            await page.OnGetAsync();
            Assert.DoesNotContain(page.DestinationLocations, l => l.AreaId == env.MpArea);
            Assert.All(page.SourceLocations, l => Assert.Equal(MainArea, l.AreaId));

            // an administrator may make the exceptional transfer; the stock is then held in the Mother Plant Polyhouse
            var adminPage = env.Page<TransfersPage>(admin);
            await adminPage.OnGetAsync();
            Assert.Contains(adminPage.DestinationLocations, l => l.PolyhouseId == env.MpPolyhouse);
            Assert.DoesNotContain(adminPage.DestinationLocations, l => l.AreaId == env.OutletArea);
            var toMp = await env.Repo.CreateAsync(env.Request(MainArea, env.MainA, env.MpArea, env.MpPolyhouse, 12m), env.Actor(env.Admin, full: true), Own(env, admin));
            Assert.True(toMp.Success, toMp.Message);
            Assert.Equal((38m, 12m), (await env.BalanceAsync(MainArea, env.MainA), await env.BalanceAsync(env.MpArea, env.MpPolyhouse)));

            // the Sowing Supervisor is not assigned to the Mother Plant Area: cannot send from it (repository AND page)
            var fromMp = await env.Repo.CreateAsync(env.Request(env.MpArea, env.MpPolyhouse, MainArea, env.MainA, 2m), env.Actor(env.Supervisor), Own(env, sup));
            Assert.Equal((false, MaterialTransferRules.NotAuthorizedSourceMessage), (fromMp.Success, fromMp.Message));
            var post = env.Page<TransfersPage>(sup);
            post.Input = env.Request(env.MpArea, env.MpPolyhouse, MainArea, env.MainA, 2m);
            Assert.IsType<PageResult>(await post.OnPostSendAsync());
            Assert.False(post.ModelState.IsValid);
            Assert.Equal(12m, await env.BalanceAsync(env.MpArea, env.MpPolyhouse));

            // the administrator brings it back (corrections are transfers, never edits)
            var back = await env.Repo.CreateAsync(env.Request(env.MpArea, env.MpPolyhouse, MainArea, env.MainA, 12m), env.Actor(env.Admin, full: true), Own(env, admin));
            Assert.True(back.Success, back.Message);
            Assert.Equal((50m, 0m), (await env.BalanceAsync(MainArea, env.MainA), await env.BalanceAsync(env.MpArea, env.MpPolyhouse)));
            Assert.Equal(0, await env.InvariantViolationsAsync());
            await env.ResetAsync((env.MpArea, env.MpPolyhouse, 0m));
        }

        // ---- 5. stock by location and history ------------------------------------------------------------------------------

        [SkippableFact]
        public async Task History_ShowsTransfersTouchingTheUsersAreas_AndTheMaterialLedgerFiltersThem()
        {
            var env = await OpenAsync();
            await env.ResetAsync((MainArea, env.MainA, 60m), (MainArea, env.MainB, 0m), (env.MpArea, env.MpPolyhouse, 0m));
            var sup = await env.LoginAsync(env.Supervisor);
            var inMain = await env.Repo.CreateAsync(env.Request(MainArea, env.MainA, MainArea, env.MainB, 4m), env.Actor(env.Supervisor), Own(env, sup));
            var toMp = await env.Repo.CreateAsync(env.Request(MainArea, env.MainA, env.MpArea, env.MpPolyhouse, 6m), env.Actor(env.Admin, full: true), _ => true);
            Assert.True(inMain.Success && toMp.Success);

            // scope: the Mother Plant Area alone sees only the transfer that reached it
            var (mpRows, mpTotal) = await env.Repo.GetTransfersAsync(new MaterialTransferFilter(), new[] { env.MpArea });
            Assert.Equal(new[] { toMp.TransferId }, mpRows.Select(r => r.Id).Where(id => id == toMp.TransferId || id == inMain.TransferId));
            Assert.True(mpTotal >= 1);
            Assert.Empty((await env.Repo.GetTransfersAsync(new MaterialTransferFilter(), Array.Empty<int>())).Rows);

            // the page (Sowing Supervisor): both transfers start in the Main Office; filtered by Area and Material
            var page = env.Page<TransfersPage>(sup);
            page.AreaId = env.MpArea;
            await page.OnGetAsync();
            Assert.Contains(page.Transfers, t => t.Id == toMp.TransferId);
            Assert.DoesNotContain(page.Transfers, t => t.Id == inMain.TransferId);
            page = env.Page<TransfersPage>(sup);
            page.MaterialTypeId = env.Pitmos;
            await page.OnGetAsync();
            Assert.DoesNotContain(page.Transfers, t => t.Id == toMp.TransferId || t.Id == inMain.TransferId);

            // Material History: the Transfer category lists both sides of the Main Office move
            var history = env.Page<HistoryPage>(sup);
            history.Type = MaterialStockRules.Transfer;
            history.Material = "COCOPEAT";
            await history.OnGetAsync();
            var refs = history.Rows.Where(r => r.ReferenceText == $"Transfer #{inMain.TransferId}").ToList();
            Assert.Equal(new[] { -4m, 4m }, refs.Select(r => r.SignedQuantity).OrderBy(q => q));
            Assert.All(refs, r => Assert.Equal(MaterialStockRules.Transfer, r.Category));
            Assert.All(history.Rows, r => Assert.Equal(MaterialStockRules.Transfer, r.Category));

            // stock by location: the Material Stock page lists the Mother Plant balance for an administrator
            var admin = await env.LoginAsync(env.Admin);
            var stock = env.Page<PlantStockManager.Pages.Production.MaterialStock.IndexModel>(admin);
            await stock.OnGetAsync();
            Assert.Contains(stock.MaterialRows, r => r.PolyhouseId == env.MpPolyhouse && r.CurrentQuantity == 6m);
            Assert.Contains(stock.AreaOptions, a => a.Id == env.MpArea);
            await env.ResetAsync((env.MpArea, env.MpPolyhouse, 0m));
        }

        // ---- 6. without the migration --------------------------------------------------------------------------------------

        [SkippableFact]
        public async Task WithoutTheMigration_TransfersAreUnavailable_EverythingElseAsBefore()
        {
            var env = await OpenAsync(requireMigration: false);
            var sup = await env.LoginAsync(env.Supervisor);
            var res = await env.Repo.CreateAsync(env.Request(MainArea, env.MainA, MainArea, env.MainB, 1m), env.Actor(env.Supervisor), _ => true);
            Assert.Equal((false, MaterialTransferSchema.MigrationMessage), (res.Success, res.Message));
            var page = env.Page<TransfersPage>(sup);
            await page.OnGetAsync();
            Assert.False(page.SchemaReady);
            Assert.Empty(page.Transfers);
            var stock = env.Page<PlantStockManager.Pages.Production.MaterialStock.IndexModel>(sup);
            await stock.OnGetAsync();
            Assert.True(stock.CanSeeMaterials);
        }
    }
}
