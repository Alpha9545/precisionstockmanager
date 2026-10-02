using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using PlantStockManager.Authorization;
using PlantStockManager.Data;
using PlantStockManager.Models;

namespace PlantStockManager.Tests
{
    // R1 + R2 regression tests (2026-10-02 production-readiness fixes):
    //   R1 -- SeedSowingRepository.CancelAsync must reverse Tray Stock.
    //   R2 -- ReadyConfirmationRepository.ConfirmAsync/CancelAsync must
    //         consume/return the extra physical trays for a Cutting Sowing
    //         overage approval into a Main Office Polyhouse.
    //
    // Same safety rules as CuttingSowingOverageE2ETests (which this file's
    // Env/OpenAsync pattern mirrors exactly): CancelAsync/ConfirmAsync each
    // open and COMMIT their own SqlTransaction internally, so they cannot
    // be wrapped in one external rolled-back transaction the way a plain
    // repository-primitive test can. Runs only against a database named
    // PlantsIMS2_Scratch_*, refuses every other database, and is SKIPPED
    // otherwise. Shares the "ScratchDb" collection so it never runs at the
    // same time as the other ScratchDb E2E suites.
    [Collection("ScratchDb")]
    public class TrayStockReversalTests
    {
        private const string ConnectionVariable = "PSM_SCRATCH_CONNECTION";
        private const string RequiredPrefix = "PlantsIMS2_Scratch_";

        // Facility-5 under Main-Office-Areas -- the same real Polyhouse
        // used throughout this session's own live verification.
        private const int MainOfficeAreaId = 2;
        private const int MainOfficePolyhouseId = 2;
        private const int MainOfficeSupervisorId = 9;   // Maya Shinde -- proven valid for AreaId=2 this session
        private const int Cavity = 24;

        // Mother Plant 114 / Area 1 -- the same growing-side fixture
        // CuttingSowingOverageE2ETests already relies on, for the Cutting
        // Stock SOURCE pool (independent of the sowing's own destination
        // Area/Polyhouse, exactly like the real Mother-Plant-to-Main-Office
        // flow).
        private const int MpA = 114, SupA = 115, AreaA = 1, Recorder = 21;

        // Minimal IOptionsMonitor<T> stand-in -- same helper
        // CuttingSowingOverageE2ETests already uses for the identical DI gap.
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
    INSERT INTO dbo.CuttingStock (SpeciesId, AreaId, PhysicalQuantity, CreatedDate, CreatedBy) VALUES (@S, @A, 0, SYSUTCDATETIME(), N'tray-reversal-test');
UPDATE dbo.CuttingStock SET InTransitQuantity = 0, PhysicalQuantity = @P WHERE SpeciesId = @S AND AreaId = @A", ("@S", Species), ("@A", area), ("@P", physical));
                var id = await ScalarAsync<int>("SELECT Id FROM dbo.CuttingStock WHERE SpeciesId = @S AND AreaId = @A", ("@S", Species), ("@A", area));
                await ExecAsync("DELETE FROM dbo.CuttingStockTransactions WHERE CuttingStockId = @P", ("@P", id));
                return id;
            }

            public async Task ResetSeedPoolAsync(decimal physical)
                => await ExecAsync("UPDATE dbo.SeedStock SET InTransitQuantity = 0, PhysicalQuantity = @P WHERE Id = @Id", ("@P", physical), ("@Id", SeedStockId));

            // (Re)sets a Tray Stock pool to a known quantity with a clean
            // ledger, exactly like ResetCuttingPoolAsync -- scratch copy only.
            public async Task<int> ResetTrayPoolAsync(int polyhouseId, string traySize, decimal physical)
            {
                await ExecAsync(@"
IF NOT EXISTS (SELECT 1 FROM dbo.TrayStock WHERE PolyhouseId = @P AND TraySize = @T)
    INSERT INTO dbo.TrayStock (PolyhouseId, TraySize, PhysicalQuantity, IsActive, CreatedDate, CreatedBy) VALUES (@P, @T, 0, 1, SYSUTCDATETIME(), N'tray-reversal-test');
UPDATE dbo.TrayStock SET PhysicalQuantity = @Q WHERE PolyhouseId = @P AND TraySize = @T", ("@P", polyhouseId), ("@T", traySize), ("@Q", physical));
                var id = await ScalarAsync<int>("SELECT Id FROM dbo.TrayStock WHERE PolyhouseId = @P AND TraySize = @T", ("@P", polyhouseId), ("@T", traySize));
                await ExecAsync("DELETE FROM dbo.TrayStockTransactions WHERE TrayStockId = @Id", ("@Id", id));
                return id;
            }

            public Task<decimal> TrayPhysicalAsync(int trayStockId) => ScalarAsync<decimal>("SELECT PhysicalQuantity FROM dbo.TrayStock WHERE Id = @Id", ("@Id", trayStockId));

            public Task<int> TrayLedgerRowCountAsync(int trayStockId, string referenceType, int referenceId)
                => ScalarAsync<int>("SELECT COUNT(*) FROM dbo.TrayStockTransactions WHERE TrayStockId = @T AND ReferenceType = @RT AND ReferenceId = @RI",
                    ("@T", trayStockId), ("@RT", referenceType), ("@RI", referenceId));

            public Task<decimal> TrayNetForReferenceAsync(int trayStockId, string referenceType, int referenceId)
                => ScalarAsync<decimal>("SELECT ISNULL(SUM(Quantity), 0) FROM dbo.TrayStockTransactions WHERE TrayStockId = @T AND ReferenceType = @RT AND ReferenceId = @RI",
                    ("@T", trayStockId), ("@RT", referenceType), ("@RI", referenceId));
        }

        private static async Task<Env> OpenAsync()
        {
            var cs = Environment.GetEnvironmentVariable(ConnectionVariable);
            Skip.If(string.IsNullOrWhiteSpace(cs), $"Set {ConnectionVariable} to a {RequiredPrefix}* database connection string to run the Tray Stock reversal tests.");

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
            var seedStockId = await env.ScalarAsync<int>(
                "SELECT TOP 1 Id FROM dbo.SeedStock WHERE SpeciesId = @S AND AreaId = @A", ("@S", species), ("@A", MainOfficeAreaId));
            if (seedStockId == 0)
            {
                await env.ExecAsync(
                    "INSERT INTO dbo.SeedStock (SpeciesId, AreaId, PhysicalQuantity, CreatedDate, CreatedBy) VALUES (@S, @A, 0, SYSUTCDATETIME(), N'tray-reversal-test')",
                    ("@S", species), ("@A", MainOfficeAreaId));
                seedStockId = await env.ScalarAsync<int>("SELECT TOP 1 Id FROM dbo.SeedStock WHERE SpeciesId = @S AND AreaId = @A", ("@S", species), ("@A", MainOfficeAreaId));
            }
            return new Env { ConnectionString = cs!, Provider = provider, Species = species, SeedStockId = seedStockId };
        }

        private static async Task<(int SowingId, int TrayStockId)> SowSeedIntoMainOfficeAsync(Env env, decimal quantity, string traySize = "24 Cavity")
        {
            var trayStockId = await env.ResetTrayPoolAsync(MainOfficePolyhouseId, traySize, 2000);
            var sowing = new SeedSowing
            {
                SourceSeedStockId = env.SeedStockId, SeedQuantity = quantity, CavityType = traySize, SowingDate = DateTime.Today,
                AreaId = MainOfficeAreaId, PolyhouseId = MainOfficePolyhouseId, SupervisorId = MainOfficeSupervisorId,
                CreatedBy = "tray-reversal-test", CreatedById = MainOfficeSupervisorId
            };
            var (ok, message, id) = await env.Get<SeedSowingRepository>().InsertAsync(sowing, MainOfficeSupervisorId, _ => true);
            Assert.True(ok, message);
            return (id, trayStockId);
        }

        private static async Task<int> SowCuttingIntoMainOfficeAsync(Env env, int cuttingPool, decimal quantity, string traySize = "24 Cavity")
        {
            var sowing = new SeedSowing
            {
                SourceCuttingStockId = cuttingPool, SeedQuantity = quantity, CavityType = traySize, SowingDate = DateTime.Today,
                AreaId = MainOfficeAreaId, PolyhouseId = MainOfficePolyhouseId, SupervisorId = MainOfficeSupervisorId,
                CreatedBy = "tray-reversal-test", CreatedById = Recorder
            };
            var (ok, message, id) = await env.Get<SeedSowingRepository>().InsertFromCuttingAsync(sowing, Recorder, _ => true, _ => true);
            Assert.True(ok, message);
            return id;
        }

        // ---- R1 ----------------------------------------------------------

        [SkippableFact]
        public async Task CancelAsync_MainOfficeSeedSowing_ReversesExactlyTheTraysItConsumed()
        {
            var env = await OpenAsync();
            await env.ResetSeedPoolAsync(1000);
            var (sowingId, trayStockId) = await SowSeedIntoMainOfficeAsync(env, 480);   // CEILING(480/24) = 20
            Assert.Equal(1980m, await env.TrayPhysicalAsync(trayStockId));

            var (ok, message) = await env.Get<SeedSowingRepository>().CancelAsync(sowingId, "tray-reversal-test", MainOfficeSupervisorId);
            Assert.True(ok, message);

            Assert.Equal(2000m, await env.TrayPhysicalAsync(trayStockId));   // exactly back to the starting balance
            Assert.Equal(2, await env.TrayLedgerRowCountAsync(trayStockId, "SeedSowing", sowingId));   // exactly 2 rows for this reference: consumption + reversal
            Assert.Equal(0m, await env.TrayNetForReferenceAsync(trayStockId, "SeedSowing", sowingId));     // net effect of this sowing on the pool is now zero
        }

        [SkippableFact]
        public async Task CancelAsync_CalledTwice_SecondCallIsRejected_NoDoubleReversal()
        {
            var env = await OpenAsync();
            await env.ResetSeedPoolAsync(1000);
            var (sowingId, trayStockId) = await SowSeedIntoMainOfficeAsync(env, 480);

            var (ok1, _) = await env.Get<SeedSowingRepository>().CancelAsync(sowingId, "tray-reversal-test", MainOfficeSupervisorId);
            Assert.True(ok1);
            var balanceAfterFirstCancel = await env.TrayPhysicalAsync(trayStockId);

            var (ok2, message2) = await env.Get<SeedSowingRepository>().CancelAsync(sowingId, "tray-reversal-test", MainOfficeSupervisorId);
            Assert.False(ok2);
            Assert.Contains("already", message2, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(balanceAfterFirstCancel, await env.TrayPhysicalAsync(trayStockId));   // unchanged by the rejected second attempt
        }

        [SkippableFact]
        public async Task CancelAsync_NonMainOfficeDestination_NoTrayTransactionExisted_CleanNoOp()
        {
            var env = await OpenAsync();
            await env.ResetSeedPoolAsync(1000);
            // Area 1 ("Shree Swami Samarth Agro") is not a Main Office Area -- no PolyhouseId at all, same as a legacy pre-Tray-Stock sowing.
            var sowing = new SeedSowing
            {
                SourceSeedStockId = env.SeedStockId, SeedQuantity = 240, CavityType = "24 Cavity", SowingDate = DateTime.Today,
                AreaId = AreaA, PolyhouseId = null, SupervisorId = SupA, CreatedBy = "tray-reversal-test", CreatedById = SupA
            };
            var (sowOk, sowMessage, sowingId) = await env.Get<SeedSowingRepository>().InsertAsync(sowing, SupA, _ => true);
            Assert.True(sowOk, sowMessage);

            var (ok, message) = await env.Get<SeedSowingRepository>().CancelAsync(sowingId, "tray-reversal-test", SupA);
            Assert.True(ok, message);   // cancellation itself still succeeds normally -- only the (absent) tray reversal is a no-op

            Assert.Equal(0, await env.ScalarAsync<int>("SELECT COUNT(*) FROM dbo.TrayStockTransactions WHERE ReferenceType = 'SeedSowing' AND ReferenceId = @Id", ("@Id", sowingId)));
        }

        // ---- R2 ----------------------------------------------------------

        [SkippableFact]
        public async Task ConfirmAsync_CuttingSowing_NoOverage_NoExtraTrayConsumption()
        {
            var env = await OpenAsync();
            var pool = await env.ResetCuttingPoolAsync(AreaA, 3000);
            var sowingId = await SowCuttingIntoMainOfficeAsync(env, pool, 480);   // 20 trays
            var trayStockId = await env.ScalarAsync<int>("SELECT Id FROM dbo.TrayStock WHERE PolyhouseId = @P AND TraySize = '24 Cavity'", ("@P", MainOfficePolyhouseId));
            var afterSow = await env.TrayPhysicalAsync(trayStockId);

            var (ok, message, confirmationId) = await env.Get<ReadyConfirmationRepository>().ConfirmAsync(
                sowingId, 20, null, null, "tray-reversal-test", "tray-reversal-test", MainOfficeSupervisorId, _ => true);   // exactly the sown trays -- no overage
            Assert.True(ok, message);

            Assert.Equal(afterSow, await env.TrayPhysicalAsync(trayStockId));   // unchanged -- no extra consumption
            Assert.Equal(0, await env.TrayLedgerRowCountAsync(trayStockId, "ReadyConfirmation", confirmationId));
        }

        [SkippableFact]
        public async Task ConfirmAsync_CuttingSowing_Overage_ConsumesExactlyTheExtraTrays_AndCancelReturnsThem()
        {
            var env = await OpenAsync();
            var pool = await env.ResetCuttingPoolAsync(AreaA, 3000);
            var sowingId = await SowCuttingIntoMainOfficeAsync(env, pool, 480);   // 20 trays sown; 3000 -> 2520
            var trayStockId = await env.ScalarAsync<int>("SELECT Id FROM dbo.TrayStock WHERE PolyhouseId = @P AND TraySize = '24 Cavity'", ("@P", MainOfficePolyhouseId));
            var afterSow = await env.TrayPhysicalAsync(trayStockId);   // 2000 - 20 = 1980

            var (ok, message, confirmationId) = await env.Get<ReadyConfirmationRepository>().ConfirmAsync(
                sowingId, 30, null, null, "tray-reversal-test", "tray-reversal-test", MainOfficeSupervisorId, _ => true);   // 30 trays approved; 10 extra
            Assert.True(ok, message);

            Assert.Equal(afterSow - 10, await env.TrayPhysicalAsync(trayStockId));   // exactly 10 extra trays deducted
            Assert.Equal(-10m, await env.TrayNetForReferenceAsync(trayStockId, "ReadyConfirmation", confirmationId));

            var (cancelOk, cancelMessage) = await env.Get<ReadyConfirmationRepository>().CancelAsync(confirmationId, "tray-reversal-test", MainOfficeSupervisorId);
            Assert.True(cancelOk, cancelMessage);

            Assert.Equal(afterSow, await env.TrayPhysicalAsync(trayStockId));   // back to exactly what it was right after sowing, before the overage
            Assert.Equal(0m, await env.TrayNetForReferenceAsync(trayStockId, "ReadyConfirmation", confirmationId));
        }

        [SkippableFact]
        public async Task ConfirmAsync_InsufficientTrayStock_RejectsAndRollsBackEverything()
        {
            var env = await OpenAsync();
            // Tray pool reset BEFORE sowing, so the sowing's own 20-tray
            // consumption exactly exhausts it, leaving zero spare for the
            // overage attempt below.
            var trayStockId = await env.ResetTrayPoolAsync(MainOfficePolyhouseId, "24 Cavity", 20);
            var pool = await env.ResetCuttingPoolAsync(AreaA, 3000 + 1008);   // enough CuttingStock to otherwise allow the overage
            var sowingId = await SowCuttingIntoMainOfficeAsync(env, pool, 480);   // consumes the 20 trays the pool started with -> 0 left
            Assert.Equal(0m, await env.TrayPhysicalAsync(trayStockId));

            var cuttingBefore = await env.ScalarAsync<decimal>("SELECT PhysicalQuantity FROM dbo.CuttingStock WHERE Id = @Id", ("@Id", pool));

            var (ok, message, confirmationId) = await env.Get<ReadyConfirmationRepository>().ConfirmAsync(
                sowingId, 25, null, null, "tray-reversal-test", "tray-reversal-test", MainOfficeSupervisorId, _ => true);   // needs 5 extra trays; 0 available

            Assert.False(ok);
            Assert.Contains("Insufficient", message, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(0, confirmationId);
            Assert.Equal(0, await env.ScalarAsync<int>("SELECT COUNT(*) FROM dbo.ReadyConfirmations WHERE SeedSowingId = @Id", ("@Id", sowingId)));   // no partial ReadyConfirmation row
            Assert.Equal(cuttingBefore, await env.ScalarAsync<decimal>("SELECT PhysicalQuantity FROM dbo.CuttingStock WHERE Id = @Id", ("@Id", pool)));   // CuttingStock untouched
            Assert.Equal(0m, await env.TrayPhysicalAsync(trayStockId));   // TrayStock untouched (still 0, never went negative)
            Assert.Equal("Sown", await env.ScalarAsync<string>("SELECT Status FROM dbo.SeedSowings WHERE Id = @Id", ("@Id", sowingId)));   // sowing not marked Completed
        }

        [SkippableFact]
        public async Task ConfirmAsync_SeedSourceOverage_NeverConsumesTrayStock()
        {
            var env = await OpenAsync();
            await env.ResetSeedPoolAsync(10000);
            var (sowingId, trayStockId) = await SowSeedIntoMainOfficeAsync(env, 480);   // 20 trays sown
            var afterSow = await env.TrayPhysicalAsync(trayStockId);

            // Enter MORE actual trays than sown for a SEED sowing -- allowed (zero-wastage outcome), but must NEVER take extra Tray Stock (Seed-source overage is not a stock-consumption event).
            var (ok, message, confirmationId) = await env.Get<ReadyConfirmationRepository>().ConfirmAsync(
                sowingId, 25, null, null, "tray-reversal-test", "tray-reversal-test", MainOfficeSupervisorId, _ => true);
            Assert.True(ok, message);

            Assert.Equal(afterSow, await env.TrayPhysicalAsync(trayStockId));   // unchanged
            Assert.Equal(0, await env.TrayLedgerRowCountAsync(trayStockId, "ReadyConfirmation", confirmationId));
        }
    }
}
