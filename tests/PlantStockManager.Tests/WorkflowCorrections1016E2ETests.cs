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
    // 2026-10-16 workflow corrections, end-to-end against a SCRATCH COPY (PlantsIMS2_Scratch_* only; SKIPPED without
    // PSM_SCRATCH_CONNECTION). Each test states which migrations it needs and is SKIPPED without them:
    //   1. Add Empty Trays: Area pool, ledger = balance, duplicate / concurrent submits, refusals.     (10-14)
    //   2. Pot Production from Main Office seedlings: lineage, conservation, duplicates, concurrency.   (10-15, 10-16 token)
    //   3. Bed Sowing batches approved only by the designated approver (Maya Shinde).                  (10-16 approver)
    //   4. Mother Plant transplanting uses no Cocopeat / Pitmos.                                       (10-14)
    //   5. Mother Plant transplanting records the untransplanted remainder as waste.                   (10-14, 10-16 waste)
    //   6. Transfer / transplant to the Area: a posted Polyhouse is never stored; Pot Production stock is never
    //      transplanted, not even by a direct insert.                                                  (10-14, 10-16 waste)
    [Collection("ScratchDb")]
    public class WorkflowCorrections1016E2ETests
    {
        private const string ConnectionVariable = "PSM_SCRATCH_CONNECTION";
        private const string RequiredPrefix = "PlantsIMS2_Scratch_";
        private const int MainArea = 2, MainPolyhouse = 4;            // Main-Office-Areas / Facility-4 (as MotherPlantAreaInventoryE2ETests)
        private const int MpArea = 127, MpP1 = 14;                     // Green Bless Nursery (Mother Plant) / P-1
        private const int SowingSupervisor = 9, Recorder = 21;
        private const int ReadyDays = 30;
        private static readonly MotherPlantTransferRepository.Actor Actor = new(Recorder, "wf1016-test");
        private static bool Any(int _) => true;

        private sealed class StaticMonitor<T> : IOptionsMonitor<T>
        {
            public StaticMonitor(T value) => CurrentValue = value;
            public T CurrentValue { get; }
            public T Get(string? name) => CurrentValue;
            public IDisposable? OnChange(Action<T, string?> listener) => null;
        }

        private sealed class Env
        {
            public string ConnectionString = "";
            public ServiceProvider Provider = null!;
            public int Cocopeat;
            public T Get<T>() where T : notnull => Provider.GetRequiredService<T>();
            public MotherPlantTransferRepository Transfers => Get<MotherPlantTransferRepository>();
            public TransplantRepository Transplants => Get<TransplantRepository>();
            public PotBatchRepository Pots => Get<PotBatchRepository>();

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

            public Task<bool> ProbeAsync(string probeSql) => ScalarAsync<int>(probeSql).ContinueWith(t => t.Result == 1);

            public Task<decimal> AreaTraysAsync(int area, string cavity)
                => ScalarAsync<decimal>("SELECT ISNULL(SUM(PhysicalQuantity), 0) FROM dbo.TrayStock WHERE AreaId = @A AND PolyhouseId IS NULL AND TraySize = @C AND IsActive = 1",
                    ("@A", area), ("@C", cavity));

            public Task<decimal> AvailableAsync(int readyStockId)
                => ScalarAsync<decimal>("SELECT Quantity - ReservedQuantity - DispatchedQuantity - InTransitQuantity FROM dbo.ReadyStock WHERE Id = @R", ("@R", readyStockId));

            // the Area's growing material: balance + ledger rows (Mother Plant transplanting must never move either)
            public async Task<(decimal Balance, int Rows)> MaterialAsync(int area)
                => (await ScalarAsync<decimal>("SELECT ISNULL(SUM(CurrentQuantity), 0) FROM dbo.MaterialStock WHERE AreaId = @A", ("@A", area)),
                    await ScalarAsync<int>("SELECT COUNT(*) FROM dbo.MaterialTransactions t JOIN dbo.MaterialStock s ON s.Id = t.MaterialStockId WHERE s.AreaId = @A", ("@A", area)));
        }

        private static async Task<Env> OpenAsync(params string[] requiredProbes)
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
                foreach (var probe in new[] { BedSowingSchema.TraysProbeSql, MotherPlantAreaSchema.ProbeSql }.Concat(requiredProbes))
                {
                    using var p = new SqlCommand(probe, conn);
                    Skip.If((int)(await p.ExecuteScalarAsync())! != 1, $"A migration this test needs is not applied to this scratch database ({probe}).");
                }
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
            var env = new Env { ConnectionString = cs!, Provider = services.BuildServiceProvider() };
            env.Cocopeat = await env.ScalarAsync<int>("SELECT Id FROM dbo.MaterialTypes WHERE Code = 'COCOPEAT'");
            return env;
        }

        private sealed record Variety(int SpeciesId, int SeedStockId);

        private static async Task<Variety> NewVarietyAsync(Env env, int? readyDays = ReadyDays)
        {
            var name = "ZZTEST Wf16 " + Guid.NewGuid().ToString("N")[..10];
            var speciesId = await env.ScalarAsync<int>(@"INSERT INTO dbo.PlantSpecies (PlantTypeId, Name, ReadyStockDays)
VALUES ((SELECT MIN(Id) FROM dbo.PlantTypes), @N, @D); SELECT CAST(SCOPE_IDENTITY() AS INT)", ("@N", name), ("@D", readyDays));
            var lot = await env.ScalarAsync<int>(@"INSERT INTO dbo.SeedStock (SpeciesId, AreaId, BatchNo, Unit, PhysicalQuantity, CreatedBy)
VALUES (@S, @A, @B, N'Seeds', 100000, N'wf1016-test'); SELECT CAST(SCOPE_IDENTITY() AS INT)", ("@S", speciesId), ("@A", MainArea), ("@B", "LOT-" + name[^10..]));
            return new Variety(speciesId, lot);
        }

        // A Main Area sowing of `plants` 24 Cavity seedlings approved in full -> its Ready Stock batch id.
        private static async Task<int> ApprovedMainBatchAsync(Env env, Variety v, int plants = 480)
        {
            const string cavity = "24 Cavity";
            var (tOk, tMsg, _, _) = await env.Get<TrayStockRepository>().AddStockAsync(MainArea, MainPolyhouse, cavity, plants / 24 + 5, DateTime.UtcNow,
                Random.Shared.Next(1, int.MaxValue), SowingSupervisor, "wf1016-test", "wf1016 setup", Any);
            Assert.True(tOk, tMsg);
            var (mOk, mMsg, _) = await env.Get<MaterialStockRepository>().AdjustAsync(MainArea, MainPolyhouse, env.Cocopeat, 1, 500m, "wf1016 setup", Guid.NewGuid(), Recorder, Any);
            Assert.True(mOk, mMsg);
            var sowing = new SeedSowing
            {
                SourceSeedStockId = v.SeedStockId, SeedQuantity = plants, CavityType = cavity, SowingDate = AppClock.Today.AddDays(-40),
                PolyhouseId = MainPolyhouse, SupervisorId = SowingSupervisor, CreatedBy = "wf1016-test", CreatedById = Recorder, GrowingMaterialTypeId = env.Cocopeat
            };
            var (ok, msg, sowingId) = await env.Get<SeedSowingRepository>().InsertAsync(sowing, Recorder, Any);
            Assert.True(ok, msg);
            var (cOk, cMsg, _) = await env.Get<ReadyConfirmationRepository>().ConfirmAsync(sowingId, plants / 24, null, null, null, "wf1016-test", SowingSupervisor, Any);
            Assert.True(cOk, cMsg);
            return await env.ScalarAsync<int>("SELECT Id FROM dbo.ReadyStock WHERE SeedSowingId = @S AND OriginType = N'Sowing'", ("@S", sowingId));
        }

        // Sent `sent` plants to the Mother Plant Area with a purpose, `received` of them received -> (transfer, received batch).
        // daysAgo: the transfer and its receipt are dated that many days back (business dates).
        private static async Task<(int Transfer, int Received)> ReceivedAsync(Env env, Variety v, string purpose, decimal sent = 240, decimal? received = null, int daysAgo = 0)
        {
            var main = await ApprovedMainBatchAsync(env, v);
            var (ok, msg, transferId, _) = await env.Transfers.CreateAsync(
                new MotherPlantTransferRepository.CreateRequest(main, sent, MpArea, null, AppClock.Today.AddDays(-daysAgo), "wf1016 test", Guid.NewGuid(), purpose), Actor, Any);
            Assert.True(ok, msg);
            var got = received ?? sent;
            var (rOk, rMsg, batch) = await env.Transfers.ReceiveAsync(transferId, got, got < sent ? "wf1016 test shortfall" : null, Actor, Any,
                daysAgo > 0 ? AppClock.Today.AddDays(-daysAgo) : null, daysAgo > 0 ? "wf1016 back-dated receipt" : null);
            Assert.True(rOk, rMsg);
            return (transferId, batch!.Value);
        }

        // daysAgo: the trays' business arrival (a back-dated transplant may only use trays that were there by then)
        private static async Task AddAreaTraysAsync(Env env, string cavity, decimal trays, int daysAgo = 0)
        {
            var (ok, msg, _, _) = await env.Get<TrayStockRepository>().AddStockAsync(MpArea, null, cavity, trays, DateTime.UtcNow.AddHours(-1).AddDays(-daysAgo),
                Random.Shared.Next(1, int.MaxValue), SowingSupervisor, "wf1016-test", "wf1016 trays", Any);
            Assert.True(ok, msg);
        }

        // A transplant request whose transplanted / waste figures follow the same rule the server applies.
        private static TransplantRepository.CreateRequest Process(int source, decimal processed, string cavity, string? reason = null,
            bool discard = false, Guid? token = null, int? polyhouse = null, DateTime? date = null)
        {
            var size = DirectSowingRules.CavityCount(cavity) ?? 1;
            var transplanted = discard ? 0 : decimal.Floor(processed / size) * size;
            return new(source, MpArea, polyhouse, cavity, processed, transplanted, processed - transplanted, date ?? AppClock.Today,
                "wf1016 transplant", token ?? Guid.NewGuid(), reason, discard);
        }

        // =====================================================================================================
        // 1. Add Empty Trays
        // =====================================================================================================

        [SkippableFact]
        public async Task AddEmptyTrays_LedgerEqualsBalance_SameFormOnceEvenConcurrently_RefusalsChangeNothing_UsedByTheNextTransplant()
        {
            var env = await OpenAsync();
            var repo = env.Get<TrayStockRepository>();
            const string cavity = "102 Cavity";
            var before = await env.AreaTraysAsync(MpArea, cavity);

            // the same form submitted 5 times at once: added once
            var token = Random.Shared.Next(1, int.MaxValue);
            var same = await Task.WhenAll(Enumerable.Range(0, 5).Select(_ =>
                repo.AddStockAsync(MpArea, null, cavity, 10, DateTime.UtcNow, token, SowingSupervisor, "wf1016-test", "wf1016 same form", Any)));
            Assert.All(same, r => Assert.True(r.Success, r.Message));
            Assert.Equal(1, same.Count(r => !r.Duplicate));
            Assert.Equal(before + 10, await env.AreaTraysAsync(MpArea, cavity));
            // four different forms at once: all four added
            var many = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ =>
                repo.AddStockAsync(MpArea, null, cavity, 3, DateTime.UtcNow, Random.Shared.Next(1, int.MaxValue), SowingSupervisor, "wf1016-test", null, Any)));
            Assert.All(many, r => Assert.True(r.Success && !r.Duplicate, r.Message));
            Assert.Equal(before + 22, await env.AreaTraysAsync(MpArea, cavity));

            // the pool's ledger explains its balance exactly; no Polyhouse pool exists in a Mother Plant Area
            var pool = await env.ScalarAsync<int>("SELECT Id FROM dbo.TrayStock WHERE AreaId = @A AND PolyhouseId IS NULL AND TraySize = @C AND IsActive = 1", ("@A", MpArea), ("@C", cavity));
            Assert.Equal(await env.ScalarAsync<decimal>("SELECT PhysicalQuantity FROM dbo.TrayStock WHERE Id = @P", ("@P", pool)),
                await env.ScalarAsync<decimal>("SELECT ISNULL(SUM(Quantity), 0) FROM dbo.TrayStockTransactions WHERE TrayStockId = @P", ("@P", pool)));
            Assert.Equal(1, await env.ScalarAsync<int>("SELECT COUNT(*) FROM dbo.TrayStockTransactions WHERE TrayStockId = @P AND ReferenceType = @R AND ReferenceId = @T",
                ("@P", pool), ("@R", TrayStockRepository.AddReferenceType), ("@T", token)));

            // refused, nothing changes: bad quantities, an unauthorized Area, an unknown cavity, an expired form
            foreach (var bad in new[] { 0m, -5m, 1.5m })
                Assert.False((await repo.AddStockAsync(MpArea, null, cavity, bad, DateTime.UtcNow, Random.Shared.Next(1, int.MaxValue), SowingSupervisor, "x", null, Any)).Success);
            var (uOk, uMsg, _, _) = await repo.AddStockAsync(MpArea, null, cavity, 5, DateTime.UtcNow, Random.Shared.Next(1, int.MaxValue), SowingSupervisor, "x", null, _ => false);
            Assert.False(uOk);
            Assert.Contains("not authorized", uMsg);
            Assert.False((await repo.AddStockAsync(MpArea, null, "100 Cavity", 5, DateTime.UtcNow, Random.Shared.Next(1, int.MaxValue), SowingSupervisor, "x", null, Any)).Success);
            Assert.False((await repo.AddStockAsync(MpArea, null, cavity, 5, DateTime.UtcNow, 0, SowingSupervisor, "x", null, Any)).Success);
            Assert.Equal(before + 22, await env.AreaTraysAsync(MpArea, cavity));

            // the new balance is what the next operation in the Area sees and uses
            var v = await NewVarietyAsync(env);
            var (_, received) = await ReceivedAsync(env, v, MotherPlantTransferRules.PurposeTrayTransplant, 240);
            var (tOk, tMsg, _, _) = await env.Transplants.CreateAsync(Process(received, 204, cavity), Actor, Any);
            Assert.True(tOk, tMsg);
            Assert.Equal(before + 20, await env.AreaTraysAsync(MpArea, cavity));
        }

        // =====================================================================================================
        // 2. Pot Production from Main Office seedlings (and Cutting, unchanged)
        // =====================================================================================================

        private sealed record PotFixture(Env Env, Variety V, int Received, int Confirmer, string Size);

        private static async Task<PotFixture> PotFixtureAsync(decimal plants = 240)
        {
            var env = await OpenAsync(PotBatchSchema.ProbeSql, WorkflowCorrectionsSchema.PotTokenProbeSql);
            var confirmer = (await env.Get<UserRoleRepository>().GetReadyConfirmersAsync(MpArea)).Select(u => u.EmployeeID).FirstOrDefault();
            Skip.If(confirmer == 0, "No user may confirm pot batches READY in the Mother Plant Area of this copy.");
            var size = await env.ScalarAsync<string>("SELECT TOP 1 Name FROM dbo.PotSizes WHERE IsActive = 1 ORDER BY Id");
            Skip.If(size == null, "No active pot size in this copy.");
            var v = await NewVarietyAsync(env);
            var (_, received) = await ReceivedAsync(env, v, MotherPlantTransferRules.PurposePotProduction, plants);
            var (ok, msg, _) = await env.Get<EmptyPotInventoryRepository>().AddAreaStockAsync(MpArea, size!, plants + 100, AppClock.Today, "wf1016 pots",
                Recorder, "wf1016-test", EmptyPotStockRules.MotherPlantAreaType);
            Assert.True(ok, msg);
            return new PotFixture(env, v, received, confirmer, size!);
        }

        private static PotProductionBatch Seedlings(PotFixture f, decimal qty, Guid? token = null, int? source = null) => new()
        {
            SourceReadyStockId = source ?? f.Received, CuttingAllocated = qty, PotSize = f.Size, ProductionStartDate = AppClock.Today,
            ExpectedReadyDate = AppClock.Today.AddDays(30), SupervisorId = f.Confirmer, Remarks = "wf1016 pots", CreatedBy = "wf1016-test",
            SubmissionToken = token ?? Guid.NewGuid()
        };

        [SkippableFact]
        public async Task PotsFromMainOfficeSeedlings_Listed_Lineage_Conserved_SameFormOnce_ConcurrentNeverOverdrawn()
        {
            var f = await PotFixtureAsync(240);
            var env = f.Env;

            // listed as a Main Office seedling source of its Area, with its transfer and Main Office batch; tray stock is not
            var (_, forTrays) = await ReceivedAsync(env, f.V, MotherPlantTransferRules.PurposeTrayTransplant, 48);
            var sources = await env.Pots.GetMainOfficeSeedlingSourcesAsync(new[] { MpArea });
            var listed = Assert.Single(sources, s => s.ReadyStockId == f.Received);
            Assert.Equal((240m, MpArea), (listed.Available, listed.AreaId));
            Assert.Equal("Main-Office-Areas", listed.SourceAreaName);
            Assert.DoesNotContain(sources, s => s.ReadyStockId == forTrays);
            Assert.Empty(await env.Pots.GetMainOfficeSeedlingSourcesAsync(Array.Empty<int>()));                    // no Area -> nothing
            Assert.DoesNotContain(await env.Pots.GetMainOfficeSeedlingSourcesAsync(new[] { MainArea }), s => s.ReadyStockId == f.Received);

            // the same form twice: one batch, the seedlings taken once
            var token = Guid.NewGuid();
            var (ok, msg, batchId) = await env.Pots.CreateFromReadyStockAsync(Seedlings(f, 100, token), Recorder, Any);
            Assert.True(ok, msg);
            var (dOk, dMsg, dupId) = await env.Pots.CreateFromReadyStockAsync(Seedlings(f, 100, token), Recorder, Any);
            Assert.True(dOk);
            Assert.Equal((batchId, PotBatchRepository.AlreadyStartedMessage), (dupId, dMsg));
            Assert.Equal(140m, await env.AvailableAsync(f.Received));

            // lineage by columns: the batch names its Ready Stock source; the ledger row names the batch
            var b = (await env.Pots.GetByIdAsync(batchId))!;
            Assert.Equal(((int?)null, (int?)f.Received), (b.SourceCuttingStockId, b.SourceReadyStockId));
            Assert.Equal(token, await env.ScalarAsync<Guid>("SELECT SubmissionToken FROM dbo.PotProductionBatches WHERE Id = @B", ("@B", batchId)));
            Assert.Equal(-100m, await env.ScalarAsync<decimal>(@"SELECT SUM(Quantity) FROM dbo.ReadyStockTransactions
WHERE ReadyStockId = @R AND TransactionType = N'PotProductionOut' AND ReferenceType = N'PotProductionBatch' AND ReferenceId = @B", ("@R", f.Received), ("@B", batchId)));

            // the same NEW form submitted 4 times at once: one batch
            var token2 = Guid.NewGuid();
            var burst = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => env.Pots.CreateFromReadyStockAsync(Seedlings(f, 20, token2), Recorder, Any)));
            Assert.All(burst, r => Assert.True(r.Success, r.Message));
            Assert.Single(burst.Select(r => r.Id).Distinct());
            Assert.Equal(120m, await env.AvailableAsync(f.Received));

            // five different forms at once, 50 each, 120 available: at most two succeed, the batch never goes negative
            var race = await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => env.Pots.CreateFromReadyStockAsync(Seedlings(f, 50), Recorder, Any)));
            var started = race.Count(r => r.Success);
            Assert.Equal(2, started);
            Assert.All(race.Where(r => !r.Success), r => Assert.Contains("Only", r.Message));
            Assert.Equal(20m, await env.AvailableAsync(f.Received));

            // conservation: received = available + every allocation to pot batches
            var allocated = await env.ScalarAsync<decimal>("SELECT SUM(CuttingAllocated) FROM dbo.PotProductionBatches WHERE SourceReadyStockId = @R AND Status <> N'Cancelled'", ("@R", f.Received));
            Assert.Equal(240m, await env.AvailableAsync(f.Received) + allocated);
            Assert.Equal(240m + await env.ScalarAsync<decimal>("SELECT SUM(Quantity) FROM dbo.ReadyStockTransactions WHERE ReadyStockId = @R AND TransactionType = N'PotProductionOut'", ("@R", f.Received)),
                await env.AvailableAsync(f.Received));
        }

        [SkippableFact]
        public async Task PotsFromMainOfficeSeedlings_Refused_Insufficient_UnauthorizedArea_TraysPurpose_NotMainOffice_ChangesNothing()
        {
            var f = await PotFixtureAsync(96);
            var env = f.Env;
            var batches0 = await env.ScalarAsync<int>("SELECT COUNT(*) FROM dbo.PotProductionBatches");
            Assert.False((await env.Pots.CreateFromReadyStockAsync(Seedlings(f, 97), Recorder, Any)).Success);                     // more than received
            var (aOk, aMsg, _) = await env.Pots.CreateFromReadyStockAsync(Seedlings(f, 10), Recorder, _ => false);                  // not the user's Area
            Assert.False(aOk);
            Assert.Contains("not authorized", aMsg);
            var (_, forTrays) = await ReceivedAsync(env, f.V, MotherPlantTransferRules.PurposeTrayTransplant, 48);
            Assert.False((await env.Pots.CreateFromReadyStockAsync(Seedlings(f, 10, source: forTrays), Recorder, Any)).Success);   // tray-transplant stock
            Assert.Equal(96m, await env.AvailableAsync(f.Received));
            Assert.Equal(48m, await env.AvailableAsync(forTrays));
            Assert.Equal(batches0, await env.ScalarAsync<int>("SELECT COUNT(*) FROM dbo.PotProductionBatches"));
        }

        [SkippableFact]
        public async Task CuttingToPots_StillWorks_AndTheSameFormStartsOneBatch()
        {
            var env = await OpenAsync(PotBatchSchema.ProbeSql, WorkflowCorrectionsSchema.PotTokenProbeSql);
            var cutting = await env.ScalarAsync<int>("SELECT TOP 1 Id FROM dbo.CuttingStock WHERE PhysicalQuantity - InTransitQuantity >= 20 ORDER BY Id DESC");
            Skip.If(cutting == 0, "No Cutting Stock with 20 available cuttings in this copy.");
            var confirmer = (await env.Get<UserRoleRepository>().GetReadyConfirmersAsync(MpArea)).Select(u => u.EmployeeID).FirstOrDefault();
            var size = await env.ScalarAsync<string>("SELECT TOP 1 Name FROM dbo.PotSizes WHERE IsActive = 1 ORDER BY Id");
            Skip.If(confirmer == 0 || size == null, "No READY confirmer or pot size in this copy.");
            Assert.True((await env.Get<EmptyPotInventoryRepository>().AddAreaStockAsync(MpArea, size!, 20, AppClock.Today, "wf1016 cutting pots",
                Recorder, "wf1016-test", EmptyPotStockRules.MotherPlantAreaType)).Success);
            var available0 = await env.ScalarAsync<decimal>("SELECT PhysicalQuantity - InTransitQuantity FROM dbo.CuttingStock WHERE Id = @C", ("@C", cutting));
            PotProductionBatch Entry(Guid token) => new()
            {
                SourceCuttingStockId = cutting, CuttingAllocated = 10, AreaId = MpArea, PotSize = size!, ProductionStartDate = AppClock.Today,
                ExpectedReadyDate = AppClock.Today.AddDays(30), SupervisorId = confirmer, CreatedBy = "wf1016-test", SubmissionToken = token
            };
            var token = Guid.NewGuid();
            var (ok, msg, id) = await env.Pots.CreateAsync(Entry(token), Recorder, Any);
            Assert.True(ok, msg);
            var (dOk, dMsg, dupId) = await env.Pots.CreateAsync(Entry(token), Recorder, Any);
            Assert.True(dOk);
            Assert.Equal((id, PotBatchRepository.AlreadyStartedMessage), (dupId, dMsg));
            Assert.Equal(available0 - 10, await env.ScalarAsync<decimal>("SELECT PhysicalQuantity - InTransitQuantity FROM dbo.CuttingStock WHERE Id = @C", ("@C", cutting)));
            var b = (await env.Pots.GetByIdAsync(id))!;
            Assert.Equal(((int?)cutting, (int?)null), (b.SourceCuttingStockId, b.SourceReadyStockId));
            Assert.True((await env.Pots.CancelAsync(id, Recorder, "wf1016-test")).Success);                  // leave the cuttings as they were
            Assert.Equal(available0, await env.ScalarAsync<decimal>("SELECT PhysicalQuantity - InTransitQuantity FROM dbo.CuttingStock WHERE Id = @C", ("@C", cutting)));
        }

        // =====================================================================================================
        // 3. Bed Sowing tray batches: the designated approver only
        // =====================================================================================================

        [SkippableFact]
        public async Task BedSowingBatch_OnlyTheDesignatedApprover_EvenHerOwnTransplant_NotOtherSupervisorsOrAdmins_NotEarly_NotTwice_Audited()
        {
            var env = await OpenAsync(BedSowingSchema.ProbeSql, WorkflowCorrectionsSchema.BedApproverProbeSql);
            var approver = await env.Get<ReadyConfirmationRepository>().GetBedApproverAsync();
            Assert.True(approver.RuleActive);
            Skip.If(approver.UserId is null, "No active Bed Sowing approver in this copy.");
            var maya = approver.UserId!.Value;
            Assert.Equal(("Maya", "Maya Shinde"), (await env.ScalarAsync<string>("SELECT Username FROM dbo.IMSUsers WHERE Id = @U", ("@U", maya)),
                await env.ScalarAsync<string>("SELECT Name FROM dbo.IMSUsers WHERE Id = @U", ("@U", maya))));

            var bedPolyhouse = await env.ScalarAsync<int>("SELECT TOP 1 PolyhouseId FROM dbo.BedSowingLocations WHERE IsActive = 1 ORDER BY PolyhouseId");
            var bedArea = await env.ScalarAsync<int>("SELECT AreaId FROM dbo.BedSowingLocations WHERE PolyhouseId = @P", ("@P", bedPolyhouse));
            var transplanter = await env.ScalarAsync<int>(@"SELECT TOP 1 ur.UserId FROM dbo.UserRoles ur JOIN dbo.Roles r ON r.Id = ur.RoleId
WHERE COALESCE(NULLIF(LTRIM(RTRIM(r.Name)), N''), r.RoleName) = N'Bed Sowing Transplanter' AND ur.AreaId = @A", ("@A", bedArea));
            var otherSupervisor = await env.ScalarAsync<int>(@"SELECT TOP 1 u.Id FROM dbo.UserRoles ur JOIN dbo.Roles r ON r.Id = ur.RoleId
JOIN dbo.IMSUsers u ON u.Id = ur.UserId AND u.IsActive = 1
WHERE COALESCE(NULLIF(LTRIM(RTRIM(r.Name)), N''), r.RoleName) = N'Sowing Supervisor' AND u.Id <> @M ORDER BY u.Id", ("@M", maya));
            var admin = await env.ScalarAsync<int>(@"SELECT TOP 1 u.Id FROM dbo.UserRoles ur JOIN dbo.Roles r ON r.Id = ur.RoleId
JOIN dbo.IMSUsers u ON u.Id = ur.UserId AND u.IsActive = 1
WHERE COALESCE(NULLIF(LTRIM(RTRIM(r.Name)), N''), r.RoleName) = N'System Administrator' AND u.Id <> @M ORDER BY u.Id", ("@M", maya));
            Skip.If(bedPolyhouse == 0 || transplanter == 0 || otherSupervisor == 0 || admin == 0, "The copy has no bed location, transplanter, second supervisor or administrator.");
            Assert.Equal(maya, transplanter);    // the approver is also the one who transplants: the Bed Sowing exception is exercised

            // a bed sown 40 days ago, two transplants by Maya: one ready since yesterday, one only today
            var v = await NewVarietyAsync(env);
            var bedSeeds = await env.ScalarAsync<int>(@"INSERT INTO dbo.SeedStock (SpeciesId, AreaId, BatchNo, Unit, PhysicalQuantity, CreatedBy)
VALUES (@S, @A, @B, N'Seeds', 100000, N'wf1016-test'); SELECT CAST(SCOPE_IDENTITY() AS INT)", ("@S", v.SpeciesId), ("@A", bedArea), ("@B", "BEDLOT-" + Guid.NewGuid().ToString("N")[..8]));
            Assert.True((await env.Get<TrayStockRepository>().AddStockAsync(bedArea, bedPolyhouse, "24 Cavity", 30, DateTime.UtcNow.AddDays(-45),
                Random.Shared.Next(1, int.MaxValue), otherSupervisor, "wf1016-test", "wf1016 bed trays", Any)).Success);
            Assert.True((await env.Get<MaterialStockRepository>().AdjustAsync(bedArea, bedPolyhouse, env.Cocopeat, 1, 100m, "wf1016 bed cocopeat", Guid.NewGuid(),
                otherSupervisor, Any, LedgerDates.Instant(AppClock.Today.AddDays(-45)).TransactionUtc)).Success);
            var beds = env.Get<BedSowingRepository>();
            var bed = new BedSowing { SpeciesId = v.SpeciesId, SeedStockId = bedSeeds, PolyhouseId = bedPolyhouse, Quantity = 960,
                SowingDate = AppClock.Today.AddDays(-40), Remarks = "wf1016 bed", SubmissionToken = Guid.NewGuid() };
            Assert.True((await beds.CreateAsync(bed, new BedSowingRepository.Actor(otherSupervisor, "wf1016-sower", false), Any)).Success);
            BedTransplantRequest Move(decimal qty, int daysAgo) => new()
            {
                BedSowingId = bed.Id, TargetPolyhouseId = bedPolyhouse, CavityType = "24 Cavity", Quantity = qty, TransplantDate = AppClock.Today.AddDays(-daysAgo),
                GrowingMaterialTypeId = env.Cocopeat, Remarks = "wf1016", SubmissionToken = Guid.NewGuid()
            };
            var mayaActor = new BedSowingRepository.Actor(maya, "wf1016-maya", false);
            // (each transplant closes the bed with its remainder as waste -- so two beds)
            var (t1, m1, _, readyBatch, _) = await beds.TransplantAsync(Move(240, ReadyDays + 1), mayaActor);
            Assert.True(t1, m1);
            var bed2 = new BedSowing { SpeciesId = v.SpeciesId, SeedStockId = bedSeeds, PolyhouseId = bedPolyhouse, Quantity = 240,
                SowingDate = AppClock.Today.AddDays(-5), Remarks = "wf1016 bed 2", SubmissionToken = Guid.NewGuid() };
            Assert.True((await beds.CreateAsync(bed2, new BedSowingRepository.Actor(otherSupervisor, "wf1016-sower", false), Any)).Success);
            var early = Move(240, 0);
            early.BedSowingId = bed2.Id;
            var (t2, m2, _, earlyBatch, _) = await beds.TransplantAsync(early, mayaActor);
            Assert.True(t2, m2);

            var approvals = env.Get<ReadyConfirmationRepository>();
            // not before the Ready Date -- not even Maya (until 2026-10-17_BedSowingApprovalAnyDate.sql: then on any date)
            if (!(await approvals.GetApprovalSettingsAsync()).BedAnyDate)
            {
                var (eOk, eMsg, _) = await approvals.ConfirmAsync(earlyBatch, 10, null, null, null, "wf1016", maya, Any);
                Assert.False(eOk);
                Assert.Contains("not ready yet", eMsg);
            }
            // another Sowing Supervisor and an administrator: refused, with the approver named
            var (sOk, sMsg, _) = await approvals.ConfirmAsync(readyBatch, 10, null, null, null, "wf1016", otherSupervisor, Any);
            Assert.False(sOk);
            Assert.Equal(DirectSowingRules.NotBedApproverMessage(approver.Name), sMsg);
            Assert.False((await approvals.ConfirmAsync(readyBatch, 10, null, null, null, "wf1016", admin, Any)).Success);
            Assert.Equal(0, await env.ScalarAsync<int>("SELECT COUNT(*) FROM dbo.ReadyConfirmations WHERE SeedSowingId = @S", ("@S", readyBatch)));
            // Maya approves the batch she transplanted herself (Bed Sowing exception), recorded as her approval and audited
            var (ok, msg, rcId) = await approvals.ConfirmAsync(readyBatch, 9, "Damaged plants", null, null, "wf1016", maya, Any);
            Assert.True(ok, msg);
            Assert.Equal(maya, await env.ScalarAsync<int>("SELECT ApprovedById FROM dbo.ReadyConfirmations WHERE Id = @R", ("@R", rcId)));
            Assert.Equal(1, await env.ScalarAsync<int>(@"SELECT COUNT(*) FROM dbo.BedSowingAuditLog
WHERE Action = N'BedBatchApproved' AND UserId = @U AND Details LIKE @D AND Details LIKE N'%Bed Sowing exception%'", ("@U", maya), ("@D", $"ReadyConfirmation {rcId} %")));
            // the batch is closed: a second approval is refused
            var (again, againMsg, _) = await approvals.ConfirmAsync(readyBatch, 1, null, null, null, "wf1016", maya, Any);
            Assert.False(again);
            Assert.Contains("cannot be approved", againMsg);
            Assert.Equal(216m, await env.ScalarAsync<decimal>("SELECT Quantity FROM dbo.ReadyStock WHERE SeedSowingId = @S", ("@S", readyBatch)));
        }

        [SkippableFact]
        public async Task OtherBatchTypes_KeepTheirApprovers_ATransplantedTrayBatchByAnyApprover_DirectSeedByItsSupervisor()
        {
            var env = await OpenAsync(WorkflowCorrectionsSchema.BedApproverProbeSql, WorkflowCorrectionsSchema.TransplantWasteProbeSql);
            // Direct Seed Sowing: still its assigned supervisor (ApprovedMainBatchAsync approves as SowingSupervisor = the assigned one)
            var v = await NewVarietyAsync(env);
            var (_, received) = await ReceivedAsync(env, v, MotherPlantTransferRules.PurposeTrayTransplant, 240, daysAgo: ReadyDays);
            // a Mother Plant transplant tray batch: approved by an approver who is not the Bed Sowing approver
            await AddAreaTraysAsync(env, "24 Cavity", 10, daysAgo: ReadyDays + 1);
            var (tOk, tMsg, transplantId, _) = await env.Transplants.CreateAsync(Process(received, 240, "24 Cavity", date: AppClock.Today.AddDays(-ReadyDays)), Actor, Any);
            Assert.True(tOk, tMsg);
            var batch = await env.ScalarAsync<int>("SELECT OutputSeedSowingId FROM dbo.ReadyStockTransplants WHERE Id = @T", ("@T", transplantId));
            var approver = await env.ScalarAsync<int>(@"SELECT TOP 1 u.Id FROM dbo.UserRoles ur JOIN dbo.RolePermissions rp ON rp.RoleId = ur.RoleId
JOIN dbo.Permissions p ON p.Id = rp.PermissionId AND p.Code = N'ReadyStock.Confirm' JOIN dbo.IMSUsers u ON u.Id = ur.UserId AND u.IsActive = 1
WHERE u.Id NOT IN (SELECT UserId FROM dbo.BedSowingApprover) AND ur.AreaId = @A ORDER BY u.Id", ("@A", MpArea));   // an approver of the batch's Area (2026-10-17)
            Skip.If(approver == 0, "No second approver in this copy.");
            var (ok, msg, _) = await env.Get<ReadyConfirmationRepository>().ConfirmAsync(batch, 10, null, null, null, "wf1016", approver, Any);
            Assert.True(ok, msg);
        }

        // =====================================================================================================
        // 4 + 5. Mother Plant transplanting: no Cocopeat / Pitmos; the untransplanted remainder is waste
        // =====================================================================================================

        [SkippableFact]
        public async Task Transplant_Example170Into150Cavity_OneTray_20Waste_NothingLeft_NoMaterial_ReadyDate_ReportsAgree()
        {
            var env = await OpenAsync(WorkflowCorrectionsSchema.TransplantWasteProbeSql);
            var v = await NewVarietyAsync(env);
            var (_, received) = await ReceivedAsync(env, v, MotherPlantTransferRules.PurposeTrayTransplant, sent: 192, received: 170);
            Assert.Equal(170m, await env.AvailableAsync(received));
            await AddAreaTraysAsync(env, "150 Cavity", 3);
            var trays0 = await env.AreaTraysAsync(MpArea, "150 Cavity");
            var material0 = await env.MaterialAsync(MpArea);
            var mainMaterial0 = await env.MaterialAsync(MainArea);
            var waste0 = (await env.Get<WastageRepository>().GetAsync(AppClock.Today, AppClock.Today, MpArea)).Where(w => w.ProductionType == WastageRepository.TransplantWasteProductionType).Sum(w => w.Quantity);
            var daily0 = (await env.Get<DailyOperationsReportRepository>().GetAsync(AppClock.Today, AppClock.Today)).Wastage.Sum(d => d.Quantity);

            // no reason for the 20 -> refused, nothing changes
            var (nOk, nMsg, _, _) = await env.Transplants.CreateAsync(Process(received, 170, "150 Cavity"), Actor, Any);
            Assert.False(nOk);
            Assert.Contains("reason", nMsg);
            Assert.Equal(170m, await env.AvailableAsync(received));

            var (ok, msg, id, _) = await env.Transplants.CreateAsync(Process(received, 170, "150 Cavity", "Weak seedlings left over"), Actor, Any);
            Assert.True(ok, msg);
            Assert.Contains("150 seedlings were transplanted into 1 150 Cavity trays", msg);
            Assert.Contains("remaining 20 were recorded as waste", msg);
            var t = (await env.Transplants.GetAsync(id))!;
            Assert.Equal((170m, 150m, 20m, 1, "Weak seedlings left over"), (t.SelectedQuantity, t.TransplantedQuantity, t.LostQuantity, t.OutputTrays, t.WasteReason));
            Assert.Equal(0m, await env.AvailableAsync(received));                                                   // nothing unaccounted
            Assert.Equal(trays0 - 1, await env.AreaTraysAsync(MpArea, "150 Cavity"));                              // only the tray used
            // the tray batch waits in Sowing Approvals; Ready Date = transplant date + Ready Days; no Ready Stock yet
            Assert.Equal(("Sown", 150m, 1, AppClock.Today.AddDays(ReadyDays)), (t.OutputSowingStatus!, await env.ScalarAsync<decimal>("SELECT QuantitySown FROM dbo.SeedSowings WHERE Id = @S", ("@S", t.OutputSeedSowingId)),
                await env.ScalarAsync<int>("SELECT NumberOfTrays FROM dbo.SeedSowings WHERE Id = @S", ("@S", t.OutputSeedSowingId)), t.OutputReadyDate!.Value.Date));
            Assert.Equal(0, await env.ScalarAsync<int>("SELECT COUNT(*) FROM dbo.ReadyStock WHERE SeedSowingId = @S", ("@S", t.OutputSeedSowingId)));
            // source ledger: -150 transplanted, -20 waste, both on the transplant business date
            Assert.Equal(-150m, await env.ScalarAsync<decimal>("SELECT SUM(Quantity) FROM dbo.ReadyStockTransactions WHERE ReadyStockId = @R AND TransactionType = N'TransplantOut' AND ReferenceId = @T", ("@R", received), ("@T", id)));
            Assert.Equal(-20m, await env.ScalarAsync<decimal>("SELECT SUM(Quantity) FROM dbo.ReadyStockTransactions WHERE ReadyStockId = @R AND TransactionType = N'TransplantLoss' AND ReferenceId = @T", ("@R", received), ("@T", id)));
            // conservation: received = transplanted + waste + left
            Assert.Equal(170m, t.TransplantedQuantity + t.LostQuantity + await env.AvailableAsync(received));
            // no Cocopeat / Pitmos anywhere, no material ledger row for the tray batch
            Assert.Equal(material0, await env.MaterialAsync(MpArea));
            Assert.Equal(mainMaterial0, await env.MaterialAsync(MainArea));
            Assert.Equal(0, await env.ScalarAsync<int>("SELECT COUNT(*) FROM dbo.MaterialTransactions WHERE ReferenceId = @S AND ReferenceType = @R", ("@S", t.OutputSeedSowingId), ("@R", MaterialStockRules.SowingReference)));
            // Wastage and the Daily Operations Report show the same 20 on the same business date
            var waste = (await env.Get<WastageRepository>().GetAsync(AppClock.Today, AppClock.Today, MpArea, t.TransplantCode)).Single(w => w.ProductionType == WastageRepository.TransplantWasteProductionType);
            Assert.Equal((AppClock.Today, 20m, "Weak seedlings left over"), (waste.Date.Date, waste.Quantity, waste.Reason));
            Assert.Equal(waste0 + 20, (await env.Get<WastageRepository>().GetAsync(AppClock.Today, AppClock.Today, MpArea)).Where(w => w.ProductionType == WastageRepository.TransplantWasteProductionType).Sum(w => w.Quantity));
            Assert.Equal(daily0 + 20, (await env.Get<DailyOperationsReportRepository>().GetAsync(AppClock.Today, AppClock.Today)).Wastage.Sum(d => d.Quantity));
        }

        [SkippableTheory]
        [InlineData(240, "24 Cavity", 240, 10, 0, 0)]        // exact tray multiple: no waste, no reason needed
        [InlineData(240, "42 Cavity", 240, 5, 30, 0)]        // several trays + a remainder
        [InlineData(240, "42 Cavity", 100, 2, 16, 140)]      // part of the batch: the unprocessed 140 stay (not waste)
        public async Task Transplant_ProcessedIntoCompleteTrays_RemainderIsWaste_UnprocessedStays(int received, string cavity, int processed, int trays, int waste, int left)
        {
            var env = await OpenAsync(WorkflowCorrectionsSchema.TransplantWasteProbeSql);
            var v = await NewVarietyAsync(env);
            var (_, batch) = await ReceivedAsync(env, v, MotherPlantTransferRules.PurposeTrayTransplant, received);
            await AddAreaTraysAsync(env, cavity, trays + 2);
            var trays0 = await env.AreaTraysAsync(MpArea, cavity);
            var (ok, msg, id, _) = await env.Transplants.CreateAsync(Process(batch, processed, cavity, waste > 0 ? "wf1016 remainder" : null), Actor, Any);
            Assert.True(ok, msg);
            var t = (await env.Transplants.GetAsync(id))!;
            var size = DirectSowingRules.CavityCount(cavity)!.Value;
            Assert.Equal(((decimal)processed, (decimal)(trays * size), (decimal)waste, trays), (t.SelectedQuantity, t.TransplantedQuantity, t.LostQuantity, t.OutputTrays));
            Assert.Equal((decimal)left, await env.AvailableAsync(batch));
            Assert.Equal(trays0 - trays, await env.AreaTraysAsync(MpArea, cavity));
            Assert.Equal((decimal)received, t.TransplantedQuantity + t.LostQuantity + await env.AvailableAsync(batch));
            Assert.Equal(waste > 0 ? "wf1016 remainder" : null, t.WasteReason);
        }

        [SkippableFact]
        public async Task Transplant_Refusals_RollBackEverything_NotEnoughTrays_LessThanATray_ForgedFigures_PotPurpose()
        {
            var env = await OpenAsync(WorkflowCorrectionsSchema.TransplantWasteProbeSql);
            var v = await NewVarietyAsync(env);
            // received 2 days ago: a transplant dated then may only use trays that were in the Area by then (none here)
            var (_, batch) = await ReceivedAsync(env, v, MotherPlantTransferRules.PurposeTrayTransplant, 240, daysAgo: 2);
            const string cavity = "9 Cavity";
            var trays0 = await env.AreaTraysAsync(MpArea, cavity);
            var rows0 = await env.ScalarAsync<int>("SELECT COUNT(*) FROM dbo.ReadyStockTransplants WHERE SourceReadyStockId = @R", ("@R", batch));
            async Task NothingChanged()
            {
                Assert.Equal(240m, await env.AvailableAsync(batch));
                Assert.Equal(trays0, await env.AreaTraysAsync(MpArea, cavity));
                Assert.Equal(rows0, await env.ScalarAsync<int>("SELECT COUNT(*) FROM dbo.ReadyStockTransplants WHERE SourceReadyStockId = @R", ("@R", batch)));
            }

            // more trays than the Area held on the transplant date (26 needed)
            var (tOk, tMsg, _, _) = await env.Transplants.CreateAsync(Process(batch, 240, cavity, "r", date: AppClock.Today.AddDays(-2)), Actor, Any);
            Assert.False(tOk);
            Assert.Contains("Not enough empty 9 Cavity trays", tMsg);
            await NothingChanged();
            // fewer seedlings than one 150-cavity tray, without confirming the discard
            var (lOk, lMsg, _, _) = await env.Transplants.CreateAsync(Process(batch, 100, "150 Cavity", "r"), Actor, Any);
            Assert.False(lOk);
            Assert.Contains("do not fill one", lMsg);
            // forged figures: posted transplanted / waste that are not what the server computes
            var forged = Process(batch, 100, "42 Cavity", "r") with { TransplantedQuantity = 100, LostQuantity = 0 };
            var (fOk, fMsg, _, _) = await env.Transplants.CreateAsync(forged, Actor, Any);
            Assert.False(fOk);
            Assert.Contains("do not match", fMsg);
            // more than available, a fractional quantity
            Assert.False((await env.Transplants.CreateAsync(Process(batch, 241, "24 Cavity"), Actor, Any)).Success);
            Assert.False((await env.Transplants.CreateAsync(Process(batch, 24.5m, "24 Cavity", "r"), Actor, Any)).Success);
            await NothingChanged();

            // Pot Production stock: refused by the repository and by the database (direct insert)
            var (_, potBatch) = await ReceivedAsync(env, v, MotherPlantTransferRules.PurposePotProduction, 48);
            await AddAreaTraysAsync(env, "24 Cavity", 2);
            var (pOk, pMsg, _, _) = await env.Transplants.CreateAsync(Process(potBatch, 48, "24 Cavity"), Actor, Any);
            Assert.False(pOk);
            Assert.Contains("Pot Production", pMsg);
            var pool = await env.ScalarAsync<int>("SELECT Id FROM dbo.TrayStock WHERE AreaId = @A AND PolyhouseId IS NULL AND TraySize = N'24 Cavity' AND IsActive = 1", ("@A", MpArea));
            var ex = await Assert.ThrowsAsync<SqlException>(() => env.ExecAsync(@"INSERT INTO dbo.ReadyStockTransplants
(TransplantCode, SourceReadyStockId, AreaId, TargetCavityType, SelectedQuantity, TransplantedQuantity, LostQuantity, OutputTrays, TrayStockId, TransplantDate, Status, SubmissionToken)
VALUES (@C, @R, @A, N'24 Cavity', 24, 24, 0, 1, @P, CAST(GETDATE() AS date), N'Completed', NEWID())",
                ("@C", "ZZT-" + Guid.NewGuid().ToString("N")[..8]), ("@R", potBatch), ("@A", MpArea), ("@P", pool)));
            Assert.Contains("received for Pot Production", ex.Message);
            Assert.Equal(48m, await env.AvailableAsync(potBatch));
        }

        [SkippableFact]
        public async Task Transplant_WasteOnlyClosure_NeedsConfirmationAndReason_NoTrayBatchNoTrays_Reversible()
        {
            var env = await OpenAsync(WorkflowCorrectionsSchema.TransplantWasteProbeSql);
            var v = await NewVarietyAsync(env);
            var (_, batch) = await ReceivedAsync(env, v, MotherPlantTransferRules.PurposeTrayTransplant, sent: 24, received: 20);
            var trays0 = await env.AreaTraysAsync(MpArea, "150 Cavity");
            var sowings0 = await env.ScalarAsync<int>("SELECT COUNT(*) FROM dbo.SeedSowings WHERE SpeciesId = @S", ("@S", v.SpeciesId));

            Assert.False((await env.Transplants.CreateAsync(Process(batch, 20, "150 Cavity", null, discard: true), Actor, Any)).Success);   // no reason
            var (ok, msg, id, _) = await env.Transplants.CreateAsync(Process(batch, 20, "150 Cavity", "Seedlings dead on arrival", discard: true), Actor, Any);
            Assert.True(ok, msg);
            Assert.Contains("No tray batch", msg);
            var t = (await env.Transplants.GetAsync(id))!;
            Assert.True(t.WasteOnly);
            Assert.Equal((20m, 0m, 20m, 0, (int?)null), (t.SelectedQuantity, t.TransplantedQuantity, t.LostQuantity, t.OutputTrays, t.OutputSeedSowingId));
            Assert.Null(await env.ScalarAsync<int?>("SELECT TrayStockId FROM dbo.ReadyStockTransplants WHERE Id = @T", ("@T", id)));
            Assert.Equal(0m, await env.AvailableAsync(batch));
            Assert.Equal(trays0, await env.AreaTraysAsync(MpArea, "150 Cavity"));
            Assert.Equal(sowings0, await env.ScalarAsync<int>("SELECT COUNT(*) FROM dbo.SeedSowings WHERE SpeciesId = @S", ("@S", v.SpeciesId)));   // no empty / zero batch
            Assert.Contains(await env.Get<WastageRepository>().GetAsync(AppClock.Today, AppClock.Today, MpArea, t.TransplantCode),
                w => w.ProductionType == WastageRepository.TransplantWasteProductionType && w.Quantity == 20 && w.Reason == "Seedlings dead on arrival");

            // a mistake can be reversed: the 20 return to the batch
            var (rOk, rMsg) = await env.Transplants.ReverseAsync(id, "recorded on the wrong batch", Actor, Any);
            Assert.True(rOk, rMsg);
            Assert.Equal(20m, await env.AvailableAsync(batch));
            Assert.Equal(TransplantRules.StatusReversed, (await env.Transplants.GetAsync(id))!.Status);
            Assert.DoesNotContain(await env.Get<WastageRepository>().GetAsync(AppClock.Today, AppClock.Today, MpArea, t.TransplantCode),
                w => w.ProductionType == WastageRepository.TransplantWasteProductionType);
        }

        [SkippableFact]
        public async Task Transplant_SameFormOnce_ConcurrentFormsNeverOverconsume_PostedPolyhouseIgnored()
        {
            var env = await OpenAsync(WorkflowCorrectionsSchema.TransplantWasteProbeSql);
            var v = await NewVarietyAsync(env);
            var (_, batch) = await ReceivedAsync(env, v, MotherPlantTransferRules.PurposeTrayTransplant, 240);
            await AddAreaTraysAsync(env, "24 Cavity", 40);

            // the same form 4 times at once: one transplant
            var token = Guid.NewGuid();
            var same = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => env.Transplants.CreateAsync(Process(batch, 48, "24 Cavity", token: token, polyhouse: MpP1), Actor, Any)));
            Assert.All(same, r => Assert.True(r.Success, r.Message));
            Assert.Single(same.Select(r => r.Id).Distinct());
            Assert.Equal(192m, await env.AvailableAsync(batch));
            // a posted Polyhouse is never stored (the Area's trays) -- on the transplant or its tray batch
            Assert.Null(await env.ScalarAsync<int?>("SELECT PolyhouseId FROM dbo.ReadyStockTransplants WHERE Id = @T", ("@T", same[0].Id)));
            Assert.Null(await env.ScalarAsync<int?>("SELECT sw.PolyhouseId FROM dbo.ReadyStockTransplants t JOIN dbo.SeedSowings sw ON sw.Id = t.OutputSeedSowingId WHERE t.Id = @T", ("@T", same[0].Id)));

            // six different forms at once, 48 each, 192 available: exactly four succeed, never below zero
            var race = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => env.Transplants.CreateAsync(Process(batch, 48, "24 Cavity"), Actor, Any)));
            Assert.Equal(4, race.Count(r => r.Success));
            Assert.Equal(0m, await env.AvailableAsync(batch));
            Assert.Equal(240m, await env.ScalarAsync<decimal>("SELECT SUM(TransplantedQuantity + LostQuantity) FROM dbo.ReadyStockTransplants WHERE SourceReadyStockId = @R AND Status = N'Completed'", ("@R", batch)));
        }

        // =====================================================================================================
        // 6. Ready Stock -> Mother Plant Area: the Area only
        // =====================================================================================================

        [SkippableFact]
        public async Task Transfer_ToTheArea_APostedPolyhouseIsNeverStored_ReceiptAndHistoryWork()
        {
            var env = await OpenAsync();
            var v = await NewVarietyAsync(env);
            var main = await ApprovedMainBatchAsync(env, v);
            var (ok, msg, transferId, _) = await env.Transfers.CreateAsync(
                new MotherPlantTransferRepository.CreateRequest(main, 96, MpArea, MpP1, AppClock.Today, "wf1016 forged polyhouse", Guid.NewGuid(), MotherPlantTransferRules.PurposeTrayTransplant), Actor, Any);
            Assert.True(ok, msg);
            Assert.Null(await env.ScalarAsync<int?>("SELECT DestinationPolyhouseId FROM dbo.InternalTransfers WHERE Id = @T", ("@T", transferId)));
            var (rOk, rMsg, received) = await env.Transfers.ReceiveAsync(transferId, 96, null, Actor, Any);
            Assert.True(rOk, rMsg);
            Assert.Null(await env.ScalarAsync<int?>("SELECT PolyhouseId FROM dbo.ReadyStock WHERE Id = @R", ("@R", received)));
            var listed = Assert.Single(await env.Transfers.ListAsync(new[] { MpArea }), t => t.Id == transferId);
            Assert.Equal((MpArea, (int?)null, MotherPlantTransferRules.PurposeTrayTransplant), (listed.DestinationAreaId, listed.DestinationPolyhouseId, listed.TransferPurpose));
        }
    }

}
