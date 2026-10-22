using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using PlantStockManager.Authorization;
using PlantStockManager.Data;
using PlantStockManager.Models;
using PlantStockManager.Services;

namespace PlantStockManager.Tests
{
    // Material Stock (Cocopeat / Pitmos) against a REAL scratch copy of the database
    // (PSM_SCRATCH_CONNECTION, a PlantsIMS2_Scratch_* copy with
    // 2026-10-08_MaterialStock.sql applied): master data, Add Material, the sowing
    // integration (only the selected material, only the sowing's own Polyhouse, one
    // transaction), duplicate submits, concurrency, cancellation, data-driven rules
    // and the database's own rules. Each test resets the balances it uses.
    [Collection("ScratchDb")]
    public class MaterialStockE2ETests
    {
        private const string ConnectionVariable = "PSM_SCRATCH_CONNECTION";
        private const string RequiredPrefix = "PlantsIMS2_Scratch_";

        private const int MainOffice = 2;                                       // Main-Office-Areas
        private const int Facility5 = 2, Facility3 = 3, Facility4 = 4, Facility1 = 6;  // its Polyhouses
        private const int GreenBless = 127, GreenBlessP1 = 14;                  // a Mother Plant Area + Polyhouse
        private const int Outlet = 124, OutletPolyhouse = 18;
        private const int SamarthP1 = 8;                                        // a Polyhouse of Area 123
        private const int Supervisor = 9;                                       // Sowing Supervisor (approver)
        private const int Recorder = 21;                                        // records the sowings
        private const int MpA = 114;                                            // Mother Plant fixture (its variety is sown)

        private sealed class StaticMonitor<T> : IOptionsMonitor<T>
        {
            public StaticMonitor(T value) => CurrentValue = value;
            public T CurrentValue { get; }
            public T Get(string? name) => CurrentValue;
            public IDisposable? OnChange(Action<T, string?> listener) => null;
        }

        private sealed class Env
        {
            public string Cs { get; init; } = "";
            public ServiceProvider Provider { get; init; } = null!;
            public int Species { get; init; }
            public int SeedStockId { get; init; }
            public int Cocopeat { get; init; }
            public int Pitmos { get; init; }
            public T Get<T>() where T : notnull => Provider.GetRequiredService<T>();
            public MaterialStockRepository Repo => Get<MaterialStockRepository>();
            public SeedSowingRepository Sowings => Get<SeedSowingRepository>();

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

            // Empties a Polyhouse's material balances (scratch-only clean-up switch), then
            // sets them through the REAL ledger path (an adjustment), so the ledger always
            // reconciles.
            public async Task SetMaterialAsync(int areaId, int polyhouseId, decimal cocopeat, decimal pitmos)
            {
                await ExecAsync(@"
EXEC sp_set_session_context N'MaterialTestCleanup', 1;
DELETE t FROM dbo.MaterialTransactions t JOIN dbo.MaterialStock s ON s.Id = t.MaterialStockId WHERE s.AreaId = @A AND s.PolyhouseId = @P;
UPDATE dbo.MaterialStock SET CurrentQuantity = 0 WHERE AreaId = @A AND PolyhouseId = @P;
EXEC sp_set_session_context N'MaterialTestCleanup', 0;", ("@A", areaId), ("@P", polyhouseId));
                foreach (var (material, kg) in new[] { (Cocopeat, cocopeat), (Pitmos, pitmos) })
                    if (kg > 0)
                    {
                        var (ok, msg, _) = await Repo.AdjustAsync(areaId, polyhouseId, material, 1, kg, "test setup", Guid.NewGuid(), Recorder, _ => true);
                        Assert.True(ok, msg);
                    }
            }

            public Task<decimal> BalanceAsync(int areaId, int polyhouseId, int material)
                => ScalarAsync<decimal>("SELECT ISNULL((SELECT CurrentQuantity FROM dbo.MaterialStock WHERE AreaId = @A AND PolyhouseId = @P AND MaterialTypeId = @M), 0)",
                    ("@A", areaId), ("@P", polyhouseId), ("@M", material));

            // 2026-10-14: a Mother Plant Area's trays sit in its ONE active Area pool once 2026-10-14_MotherPlantAreaTraysAndTransferPurpose.sql
            // is applied (inactive before it); every other Area keeps the Polyhouse pool.
            private const string TrayPool = "(t.PolyhouseId = @P OR (t.PolyhouseId IS NULL AND t.IsActive = 1 AND EXISTS (SELECT 1 FROM dbo.Area a WHERE a.Id = t.AreaId AND a.AreaType = N'MotherPlant')))";

            public async Task SetTraysAsync(int areaId, int polyhouseId, string cavity, decimal trays)
            {
                await ExecAsync(@"
UPDATE t SET PhysicalQuantity = 0 FROM dbo.TrayStock t WHERE t.AreaId = @A AND " + TrayPool + @" AND t.TraySize = @T;
DELETE tr FROM dbo.TrayStockTransactions tr JOIN dbo.TrayStock t ON t.Id = tr.TrayStockId WHERE t.AreaId = @A AND " + TrayPool + @" AND t.TraySize = @T",
                    ("@A", areaId), ("@P", polyhouseId), ("@T", cavity));
                var (ok, msg, _, _) = await Get<TrayStockRepository>().AddStockAsync(areaId, polyhouseId, cavity, trays, DateTime.UtcNow,
                    Random.Shared.Next(1, int.MaxValue), Supervisor, "mat-test", "test setup", _ => true);
                Assert.True(ok, msg);
            }

            public Task<decimal> TraysAsync(int areaId, int polyhouseId, string cavity)
                => ScalarAsync<decimal>("SELECT ISNULL(SUM(t.PhysicalQuantity), 0) FROM dbo.TrayStock t WHERE t.AreaId = @A AND " + TrayPool + " AND t.TraySize = @T",
                    ("@A", areaId), ("@P", polyhouseId), ("@T", cavity));

            public Task SetSeedAsync(decimal physical)
                => ExecAsync("UPDATE dbo.SeedStock SET InTransitQuantity = 0, PhysicalQuantity = @P WHERE Id = @Id", ("@P", physical), ("@Id", SeedStockId));

            public Task<decimal> SeedAsync() => ScalarAsync<decimal>("SELECT PhysicalQuantity FROM dbo.SeedStock WHERE Id = @Id", ("@Id", SeedStockId));

            // Every balance equals the sum of its ledger and is never negative.
            public Task<int> InvariantViolationsAsync() => ScalarAsync<int>(@"
SELECT COUNT(*) FROM dbo.MaterialStock s
WHERE s.CurrentQuantity < 0
   OR s.CurrentQuantity <> ISNULL((SELECT SUM(t.Direction * t.StockQuantity) FROM dbo.MaterialTransactions t WHERE t.MaterialStockId = s.Id), 0)");

            public SeedSowing Sowing(int areaId, int polyhouseId, string cavity, int trays, int? material, Guid? token = null) => new()
            {
                SourceSeedStockId = SeedStockId, SeedQuantity = trays * DirectSowingRules.CavityCount(cavity)!.Value, CavityType = cavity,
                SowingDate = DateTime.Today, AreaId = areaId, PolyhouseId = polyhouseId, SupervisorId = Supervisor,
                CreatedBy = "mat-test", CreatedById = Recorder, GrowingMaterialTypeId = material, SubmissionToken = token ?? Guid.NewGuid(),
            };

            public Task<(bool Success, string? Message, int Id)> SowAsync(SeedSowing s) => Sowings.InsertAsync(s, Recorder, _ => true);
        }

        private static async Task<Env> OpenAsync()
        {
            var cs = Environment.GetEnvironmentVariable(ConnectionVariable);
            Skip.If(string.IsNullOrWhiteSpace(cs), $"Set {ConnectionVariable} to a {RequiredPrefix}* database connection string to run the Material Stock tests.");
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
            services.AddSingleton<DatabaseHelper>();
            services.AddSingleton<AreaAccessService>();
            services.AddSingleton<IOptions<SecurityOptions>>(Options.Create(new SecurityOptions()));
            services.AddSingleton<IOptionsMonitor<SeedlingWorkflowOptions>>(new StaticMonitor<SeedlingWorkflowOptions>(new SeedlingWorkflowOptions()));
            services.AddSingleton<SeedlingAreaScope>();
            foreach (var t in typeof(DatabaseHelper).Assembly.GetTypes().Where(t => t.Namespace == "PlantStockManager.Data" && t.IsClass && t.Name.EndsWith("Repository")))
                services.AddScoped(t);
            var provider = services.BuildServiceProvider();

            var probe = new Env { Cs = cs!, Provider = provider };
            Skip.If(await probe.ScalarAsync<int>("SELECT CASE WHEN OBJECT_ID('dbo.MaterialStock', 'U') IS NULL THEN 0 ELSE 1 END") == 0,
                "Apply Database/Migrations/2026-10-08_MaterialStock.sql to the scratch copy first.");
            var species = await probe.ScalarAsync<int>("SELECT SpeciesId FROM dbo.MotherPlants WHERE Id = @M", ("@M", MpA));
            await probe.ExecAsync("UPDATE dbo.PlantSpecies SET ReadyStockDays = 60 WHERE Id = @S AND ReadyStockDays IS NULL", ("@S", species));
            var seedStockId = await probe.ScalarAsync<int>("SELECT TOP 1 Id FROM dbo.SeedStock WHERE SpeciesId = @S AND AreaId = @A ORDER BY Id", ("@S", species), ("@A", MainOffice));
            if (seedStockId == 0)
            {
                await probe.ExecAsync("INSERT INTO dbo.SeedStock (SpeciesId, AreaId, PhysicalQuantity, CreatedDate, CreatedBy) VALUES (@S, @A, 0, SYSUTCDATETIME(), N'mat-test')",
                    ("@S", species), ("@A", MainOffice));
                seedStockId = await probe.ScalarAsync<int>("SELECT TOP 1 Id FROM dbo.SeedStock WHERE SpeciesId = @S AND AreaId = @A ORDER BY Id", ("@S", species), ("@A", MainOffice));
            }
            // Restore the documented approximate values (a test may have changed them).
            await probe.ExecAsync(@"
UPDATE dbo.MaterialTypes SET ConversionFactor = 5.2 WHERE Code = 'COCOPEAT';
UPDATE dbo.MaterialTypes SET ConversionFactor = 1.0 WHERE Code = 'PITMOS';
UPDATE dbo.MaterialConsumptionRules SET QuantityPerTray = 1.5, IsActive = 1;");
            return new Env
            {
                Cs = cs!, Provider = provider, Species = species, SeedStockId = seedStockId,
                Cocopeat = await probe.ScalarAsync<int>("SELECT Id FROM dbo.MaterialTypes WHERE Code = 'COCOPEAT'"),
                Pitmos = await probe.ScalarAsync<int>("SELECT Id FROM dbo.MaterialTypes WHERE Code = 'PITMOS'"),
            };
        }

        // ---- master data ----------------------------------------------------------------------

        [SkippableFact]
        public async Task Master_CocopeatAndPitmos_WithTheirFactors_AndARuleForEveryCavity()
        {
            var env = await OpenAsync();
            var types = await env.Repo.GetTypesAsync();
            Assert.Equal(5.2m, types.Single(t => t.Code == "COCOPEAT").ConversionFactor);
            Assert.Equal("Cocopeat", types.Single(t => t.Code == "COCOPEAT").Name);
            Assert.Equal(1.0m, types.Single(t => t.Code == "PITMOS").ConversionFactor);
            Assert.Equal("Pitmos", types.Single(t => t.Code == "PITMOS").Name);

            var rules = (await env.Repo.GetRulesAsync()).Where(r => r.IsActive).ToList();
            // 2026-10-19: "70 Cavity" has its rules only once 2026-10-19_Cavity70.sql is applied (and is never offered before)
            var cavity70 = await env.ScalarAsync<int>(WorkflowCorrectionsSchema.Cavity70ProbeSql) == 1;
            foreach (var cavity in DirectSowingRules.CavityTypes.Where(c => cavity70 || c != DirectSowingRules.Cavity70))
                foreach (var material in new[] { "Cocopeat", "Pitmos" })
                    Assert.Equal(1.5m, rules.Single(r => r.CavityType == cavity && r.MaterialName == material).QuantityPerTray);
            if (!cavity70)
                Assert.DoesNotContain(rules, r => r.CavityType == DirectSowingRules.Cavity70);
        }

        // ---- Add Material ------------------------------------------------------------------------

        [SkippableTheory]
        [InlineData("COCOPEAT", 5, 26)]
        [InlineData("COCOPEAT", 10, 52)]
        [InlineData("PITMOS", 5, 5)]
        [InlineData("PITMOS", 10, 10)]
        public async Task Receive_ConvertsTheEnteredQuantity_AndKeepsTheInputAndFactor(string code, decimal input, decimal expectedStock)
        {
            var env = await OpenAsync();
            await env.SetMaterialAsync(MainOffice, Facility4, 0, 0);
            var material = code == "COCOPEAT" ? env.Cocopeat : env.Pitmos;
            var token = Guid.NewGuid();

            var r = await env.Repo.ReceiveAsync(MainOffice, Facility4, material, input, token, Supervisor, "delivery", _ => true);

            Assert.True(r.Success, r.Message);
            Assert.Equal(expectedStock, r.StockAdded);
            Assert.Equal(expectedStock, await env.BalanceAsync(MainOffice, Facility4, material));
            Assert.Equal(input, await env.ScalarAsync<decimal>("SELECT InputQuantity FROM dbo.MaterialTransactions WHERE SubmissionToken = @T", ("@T", token)));
            Assert.Equal(code == "COCOPEAT" ? 5.2m : 1.0m, await env.ScalarAsync<decimal>("SELECT ConversionFactor FROM dbo.MaterialTransactions WHERE SubmissionToken = @T", ("@T", token)));
            Assert.Equal(expectedStock, await env.ScalarAsync<decimal>("SELECT StockQuantity FROM dbo.MaterialTransactions WHERE SubmissionToken = @T AND TransactionType = 'RECEIVE'", ("@T", token)));
            Assert.Equal(Supervisor, await env.ScalarAsync<int>("SELECT CreatedById FROM dbo.MaterialTransactions WHERE SubmissionToken = @T", ("@T", token)));
            Assert.Equal(0, await env.InvariantViolationsAsync());
        }

        [SkippableFact]
        public async Task Receive_TheTwoMaterialsAreSeparateBalances_PerPolyhouse_AndDecimalsAreKept()
        {
            var env = await OpenAsync();
            await env.SetMaterialAsync(MainOffice, Facility3, 0, 0);
            await env.SetMaterialAsync(MainOffice, Facility1, 0, 0);

            Assert.True((await env.Repo.ReceiveAsync(MainOffice, Facility3, env.Cocopeat, 100, Guid.NewGuid(), Supervisor, null, _ => true)).Success);   // 520
            Assert.True((await env.Repo.ReceiveAsync(MainOffice, Facility3, env.Pitmos, 200, Guid.NewGuid(), Supervisor, null, _ => true)).Success);
            Assert.True((await env.Repo.ReceiveAsync(MainOffice, Facility3, env.Pitmos, 0.75m, Guid.NewGuid(), Supervisor, null, _ => true)).Success);
            Assert.True((await env.Repo.ReceiveAsync(MainOffice, Facility1, env.Pitmos, 2.25m, Guid.NewGuid(), Supervisor, null, _ => true)).Success);

            Assert.Equal(520m, await env.BalanceAsync(MainOffice, Facility3, env.Cocopeat));
            Assert.Equal(200.75m, await env.BalanceAsync(MainOffice, Facility3, env.Pitmos));
            Assert.Equal(2.25m, await env.BalanceAsync(MainOffice, Facility1, env.Pitmos));
            Assert.Equal(0m, await env.BalanceAsync(MainOffice, Facility1, env.Cocopeat));
            Assert.Equal(0, await env.InvariantViolationsAsync());
        }

        [SkippableFact]
        public async Task Receive_InvalidLocationsAndValues_AreRejected_NothingIsWritten()
        {
            var env = await OpenAsync();
            var before = await env.ScalarAsync<int>("SELECT COUNT(*) FROM dbo.MaterialTransactions");
            var cases = new (int Area, int Polyhouse, int Material, decimal Qty, Func<int, bool> Access)[]
            {
                (Outlet, OutletPolyhouse, env.Cocopeat, 5, _ => true),          // Outlet
                (GreenBless, GreenBlessP1, env.Cocopeat, 5, _ => true),        // not a Main Office Area
                (MainOffice, SamarthP1, env.Pitmos, 5, _ => true),              // Polyhouse of another Area
                (MainOffice, 999999, env.Pitmos, 5, _ => true),                 // no such Polyhouse
                (MainOffice, Facility4, 999999, 5, _ => true),                  // no such material
                (MainOffice, Facility4, env.Pitmos, 5, _ => false),             // Area not authorized
                (MainOffice, Facility4, env.Pitmos, 0, _ => true),              // zero
                (MainOffice, Facility4, env.Pitmos, -5, _ => true),             // negative
                (MainOffice, Facility4, env.Pitmos, 1.2345m, _ => true),        // more than 3 decimals
            };
            foreach (var c in cases)
                Assert.False((await env.Repo.ReceiveAsync(c.Area, c.Polyhouse, c.Material, c.Qty, Guid.NewGuid(), Supervisor, null, c.Access)).Success);
            Assert.False((await env.Repo.ReceiveAsync(MainOffice, Facility4, env.Pitmos, 5, Guid.Empty, Supervisor, null, _ => true)).Success);   // expired form
            Assert.Equal(before, await env.ScalarAsync<int>("SELECT COUNT(*) FROM dbo.MaterialTransactions"));
            Assert.Equal(0, await env.ScalarAsync<int>("SELECT COUNT(*) FROM dbo.MaterialStock s JOIN dbo.Area a ON a.Id = s.AreaId WHERE ISNULL(a.AreaType, '') <> 'MainOffice'"));
        }

        [SkippableFact]
        public async Task Receive_SameFormTwice_OrSimultaneously_AddsOnce()
        {
            var env = await OpenAsync();
            await env.SetMaterialAsync(MainOffice, Facility4, 0, 0);
            var token = Guid.NewGuid();
            var results = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ =>
                env.Repo.ReceiveAsync(MainOffice, Facility4, env.Cocopeat, 5, token, Supervisor, null, _ => true)));
            Assert.All(results, r => Assert.True(r.Success, r.Message));
            Assert.Equal(5, results.Count(r => r.Duplicate));
            Assert.Equal(26m, await env.BalanceAsync(MainOffice, Facility4, env.Cocopeat));
            Assert.Equal(1, await env.ScalarAsync<int>("SELECT COUNT(*) FROM dbo.MaterialTransactions WHERE SubmissionToken = @T", ("@T", token)));
        }

        // ---- sowing: consumption --------------------------------------------------------------

        [SkippableTheory]
        [InlineData("150 Cavity")]
        [InlineData("102 Cavity")]
        [InlineData("42 Cavity")]
        [InlineData("24 Cavity")]
        [InlineData("9 Cavity")]
        public async Task Sowing_EveryTrayType_Uses1Point5KgPerTray(string cavity)
        {
            var env = await OpenAsync();
            await env.SetMaterialAsync(MainOffice, Facility5, 500, 0);
            await env.SetTraysAsync(MainOffice, Facility5, cavity, 20);
            await env.SetSeedAsync(1_000_000);

            var (ok, msg, id) = await env.SowAsync(env.Sowing(MainOffice, Facility5, cavity, 10, env.Cocopeat));

            Assert.True(ok, msg);
            Assert.Equal(485m, await env.BalanceAsync(MainOffice, Facility5, env.Cocopeat));   // 10 x 1.5 = 15
            Assert.Equal(15m, await env.ScalarAsync<decimal>(
                "SELECT StockQuantity FROM dbo.MaterialTransactions WHERE ReferenceType = 'SeedSowing' AND ReferenceId = @Id AND TransactionType = 'CONSUME' AND Trays = 10 AND QuantityPerTray = 1.5", ("@Id", id)));
            Assert.Equal(env.Cocopeat, await env.ScalarAsync<int>("SELECT GrowingMaterialTypeId FROM dbo.SeedSowings WHERE Id = @Id", ("@Id", id)));
        }

        [SkippableFact]
        public async Task Sowing_OnlyTheSelectedMaterialIsDeducted()
        {
            var env = await OpenAsync();
            await env.SetMaterialAsync(MainOffice, Facility5, 500, 300);
            await env.SetTraysAsync(MainOffice, Facility5, "150 Cavity", 300);
            await env.SetSeedAsync(1_000_000);

            Assert.True((await env.SowAsync(env.Sowing(MainOffice, Facility5, "150 Cavity", 100, env.Cocopeat))).Success);
            Assert.Equal(350m, await env.BalanceAsync(MainOffice, Facility5, env.Cocopeat));
            Assert.Equal(300m, await env.BalanceAsync(MainOffice, Facility5, env.Pitmos));

            Assert.True((await env.SowAsync(env.Sowing(MainOffice, Facility5, "150 Cavity", 100, env.Pitmos))).Success);
            Assert.Equal(350m, await env.BalanceAsync(MainOffice, Facility5, env.Cocopeat));
            Assert.Equal(150m, await env.BalanceAsync(MainOffice, Facility5, env.Pitmos));
            Assert.Equal(0, await env.InvariantViolationsAsync());
        }

        [SkippableFact]
        public async Task Sowing_IsDeductedFromItsOwnPolyhouseOnly()
        {
            var env = await OpenAsync();
            await env.SetMaterialAsync(MainOffice, Facility1, 200, 0);    // "PH-01"
            await env.SetMaterialAsync(MainOffice, Facility3, 100, 0);    // "PH-02"
            await env.SetTraysAsync(MainOffice, Facility3, "24 Cavity", 60);
            await env.SetSeedAsync(1_000_000);

            Assert.True((await env.SowAsync(env.Sowing(MainOffice, Facility3, "24 Cavity", 50, env.Cocopeat))).Success);

            Assert.Equal(25m, await env.BalanceAsync(MainOffice, Facility3, env.Cocopeat));   // 100 - 75
            Assert.Equal(200m, await env.BalanceAsync(MainOffice, Facility1, env.Cocopeat));
        }

        [SkippableFact]
        public async Task Sowing_ExactStock_UsesItAll_AndOneMoreTrayIsRefused()
        {
            var env = await OpenAsync();
            await env.SetMaterialAsync(MainOffice, Facility5, 0, 30);
            await env.SetTraysAsync(MainOffice, Facility5, "42 Cavity", 40);
            await env.SetSeedAsync(1_000_000);

            Assert.True((await env.SowAsync(env.Sowing(MainOffice, Facility5, "42 Cavity", 20, env.Pitmos))).Success);   // 30 kg
            Assert.Equal(0m, await env.BalanceAsync(MainOffice, Facility5, env.Pitmos));
            var (ok, msg, _) = await env.SowAsync(env.Sowing(MainOffice, Facility5, "42 Cavity", 1, env.Pitmos));
            Assert.False(ok);
            Assert.Contains("Insufficient Pitmos stock", msg);
        }

        [SkippableFact]
        public async Task Sowing_InsufficientStock_IsRefused_AndNothingAtAllChanges()
        {
            var env = await OpenAsync();
            await env.SetMaterialAsync(MainOffice, Facility3, 100, 1000);
            await env.SetTraysAsync(MainOffice, Facility3, "150 Cavity", 200);
            await env.SetSeedAsync(1_000_000);
            var sowingsBefore = await env.ScalarAsync<int>("SELECT COUNT(*) FROM dbo.SeedSowings");
            var ledgerBefore = await env.ScalarAsync<int>("SELECT COUNT(*) FROM dbo.MaterialTransactions");

            var (ok, msg, _) = await env.SowAsync(env.Sowing(MainOffice, Facility3, "150 Cavity", 100, env.Cocopeat));   // needs 150

            Assert.False(ok);
            var polyhouse = (await env.ScalarAsync<string>("SELECT Name FROM dbo.Polyhouses WHERE Id = @P", ("@P", Facility3))).Trim();
            Assert.Equal(MaterialStockRules.InsufficientMessage("Cocopeat", polyhouse, 100, 150), msg);
            Assert.Equal(100m, await env.BalanceAsync(MainOffice, Facility3, env.Cocopeat));
            Assert.Equal(1000m, await env.BalanceAsync(MainOffice, Facility3, env.Pitmos));
            Assert.Equal(200m, await env.TraysAsync(MainOffice, Facility3, "150 Cavity"));   // trays rolled back too
            Assert.Equal(1_000_000m, await env.SeedAsync());                                   // seed rolled back too
            Assert.Equal(sowingsBefore, await env.ScalarAsync<int>("SELECT COUNT(*) FROM dbo.SeedSowings"));
            Assert.Equal(ledgerBefore, await env.ScalarAsync<int>("SELECT COUNT(*) FROM dbo.MaterialTransactions"));
        }

        [SkippableFact]
        public async Task Sowing_AtMainOffice_WithoutAMaterial_IsRefused()
        {
            var env = await OpenAsync();
            await env.SetMaterialAsync(MainOffice, Facility5, 500, 500);
            await env.SetTraysAsync(MainOffice, Facility5, "24 Cavity", 20);
            await env.SetSeedAsync(1_000_000);
            var (ok, msg, _) = await env.SowAsync(env.Sowing(MainOffice, Facility5, "24 Cavity", 5, null));
            Assert.False(ok);
            Assert.Equal(MaterialStockRules.MaterialRequiredMessage, msg);
            Assert.Equal(500m, await env.BalanceAsync(MainOffice, Facility5, env.Cocopeat));
            Assert.Equal(500m, await env.BalanceAsync(MainOffice, Facility5, env.Pitmos));
            Assert.Equal(20m, await env.TraysAsync(MainOffice, Facility5, "24 Cavity"));
        }

        // 2026-10-08: Direct Seed Sowing is done only in Main Area Polyhouses, so a Direct
        // Seed Sowing at a Mother Plant Area is refused whether or not a material is chosen
        // (a CUTTING sowing there still uses no material -- MaterialStockCuttingE2ETests).
        [SkippableFact]
        public async Task DirectSowing_OutsideTheMainArea_IsRefused_WithOrWithoutMaterial_NothingChanges()
        {
            var env = await OpenAsync();
            await env.SetTraysAsync(GreenBless, GreenBlessP1, "24 Cavity", 20);
            await env.SetSeedAsync(1_000_000);
            var sowings = await env.ScalarAsync<int>("SELECT COUNT(*) FROM dbo.SeedSowings");

            foreach (var material in new int?[] { env.Cocopeat, null })
            {
                var (ok, msg, id) = await env.SowAsync(env.Sowing(GreenBless, GreenBlessP1, "24 Cavity", 2, material));
                Assert.False(ok);
                Assert.Equal(0, id);
                Assert.Equal(DirectSowingRules.DirectSowingNotMainAreaMessage, msg);
            }
            Assert.Equal(sowings, await env.ScalarAsync<int>("SELECT COUNT(*) FROM dbo.SeedSowings"));
            Assert.Equal(20m, await env.TraysAsync(GreenBless, GreenBlessP1, "24 Cavity"));
            Assert.Equal(1_000_000m, await env.SeedAsync());
        }

        [SkippableFact]
        public async Task Sowing_TamperedLocation_IsRefused_NoMaterialMoves()
        {
            var env = await OpenAsync();
            await env.SetMaterialAsync(MainOffice, Facility5, 500, 0);
            await env.SetSeedAsync(1_000_000);
            var (ok, _, _) = await env.SowAsync(env.Sowing(MainOffice, SamarthP1, "24 Cavity", 2, env.Cocopeat));   // a Polyhouse of another Area
            Assert.False(ok);
            Assert.Equal(500m, await env.BalanceAsync(MainOffice, Facility5, env.Cocopeat));
            Assert.Equal(0m, await env.BalanceAsync(MainOffice, SamarthP1, env.Cocopeat));
        }

        [SkippableFact]
        public async Task Sowing_NoRuleForTheCavity_RollsBackTheWholeSowing()
        {
            var env = await OpenAsync();
            try
            {
                await env.SetMaterialAsync(MainOffice, Facility5, 500, 0);
                await env.SetTraysAsync(MainOffice, Facility5, "9 Cavity", 20);
                await env.SetSeedAsync(1_000_000);
                await env.ExecAsync("UPDATE dbo.MaterialConsumptionRules SET IsActive = 0 WHERE MaterialTypeId = @M AND CavityType = N'9 Cavity'", ("@M", env.Cocopeat));

                var (ok, msg, _) = await env.SowAsync(env.Sowing(MainOffice, Facility5, "9 Cavity", 5, env.Cocopeat));

                Assert.False(ok);
                Assert.Contains("No Cocopeat consumption is configured for 9 Cavity", msg);
                Assert.Equal(20m, await env.TraysAsync(MainOffice, Facility5, "9 Cavity"));
                Assert.Equal(1_000_000m, await env.SeedAsync());
                Assert.Equal(500m, await env.BalanceAsync(MainOffice, Facility5, env.Cocopeat));
            }
            finally
            {
                await env.ExecAsync("UPDATE dbo.MaterialConsumptionRules SET IsActive = 1 WHERE CavityType = N'9 Cavity'");
            }
        }

        [SkippableFact]
        public async Task Rules_AreData_AChangedKgPerTrayAppliesToNewSowings_ACancelReturnsWhatWasTaken()
        {
            var env = await OpenAsync();
            try
            {
                await env.SetMaterialAsync(MainOffice, Facility5, 100, 0);
                await env.SetTraysAsync(MainOffice, Facility5, "150 Cavity", 50);
                await env.SetSeedAsync(1_000_000);
                var rule = await env.ScalarAsync<int>("SELECT Id FROM dbo.MaterialConsumptionRules WHERE MaterialTypeId = @M AND CavityType = N'150 Cavity'", ("@M", env.Cocopeat));
                Assert.Null(await env.Repo.UpdateQuantityPerTrayAsync(rule, 1.25m, Recorder));

                var (ok, msg, id) = await env.SowAsync(env.Sowing(MainOffice, Facility5, "150 Cavity", 10, env.Cocopeat));
                Assert.True(ok, msg);
                Assert.Equal(87.5m, await env.BalanceAsync(MainOffice, Facility5, env.Cocopeat));   // 10 x 1.25

                // The value changes again; the cancellation still returns exactly what was taken.
                Assert.Null(await env.Repo.UpdateQuantityPerTrayAsync(rule, 2m, Recorder));
                var (cancelled, cancelMsg) = await env.Sowings.CancelAsync(id, "mat-test", Recorder);
                Assert.True(cancelled, cancelMsg);
                Assert.Equal(100m, await env.BalanceAsync(MainOffice, Facility5, env.Cocopeat));
                Assert.Equal(1, await env.ScalarAsync<int>("SELECT COUNT(*) FROM dbo.MaterialTransactions WHERE ReferenceType = 'SeedSowing' AND ReferenceId = @Id AND TransactionType = 'REVERSAL'", ("@Id", id)));
                Assert.False((await env.Sowings.CancelAsync(id, "mat-test", Recorder)).Success);   // never twice
                Assert.Equal(100m, await env.BalanceAsync(MainOffice, Facility5, env.Cocopeat));
                Assert.Equal(0, await env.InvariantViolationsAsync());
            }
            finally
            {
                await env.ExecAsync("UPDATE dbo.MaterialConsumptionRules SET QuantityPerTray = 1.5");
            }
        }

        [SkippableFact]
        public async Task ConversionFactor_IsData_AChangeAppliesToNewStockOnly()
        {
            var env = await OpenAsync();
            try
            {
                await env.SetMaterialAsync(MainOffice, Facility4, 0, 0);
                Assert.Null(await env.Repo.UpdateConversionFactorAsync(env.Cocopeat, 5.0m, Recorder));
                var r = await env.Repo.ReceiveAsync(MainOffice, Facility4, env.Cocopeat, 10, Guid.NewGuid(), Supervisor, null, _ => true);
                Assert.Equal(50m, r.StockAdded);
                Assert.Equal(5.0m, r.ConversionFactor);
                Assert.NotNull(await env.Repo.UpdateConversionFactorAsync(env.Cocopeat, 0m, Recorder));   // must be positive
            }
            finally
            {
                await env.ExecAsync("UPDATE dbo.MaterialTypes SET ConversionFactor = 5.2 WHERE Code = 'COCOPEAT'");
            }
        }

        // ---- duplicates and concurrency --------------------------------------------------------

        [SkippableFact]
        public async Task Sowing_SameFormSubmittedRepeatedly_CreatesOneSowing_AndOneDeduction()
        {
            var env = await OpenAsync();
            await env.SetMaterialAsync(MainOffice, Facility3, 300, 0);
            await env.SetTraysAsync(MainOffice, Facility3, "24 Cavity", 100);
            await env.SetSeedAsync(1_000_000);
            var token = Guid.NewGuid();

            var results = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => env.SowAsync(env.Sowing(MainOffice, Facility3, "24 Cavity", 10, env.Cocopeat, token))));
            var again = await env.SowAsync(env.Sowing(MainOffice, Facility3, "24 Cavity", 10, env.Cocopeat, token));

            Assert.All(results, r => Assert.True(r.Success, r.Message));
            Assert.Single(results.Select(r => r.Id).Append(again.Id).Distinct());
            Assert.Equal(1, await env.ScalarAsync<int>("SELECT COUNT(*) FROM dbo.SeedSowings WHERE SubmissionToken = @T", ("@T", token)));
            Assert.Equal(285m, await env.BalanceAsync(MainOffice, Facility3, env.Cocopeat));   // one deduction of 15
            Assert.Equal(90m, await env.TraysAsync(MainOffice, Facility3, "24 Cavity"));
        }

        [SkippableFact]
        public async Task Concurrency_SimultaneousSowings_OnlyThoseTheStockCoversSucceed_NeverNegative()
        {
            var env = await OpenAsync();
            for (var round = 0; round < 3; round++)
            {
                await env.SetMaterialAsync(MainOffice, Facility5, 150, 0);          // covers exactly one 100-tray sowing
                await env.SetTraysAsync(MainOffice, Facility5, "24 Cavity", 2000);
                await env.SetSeedAsync(10_000_000);
                var ledgerBefore = await env.ScalarAsync<int>("SELECT COUNT(*) FROM dbo.MaterialTransactions WHERE TransactionType = 'CONSUME'");

                var results = await Task.WhenAll(Enumerable.Range(0, 2).Select(_ => env.SowAsync(env.Sowing(MainOffice, Facility5, "24 Cavity", 100, env.Cocopeat))));

                Assert.Equal(1, results.Count(r => r.Success));
                Assert.Contains(results, r => !r.Success && r.Message!.StartsWith("Insufficient Cocopeat stock"));
                Assert.Equal(0m, await env.BalanceAsync(MainOffice, Facility5, env.Cocopeat));
                Assert.Equal(ledgerBefore + 1, await env.ScalarAsync<int>("SELECT COUNT(*) FROM dbo.MaterialTransactions WHERE TransactionType = 'CONSUME'"));
            }

            // Many at once: 8 sowings of 15 kg against 45 kg -> exactly 3.
            await env.SetMaterialAsync(MainOffice, Facility5, 45, 0);
            var many = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => env.SowAsync(env.Sowing(MainOffice, Facility5, "24 Cavity", 10, env.Cocopeat))));
            Assert.Equal(3, many.Count(r => r.Success));
            Assert.Equal(0m, await env.BalanceAsync(MainOffice, Facility5, env.Cocopeat));
            Assert.Equal(0, await env.InvariantViolationsAsync());
        }

        [SkippableFact]
        public async Task Concurrency_SimultaneousRemovals_OnTheMaterialLockItself_NeverGoNegative()
        {
            var env = await OpenAsync();
            await env.SetMaterialAsync(MainOffice, Facility1, 0, 100);
            var results = await Task.WhenAll(Enumerable.Range(0, 10).Select(_ =>
                env.Repo.AdjustAsync(MainOffice, Facility1, env.Pitmos, -1, 30, "count", Guid.NewGuid(), Recorder, _ => true)));
            Assert.Equal(3, results.Count(r => r.Success));
            Assert.Equal(10m, await env.BalanceAsync(MainOffice, Facility1, env.Pitmos));
            Assert.Equal(0, await env.InvariantViolationsAsync());
        }

        // ---- the database's own rules ------------------------------------------------------------

        private static async Task<int> SqlErrorAsync(Env env, string sql, params (string Name, object? Value)[] args)
        {
            var ex = await Assert.ThrowsAsync<SqlException>(() => env.ExecAsync(sql, args));
            return ex.Number;
        }

        [SkippableFact]
        public async Task Database_RefusesEveryDirectViolation()
        {
            var env = await OpenAsync();
            await env.SetMaterialAsync(MainOffice, Facility5, 500, 100);
            await env.SetTraysAsync(MainOffice, Facility5, "24 Cavity", 20);
            await env.SetSeedAsync(1_000_000);
            var (ok, msg, sowingId) = await env.SowAsync(env.Sowing(MainOffice, Facility5, "24 Cavity", 2, env.Cocopeat));
            Assert.True(ok, msg);
            var cocoStock = await env.ScalarAsync<int>("SELECT Id FROM dbo.MaterialStock WHERE AreaId = @A AND PolyhouseId = @P AND MaterialTypeId = @M", ("@A", MainOffice), ("@P", Facility5), ("@M", env.Cocopeat));
            var pitStock = await env.ScalarAsync<int>("SELECT Id FROM dbo.MaterialStock WHERE AreaId = @A AND PolyhouseId = @P AND MaterialTypeId = @M", ("@A", MainOffice), ("@P", Facility5), ("@M", env.Pitmos));

            // stock only at a Main Office Polyhouse
            Assert.Equal(51923, await SqlErrorAsync(env, "INSERT INTO dbo.MaterialStock (MaterialTypeId, AreaId, PolyhouseId) VALUES (@M, @A, @P)", ("@M", env.Pitmos), ("@A", Outlet), ("@P", OutletPolyhouse)));
            Assert.Equal(51923, await SqlErrorAsync(env, "INSERT INTO dbo.MaterialStock (MaterialTypeId, AreaId, PolyhouseId) VALUES (@M, @A, @P)", ("@M", env.Pitmos), ("@A", MainOffice), ("@P", SamarthP1)));
            // stock changes only through the ledger
            Assert.Equal(51925, await SqlErrorAsync(env, "UPDATE dbo.MaterialStock SET CurrentQuantity = CurrentQuantity + 1 WHERE Id = @Id", ("@Id", cocoStock)));
            // the ledger is insert-only
            Assert.Equal(51931, await SqlErrorAsync(env, "UPDATE dbo.MaterialTransactions SET Remarks = N'x' WHERE MaterialStockId = @Id", ("@Id", cocoStock)));
            Assert.Equal(51932, await SqlErrorAsync(env, "DELETE FROM dbo.MaterialTransactions WHERE MaterialStockId = @Id", ("@Id", cocoStock)));
            // never negative, never a wrong balance
            Assert.Equal(547, await SqlErrorAsync(env, @"INSERT INTO dbo.MaterialTransactions (MaterialStockId, TransactionType, Direction, StockQuantity, BalanceBefore, BalanceAfter, Remarks, CreatedById)
SELECT Id, 'ADJUSTMENT', -1, CurrentQuantity + 1, CurrentQuantity, -1, N'x', @U FROM dbo.MaterialStock WHERE Id = @Id", ("@Id", pitStock), ("@U", Recorder)));
            // an adjustment needs a reason
            Assert.Equal(547, await SqlErrorAsync(env, @"INSERT INTO dbo.MaterialTransactions (MaterialStockId, TransactionType, Direction, StockQuantity, BalanceBefore, BalanceAfter, CreatedById)
SELECT Id, 'ADJUSTMENT', 1, 1, CurrentQuantity, CurrentQuantity + 1, @U FROM dbo.MaterialStock WHERE Id = @Id", ("@Id", pitStock), ("@U", Recorder)));
            // a consumption must match its sowing's material (this sowing used Cocopeat)
            Assert.Equal(51935, await SqlErrorAsync(env, @"INSERT INTO dbo.MaterialTransactions (MaterialStockId, TransactionType, Direction, StockQuantity, Trays, QuantityPerTray, BalanceBefore, BalanceAfter, ReferenceType, ReferenceId)
SELECT Id, 'REVERSAL', 1, 3, NULL, NULL, CurrentQuantity, CurrentQuantity + 3, 'SeedSowing', @S FROM dbo.MaterialStock WHERE Id = @Id", ("@Id", pitStock), ("@S", sowingId)));
            // a sowing's material never changes
            Assert.Equal(51911, await SqlErrorAsync(env, "UPDATE dbo.SeedSowings SET GrowingMaterialTypeId = @M WHERE Id = @Id", ("@M", env.Pitmos), ("@Id", sowingId)));
            // a new Main Office sowing names a material
            Assert.Equal(51912, await SqlErrorAsync(env, @"
INSERT INTO dbo.SeedSowings (SowingCode, SourceType, SourceSeedStockId, SourceCuttingStockId, SpeciesId, AreaId, PolyhouseId, BatchNo, SeedSourceId, CavityType,
    NumberOfTrays, QuantitySown, SeedQuantity, SowingDate, ReadyStockDays, ExpectedReadyDate, Status, SupervisorId, Remarks, CreatedDate, CreatedBy, CreatedById, GrowingMaterialTypeId)
SELECT 'E2EM' + LEFT(REPLACE(CONVERT(NVARCHAR(36), NEWID()), '-', ''), 14), SourceType, SourceSeedStockId, SourceCuttingStockId, SpeciesId, AreaId, PolyhouseId, BatchNo, SeedSourceId, CavityType,
    NumberOfTrays, QuantitySown, SeedQuantity, SowingDate, ReadyStockDays, ExpectedReadyDate, Status, SupervisorId, Remarks, SYSUTCDATETIME(), CreatedBy, CreatedById, NULL
FROM dbo.SeedSowings WHERE Id = @Id", ("@Id", sowingId)));
            // the same form token can never make a second sowing
            Assert.Equal(2601, await SqlErrorAsync(env, @"
INSERT INTO dbo.SeedSowings (SowingCode, SourceType, SourceSeedStockId, SourceCuttingStockId, SpeciesId, AreaId, PolyhouseId, BatchNo, SeedSourceId, CavityType,
    NumberOfTrays, QuantitySown, SeedQuantity, SowingDate, ReadyStockDays, ExpectedReadyDate, Status, SupervisorId, Remarks, CreatedDate, CreatedBy, CreatedById, GrowingMaterialTypeId, SubmissionToken)
SELECT 'E2EM' + LEFT(REPLACE(CONVERT(NVARCHAR(36), NEWID()), '-', ''), 14), SourceType, SourceSeedStockId, SourceCuttingStockId, SpeciesId, AreaId, PolyhouseId, BatchNo, SeedSourceId, CavityType,
    NumberOfTrays, QuantitySown, SeedQuantity, SowingDate, ReadyStockDays, ExpectedReadyDate, Status, SupervisorId, Remarks, SYSUTCDATETIME(), CreatedBy, CreatedById, GrowingMaterialTypeId, SubmissionToken
FROM dbo.SeedSowings WHERE Id = @Id", ("@Id", sowingId)));
            // invalid master values
            Assert.Equal(547, await SqlErrorAsync(env, "UPDATE dbo.MaterialTypes SET ConversionFactor = 0 WHERE Id = @M", ("@M", env.Cocopeat)));
            Assert.Equal(547, await SqlErrorAsync(env, "UPDATE dbo.MaterialConsumptionRules SET QuantityPerTray = -1 WHERE MaterialTypeId = @M", ("@M", env.Cocopeat)));

            Assert.Equal(0, await env.InvariantViolationsAsync());
        }

        [SkippableFact]
        public async Task Permissions_TheSowingSupervisorRoleHoldsViewAddHistory_NobodyHoldsManage()
        {
            var env = await OpenAsync();
            Assert.Equal("MaterialStock.Add,MaterialStock.History,MaterialStock.View", await env.ScalarAsync<string>(@"
SELECT STRING_AGG(p.Code, ',') WITHIN GROUP (ORDER BY p.Code)
FROM dbo.RolePermissions rp JOIN dbo.Roles r ON r.Id = rp.RoleId JOIN dbo.Permissions p ON p.Id = rp.PermissionId
WHERE p.Code LIKE N'MaterialStock.%' AND COALESCE(NULLIF(LTRIM(RTRIM(r.Name)), N''), r.RoleName) = N'Sowing Supervisor'"));
            Assert.Equal(0, await env.ScalarAsync<int>(@"
SELECT COUNT(*) FROM dbo.RolePermissions rp JOIN dbo.Permissions p ON p.Id = rp.PermissionId WHERE p.Code = N'MaterialStock.Manage'"));
            Assert.Equal(3, await env.ScalarAsync<int>(@"
SELECT COUNT(*) FROM dbo.RolePermissions rp JOIN dbo.Permissions p ON p.Id = rp.PermissionId WHERE p.Code LIKE N'MaterialStock.%'"));
        }
    }
}
