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
    // Material Stock x Cutting Sowing, against a REAL scratch copy (PSM_SCRATCH_CONNECTION,
    // PlantsIMS2_Scratch_*, with 2026-10-08_MaterialStock.sql and 2026-10-08_MaterialStockApprovals.sql):
    //   * a Cutting Sowing at a Main Office Polyhouse needs exactly one material and takes it from
    //     that Polyhouse; outside the Main Office it uses none;
    //   * Supervisor Approval with MORE trays than sown (overage) takes extra trays x the sowing's own
    //     kg per tray of the SAME material from the SAME Polyhouse, in the approval's transaction
    //     (100 trays = 150 kg; approved 105 -> +7.5 kg = 157.5 kg in all);
    //   * not enough material -> the whole approval is refused, nothing changes;
    //   * cancelling the approval returns its 7.5 kg, cancelling the sowing its 150 kg -- exactly the
    //     ledger quantities, never recalculated, never the other material.
    [Collection("ScratchDb")]
    public class MaterialStockCuttingE2ETests
    {
        private const string ConnectionVariable = "PSM_SCRATCH_CONNECTION";
        private const string RequiredPrefix = "PlantsIMS2_Scratch_";

        private const int MainOffice = 2, Facility5 = 2, Facility3 = 3;   // Main Office Area + two of its Polyhouses
        private const int GreenBless = 127, GreenBlessP1 = 14;            // a Mother Plant Area + Polyhouse
        private const int Recorder = 21;
        private const int MpA = 114;                                      // its variety is sown
        private const string Cavity = "150 Cavity";

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
            public int Cocopeat { get; init; }
            public int Pitmos { get; init; }
            public T Get<T>() where T : notnull => Provider.GetRequiredService<T>();
            public MaterialStockRepository Repo => Get<MaterialStockRepository>();

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

            public async Task SetTraysAsync(int areaId, int polyhouseId, decimal trays)
            {
                await ExecAsync(@"
UPDATE dbo.TrayStock SET PhysicalQuantity = 0 WHERE AreaId = @A AND PolyhouseId = @P AND TraySize = @T;
DELETE tr FROM dbo.TrayStockTransactions tr JOIN dbo.TrayStock t ON t.Id = tr.TrayStockId WHERE t.AreaId = @A AND t.PolyhouseId = @P AND t.TraySize = @T",
                    ("@A", areaId), ("@P", polyhouseId), ("@T", Cavity));
                var (ok, msg, _, _) = await Get<TrayStockRepository>().AddStockAsync(areaId, polyhouseId, Cavity, trays, DateTime.UtcNow,
                    Random.Shared.Next(1, int.MaxValue), Recorder, "mat-cut-test", "test setup", _ => true);
                Assert.True(ok, msg);
            }

            public Task<decimal> TraysAsync(int areaId, int polyhouseId)
                => ScalarAsync<decimal>("SELECT ISNULL((SELECT PhysicalQuantity FROM dbo.TrayStock WHERE AreaId = @A AND PolyhouseId = @P AND TraySize = @T), 0)",
                    ("@A", areaId), ("@P", polyhouseId), ("@T", Cavity));

            // (re)creates the variety's Cutting Stock pool in an Area with a clean ledger -- scratch only
            public async Task<int> ResetPoolAsync(int area, decimal physical)
            {
                await ExecAsync(@"
IF NOT EXISTS (SELECT 1 FROM dbo.CuttingStock WHERE SpeciesId = @S AND AreaId = @A)
    INSERT INTO dbo.CuttingStock (SpeciesId, AreaId, PhysicalQuantity, CreatedDate, CreatedBy) VALUES (@S, @A, 0, SYSUTCDATETIME(), N'mat-cut-test');
UPDATE dbo.CuttingStock SET InTransitQuantity = 0 WHERE SpeciesId = @S AND AreaId = @A;
UPDATE dbo.CuttingStock SET PhysicalQuantity = @P WHERE SpeciesId = @S AND AreaId = @A", ("@S", Species), ("@A", area), ("@P", physical));
                var id = await ScalarAsync<int>("SELECT Id FROM dbo.CuttingStock WHERE SpeciesId = @S AND AreaId = @A", ("@S", Species), ("@A", area));
                await ExecAsync("DELETE FROM dbo.CuttingStockTransactions WHERE CuttingStockId = @P", ("@P", id));
                return id;
            }

            public Task<decimal> PoolAsync(int poolId) => ScalarAsync<decimal>("SELECT PhysicalQuantity FROM dbo.CuttingStock WHERE Id = @I", ("@I", poolId));

            public Task<decimal> ReadyStockAsync(int sowingId)
                => ScalarAsync<decimal>("SELECT ISNULL((SELECT Quantity FROM dbo.ReadyStock WHERE SeedSowingId = @S), 0)", ("@S", sowingId));

            public Task<int> InvariantViolationsAsync() => ScalarAsync<int>(@"
SELECT COUNT(*) FROM dbo.MaterialStock s
WHERE s.CurrentQuantity < 0
   OR s.CurrentQuantity <> ISNULL((SELECT SUM(t.Direction * t.StockQuantity) FROM dbo.MaterialTransactions t WHERE t.MaterialStockId = s.Id), 0)");

            // a user who may be the Sowing Supervisor of a Cutting Sowing in the Area
            public async Task<int> SupervisorForAsync(int areaId)
            {
                var candidates = await Get<UserRoleRepository>().GetCuttingSowingSupervisorsAsync(areaId);
                Skip.If(candidates.Count == 0, $"No user may supervise a Cutting Sowing in Area {areaId} in this copy.");
                return candidates[0].EmployeeID;
            }

            public async Task<(bool Success, string? Message, int Id)> SowAsync(int pool, int polyhouseId, int trays, int? material, int supervisor, Guid? token = null)
                => await Get<SeedSowingRepository>().InsertFromCuttingAsync(new SeedSowing
                {
                    SourceCuttingStockId = pool, SeedQuantity = trays * DirectSowingRules.CavityCount(Cavity)!.Value, CavityType = Cavity,
                    SowingDate = DateTime.Today, PolyhouseId = polyhouseId, SupervisorId = supervisor, CreatedBy = "mat-cut-test", CreatedById = Recorder,
                    GrowingMaterialTypeId = material, SubmissionToken = token ?? Guid.NewGuid(),
                }, Recorder, _ => true, _ => true);

            public Task<(bool Success, string? Message, int Id)> ApproveAsync(int sowingId, decimal trays, int supervisor)
                => Get<ReadyConfirmationRepository>().ConfirmAsync(sowingId, trays, null, null, "mat-cut-test", "mat-cut-approver", supervisor, _ => true);

            public Task<int> RowsAsync(string referenceType, int referenceId, string type)
                => ScalarAsync<int>("SELECT COUNT(*) FROM dbo.MaterialTransactions WHERE ReferenceType = @RT AND ReferenceId = @Id AND TransactionType = @T",
                    ("@RT", referenceType), ("@Id", referenceId), ("@T", type));
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
            Skip.If(await probe.ScalarAsync<int>("SELECT COUNT(*) FROM sys.check_constraints WHERE name = 'CK_MaterialTransactions_Sowing' AND definition LIKE '%ReadyConfirmation%'") == 0,
                "Apply Database/Migrations/2026-10-08_MaterialStockApprovals.sql to the scratch copy first.");
            var species = await probe.ScalarAsync<int>("SELECT SpeciesId FROM dbo.MotherPlants WHERE Id = @M", ("@M", MpA));
            await probe.ExecAsync("UPDATE dbo.PlantSpecies SET ReadyStockDays = 60 WHERE Id = @S AND ReadyStockDays IS NULL", ("@S", species));
            await probe.ExecAsync(@"
UPDATE dbo.MaterialTypes SET ConversionFactor = 5.2 WHERE Code = 'COCOPEAT';
UPDATE dbo.MaterialTypes SET ConversionFactor = 1.0 WHERE Code = 'PITMOS';
UPDATE dbo.MaterialConsumptionRules SET QuantityPerTray = 1.5, IsActive = 1;");
            return new Env
            {
                Cs = cs!, Provider = provider, Species = species,
                Cocopeat = await probe.ScalarAsync<int>("SELECT Id FROM dbo.MaterialTypes WHERE Code = 'COCOPEAT'"),
                Pitmos = await probe.ScalarAsync<int>("SELECT Id FROM dbo.MaterialTypes WHERE Code = 'PITMOS'"),
            };
        }

        // 100 trays of 150 cavity at Facility-5 with Cocopeat; returns (env, pool, supervisor, sowing id)
        private static async Task<(Env Env, int Pool, int Supervisor, int SowingId)> SowHundredAsync(decimal cocopeat, decimal pitmos = 0, decimal pool = 40_000)
        {
            var env = await OpenAsync();
            var supervisor = await env.SupervisorForAsync(MainOffice);
            await env.SetMaterialAsync(MainOffice, Facility5, cocopeat, pitmos);
            await env.SetMaterialAsync(MainOffice, Facility3, 1000, 1000);
            await env.SetTraysAsync(MainOffice, Facility5, 500);
            var poolId = await env.ResetPoolAsync(MainOffice, pool);
            var (ok, msg, id) = await env.SowAsync(poolId, Facility5, 100, env.Cocopeat, supervisor);
            Assert.True(ok, msg);
            return (env, poolId, supervisor, id);
        }

        // ---- cutting sowing: Main Office needs one material, elsewhere none --------------------------

        [SkippableFact]
        public async Task CuttingSowing_AtMainOffice_RequiresOneMaterial_AndTakes150KgFromItsOwnPolyhouse()
        {
            var env = await OpenAsync();
            var supervisor = await env.SupervisorForAsync(MainOffice);
            await env.SetMaterialAsync(MainOffice, Facility5, 155, 40);
            await env.SetMaterialAsync(MainOffice, Facility3, 1000, 1000);
            await env.SetTraysAsync(MainOffice, Facility5, 500);
            var pool = await env.ResetPoolAsync(MainOffice, 40_000);

            var (none, noneMsg, _) = await env.SowAsync(pool, Facility5, 100, null, supervisor);
            Assert.False(none);
            Assert.Equal(MaterialStockRules.MaterialRequiredMessage, noneMsg);
            var (bogus, _, _) = await env.SowAsync(pool, Facility5, 100, 999_999, supervisor);
            Assert.False(bogus);
            Assert.Equal(40_000m, await env.PoolAsync(pool));            // nothing taken by the refusals
            Assert.Equal(500m, await env.TraysAsync(MainOffice, Facility5));

            var (ok, msg, id) = await env.SowAsync(pool, Facility5, 100, env.Cocopeat, supervisor);
            Assert.True(ok, msg);
            Assert.Equal(5m, await env.BalanceAsync(MainOffice, Facility5, env.Cocopeat));      // 155 - 100 x 1.5
            Assert.Equal(40m, await env.BalanceAsync(MainOffice, Facility5, env.Pitmos));       // the other material untouched
            Assert.Equal(1000m, await env.BalanceAsync(MainOffice, Facility3, env.Cocopeat));   // another Polyhouse untouched
            Assert.Equal(400m, await env.TraysAsync(MainOffice, Facility5));
            Assert.Equal(40_000m - 15_000m, await env.PoolAsync(pool));
            Assert.Equal(1, await env.ScalarAsync<int>(@"SELECT COUNT(*) FROM dbo.MaterialTransactions WHERE ReferenceType = 'SeedSowing' AND ReferenceId = @Id
                AND TransactionType = 'CONSUME' AND Trays = 100 AND QuantityPerTray = 1.5 AND StockQuantity = 150 AND BalanceBefore = 155 AND BalanceAfter = 5", ("@Id", id)));
        }

        [SkippableFact]
        public async Task CuttingSowing_OutsideMainOffice_UsesNoMaterial_RefusesOne_AndItsOverageTakesNone()
        {
            var env = await OpenAsync();
            var supervisor = await env.SupervisorForAsync(GreenBless);
            await env.SetTraysAsync(GreenBless, GreenBlessP1, 500);
            var pool = await env.ResetPoolAsync(GreenBless, 40_000);
            var ledgerBefore = await env.ScalarAsync<int>("SELECT COUNT(*) FROM dbo.MaterialTransactions");

            var (refused, refusedMsg, _) = await env.SowAsync(pool, GreenBlessP1, 10, env.Cocopeat, supervisor);
            Assert.False(refused);
            Assert.Equal(MaterialStockRules.NotAtThisLocationMessage, refusedMsg);

            var (ok, msg, id) = await env.SowAsync(pool, GreenBlessP1, 10, null, supervisor);
            Assert.True(ok, msg);
            Assert.Null(await env.ScalarAsync<int?>("SELECT GrowingMaterialTypeId FROM dbo.SeedSowings WHERE Id = @Id", ("@Id", id)));

            var (approved, approveMsg, confirmationId) = await env.ApproveAsync(id, 12, supervisor);   // 2 extra trays
            Assert.True(approved, approveMsg);
            Assert.Equal(498m - 10m, await env.TraysAsync(GreenBless, GreenBlessP1));                    // extra trays still taken
            Assert.Equal(0, await env.RowsAsync(MaterialStockRules.ApprovalReference, confirmationId, "CONSUME"));
            Assert.Equal(ledgerBefore, await env.ScalarAsync<int>("SELECT COUNT(*) FROM dbo.MaterialTransactions"));   // no material anywhere

            var (cancelled, cancelMsg) = await env.Get<ReadyConfirmationRepository>().CancelAsync(confirmationId, "mat-cut-test", supervisor);
            Assert.True(cancelled, cancelMsg);
            Assert.Equal(ledgerBefore, await env.ScalarAsync<int>("SELECT COUNT(*) FROM dbo.MaterialTransactions"));
        }

        // ---- overage: +7.5 kg of the SAME material from the SAME Polyhouse ---------------------------

        [SkippableFact]
        public async Task Overage_105Trays_Takes7Point5KgMore_OfTheSameMaterial_FromTheSamePolyhouse()
        {
            var (env, pool, supervisor, id) = await SowHundredAsync(cocopeat: 200, pitmos: 50);
            Assert.Equal(50m, await env.BalanceAsync(MainOffice, Facility5, env.Cocopeat));   // 200 - 150

            var (ok, msg, confirmationId) = await env.ApproveAsync(id, 105, supervisor);
            Assert.True(ok, msg);

            Assert.Equal(42.5m, await env.BalanceAsync(MainOffice, Facility5, env.Cocopeat));   // 50 - 5 x 1.5
            Assert.Equal(50m, await env.BalanceAsync(MainOffice, Facility5, env.Pitmos));       // never the other material
            Assert.Equal(1000m, await env.BalanceAsync(MainOffice, Facility3, env.Cocopeat));   // never another Polyhouse
            Assert.Equal(1000m, await env.BalanceAsync(MainOffice, Facility3, env.Pitmos));
            Assert.Equal(1, await env.ScalarAsync<int>(@"SELECT COUNT(*) FROM dbo.MaterialTransactions t JOIN dbo.MaterialStock s ON s.Id = t.MaterialStockId
                WHERE t.ReferenceType = 'ReadyConfirmation' AND t.ReferenceId = @C AND t.TransactionType = 'CONSUME' AND t.Trays = 5 AND t.QuantityPerTray = 1.5
                  AND t.StockQuantity = 7.5 AND t.BalanceBefore = 50 AND t.BalanceAfter = 42.5 AND s.MaterialTypeId = @M AND s.AreaId = @A AND s.PolyhouseId = @P",
                ("@C", confirmationId), ("@M", env.Cocopeat), ("@A", MainOffice), ("@P", Facility5)));
            // the sowing's total: 150 + 7.5 = 157.5 kg
            Assert.Equal(157.5m, await env.ScalarAsync<decimal>(@"SELECT SUM(StockQuantity) FROM dbo.MaterialTransactions WHERE TransactionType = 'CONSUME'
                AND ((ReferenceType = 'SeedSowing' AND ReferenceId = @S) OR (ReferenceType = 'ReadyConfirmation' AND ReferenceId = @C))", ("@S", id), ("@C", confirmationId)));
            // the rest of the approval happened in the same transaction
            Assert.Equal(400m - 5m, await env.TraysAsync(MainOffice, Facility5));
            Assert.Equal(40_000m - 15_000m - 750m, await env.PoolAsync(pool));
            Assert.Equal(105m * 150m, await env.ReadyStockAsync(id));
            // the sowing's Details list shows both rows
            var rows = await env.Repo.GetForSowingAsync(id);
            Assert.Equal(new[] { (MaterialStockRules.SowingReference, 150m), (MaterialStockRules.ApprovalReference, 7.5m) },
                rows.Select(r => (r.ReferenceType!, r.StockQuantity)).ToArray());
            // ...and the approval's confirmation message names it
            Assert.Equal(" Cocopeat used for the extra trays: 7.50 kg from Facility-5 (5 trays x 1.50 kg).",
                await env.Get<ReadyConfirmationRepository>().GetExtraMaterialTextAsync(confirmationId));
            Assert.Equal(0, await env.InvariantViolationsAsync());
        }

        [SkippableFact]
        public async Task Overage_NotMoreThanSown_TakesNoExtraMaterial()
        {
            var (env, _, supervisor, id) = await SowHundredAsync(cocopeat: 200);
            var (ok, msg, confirmationId) = await env.ApproveAsync(id, 100, supervisor);
            Assert.True(ok, msg);
            Assert.Equal(50m, await env.BalanceAsync(MainOffice, Facility5, env.Cocopeat));
            Assert.Equal(0, await env.RowsAsync(MaterialStockRules.ApprovalReference, confirmationId, "CONSUME"));
        }

        [SkippableFact]
        public async Task Overage_InsufficientMaterial_RefusesTheWholeApproval_NothingChanges()
        {
            var (env, pool, supervisor, id) = await SowHundredAsync(cocopeat: 155, pitmos: 500);   // 5 kg left, 7.5 needed
            var readyRowsBefore = await env.ScalarAsync<int>("SELECT COUNT(*) FROM dbo.ReadyConfirmations WHERE SeedSowingId = @S", ("@S", id));

            var (ok, msg, _) = await env.ApproveAsync(id, 105, supervisor);

            Assert.False(ok);
            Assert.Equal("Insufficient Cocopeat stock in " + await env.ScalarAsync<string>("SELECT LTRIM(RTRIM(Name)) FROM dbo.Polyhouses WHERE Id = @P", ("@P", Facility5))
                + ". Available: 5.00 kg. Required: 7.50 kg.", msg);
            Assert.Equal(5m, await env.BalanceAsync(MainOffice, Facility5, env.Cocopeat));
            Assert.Equal(500m, await env.BalanceAsync(MainOffice, Facility5, env.Pitmos));       // never falls back to the other material
            Assert.Equal(400m, await env.TraysAsync(MainOffice, Facility5));                      // extra trays not taken
            Assert.Equal(25_000m, await env.PoolAsync(pool));                                    // extra cuttings not taken
            Assert.Equal(0m, await env.ReadyStockAsync(id));                                     // no Ready Stock
            Assert.Equal(readyRowsBefore, await env.ScalarAsync<int>("SELECT COUNT(*) FROM dbo.ReadyConfirmations WHERE SeedSowingId = @S", ("@S", id)));
            Assert.Equal("Sown", await env.ScalarAsync<string>("SELECT Status FROM dbo.SeedSowings WHERE Id = @S", ("@S", id)));
            Assert.Equal(0m, await env.ScalarAsync<decimal>("SELECT ConfirmedReadyQuantity FROM dbo.SeedSowings WHERE Id = @S", ("@S", id)));
            Assert.Equal(0, await env.ScalarAsync<int>("SELECT COUNT(*) FROM dbo.MaterialTransactions WHERE ReferenceType = 'ReadyConfirmation'"
                + " AND ReferenceId NOT IN (SELECT Id FROM dbo.ReadyConfirmations)"));

            // with enough stock the same approval succeeds
            var (added, addMsg, _) = await env.Repo.AdjustAsync(MainOffice, Facility5, env.Cocopeat, 1, 2.5m, "top up", Guid.NewGuid(), Recorder, _ => true);
            Assert.True(added, addMsg);
            var (ok2, msg2, _) = await env.ApproveAsync(id, 105, supervisor);
            Assert.True(ok2, msg2);
            Assert.Equal(0m, await env.BalanceAsync(MainOffice, Facility5, env.Cocopeat));       // exactly used up, never negative
            Assert.Equal(0, await env.InvariantViolationsAsync());
        }

        // ---- cancellation: exactly 7.5 + 150 = 157.5 kg back, never recalculated ----------------------

        [SkippableFact]
        public async Task Cancel_ApprovalReturns7Point5_SowingReturns150_Total157Point5_EvenAfterTheRuleChanged()
        {
            var (env, _, supervisor, id) = await SowHundredAsync(cocopeat: 200, pitmos: 50);
            var (ok, msg, confirmationId) = await env.ApproveAsync(id, 105, supervisor);
            Assert.True(ok, msg);
            Assert.Equal(42.5m, await env.BalanceAsync(MainOffice, Facility5, env.Cocopeat));
            var rule = await env.ScalarAsync<int>("SELECT Id FROM dbo.MaterialConsumptionRules WHERE MaterialTypeId = @M AND CavityType = @C", ("@M", env.Cocopeat), ("@C", Cavity));
            try
            {
                Assert.Null(await env.Repo.UpdateQuantityPerTrayAsync(rule, 1.25m, Recorder));   // the value changes afterwards

                var (c1, c1Msg) = await env.Get<ReadyConfirmationRepository>().CancelAsync(confirmationId, "mat-cut-test", supervisor);
                Assert.True(c1, c1Msg);
                Assert.Equal(50m, await env.BalanceAsync(MainOffice, Facility5, env.Cocopeat));   // +7.5 (not 5 x 1.25)
                Assert.Equal(1, await env.ScalarAsync<int>(@"SELECT COUNT(*) FROM dbo.MaterialTransactions WHERE ReferenceType = 'ReadyConfirmation' AND ReferenceId = @C
                    AND TransactionType = 'REVERSAL' AND StockQuantity = 7.5", ("@C", confirmationId)));

                var (c2, c2Msg) = await env.Get<SeedSowingRepository>().CancelAsync(id, "mat-cut-test", Recorder);
                Assert.True(c2, c2Msg);
                Assert.Equal(200m, await env.BalanceAsync(MainOffice, Facility5, env.Cocopeat));  // +150 (not 100 x 1.25): 157.5 back in all
                Assert.Equal(50m, await env.BalanceAsync(MainOffice, Facility5, env.Pitmos));     // never the other material
                Assert.Equal(1000m, await env.BalanceAsync(MainOffice, Facility3, env.Cocopeat)); // never another Polyhouse

                // history stays: two consumptions, two reversals, unchanged
                var rows = await env.Repo.GetForSowingAsync(id);
                Assert.Equal(new[] { ("CONSUME", 150m), ("CONSUME", 7.5m), ("REVERSAL", 7.5m), ("REVERSAL", 150m) },
                    rows.Select(r => (r.TransactionType, r.StockQuantity)).ToArray());
                Assert.Equal(1.5m, await env.ScalarAsync<decimal>("SELECT QuantityPerTray FROM dbo.MaterialTransactions WHERE ReferenceType = 'SeedSowing' AND ReferenceId = @S AND TransactionType = 'CONSUME'", ("@S", id)));

                // never twice
                Assert.False((await env.Get<ReadyConfirmationRepository>().CancelAsync(confirmationId, "mat-cut-test", supervisor)).Success);
                Assert.Equal(200m, await env.BalanceAsync(MainOffice, Facility5, env.Cocopeat));
                Assert.Equal(0, await env.InvariantViolationsAsync());
            }
            finally
            {
                await env.ExecAsync("UPDATE dbo.MaterialConsumptionRules SET QuantityPerTray = 1.5");
            }
        }

        [SkippableFact]
        public async Task ConfigurationChange_DoesNotChangeOldSowings_AndTheOverageUsesTheSowingsOwnRate()
        {
            var (env, _, supervisor, id) = await SowHundredAsync(cocopeat: 300);
            var rule = await env.ScalarAsync<int>("SELECT Id FROM dbo.MaterialConsumptionRules WHERE MaterialTypeId = @M AND CavityType = @C", ("@M", env.Cocopeat), ("@C", Cavity));
            try
            {
                Assert.Null(await env.Repo.UpdateQuantityPerTrayAsync(rule, 1.25m, Recorder));
                // the old sowing's row is untouched
                Assert.Equal(150m, await env.ScalarAsync<decimal>("SELECT StockQuantity FROM dbo.MaterialTransactions WHERE ReferenceType = 'SeedSowing' AND ReferenceId = @S", ("@S", id)));
                Assert.Equal(150m, await env.BalanceAsync(MainOffice, Facility5, env.Cocopeat));

                // its overage is charged at ITS rate (1.5), so the sowing stays 105 x 1.5 = 157.5
                var (ok, msg, confirmationId) = await env.ApproveAsync(id, 105, supervisor);
                Assert.True(ok, msg);
                Assert.Equal(1.5m, await env.ScalarAsync<decimal>("SELECT QuantityPerTray FROM dbo.MaterialTransactions WHERE ReferenceType = 'ReadyConfirmation' AND ReferenceId = @C", ("@C", confirmationId)));
                Assert.Equal(142.5m, await env.BalanceAsync(MainOffice, Facility5, env.Cocopeat));

                // a NEW sowing uses the new value: 10 x 1.25 = 12.5
                var pool2 = await env.ResetPoolAsync(MainOffice, 40_000);
                var (ok2, msg2, _) = await env.SowAsync(pool2, Facility5, 10, env.Cocopeat, supervisor);
                Assert.True(ok2, msg2);
                Assert.Equal(130m, await env.BalanceAsync(MainOffice, Facility5, env.Cocopeat));
            }
            finally
            {
                await env.ExecAsync("UPDATE dbo.MaterialConsumptionRules SET QuantityPerTray = 1.5");
            }
        }

        // ---- concurrency: two overage approvals racing for the last kilograms -------------------------

        [SkippableFact]
        public async Task Concurrency_TwoOverageApprovals_OnlyTheOneTheStockCoversSucceeds_NeverNegative()
        {
            var env = await OpenAsync();
            var supervisor = await env.SupervisorForAsync(MainOffice);
            await env.SetMaterialAsync(MainOffice, Facility5, 300 + 7.5m, 0);   // two sowings + ONE overage of 5 trays
            await env.SetMaterialAsync(MainOffice, Facility3, 1000, 1000);
            await env.SetTraysAsync(MainOffice, Facility5, 500);
            var pool = await env.ResetPoolAsync(MainOffice, 80_000);
            var (ok1, m1, s1) = await env.SowAsync(pool, Facility5, 100, env.Cocopeat, supervisor);
            var (ok2, m2, s2) = await env.SowAsync(pool, Facility5, 100, env.Cocopeat, supervisor);
            Assert.True(ok1, m1);
            Assert.True(ok2, m2);
            Assert.Equal(7.5m, await env.BalanceAsync(MainOffice, Facility5, env.Cocopeat));

            var results = await Task.WhenAll(env.ApproveAsync(s1, 105, supervisor), env.ApproveAsync(s2, 105, supervisor));

            Assert.Equal(1, results.Count(r => r.Success));
            Assert.Contains(results, r => !r.Success && r.Message!.StartsWith("Insufficient Cocopeat stock"));
            Assert.Equal(0m, await env.BalanceAsync(MainOffice, Facility5, env.Cocopeat));
            Assert.Equal(1, await env.ScalarAsync<int>("SELECT COUNT(*) FROM dbo.ReadyConfirmations WHERE SeedSowingId IN (@A, @B)", ("@A", s1), ("@B", s2)));
            Assert.Equal(0, await env.InvariantViolationsAsync());
        }

        // ---- the database's own rule for approval rows -----------------------------------------------

        [SkippableFact]
        public async Task Database_AnApprovalRowMustMatchItsSowingsMaterialAndPolyhouse()
        {
            var (env, _, supervisor, id) = await SowHundredAsync(cocopeat: 200, pitmos: 50);
            var (ok, msg, confirmationId) = await env.ApproveAsync(id, 100, supervisor);   // no overage: no row yet
            Assert.True(ok, msg);
            var pitStock = await env.ScalarAsync<int>("SELECT Id FROM dbo.MaterialStock WHERE AreaId = @A AND PolyhouseId = @P AND MaterialTypeId = @M", ("@A", MainOffice), ("@P", Facility5), ("@M", env.Pitmos));
            var otherPh = await env.ScalarAsync<int>("SELECT Id FROM dbo.MaterialStock WHERE AreaId = @A AND PolyhouseId = @P AND MaterialTypeId = @M", ("@A", MainOffice), ("@P", Facility3), ("@M", env.Cocopeat));

            async Task<int> Forge(int stockId, string refType, int refId)
            {
                var ex = await Assert.ThrowsAsync<SqlException>(() => env.ExecAsync(@"
INSERT INTO dbo.MaterialTransactions (MaterialStockId, TransactionType, Direction, StockQuantity, Trays, QuantityPerTray, BalanceBefore, BalanceAfter, ReferenceType, ReferenceId)
SELECT Id, 'CONSUME', -1, 1.5, 1, 1.5, CurrentQuantity, CurrentQuantity - 1.5, @RT, @R FROM dbo.MaterialStock WHERE Id = @Id",
                    ("@Id", stockId), ("@RT", refType), ("@R", refId)));
                return ex.Number;
            }
            Assert.Equal(51935, await Forge(pitStock, "ReadyConfirmation", confirmationId));   // the other material
            Assert.Equal(51935, await Forge(otherPh, "ReadyConfirmation", confirmationId));    // another Polyhouse
            Assert.Equal(51935, await Forge(pitStock, "ReadyConfirmation", -1));               // no such approval
            Assert.Equal(547, await Forge(pitStock, "Bogus", id));                              // unknown reference type
            Assert.Equal(50m, await env.BalanceAsync(MainOffice, Facility5, env.Pitmos));
            Assert.Equal(0, await env.InvariantViolationsAsync());
        }
    }
}
