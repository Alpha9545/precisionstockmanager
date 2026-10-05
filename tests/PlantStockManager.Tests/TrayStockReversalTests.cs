using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using PlantStockManager.Authorization;
using PlantStockManager.Data;
using PlantStockManager.Models;

namespace PlantStockManager.Tests
{
    // Tray Stock end-to-end tests -- AREA + POLYHOUSE + CAVITY (2026-10-04).
    // Real repositories against a SCRATCH COPY of the database:
    //   * Add Tray Stock (authorization, Area/Polyhouse mismatch, Outlet, whole
    //     numbers, second addition to the same pool, separate Polyhouses,
    //     duplicate submit)
    //   * Seed / Cutting Sowing consume CEILING(qty / cavity) trays from the
    //     sowing's own Area + Polyhouse + Cavity pool; not enough trays, no
    //     Polyhouse, or a Polyhouse of another Area -> nothing is created
    //   * cancellation returns exactly the consumed trays to the SAME pool
    //   * Ready Confirmation Cutting overage consumes/returns only the extra
    //     trays, from/to the same pool
    //   * 50 available, two simultaneous 30-tray sowings -> exactly one succeeds
    //   * Admin > Polyhouse: no Area change while the Polyhouse holds trays
    //
    // CancelAsync/ConfirmAsync/InsertAsync each COMMIT their own transaction,
    // so these tests run only against a database named PlantsIMS2_Scratch_*
    // (refused otherwise, SKIPPED when PSM_SCRATCH_CONNECTION is not set).
    // Shares the "ScratchDb" collection so it never runs alongside the other
    // ScratchDb suites.
    [Collection("ScratchDb")]
    public class TrayStockReversalTests
    {
        private const string ConnectionVariable = "PSM_SCRATCH_CONNECTION";
        private const string RequiredPrefix = "PlantsIMS2_Scratch_";

        private const int MainOfficeAreaId = 2;
        private const int Facility5 = 2, Facility3 = 3;          // Main-Office-Areas
        private const int GreenBlessAreaId = 127;               // Green Bless Nursery (a growing Area)
        private const int GreenBlessP1 = 14, GreenBlessP2 = 15, GreenBlessNetHouse = 16;
        private const int SamarthAreaId = 123, SamarthP1 = 8;   // a Polyhouse of ANOTHER Area
        private const int OutletAreaId = 124, OutletPolyhouse = 18;
        private const int SupervisorId = 9;                     // Sowing Supervisor (Maya)
        private const string RoleSowingSupervisor = "Sowing Supervisor";

        // Mother Plant 114 / Area 1 -- the Cutting Stock SOURCE pool fixture
        // CuttingSowingOverageE2ETests also relies on.
        private const int MpA = 114, AreaA = 1, Recorder = 21;

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
            public int Species { get; init; }
            public int SeedStockId { get; init; }
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

            public async Task<int> ResetCuttingPoolAsync(int area, decimal physical)
            {
                await ExecAsync(@"
IF NOT EXISTS (SELECT 1 FROM dbo.CuttingStock WHERE SpeciesId = @S AND AreaId = @A)
    INSERT INTO dbo.CuttingStock (SpeciesId, AreaId, PhysicalQuantity, CreatedDate, CreatedBy) VALUES (@S, @A, 0, SYSUTCDATETIME(), N'tray-test');
UPDATE dbo.CuttingStock SET InTransitQuantity = 0, PhysicalQuantity = @P WHERE SpeciesId = @S AND AreaId = @A", ("@S", Species), ("@A", area), ("@P", physical));
                var id = await ScalarAsync<int>("SELECT Id FROM dbo.CuttingStock WHERE SpeciesId = @S AND AreaId = @A", ("@S", Species), ("@A", area));
                await ExecAsync("DELETE FROM dbo.CuttingStockTransactions WHERE CuttingStockId = @P", ("@P", id));
                return id;
            }

            public async Task ResetSeedPoolAsync(decimal physical)
                => await ExecAsync("UPDATE dbo.SeedStock SET InTransitQuantity = 0, PhysicalQuantity = @P WHERE Id = @Id", ("@P", physical), ("@Id", SeedStockId));

            // Sets an Area + Polyhouse + Cavity pool to a known balance THROUGH THE
            // REAL Add Tray Stock path (so the ledger always reconciles): empties it
            // with a direct reset first (scratch copy only), then adds.
            public async Task<int> SetPoolAsync(int areaId, int polyhouseId, string traySize, decimal physical)
            {
                await ExecAsync(@"
UPDATE dbo.TrayStock SET PhysicalQuantity = 0 WHERE AreaId = @A AND PolyhouseId = @P AND TraySize = @T;
DELETE tr FROM dbo.TrayStockTransactions tr JOIN dbo.TrayStock t ON t.Id = tr.TrayStockId WHERE t.AreaId = @A AND t.PolyhouseId = @P AND t.TraySize = @T",
                    ("@A", areaId), ("@P", polyhouseId), ("@T", traySize));
                if (physical > 0)
                {
                    var (ok, msg, _, _) = await Get<TrayStockRepository>().AddStockAsync(
                        areaId, polyhouseId, traySize, physical, DateTime.UtcNow, Random.Shared.Next(1, int.MaxValue), SupervisorId, "tray-test", "test setup", _ => true);
                    Assert.True(ok, msg);
                }
                return await PoolIdAsync(areaId, polyhouseId, traySize);
            }

            public Task<int> PoolIdAsync(int areaId, int polyhouseId, string traySize)
                => ScalarAsync<int>("SELECT Id FROM dbo.TrayStock WHERE AreaId = @A AND PolyhouseId = @P AND TraySize = @T", ("@A", areaId), ("@P", polyhouseId), ("@T", traySize));

            public Task<decimal> TrayPhysicalAsync(int trayStockId) => ScalarAsync<decimal>("SELECT PhysicalQuantity FROM dbo.TrayStock WHERE Id = @Id", ("@Id", trayStockId));

            public Task<decimal> TrayNetForReferenceAsync(string referenceType, int referenceId)
                => ScalarAsync<decimal>("SELECT ISNULL(SUM(Quantity), 0) FROM dbo.TrayStockTransactions WHERE ReferenceType = @RT AND ReferenceId = @RI",
                    ("@RT", referenceType), ("@RI", referenceId));

            public Task<decimal> TrayNetForReferenceInPoolAsync(int trayStockId, string referenceType, int referenceId)
                => ScalarAsync<decimal>("SELECT ISNULL(SUM(Quantity), 0) FROM dbo.TrayStockTransactions WHERE TrayStockId = @T AND ReferenceType = @RT AND ReferenceId = @RI",
                    ("@T", trayStockId), ("@RT", referenceType), ("@RI", referenceId));

            public Task<int> InvariantViolationsAsync()
                => ScalarAsync<int>(@"
SELECT (SELECT COUNT(*) FROM dbo.TrayStock t WHERE t.PhysicalQuantity <> ISNULL((SELECT SUM(x.Quantity) FROM dbo.TrayStockTransactions x WHERE x.TrayStockId = t.Id), 0) OR t.PhysicalQuantity < 0)
     + (SELECT COUNT(*) FROM dbo.TrayStock WHERE IsActive = 1 AND PolyhouseId IS NULL)
     + (SELECT COUNT(*) FROM dbo.TrayStock t JOIN dbo.Polyhouses p ON p.Id = t.PolyhouseId WHERE t.IsActive = 1 AND ISNULL(p.AreaId, -1) <> t.AreaId)");
        }

        private static async Task<Env> OpenAsync()
        {
            var cs = Environment.GetEnvironmentVariable(ConnectionVariable);
            Skip.If(string.IsNullOrWhiteSpace(cs), $"Set {ConnectionVariable} to a {RequiredPrefix}* database connection string to run the Tray Stock tests.");

            string name;
            using (var conn = new SqlConnection(cs))
            {
                await conn.OpenAsync();
                using var cmd = new SqlCommand("SELECT DB_NAME()", conn);
                name = (string)(await cmd.ExecuteScalarAsync())!;
            }
            if (!name.StartsWith(RequiredPrefix, StringComparison.Ordinal))
                throw new InvalidOperationException($"Refusing to run these tests against '{name}'. Only databases named {RequiredPrefix}* are allowed.");

            var services = new ServiceCollection();
            services.AddSingleton<IConfiguration>(new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:DefaultConnection"] = cs }).Build());
            services.AddSingleton<DatabaseHelper>();
            services.AddSingleton<AreaAccessService>();
            services.AddSingleton<IOptions<SecurityOptions>>(Options.Create(new SecurityOptions()));
            services.AddSingleton<IOptionsMonitor<SeedlingWorkflowOptions>>(new StaticMonitor<SeedlingWorkflowOptions>(new SeedlingWorkflowOptions()));
            services.AddSingleton<SeedlingAreaScope>();
            foreach (var t in typeof(DatabaseHelper).Assembly.GetTypes().Where(t => t.Namespace == "PlantStockManager.Data" && t.IsClass && t.Name.EndsWith("Repository")))
                services.AddScoped(t);
            var provider = services.BuildServiceProvider();

            var env = new Env { ConnectionString = cs!, Provider = provider };
            var species = await env.ScalarAsync<int>("SELECT SpeciesId FROM dbo.MotherPlants WHERE Id = @M", ("@M", MpA));
            await env.ExecAsync("UPDATE dbo.PlantSpecies SET ReadyStockDays = 60 WHERE Id = @S AND ReadyStockDays IS NULL", ("@S", species));
            // Scratch fixture: the supervisor is also a Sowing Supervisor of Green Bless
            // Nursery, so a Cutting Sowing into that Area has an eligible supervisor.
            await env.ExecAsync(@"
DECLARE @R INT = (SELECT Id FROM dbo.Roles WHERE RoleName = @RoleName);
IF NOT EXISTS (SELECT 1 FROM dbo.UserRoles WHERE UserId = @U AND RoleId = @R AND AreaId = @A)
    INSERT INTO dbo.UserRoles (UserId, RoleId, AreaId) VALUES (@U, @R, @A)", ("@U", SupervisorId), ("@RoleName", RoleSowingSupervisor), ("@A", GreenBlessAreaId));
            var seedStockId = await env.ScalarAsync<int>(
                "SELECT TOP 1 Id FROM dbo.SeedStock WHERE SpeciesId = @S AND AreaId = @A", ("@S", species), ("@A", MainOfficeAreaId));
            if (seedStockId == 0)
            {
                await env.ExecAsync(
                    "INSERT INTO dbo.SeedStock (SpeciesId, AreaId, PhysicalQuantity, CreatedDate, CreatedBy) VALUES (@S, @A, 0, SYSUTCDATETIME(), N'tray-test')",
                    ("@S", species), ("@A", MainOfficeAreaId));
                seedStockId = await env.ScalarAsync<int>("SELECT TOP 1 Id FROM dbo.SeedStock WHERE SpeciesId = @S AND AreaId = @A", ("@S", species), ("@A", MainOfficeAreaId));
            }
            return new Env { ConnectionString = cs!, Provider = provider, Species = species, SeedStockId = seedStockId };
        }

        private static SeedSowing SeedSowingInto(Env env, int areaId, int? polyhouseId, decimal quantity, string traySize = "24 Cavity") => new()
        {
            SourceSeedStockId = env.SeedStockId, SeedQuantity = quantity, CavityType = traySize, SowingDate = DateTime.Today,
            AreaId = areaId, PolyhouseId = polyhouseId, SupervisorId = SupervisorId, CreatedBy = "tray-test", CreatedById = Recorder
        };

        private static SeedSowing CuttingSowingInto(int cuttingPool, int areaId, int polyhouseId, decimal quantity, string traySize = "24 Cavity") => new()
        {
            SourceCuttingStockId = cuttingPool, SeedQuantity = quantity, CavityType = traySize, SowingDate = DateTime.Today,
            AreaId = areaId, PolyhouseId = polyhouseId, SupervisorId = SupervisorId, CreatedBy = "tray-test", CreatedById = Recorder
        };

        private static async Task<int> SowCuttingAsync(Env env, int cuttingPool, int areaId, int polyhouseId, decimal quantity, string traySize = "24 Cavity")
        {
            var (ok, message, id) = await env.Get<SeedSowingRepository>().InsertFromCuttingAsync(CuttingSowingInto(cuttingPool, areaId, polyhouseId, quantity, traySize), Recorder, _ => true, _ => true);
            Assert.True(ok, message);
            return id;
        }

        // ---- Add Tray Stock -------------------------------------------------------------------------------

        [SkippableFact]
        public async Task AddStock_CreatesTheAreaPolyhouseCavityPool_WithALedgerRow_InUtc_AndASecondAdditionGoesToTheSamePool()
        {
            var env = await OpenAsync();
            await env.SetPoolAsync(GreenBlessAreaId, GreenBlessP2, "42 Cavity", 0);
            var repo = env.Get<TrayStockRepository>();
            var whenLocal = new DateTime(2026, 10, 4, 8, 15, 0, DateTimeKind.Local);

            var first = await repo.AddStockAsync(GreenBlessAreaId, GreenBlessP2, "42 Cavity", 100, whenLocal.ToUniversalTime(), 424242, SupervisorId, "t", "new trays", id => id == GreenBlessAreaId);
            var second = await repo.AddStockAsync(GreenBlessAreaId, GreenBlessP2, "42 Cavity", 25, DateTime.UtcNow, 424243, SupervisorId, "t", null, id => id == GreenBlessAreaId);

            Assert.True(first.Success, first.Message);
            Assert.True(second.Success, second.Message);
            Assert.Equal(125m, second.Balance);
            var pool = await env.PoolIdAsync(GreenBlessAreaId, GreenBlessP2, "42 Cavity");
            Assert.Equal(125m, await env.TrayPhysicalAsync(pool));
            Assert.Equal(1, await env.ScalarAsync<int>("SELECT COUNT(*) FROM dbo.TrayStock WHERE AreaId = @A AND PolyhouseId = @P AND TraySize = '42 Cavity'", ("@A", GreenBlessAreaId), ("@P", GreenBlessP2)));
            Assert.Equal(whenLocal.ToUniversalTime(), await env.ScalarAsync<DateTime>(
                "SELECT TransactionDate FROM dbo.TrayStockTransactions WHERE TrayStockId = @T AND ReferenceType = 'TrayStockAdd' AND ReferenceId = 424242", ("@T", pool)));
            Assert.Equal(0, await env.InvariantViolationsAsync());
        }

        [SkippableFact]
        public async Task AddStock_SameCavityInTwoPolyhouses_StaysSeparate()
        {
            var env = await OpenAsync();
            var p2 = await env.SetPoolAsync(GreenBlessAreaId, GreenBlessP2, "24 Cavity", 100);
            var p1 = await env.SetPoolAsync(GreenBlessAreaId, GreenBlessP1, "24 Cavity", 50);

            Assert.NotEqual(p1, p2);
            Assert.Equal(100m, await env.TrayPhysicalAsync(p2));
            Assert.Equal(50m, await env.TrayPhysicalAsync(p1));
        }

        [SkippableFact]
        public async Task AddStock_UnauthorizedArea_IsRejected_NothingWritten()
        {
            var env = await OpenAsync();
            var pool = await env.SetPoolAsync(GreenBlessAreaId, GreenBlessP1, "9 Cavity", 5);

            // The user may only access Main Office; a tampered AreaId/PolyhouseId of Green Bless is refused.
            var (ok, msg, _, _) = await env.Get<TrayStockRepository>().AddStockAsync(
                GreenBlessAreaId, GreenBlessP1, "9 Cavity", 100, DateTime.UtcNow, 77, SupervisorId, "t", null, id => id == MainOfficeAreaId);

            Assert.False(ok);
            Assert.Contains("not authorized", msg, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(5m, await env.TrayPhysicalAsync(pool));
        }

        [SkippableTheory]
        [InlineData(GreenBlessAreaId, SamarthP1)]   // authorized Area + a Polyhouse of another Area
        [InlineData(MainOfficeAreaId, GreenBlessP1)] // authorized Area + a Polyhouse of an unauthorized Area
        [InlineData(GreenBlessAreaId, 999999)]      // a Polyhouse that does not exist
        public async Task AddStock_PolyhouseNotInTheArea_IsRejected_NothingWritten(int areaId, int polyhouseId)
        {
            var env = await OpenAsync();
            var poolsBefore = await env.ScalarAsync<int>("SELECT COUNT(*) FROM dbo.TrayStock");
            var txBefore = await env.ScalarAsync<int>("SELECT COUNT(*) FROM dbo.TrayStockTransactions");

            var (ok, msg, _, _) = await env.Get<TrayStockRepository>().AddStockAsync(
                areaId, polyhouseId, "24 Cavity", 100, DateTime.UtcNow, 5150, SupervisorId, "t", null, _ => true);

            Assert.False(ok);
            Assert.True(msg!.Contains("does not belong", StringComparison.OrdinalIgnoreCase) || msg.Contains("does not exist", StringComparison.OrdinalIgnoreCase), msg);
            Assert.Equal(poolsBefore, await env.ScalarAsync<int>("SELECT COUNT(*) FROM dbo.TrayStock"));
            Assert.Equal(txBefore, await env.ScalarAsync<int>("SELECT COUNT(*) FROM dbo.TrayStockTransactions"));
        }

        [SkippableFact]
        public async Task AddStock_OutletArea_FractionalQuantity_UnknownCavity_AndMissingPolyhouse_AreRejected()
        {
            var env = await OpenAsync();
            var repo = env.Get<TrayStockRepository>();
            Assert.False((await repo.AddStockAsync(OutletAreaId, OutletPolyhouse, "24 Cavity", 10, DateTime.UtcNow, 11, SupervisorId, "t", null, _ => true)).Success);
            Assert.False((await repo.AddStockAsync(GreenBlessAreaId, GreenBlessP1, "24 Cavity", 10.5m, DateTime.UtcNow, 12, SupervisorId, "t", null, _ => true)).Success);
            Assert.False((await repo.AddStockAsync(GreenBlessAreaId, GreenBlessP1, "50 Cavity", 10, DateTime.UtcNow, 13, SupervisorId, "t", null, _ => true)).Success);
            Assert.False((await repo.AddStockAsync(GreenBlessAreaId, 0, "24 Cavity", 10, DateTime.UtcNow, 14, SupervisorId, "t", null, _ => true)).Success);
            Assert.Equal(0, await env.ScalarAsync<int>("SELECT COUNT(*) FROM dbo.TrayStock WHERE AreaId = @A", ("@A", OutletAreaId)));
        }

        [SkippableFact]
        public async Task AddStock_SameFormSubmittedTwice_AddsOnlyOnce()
        {
            var env = await OpenAsync();
            await env.SetPoolAsync(GreenBlessAreaId, GreenBlessNetHouse, "150 Cavity", 0);
            var repo = env.Get<TrayStockRepository>();
            const int token = 98765;

            var first = await repo.AddStockAsync(GreenBlessAreaId, GreenBlessNetHouse, "150 Cavity", 40, DateTime.UtcNow, token, SupervisorId, "t", null, _ => true);
            var second = await repo.AddStockAsync(GreenBlessAreaId, GreenBlessNetHouse, "150 Cavity", 40, DateTime.UtcNow, token, SupervisorId, "t", null, _ => true);

            Assert.True(first.Success, first.Message);
            Assert.True(second.Success, second.Message);
            Assert.False(first.Duplicate);
            Assert.True(second.Duplicate);
            Assert.Equal(40m, await env.TrayPhysicalAsync(await env.PoolIdAsync(GreenBlessAreaId, GreenBlessNetHouse, "150 Cavity")));
        }

        [SkippableFact]
        public async Task RetiredAreaLevelPool_CanNeverBeUsedAgain()
        {
            var env = await OpenAsync();
            var areaPool = await env.ScalarAsync<int>("SELECT TOP 1 Id FROM dbo.TrayStock WHERE PolyhouseId IS NULL AND IsActive = 0");
            Skip.If(areaPool == 0, "No retired Area-level pool in this copy.");

            using var conn = new SqlConnection(env.ConnectionString);
            await conn.OpenAsync();
            using var tx = conn.BeginTransaction();
            var (ok, msg) = await env.Get<TrayStockRepository>().RecordTransactionAsync(conn, tx, areaPool, 10, "Allocation", null, null, SupervisorId, "should fail");
            tx.Rollback();
            Assert.False(ok);
            Assert.Contains("retired", msg, StringComparison.OrdinalIgnoreCase);

            // ...and the database itself refuses to re-activate it.
            await Assert.ThrowsAsync<SqlException>(() => env.ExecAsync("UPDATE dbo.TrayStock SET IsActive = 1 WHERE Id = @Id", ("@Id", areaPool)));
        }

        // ---- Sowing: the Green Bless Nursery example ------------------------------------------------------

        [SkippableFact]
        public async Task SeedSowing_P2_100Trays_Sow950At24_Consumes40_FromP2Only()
        {
            var env = await OpenAsync();
            await env.ResetSeedPoolAsync(5000);
            var p2 = await env.SetPoolAsync(GreenBlessAreaId, GreenBlessP2, "24 Cavity", 100);
            var p1 = await env.SetPoolAsync(GreenBlessAreaId, GreenBlessP1, "24 Cavity", 50);

            var sowing = SeedSowingInto(env, GreenBlessAreaId, GreenBlessP2, 950);
            var (ok, msg, sowingId) = await env.Get<SeedSowingRepository>().InsertAsync(sowing, Recorder, _ => true);

            Assert.True(ok, msg);
            Assert.Equal(39, sowing.NumberOfTrays!.Value);           // complete trays (FLOOR) -- unchanged rule
            Assert.Equal(60m, await env.TrayPhysicalAsync(p2));      // CEILING(950/24) = 40 physical trays
            Assert.Equal(50m, await env.TrayPhysicalAsync(p1));      // the other Polyhouse is untouched
            Assert.Equal(-40m, await env.TrayNetForReferenceInPoolAsync(p2, "SeedSowing", sowingId));
            Assert.Equal(0, await env.InvariantViolationsAsync());
        }

        [SkippableFact]
        public async Task SeedSowing_Only30Trays_Sow950At24_IsBlocked_NothingCreated()
        {
            var env = await OpenAsync();
            await env.ResetSeedPoolAsync(5000);
            var pool = await env.SetPoolAsync(GreenBlessAreaId, GreenBlessP2, "24 Cavity", 30);
            var sowingsBefore = await env.ScalarAsync<int>("SELECT COUNT(*) FROM dbo.SeedSowings");
            var seedBefore = await env.ScalarAsync<decimal>("SELECT PhysicalQuantity FROM dbo.SeedStock WHERE Id = @Id", ("@Id", env.SeedStockId));

            var (ok, msg, sowingId) = await env.Get<SeedSowingRepository>().InsertAsync(
                SeedSowingInto(env, GreenBlessAreaId, GreenBlessP2, 950), Recorder, _ => true);

            Assert.False(ok);
            Assert.Equal(0, sowingId);
            Assert.Contains("Insufficient tray stock for this Area and Polyhouse", msg);
            Assert.Contains("Available: 30", msg);
            Assert.Contains("Required: 40", msg);
            Assert.Equal(sowingsBefore, await env.ScalarAsync<int>("SELECT COUNT(*) FROM dbo.SeedSowings"));
            Assert.Equal(seedBefore, await env.ScalarAsync<decimal>("SELECT PhysicalQuantity FROM dbo.SeedStock WHERE Id = @Id", ("@Id", env.SeedStockId)));
            Assert.Equal(30m, await env.TrayPhysicalAsync(pool));
            Assert.Equal(0, await env.InvariantViolationsAsync());
        }

        [SkippableFact]
        public async Task SeedSowing_WithoutPolyhouse_IsRejected_NothingCreated()
        {
            var env = await OpenAsync();
            await env.ResetSeedPoolAsync(5000);
            var sowingsBefore = await env.ScalarAsync<int>("SELECT COUNT(*) FROM dbo.SeedSowings");
            var seedBefore = await env.ScalarAsync<decimal>("SELECT PhysicalQuantity FROM dbo.SeedStock WHERE Id = @Id", ("@Id", env.SeedStockId));

            var (ok, msg, _) = await env.Get<SeedSowingRepository>().InsertAsync(SeedSowingInto(env, GreenBlessAreaId, null, 480), Recorder, _ => true);

            Assert.False(ok);
            Assert.Equal(TrayStockRepository.PolyhouseRequiredMessage, msg);
            Assert.Equal(sowingsBefore, await env.ScalarAsync<int>("SELECT COUNT(*) FROM dbo.SeedSowings"));
            Assert.Equal(seedBefore, await env.ScalarAsync<decimal>("SELECT PhysicalQuantity FROM dbo.SeedStock WHERE Id = @Id", ("@Id", env.SeedStockId)));
        }

        [SkippableFact]
        public async Task SeedSowing_PolyhouseOfAnotherArea_IsRejected_NothingCreated()
        {
            var env = await OpenAsync();
            await env.ResetSeedPoolAsync(5000);
            var sowingsBefore = await env.ScalarAsync<int>("SELECT COUNT(*) FROM dbo.SeedSowings");

            var (ok, msg, _) = await env.Get<SeedSowingRepository>().InsertAsync(SeedSowingInto(env, GreenBlessAreaId, SamarthP1, 480), Recorder, _ => true);

            Assert.False(ok);
            Assert.Contains("different Area", msg);
            Assert.Equal(sowingsBefore, await env.ScalarAsync<int>("SELECT COUNT(*) FROM dbo.SeedSowings"));
        }

        [SkippableFact]
        public async Task CuttingSowing_ConsumesFromItsDestinationPolyhouse_Only()
        {
            var env = await OpenAsync();
            var cuttingPool = await env.ResetCuttingPoolAsync(AreaA, 950);
            var p2 = await env.SetPoolAsync(GreenBlessAreaId, GreenBlessP2, "24 Cavity", 100);
            var p1 = await env.SetPoolAsync(GreenBlessAreaId, GreenBlessP1, "24 Cavity", 50);

            var sowingId = await SowCuttingAsync(env, cuttingPool, GreenBlessAreaId, GreenBlessP2, 950);

            Assert.Equal(60m, await env.TrayPhysicalAsync(p2));
            Assert.Equal(50m, await env.TrayPhysicalAsync(p1));
            Assert.Equal(-40m, await env.TrayNetForReferenceInPoolAsync(p2, "SeedSowing", sowingId));
        }

        [SkippableFact]
        public async Task CuttingSowing_InsufficientTrays_IsBlocked_CuttingStockUntouched()
        {
            var env = await OpenAsync();
            var cuttingPool = await env.ResetCuttingPoolAsync(AreaA, 950);
            var pool = await env.SetPoolAsync(GreenBlessAreaId, GreenBlessP2, "24 Cavity", 30);

            var (ok, msg, _) = await env.Get<SeedSowingRepository>().InsertFromCuttingAsync(CuttingSowingInto(cuttingPool, GreenBlessAreaId, GreenBlessP2, 950), Recorder, _ => true, _ => true);

            Assert.False(ok);
            Assert.Contains("Insufficient tray stock for this Area and Polyhouse", msg);
            Assert.Contains("Available: 30", msg);
            Assert.Equal(950m, await env.ScalarAsync<decimal>("SELECT PhysicalQuantity FROM dbo.CuttingStock WHERE Id = @Id", ("@Id", cuttingPool)));
            Assert.Equal(30m, await env.TrayPhysicalAsync(pool));
        }

        // ---- Cancellation / reversal ----------------------------------------------------------------------

        [SkippableFact]
        public async Task CancelAsync_ReturnsExactlyTheConsumedTrays_ToTheSamePolyhousePool_AndOnlyOnce()
        {
            var env = await OpenAsync();
            await env.ResetSeedPoolAsync(5000);
            var p2 = await env.SetPoolAsync(GreenBlessAreaId, GreenBlessP2, "24 Cavity", 100);
            var p1 = await env.SetPoolAsync(GreenBlessAreaId, GreenBlessP1, "24 Cavity", 50);
            var (ok, msg, sowingId) = await env.Get<SeedSowingRepository>().InsertAsync(SeedSowingInto(env, GreenBlessAreaId, GreenBlessP2, 950), Recorder, _ => true);
            Assert.True(ok, msg);
            Assert.Equal(60m, await env.TrayPhysicalAsync(p2));

            var (c1, m1) = await env.Get<SeedSowingRepository>().CancelAsync(sowingId, "tray-test", SupervisorId);
            var (c2, m2) = await env.Get<SeedSowingRepository>().CancelAsync(sowingId, "tray-test", SupervisorId);

            Assert.True(c1, m1);
            Assert.False(c2);
            Assert.Contains("already", m2, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(100m, await env.TrayPhysicalAsync(p2));   // back to the same Polyhouse
            Assert.Equal(50m, await env.TrayPhysicalAsync(p1));    // the other Polyhouse never touched
            Assert.Equal(0m, await env.TrayNetForReferenceInPoolAsync(p2, "SeedSowing", sowingId));
            Assert.Equal(0, await env.InvariantViolationsAsync());
        }

        [SkippableFact]
        public async Task CancelAsync_SowingRecordedBeforeTheChange_ReturnsTraysToItsOriginalPolyhousePool()
        {
            var env = await OpenAsync();
            // A sowing recorded before 2026-10-04 that consumed from a Polyhouse pool and can still be cancelled.
            var sowingId = await env.ScalarAsync<int>(@"
SELECT TOP 1 sw.Id FROM dbo.SeedSowings sw
WHERE sw.Status = 'Sown' AND sw.ConfirmedReadyQuantity = 0 AND sw.WastageQuantity = 0
  AND (SELECT ISNULL(SUM(tr.Quantity), 0) FROM dbo.TrayStockTransactions tr WHERE tr.ReferenceType = 'SeedSowing' AND tr.ReferenceId = sw.Id) < 0
  AND sw.CreatedDate < '2026-10-04'
ORDER BY sw.Id");
            Skip.If(sowingId == 0, "No cancellable pre-2026-10-04 sowing with a tray consumption in this copy.");
            var pool = await env.ScalarAsync<int>("SELECT TOP 1 TrayStockId FROM dbo.TrayStockTransactions WHERE ReferenceType = 'SeedSowing' AND ReferenceId = @Id AND TransactionType = 'Sowing'", ("@Id", sowingId));
            var consumed = -await env.TrayNetForReferenceAsync("SeedSowing", sowingId);
            var before = await env.TrayPhysicalAsync(pool);

            var (ok, msg) = await env.Get<SeedSowingRepository>().CancelAsync(sowingId, "tray-test", SupervisorId);

            Assert.True(ok, msg);
            Assert.Equal(before + consumed, await env.TrayPhysicalAsync(pool));   // the exact pool it came from
            Assert.Equal(0m, await env.TrayNetForReferenceAsync("SeedSowing", sowingId));
            Assert.Equal(0, await env.InvariantViolationsAsync());
        }

        // ---- Ready Confirmation (Cutting overage) ---------------------------------------------------------

        [SkippableFact]
        public async Task ConfirmAsync_CuttingSowing_NoOverage_NoExtraTrayConsumption()
        {
            var env = await OpenAsync();
            var cuttingPool = await env.ResetCuttingPoolAsync(AreaA, 480);
            var pool = await env.SetPoolAsync(GreenBlessAreaId, GreenBlessP2, "24 Cavity", 100);
            var sowingId = await SowCuttingAsync(env, cuttingPool, GreenBlessAreaId, GreenBlessP2, 480);   // 20 trays
            Assert.Equal(80m, await env.TrayPhysicalAsync(pool));

            var (ok, message, confirmationId) = await env.Get<ReadyConfirmationRepository>().ConfirmAsync(
                sowingId, 20, null, null, "tray-test", "tray-test", SupervisorId, _ => true);
            Assert.True(ok, message);

            Assert.Equal(80m, await env.TrayPhysicalAsync(pool));
            Assert.Equal(0m, await env.TrayNetForReferenceAsync("ReadyConfirmation", confirmationId));
        }

        [SkippableFact]
        public async Task ConfirmAsync_CuttingOverage_TakesTheExtraTraysFromTheSamePolyhouse_AndCancelReturnsThemThere()
        {
            var env = await OpenAsync();
            var cuttingPool = await env.ResetCuttingPoolAsync(AreaA, 480);
            var p2 = await env.SetPoolAsync(GreenBlessAreaId, GreenBlessP2, "24 Cavity", 100);
            var p1 = await env.SetPoolAsync(GreenBlessAreaId, GreenBlessP1, "24 Cavity", 50);
            var sowingId = await SowCuttingAsync(env, cuttingPool, GreenBlessAreaId, GreenBlessP2, 480);   // 20 trays -> P-2 80
            await env.ExecAsync("UPDATE dbo.CuttingStock SET PhysicalQuantity = 3000 WHERE Id = @Id", ("@Id", cuttingPool));   // scratch: cuttings for the overage

            var (ok, message, confirmationId) = await env.Get<ReadyConfirmationRepository>().ConfirmAsync(
                sowingId, 30, null, null, "tray-test", "tray-test", SupervisorId, _ => true);   // 10 extra trays
            Assert.True(ok, message);
            Assert.Equal(70m, await env.TrayPhysicalAsync(p2));
            Assert.Equal(50m, await env.TrayPhysicalAsync(p1));
            Assert.Equal(-10m, await env.TrayNetForReferenceInPoolAsync(p2, "ReadyConfirmation", confirmationId));
            Assert.Equal(-20m, await env.TrayNetForReferenceAsync("SeedSowing", sowingId));   // the sowing's own trays are never taken twice

            var (cancelOk, cancelMessage) = await env.Get<ReadyConfirmationRepository>().CancelAsync(confirmationId, "tray-test", SupervisorId);
            Assert.True(cancelOk, cancelMessage);
            Assert.Equal(80m, await env.TrayPhysicalAsync(p2));
            Assert.Equal(50m, await env.TrayPhysicalAsync(p1));
            Assert.Equal(0m, await env.TrayNetForReferenceAsync("ReadyConfirmation", confirmationId));
        }

        [SkippableFact]
        public async Task ConfirmAsync_InsufficientTrayStockForOverage_RejectsAndRollsBackEverything()
        {
            var env = await OpenAsync();
            var cuttingPool = await env.ResetCuttingPoolAsync(AreaA, 480);
            var pool = await env.SetPoolAsync(GreenBlessAreaId, GreenBlessP2, "24 Cavity", 20);
            var sowingId = await SowCuttingAsync(env, cuttingPool, GreenBlessAreaId, GreenBlessP2, 480);   // uses all 20
            Assert.Equal(0m, await env.TrayPhysicalAsync(pool));
            await env.ExecAsync("UPDATE dbo.CuttingStock SET PhysicalQuantity = 3000 WHERE Id = @Id", ("@Id", cuttingPool));
            var cuttingBefore = await env.ScalarAsync<decimal>("SELECT PhysicalQuantity FROM dbo.CuttingStock WHERE Id = @Id", ("@Id", cuttingPool));

            var (ok, message, confirmationId) = await env.Get<ReadyConfirmationRepository>().ConfirmAsync(
                sowingId, 25, null, null, "tray-test", "tray-test", SupervisorId, _ => true);   // needs 5 extra; 0 available

            Assert.False(ok);
            Assert.Contains("Insufficient", message, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(0, confirmationId);
            Assert.Equal(0, await env.ScalarAsync<int>("SELECT COUNT(*) FROM dbo.ReadyConfirmations WHERE SeedSowingId = @Id", ("@Id", sowingId)));
            Assert.Equal(cuttingBefore, await env.ScalarAsync<decimal>("SELECT PhysicalQuantity FROM dbo.CuttingStock WHERE Id = @Id", ("@Id", cuttingPool)));
            Assert.Equal(0m, await env.TrayPhysicalAsync(pool));
            Assert.Equal("Sown", await env.ScalarAsync<string>("SELECT Status FROM dbo.SeedSowings WHERE Id = @Id", ("@Id", sowingId)));
        }

        [SkippableFact]
        public async Task ConfirmAsync_SeedSourceOverage_NeverConsumesTrayStock()
        {
            var env = await OpenAsync();
            await env.ResetSeedPoolAsync(10000);
            var pool = await env.SetPoolAsync(GreenBlessAreaId, GreenBlessP2, "24 Cavity", 100);
            var (sowOk, sowMsg, sowingId) = await env.Get<SeedSowingRepository>().InsertAsync(SeedSowingInto(env, GreenBlessAreaId, GreenBlessP2, 480), Recorder, _ => true);
            Assert.True(sowOk, sowMsg);

            var (ok, message, confirmationId) = await env.Get<ReadyConfirmationRepository>().ConfirmAsync(
                sowingId, 25, null, null, "tray-test", "tray-test", SupervisorId, _ => true);
            Assert.True(ok, message);

            Assert.Equal(80m, await env.TrayPhysicalAsync(pool));
            Assert.Equal(0m, await env.TrayNetForReferenceAsync("ReadyConfirmation", confirmationId));
        }

        // ---- Concurrency ----------------------------------------------------------------------------------

        [SkippableFact]
        public async Task TwoSimultaneousSowings_30Each_Against50_ExactlyOneSucceeds_NeverNegative()
        {
            var env = await OpenAsync();
            await env.ResetSeedPoolAsync(5000);
            var pool = await env.SetPoolAsync(GreenBlessAreaId, GreenBlessP2, "24 Cavity", 50);

            using var scopeA = env.Provider.CreateScope();
            using var scopeB = env.Provider.CreateScope();
            var taskA = scopeA.ServiceProvider.GetRequiredService<SeedSowingRepository>().InsertAsync(SeedSowingInto(env, GreenBlessAreaId, GreenBlessP2, 720), Recorder, _ => true);
            var taskB = scopeB.ServiceProvider.GetRequiredService<SeedSowingRepository>().InsertAsync(SeedSowingInto(env, GreenBlessAreaId, GreenBlessP2, 720), Recorder, _ => true);
            var results = await Task.WhenAll(taskA, taskB);   // 720/24 = 30 trays each

            Assert.Equal(1, results.Count(r => r.Success));
            var failed = results.Single(r => !r.Success);
            Assert.Contains("Available: 20", failed.Message);
            Assert.Contains("Required: 30", failed.Message);
            Assert.Equal(20m, await env.TrayPhysicalAsync(pool));
            Assert.Equal(0, await env.InvariantViolationsAsync());
        }

        // ---- Admin > Polyhouse: Area change guard ---------------------------------------------------------

        [SkippableFact]
        public async Task PolyhouseWithTrays_CannotMoveToAnotherArea_NothingChanges()
        {
            var env = await OpenAsync();
            await env.SetPoolAsync(GreenBlessAreaId, GreenBlessNetHouse, "24 Cavity", 15);
            var name = await env.ScalarAsync<string>("SELECT Name FROM dbo.Polyhouses WHERE Id = @P", ("@P", GreenBlessNetHouse));

            var (ok, msg) = await env.Get<PolyhouseRepository>().UpdatePolyhouse(GreenBlessNetHouse, name, SamarthAreaId);

            Assert.False(ok);
            Assert.Equal(PolyhouseRepository.MoveBlockedByTrayStockMessage, msg);
            Assert.Equal(GreenBlessAreaId, await env.ScalarAsync<int>("SELECT AreaId FROM dbo.Polyhouses WHERE Id = @P", ("@P", GreenBlessNetHouse)));
            // Renaming without changing the Area stays allowed.
            var (renameOk, renameMsg) = await env.Get<PolyhouseRepository>().UpdatePolyhouse(GreenBlessNetHouse, name, GreenBlessAreaId);
            Assert.True(renameOk, renameMsg);
        }

        [SkippableFact]
        public async Task PolyhouseWithZeroTrays_CanMoveArea_EmptyPoolsRetired_AndReactivatedWhenItMovesBack()
        {
            var env = await OpenAsync();
            await env.SetPoolAsync(GreenBlessAreaId, GreenBlessNetHouse, "24 Cavity", 0);
            await env.ExecAsync("UPDATE dbo.TrayStock SET PhysicalQuantity = 0 WHERE PolyhouseId = @P", ("@P", GreenBlessNetHouse));
            await env.ExecAsync("DELETE tr FROM dbo.TrayStockTransactions tr JOIN dbo.TrayStock t ON t.Id = tr.TrayStockId WHERE t.PolyhouseId = @P", ("@P", GreenBlessNetHouse));
            var repo = env.Get<PolyhouseRepository>();
            var name = await env.ScalarAsync<string>("SELECT Name FROM dbo.Polyhouses WHERE Id = @P", ("@P", GreenBlessNetHouse));

            var (moved, msg) = await repo.UpdatePolyhouse(GreenBlessNetHouse, name, SamarthAreaId);
            Assert.True(moved, msg);
            Assert.Equal(0, await env.ScalarAsync<int>("SELECT COUNT(*) FROM dbo.TrayStock WHERE PolyhouseId = @P AND IsActive = 1", ("@P", GreenBlessNetHouse)));

            try
            {
                // Back to Green Bless: adding trays there re-activates the same pool row.
                var (back, backMsg) = await repo.UpdatePolyhouse(GreenBlessNetHouse, name, GreenBlessAreaId);
                Assert.True(back, backMsg);
                var poolBefore = await env.PoolIdAsync(GreenBlessAreaId, GreenBlessNetHouse, "24 Cavity");
                var pool = await env.SetPoolAsync(GreenBlessAreaId, GreenBlessNetHouse, "24 Cavity", 5);
                Assert.Equal(poolBefore, pool);
                Assert.Equal(5m, await env.TrayPhysicalAsync(pool));
                Assert.Equal(0, await env.InvariantViolationsAsync());
            }
            finally
            {
                await env.ExecAsync("UPDATE dbo.Polyhouses SET AreaId = @A WHERE Id = @P", ("@A", GreenBlessAreaId), ("@P", GreenBlessNetHouse));
            }
        }
    }
}
