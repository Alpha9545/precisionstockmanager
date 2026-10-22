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
    // Main Area Ready Stock -> Mother Plant Area transfer -> receive -> transplanting, end-to-end
    // against a SCRATCH COPY (PlantsIMS2_Scratch_*, refused otherwise; SKIPPED without
    // PSM_SCRATCH_CONNECTION). Every test builds its own data through the real code: Tray Stock +
    // a Main Area seed sowing -> Supervisor Approval -> Ready Stock (480 plants = 20 x 24 Cavity).
    [Collection("ScratchDb")]
    public class ReadyStockTransplantingE2ETests
    {
        private const string ConnectionVariable = "PSM_SCRATCH_CONNECTION";
        private const string RequiredPrefix = "PlantsIMS2_Scratch_";
        private const int MainArea = 2, MainPolyhouse = 4;              // Main-Office-Areas / Facility-4
        private const int MpArea = 127, MpP1 = 14, MpP2 = 15;           // Green Bless Nursery (Mother Plant) / P-1, P-2
        private const int OtherMpArea = 1;                               // Shree Swami Samarth Agro (Mother Plant)
        private const int SupervisorId = 9, Recorder = 21;
        private const int MotherPlantForSpecies = 114;
        private const string Cavity = "24 Cavity";
        private static readonly MotherPlantTransferRepository.Actor Actor = new(Recorder, "transplant-test");
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
            // 2026-10-16: with 2026-10-14_MotherPlantAreaTraysAndTransferPurpose.sql a transfer / transplant to the Mother
            // Plant Area stores NO Polyhouse, even when one is posted; before it, the posted Polyhouse (MpP1).
            public bool AreaOnly;
            public int? MpPolyhouse => AreaOnly ? null : MpP1;
            public string ConnectionString { get; init; } = "";
            public ServiceProvider Provider { get; init; } = null!;
            public int Species { get; init; }
            public int SeedStockId { get; init; }
            public int Cocopeat { get; init; }
            public T Get<T>() where T : notnull => Provider.GetRequiredService<T>();
            public MotherPlantTransferRepository Transfers => Get<MotherPlantTransferRepository>();
            public TransplantRepository Transplants => Get<TransplantRepository>();

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

            public async Task<(decimal Qty, decimal Reserved, decimal Dispatched, decimal InTransit, decimal Available)> BatchAsync(int id)
            {
                using var conn = new SqlConnection(ConnectionString);
                await conn.OpenAsync();
                using var cmd = new SqlCommand("SELECT Quantity, ReservedQuantity, DispatchedQuantity, InTransitQuantity, AvailableQuantity FROM dbo.ReadyStock WHERE Id = @Id", conn);
                cmd.Parameters.AddWithValue("@Id", id);
                using var r = await cmd.ExecuteReaderAsync();
                Assert.True(await r.ReadAsync());
                return (r.GetDecimal(0), r.GetDecimal(1), r.GetDecimal(2), r.GetDecimal(3), r.GetDecimal(4));
            }

            // The ledger explains the batch: SUM(rows that change Quantity) = Quantity - Dispatched,
            // and the in-transit hold rows sum to InTransitQuantity (DailyOperationsReport invariant).
            public async Task AssertLedgerExplainsAsync(int id)
            {
                var b = await BatchAsync(id);
                var net = await ScalarAsync<decimal>(@"SELECT ISNULL(SUM(Quantity), 0) FROM dbo.ReadyStockTransactions
WHERE ReadyStockId = @Id AND TransactionType NOT IN ('Reservation', 'ReservationRelease', 'TransferHold', 'TransferHoldRelease')", ("@Id", id));
                Assert.Equal(b.Qty - b.Dispatched, net);
                var hold = await ScalarAsync<decimal>("SELECT ISNULL(SUM(Quantity), 0) FROM dbo.ReadyStockTransactions WHERE ReadyStockId = @Id AND TransactionType IN ('TransferHold', 'TransferHoldRelease')", ("@Id", id));
                Assert.Equal(b.InTransit, hold);
            }

            // 2026-10-14: with 2026-10-14_MotherPlantAreaTraysAndTransferPurpose.sql a Mother Plant Area holds ONE tray pool
            // (no Polyhouse) that every one of its Polyhouses uses; before it, the Polyhouse's own pool. Both are counted.
            public Task<decimal> TraysAsync(int polyhouse, string cavity)
                => ScalarAsync<decimal>("SELECT ISNULL(SUM(PhysicalQuantity), 0) FROM dbo.TrayStock WHERE AreaId = @A AND (PolyhouseId = @P OR PolyhouseId IS NULL) AND TraySize = @C AND IsActive = 1",
                    ("@A", MpArea), ("@P", polyhouse), ("@C", cavity));
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
                using var mig = new SqlCommand("SELECT COL_LENGTH('dbo.ReadyStock', 'InTransitQuantity')", conn);
                Skip.If(await mig.ExecuteScalarAsync() is DBNull or null, "2026-10-09_ReadyStockTransplanting.sql is not applied to this scratch database.");
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

            var env = new Env { ConnectionString = cs!, Provider = provider };
            env.AreaOnly = await env.ScalarAsync<int>(MotherPlantAreaSchema.ProbeSql) == 1;
            var species = await env.ScalarAsync<int>("SELECT SpeciesId FROM dbo.MotherPlants WHERE Id = @M", ("@M", MotherPlantForSpecies));
            var seedStockId = await env.ScalarAsync<int>("SELECT TOP 1 Id FROM dbo.SeedStock WHERE SpeciesId = @S AND AreaId = @A", ("@S", species), ("@A", MainArea));
            if (seedStockId == 0)
            {
                await env.ExecAsync("INSERT INTO dbo.SeedStock (SpeciesId, AreaId, PhysicalQuantity, CreatedDate, CreatedBy) VALUES (@S, @A, 0, SYSUTCDATETIME(), N'transplant-test')",
                    ("@S", species), ("@A", MainArea));
                seedStockId = await env.ScalarAsync<int>("SELECT TOP 1 Id FROM dbo.SeedStock WHERE SpeciesId = @S AND AreaId = @A", ("@S", species), ("@A", MainArea));
            }
            await env.ExecAsync("UPDATE dbo.SeedStock SET InTransitQuantity = 0, PhysicalQuantity = 1000000 WHERE Id = @Id", ("@Id", seedStockId));
            var cocopeat = await env.ScalarAsync<int>("SELECT Id FROM dbo.MaterialTypes WHERE Code = 'COCOPEAT'");
            return new Env { ConnectionString = cs!, Provider = provider, Species = species, SeedStockId = seedStockId, Cocopeat = cocopeat, AreaOnly = env.AreaOnly };
        }

        // A Main Area sowing of `plants` (default 24 Cavity) approved in full -> its Ready Stock batch id.
        private static async Task<int> ApprovedMainBatchAsync(Env env, int plants = 480, string cavity = Cavity)
        {
            var size = DirectSowingRules.CavityCount(cavity)!.Value;
            var (tOk, tMsg, _, _) = await env.Get<TrayStockRepository>().AddStockAsync(MainArea, MainPolyhouse, cavity, plants / size + 5, DateTime.UtcNow,
                Random.Shared.Next(1, int.MaxValue), SupervisorId, "transplant-test", "transplant test setup", Any);
            Assert.True(tOk, tMsg);
            var (mOk, mMsg, _) = await env.Get<MaterialStockRepository>().AdjustAsync(MainArea, MainPolyhouse, env.Cocopeat, 1, 500m, "transplant test setup", Guid.NewGuid(), Recorder, Any);
            Assert.True(mOk, mMsg);
            var sowing = new SeedSowing
            {
                SourceSeedStockId = env.SeedStockId, SeedQuantity = plants, CavityType = cavity, SowingDate = AppClock.Today.AddDays(-40),
                PolyhouseId = MainPolyhouse, SupervisorId = SupervisorId, CreatedBy = "transplant-test", CreatedById = Recorder, GrowingMaterialTypeId = env.Cocopeat
            };
            var (ok, msg, sowingId) = await env.Get<SeedSowingRepository>().InsertAsync(sowing, Recorder, Any);
            Assert.True(ok, msg);
            var (cOk, cMsg, _) = await env.Get<ReadyConfirmationRepository>().ConfirmAsync(sowingId, plants / size, null, null, null, "transplant-test", SupervisorId, Any);
            Assert.True(cOk, cMsg);
            return await env.ScalarAsync<int>("SELECT Id FROM dbo.ReadyStock WHERE SeedSowingId = @S AND OriginType = N'Sowing'", ("@S", sowingId));
        }

        // 2026-10-14: sent for transplanting (the purpose is required -- and only used -- once the 2026-10-14 migration is applied).
        private static MotherPlantTransferRepository.CreateRequest Send(int batch, decimal qty, int? area = MpArea, int? polyhouse = MpP1, Guid? token = null)
            => new(batch, qty, area, polyhouse, AppClock.Today, "test transfer", token ?? Guid.NewGuid(), MotherPlantTransferRules.PurposeTrayTransplant);

        // arrivedUtc (2026-10-10): when the trays arrived -- a back-dated transplant may only use trays that
        // were there on its date (TrayStockRepository.AvailableFromAsync), so such tests stock them earlier.
        private static async Task AddMpTraysAsync(Env env, int polyhouse, string cavity, decimal trays, DateTime? arrivedUtc = null)
        {
            var (ok, msg, _, _) = await env.Get<TrayStockRepository>().AddStockAsync(MpArea, polyhouse, cavity, trays, arrivedUtc ?? DateTime.UtcNow,
                Random.Shared.Next(1, int.MaxValue), SupervisorId, "transplant-test", "transplant test trays", Any);
            Assert.True(ok, msg);
        }

        private static async Task<int> ReceivedBatchAsync(Env env, int plants = 240)
        {
            var main = await ApprovedMainBatchAsync(env);
            var (ok, msg, transferId, _) = await env.Transfers.CreateAsync(Send(main, plants), Actor, Any);
            Assert.True(ok, msg);
            var (rOk, rMsg, batch) = await env.Transfers.ReceiveAsync(transferId, plants, null, Actor, Any);
            Assert.True(rOk, rMsg);
            return batch!.Value;
        }

        // 2026-10-13_TraysToSowingApprovals.sql: a transplant becomes a growing tray batch in Sowing
        // Approvals (complete trays, Ready Date = transplant date + Ready Days) instead of an immediate
        // Ready Stock batch. Tests of the immediate-output shape run only on a copy without it; the
        // tray-batch tests ("tray-batch path" below) run only on a copy with it.
        private const string SupersededByTrayBatchPath =
            "Superseded by the 2026-10-13 tray-batch path on this copy (covered by the TrayBatch_* tests).";
        private static async Task<bool> TrayBatchPathAsync(Env env) => await env.ScalarAsync<int>(BedSowingSchema.TraysProbeSql) == 1;
        private static async Task SkipOnTrayBatchPathAsync(Env env) => Skip.If(await TrayBatchPathAsync(env), SupersededByTrayBatchPath);

        // ===== transfer ===================================================================================

        [SkippableFact]
        public async Task Transfer_HoldsPlantsInTransit_NotAvailableAtEitherLocation_QuantityUnchanged()
        {
            var env = await OpenAsync();
            var main = await ApprovedMainBatchAsync(env);
            var (ok, msg, id, dup) = await env.Transfers.CreateAsync(Send(main, 240), Actor, Any);
            Assert.True(ok, msg);
            Assert.False(dup);

            var b = await env.BatchAsync(main);
            Assert.Equal(480m, b.Qty);          // nothing created or destroyed yet
            Assert.Equal(240m, b.InTransit);
            Assert.Equal(240m, b.Available);    // only the rest can be booked / sent
            Assert.Equal(0, await env.ScalarAsync<int>("SELECT COUNT(*) FROM dbo.ReadyStock WHERE OriginType = N'Transfer' AND OriginReferenceId = @T", ("@T", id)));
            Assert.Equal(MotherPlantTransferRules.StatusPending, (await env.Transfers.GetAsync(id))!.Status);

            // A booking reservation cannot take the in-transit plants.
            using (var conn = new SqlConnection(env.ConnectionString))
            {
                await conn.OpenAsync();
                using var tx = conn.BeginTransaction();
                var (resOk, _) = await env.Get<ReadyStockRepository>().RecordReservationAsync(conn, tx, main, 264, "Test", null, Recorder, null);
                Assert.False(resOk);
                tx.Rollback();
            }
            await env.AssertLedgerExplainsAsync(main);
        }

        [SkippableFact]
        public async Task Transfer_IsValidatedOnTheServer()
        {
            var env = await OpenAsync();
            var main = await ApprovedMainBatchAsync(env);
            async Task<string?> Fails(MotherPlantTransferRepository.CreateRequest req, Func<int, bool>? access = null)
            {
                var (ok, msg, _, _) = await env.Transfers.CreateAsync(req, Actor, access ?? Any);
                Assert.False(ok);
                return msg;
            }
            Assert.Contains("multiple of 24", await Fails(Send(main, 100)));                       // not whole trays
            Assert.Contains("available", await Fails(Send(main, 504)));                            // more than the batch
            Assert.Contains("Mother Plant Area", await Fails(Send(main, 24, area: MainArea, polyhouse: MainPolyhouse)));   // not a Mother Plant Area
            Assert.Contains("does not belong", await Fails(Send(main, 24, area: MpArea, polyhouse: MainPolyhouse)));      // forged Polyhouse
            Assert.Contains("not allowed", await Fails(Send(main, 24), _ => false));               // no Area access
            var (fOk, fMsg, _, _) = await env.Transfers.CreateAsync(Send(main, 24) with { TransferDate = AppClock.Today.AddDays(1) }, Actor, Any);
            Assert.False(fOk); Assert.Contains("future", fMsg);
            // A received batch (not a Main Area sowing batch) cannot be sent on.
            var received = await ReceivedBatchAsync(env);
            Assert.Contains("Main Area", await Fails(Send(received, 24, area: OtherMpArea, polyhouse: 11)));
            Assert.Equal(0m, (await env.BatchAsync(main)).InTransit);   // nothing was held by any refused request
        }

        [SkippableFact]
        public async Task Transfer_RepeatedSubmit_SameToken_CreatesOneTransfer_OneHold()
        {
            var env = await OpenAsync();
            var main = await ApprovedMainBatchAsync(env);
            var token = Guid.NewGuid();
            var results = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => env.Transfers.CreateAsync(Send(main, 48, token: token), Actor, Any)));
            Assert.All(results, r => Assert.True(r.Success, r.Message));
            Assert.Single(results.Select(r => r.Id).Distinct());
            Assert.Equal(48m, (await env.BatchAsync(main)).InTransit);
            Assert.Equal(1, await env.ScalarAsync<int>("SELECT COUNT(*) FROM dbo.InternalTransfers WHERE SubmissionToken = @T", ("@T", token)));
        }

        [SkippableFact]
        public async Task Transfer_ConcurrentSends_NeverHoldMoreThanAvailable()
        {
            var env = await OpenAsync();
            var main = await ApprovedMainBatchAsync(env);   // 480
            var results = await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => env.Transfers.CreateAsync(Send(main, 192), Actor, Any)));
            Assert.Equal(2, results.Count(r => r.Success));   // 2 x 192 = 384 <= 480 < 3 x 192
            var b = await env.BatchAsync(main);
            Assert.Equal(384m, b.InTransit);
            Assert.True(b.Available >= 0);
            await env.AssertLedgerExplainsAsync(main);
        }

        [SkippableFact]
        public async Task Receive_MovesThePlants_CreatesOneTraceableBatch_AndIsIdempotent()
        {
            var env = await OpenAsync();
            var main = await ApprovedMainBatchAsync(env);
            var (_, _, id, _) = await env.Transfers.CreateAsync(Send(main, 240), Actor, Any);

            var results = await Task.WhenAll(Enumerable.Range(0, 3).Select(_ => env.Transfers.ReceiveAsync(id, 240, null, Actor, Any)));
            Assert.All(results, r => Assert.True(r.Success, r.Message));
            Assert.Single(results.Select(r => r.ReceivedReadyStockId).Distinct());
            var received = results[0].ReceivedReadyStockId!.Value;

            var src = await env.BatchAsync(main);
            Assert.Equal(240m, src.Qty);            // 480 - 240 left the Main Area
            Assert.Equal(0m, src.InTransit);
            var dst = await env.BatchAsync(received);
            Assert.Equal(240m, dst.Qty);            // exactly what arrived -- no plant created
            var batch = (await env.Get<ReadyStockRepository>().GetByIdAsync(received))!;
            Assert.Equal(ReadyStock.OriginTransfer, batch.OriginType);
            Assert.Equal(main, batch.ParentReadyStockId);
            Assert.Equal(id, batch.OriginReferenceId);
            Assert.Equal(MpArea, batch.AreaId);
            Assert.Equal(env.MpPolyhouse, batch.PolyhouseId);
            Assert.Equal((await env.Get<ReadyStockRepository>().GetByIdAsync(main))!.SeedSowingId, batch.SeedSowingId);   // original sowing kept
            Assert.Equal(1, await env.ScalarAsync<int>("SELECT COUNT(*) FROM dbo.ReadyStock WHERE OriginType = N'Transfer' AND OriginReferenceId = @T", ("@T", id)));
            Assert.Equal(MotherPlantTransferRules.StatusCompleted, (await env.Transfers.GetAsync(id))!.Status);
            await env.AssertLedgerExplainsAsync(main);
            await env.AssertLedgerExplainsAsync(received);

            // Closed: neither reject nor cancel can undo a receipt.
            Assert.False((await env.Transfers.RejectAsync(id, "late", Actor, Any)).Success);
            Assert.False((await env.Transfers.CancelAsync(id, "late", Actor, Any)).Success);
        }

        [SkippableFact]
        public async Task Receive_Shortfall_IsRecordedAsTransitLoss_WithAReason_MainBatchStaysWholeTrays()
        {
            var env = await OpenAsync();
            var main = await ApprovedMainBatchAsync(env);
            var (_, _, id, _) = await env.Transfers.CreateAsync(Send(main, 240), Actor, Any);
            Assert.False((await env.Transfers.ReceiveAsync(id, 230, null, Actor, Any)).Success);   // reason required
            Assert.False((await env.Transfers.ReceiveAsync(id, 250, "x", Actor, Any)).Success);    // more than sent
            var (ok, msg, received) = await env.Transfers.ReceiveAsync(id, 230, "10 seedlings broken in the vehicle", Actor, Any);
            Assert.True(ok, msg);
            Assert.Equal(240m, (await env.BatchAsync(main)).Qty);          // 480 - 230 - 10 (one balance change, whole trays)
            Assert.Equal(230m, (await env.BatchAsync(received!.Value)).Qty);
            Assert.Equal(-10m, await env.ScalarAsync<decimal>("SELECT SUM(Quantity) FROM dbo.ReadyStockTransactions WHERE ReadyStockId = @R AND TransactionType = N'TransferLoss'", ("@R", main)));
            Assert.Equal("10 seedlings broken in the vehicle", (await env.Transfers.GetAsync(id))!.DiscrepancyReason);
            await env.AssertLedgerExplainsAsync(main);
        }

        [SkippableFact]
        public async Task RejectAndCancel_ReleaseTheHold_NothingElseMoves_AndOldScreensCannotCloseIt()
        {
            var env = await OpenAsync();
            var main = await ApprovedMainBatchAsync(env);
            var (_, _, t1, _) = await env.Transfers.CreateAsync(Send(main, 96), Actor, Any);
            var (_, _, t2, _) = await env.Transfers.CreateAsync(Send(main, 48), Actor, Any);
            Assert.Equal(144m, (await env.BatchAsync(main)).InTransit);

            // The generic Internal Transfer screens must not close it (they would not release the hold).
            Assert.False((await env.Get<InternalTransferRepository>().RejectAsync(t1, "x", "test")).Success);
            Assert.False((await env.Get<InternalTransferRepository>().CancelAsync(t1, "test", Recorder)).Success);

            Assert.False((await env.Transfers.RejectAsync(t1, " ", Actor, Any)).Success);                     // reason required
            Assert.False((await env.Transfers.RejectAsync(t1, "wrong variety", Actor, _ => false)).Success);  // destination access
            Assert.True((await env.Transfers.RejectAsync(t1, "wrong variety", Actor, Any)).Success);
            Assert.True((await env.Transfers.CancelAsync(t2, "sent by mistake", Actor, Any)).Success);
            Assert.False((await env.Transfers.CancelAsync(t2, "again", Actor, Any)).Success);                 // replayed
            Assert.False((await env.Transfers.ReceiveAsync(t1, 96, null, Actor, Any)).Success);               // rejected cannot be received

            var b = await env.BatchAsync(main);
            Assert.Equal(480m, b.Qty);
            Assert.Equal(0m, b.InTransit);
            Assert.Equal(480m, b.Available);
            await env.AssertLedgerExplainsAsync(main);
        }

        // ===== transplanting ==============================================================================

        private static TransplantRepository.CreateRequest Plant(int source, decimal selected, decimal transplanted, decimal lost,
            string cavity = "9 Cavity", int? polyhouse = MpP1, Guid? token = null)
            => new(source, MpArea, polyhouse, cavity, selected, transplanted, lost, AppClock.Today, "test transplant", token ?? Guid.NewGuid(),
                lost > 0 ? "test loss" : null);   // 2026-10-16: waste needs a reason

        [SkippableFact]
        public async Task Transplant_TransformsStock_ExactPlants_PartialLastTray_TraysConsumed_LossSeparate()
        {
            var env = await OpenAsync();
            await SkipOnTrayBatchPathAsync(env);
            var received = await ReceivedBatchAsync(env);            // 240 in P-1
            await AddMpTraysAsync(env, MpP1, "9 Cavity", 30);
            var traysBefore = await env.TraysAsync(MpP1, "9 Cavity");

            var (ok, msg, id, _) = await env.Transplants.CreateAsync(Plant(received, 150, 145, 5), Actor, Any);
            Assert.True(ok, msg);
            var t = (await env.Transplants.GetAsync(id))!;
            Assert.Equal(17, t.OutputTrays);                         // CEILING(145 / 9): 16 full + 1 tray with 1 plant
            Assert.Equal(1m, t.PlantsInLastTray);
            Assert.Equal(90m, (await env.BatchAsync(received)).Qty); // 240 - 145 - 5
            var output = (await env.Get<ReadyStockRepository>().GetByIdAsync(t.OutputReadyStockId!.Value))!;
            Assert.Equal(145m, output.Quantity);                     // only the transplanted plants -- no plant invented
            Assert.Equal("9 Cavity", output.CavityType);
            Assert.Equal(ReadyStock.OriginTransplant, output.OriginType);
            Assert.Equal(received, output.ParentReadyStockId);
            Assert.Equal(17m, output.PhysicalTrays);
            Assert.Equal(traysBefore - 17, await env.TraysAsync(MpP1, "9 Cavity"));
            Assert.Equal(-5m, await env.ScalarAsync<decimal>("SELECT SUM(Quantity) FROM dbo.ReadyStockTransactions WHERE ReadyStockId = @R AND TransactionType = N'TransplantLoss'", ("@R", received)));
            await env.AssertLedgerExplainsAsync(received);
            await env.AssertLedgerExplainsAsync(output.Id);

            // Lineage: original Main Area sowing batch -> received batch -> transplanted batch.
            var lineage = await env.Get<ReadyStockRepository>().GetLineageAsync(output.Id);
            Assert.Equal(new[] { ReadyStock.OriginSowing, ReadyStock.OriginTransfer, ReadyStock.OriginTransplant }, lineage.Select(b => b.OriginType));
            Assert.Equal(MainArea, lineage[0].AreaId);
        }

        [SkippableFact]
        public async Task Transplant_IsRefused_WithoutEnoughTrays_OrStock_OrAccess_AndChangesNothing()
        {
            var env = await OpenAsync();
            var received = await ReceivedBatchAsync(env);
            var tray42 = await env.TraysAsync(MpP2, "42 Cavity");
            async Task Refused(TransplantRepository.CreateRequest req, string expect, Func<int, bool>? access = null)
            {
                var (ok, msg, _, _) = await env.Transplants.CreateAsync(req, Actor, access ?? Any);
                Assert.False(ok);
                Assert.Contains(expect, msg);
            }
            var trayBatchPath = await TrayBatchPathAsync(env);
            var plants42 = trayBatchPath ? 210 : 240;                 // complete trays only on the tray-batch path
            var needed = (int)Math.Ceiling(plants42 / 42m);
            if (tray42 < needed)
                await Refused(Plant(received, plants42, plants42, 0, "42 Cavity", MpP2), "Not enough empty");
            if (trayBatchPath)
                await Refused(Plant(received, 100, 100, 0, "24 Cavity"), "4 seedlings do not fill a complete tray and will be recorded as waste: enter the reason");
            await Refused(Plant(received, 300, 300, 0), "available");
            await Refused(Plant(received, 100, 90, 5), trayBatchPath ? "do not match" : "must equal");
            await Refused(Plant(received, 10, 10, 0, "10 Cavity"), "Target tray");
            await Refused(Plant(received, 10, 10, 0), "not allowed", _ => false);
            await Refused(Plant(received, 10, 10, 0) with { TargetAreaId = MainArea, TargetPolyhouseId = MainPolyhouse }, "Mother Plant Area");
            await Refused(Plant(received, 10, 10, 0) with { TransplantDate = AppClock.Today.AddDays(1) }, "future");
            Assert.Equal(240m, (await env.BatchAsync(received)).Qty);
            Assert.Equal(tray42, await env.TraysAsync(MpP2, "42 Cavity"));
        }

        [SkippableFact]
        public async Task Transplant_RepeatedSave_AndConcurrentUse_NeverConsumeTwice()
        {
            var env = await OpenAsync();
            var received = await ReceivedBatchAsync(env);   // 240
            await AddMpTraysAsync(env, MpP1, "24 Cavity", 50);
            var token = Guid.NewGuid();
            var same = await Task.WhenAll(Enumerable.Range(0, 3).Select(_ => env.Transplants.CreateAsync(Plant(received, 48, 48, 0, "24 Cavity", token: token), Actor, Any)));
            Assert.All(same, r => Assert.True(r.Success, r.Message));
            Assert.Single(same.Select(r => r.Id).Distinct());
            Assert.Equal(192m, (await env.BatchAsync(received)).Qty);

            var racing = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => env.Transplants.CreateAsync(Plant(received, 72, 72, 0, "24 Cavity"), Actor, Any)));
            Assert.Equal(2, racing.Count(r => r.Success));   // 2 x 72 = 144 <= 192 < 3 x 72
            Assert.Equal(48m, (await env.BatchAsync(received)).Qty);
            await env.AssertLedgerExplainsAsync(received);
        }

        [SkippableFact]
        public async Task Reverse_RestoresExactlyWhatWasConsumed_OnlyWhileTheOutputIsUntouched()
        {
            var env = await OpenAsync();
            await SkipOnTrayBatchPathAsync(env);
            var received = await ReceivedBatchAsync(env);
            await AddMpTraysAsync(env, MpP1, "9 Cavity", 30);
            var trays = await env.TraysAsync(MpP1, "9 Cavity");
            var (_, _, id, _) = await env.Transplants.CreateAsync(Plant(received, 100, 96, 4), Actor, Any);
            var output = (await env.Transplants.GetAsync(id))!.OutputReadyStockId!.Value;

            Assert.False((await env.Transplants.ReverseAsync(id, "", Actor, Any)).Success);            // reason required
            Assert.False((await env.Transplants.ReverseAsync(id, "x", Actor, _ => false)).Success);    // Area access
            var (ok, msg) = await env.Transplants.ReverseAsync(id, "recorded against the wrong batch", Actor, Any);
            Assert.True(ok, msg);
            Assert.Equal(240m, (await env.BatchAsync(received)).Qty);   // 96 transplanted + 4 lost returned
            Assert.Equal(0m, (await env.BatchAsync(output)).Qty);
            Assert.Equal(trays, await env.TraysAsync(MpP1, "9 Cavity"));
            Assert.Equal(TransplantRules.StatusReversed, (await env.Transplants.GetAsync(id))!.Status);
            Assert.False((await env.Transplants.ReverseAsync(id, "again", Actor, Any)).Success);       // replayed
            await env.AssertLedgerExplainsAsync(received);
            await env.AssertLedgerExplainsAsync(output);

            // A transplanting whose output is already booked cannot be reversed.
            var (_, _, id2, _) = await env.Transplants.CreateAsync(Plant(received, 90, 90, 0), Actor, Any);
            var output2 = (await env.Transplants.GetAsync(id2))!.OutputReadyStockId!.Value;
            using (var conn = new SqlConnection(env.ConnectionString))
            {
                await conn.OpenAsync();
                using var tx = conn.BeginTransaction();
                var (resOk, resMsg) = await env.Get<ReadyStockRepository>().RecordReservationAsync(conn, tx, output2, 9, "Test", null, Recorder, "test booking");
                Assert.True(resOk, resMsg);
                tx.Commit();
            }
            var (rOk, rMsg) = await env.Transplants.ReverseAsync(id2, "too late", Actor, Any);
            Assert.False(rOk);
            Assert.Contains("already been used", rMsg);
        }

        [SkippableFact]
        public async Task DaySummary_CountsInTransitAndToday_WithoutDoubleCounting()
        {
            var env = await OpenAsync();
            var before = await env.Transplants.GetDaySummaryAsync(AppClock.Today);
            var main = await ApprovedMainBatchAsync(env);
            var (_, _, pending, _) = await env.Transfers.CreateAsync(Send(main, 72), Actor, Any);
            var received = await ReceivedBatchAsync(env);
            await AddMpTraysAsync(env, MpP1, "24 Cavity", 20);
            var (ok, msg, _, _) = await env.Transplants.CreateAsync(Plant(received, 50, 48, 2, "24 Cavity"), Actor, Any);
            Assert.True(ok, msg);

            var after = await env.Transplants.GetDaySummaryAsync(AppClock.Today);
            Assert.Equal(before.InTransitPlants + 72, after.InTransitPlants);
            Assert.Equal(before.PendingTransfers + 1, after.PendingTransfers);
            Assert.Equal(before.TransplantedToday + 48, after.TransplantedToday);
            Assert.Equal(before.LostToday + 2, after.LostToday);
            // Mother Plant Area stock: +240 received, then -2 lost (transplanting only moves 48 between batches);
            // on the tray-batch path the 48 are in a tray batch awaiting Sowing Approval, not Ready Stock.
            Assert.Equal(before.MotherPlantAreaReadyStock + (await TrayBatchPathAsync(env) ? 190 : 238), after.MotherPlantAreaReadyStock);
            Assert.True((await env.Transfers.CancelAsync(pending, "test cleanup", Actor, Any)).Success);
        }

        // ===== 2026-10-09 take-over: acceptance scenario, every cavity, atomicity, totals =====================

        private static readonly SeedlingFulfilmentRepository.Actor BookingActor = new("transplant-test", Recorder);

        private static async Task<int> NewBookingAsync(Env env, int quantity)
        {
            var plantType = await env.ScalarAsync<int>("SELECT PlantTypeId FROM dbo.PlantSpecies WHERE Id = @S", ("@S", env.Species));
            return await env.ScalarAsync<int>(@"
INSERT INTO dbo.Bookings (SpeciesId, Quantity, CustomerName, Status, PlantId, DeliveryDate, AddedBy, BookingDate)
VALUES (@S, @Q, N'transplant-test customer', N'Pending', @P, CAST(GETDATE() AS date), N'transplant-test', GETDATE());
SELECT CAST(SCOPE_IDENTITY() AS INT);", ("@S", env.Species), ("@Q", quantity), ("@P", plantType));
        }

        // Plants still owned somewhere (approved - dispatched), over a set of batches.
        private static async Task<decimal> OwnedAsync(Env env, params int[] batches)
        {
            decimal total = 0;
            foreach (var b in batches) { var x = await env.BatchAsync(b); total += x.Qty - x.Dispatched; }
            return total;
        }

        // The specification's STEP 1-8, through the real code paths, with the stock equation checked at every step.
        [SkippableFact]
        public async Task Acceptance_Sow150_Ready_Transfer_Receive_Transplant480Into24_Loss5_ThenCustomerDispatch()
        {
            var env = await OpenAsync();
            // STEP 1-2: Main Area sowing in 150-cavity trays, approved -> Ready Stock (actual quantity from the system).
            var main = await ApprovedMainBatchAsync(env, 1050, "150 Cavity");                      // 7 trays
            Assert.Equal((1050m, 0m, 0m, 0m, 1050m), await env.BatchAsync(main));

            // STEP 3: Option A and Option B never share plants -- a customer reservation reduces what can be sent,
            // and plants in transit can never be reserved.
            var fulfil = env.Get<SeedlingFulfilmentRepository>();
            var bookingA = await NewBookingAsync(env, 150);
            var (aOk, aMsg) = await fulfil.AllocateBatchAsync(bookingA, main, 150, null, BookingActor, Any);      // 1 tray reserved
            Assert.True(aOk, aMsg);
            Assert.False((await env.Transfers.CreateAsync(Send(main, 1050), Actor, Any)).Success);                   // 7 trays > 6 available
            var (sOk, sMsg, transferId, _) = await env.Transfers.CreateAsync(Send(main, 600), Actor, Any);          // 4 trays
            Assert.True(sOk, sMsg);
            Assert.Equal((1050m, 150m, 0m, 600m, 300m), await env.BatchAsync(main));
            var bookingB = await NewBookingAsync(env, 450);
            Assert.False((await fulfil.AllocateBatchAsync(bookingB, main, 450, null, BookingActor, Any)).Success);   // only 300 free

            // STEP 4: the Mother Plant Supervisor receives into Green Bless P-1; the history keeps both places.
            var (rOk, rMsg, received) = await env.Transfers.ReceiveAsync(transferId, 600, null, Actor, Any);
            Assert.True(rOk, rMsg);
            Assert.Equal((450m, 150m, 0m, 0m, 300m), await env.BatchAsync(main));
            Assert.Equal((600m, 0m, 0m, 0m, 600m), await env.BatchAsync(received!.Value));
            var rb = (await env.Get<ReadyStockRepository>().GetByIdAsync(received.Value))!;
            Assert.Equal((MpArea, env.MpPolyhouse, "150 Cavity", main, ReadyStock.OriginTransfer), (rb.AreaId, rb.PolyhouseId, rb.CavityType, rb.ParentReadyStockId!.Value, rb.OriginType));

            // STEP 5: transplant 485 selected = 480 transplanted + 5 lost into 24-cavity trays -> exactly 20 trays.
            // Tray-batch path (2026-10-13): the batch is approved only from its Ready Date (transplant date +
            // Ready Days), so the transplant is dated Ready Days ago (fixture: the received batch is usable
            // since then and the trays were there before it) and the approval below falls on its Ready Date.
            var trayBatchPath = await TrayBatchPathAsync(env);
            var readyDays = trayBatchPath ? await env.ScalarAsync<int>("SELECT ISNULL(ReadyStockDays, 0) FROM dbo.PlantSpecies WHERE Id = @S", ("@S", env.Species)) : 0;
            Skip.If(trayBatchPath && readyDays <= 0, "The test variety has no Ready Days in this copy.");
            var transplantOn = AppClock.Today.AddDays(-readyDays);
            if (trayBatchPath)
                await env.ExecAsync("UPDATE dbo.ReadyStock SET FirstConfirmationDate = DATEADD(DAY, -@D - 1, SYSUTCDATETIME()) WHERE Id = @Id", ("@D", readyDays), ("@Id", received.Value));
            await AddMpTraysAsync(env, MpP1, "24 Cavity", 25, trayBatchPath ? AppClock.IndiaToUtc(transplantOn.AddDays(-1).AddHours(12)) : null);
            var trays0 = await env.TraysAsync(MpP1, "24 Cavity");
            var owned0 = await OwnedAsync(env, main, received.Value);
            var (tOk, tMsg, tid, _) = await env.Transplants.CreateAsync(new TransplantRepository.CreateRequest(received.Value, MpArea, MpP1, "24 Cavity",
                485, 480, 5, transplantOn, "acceptance", Guid.NewGuid(), "acceptance loss"), Actor, Any);
            Assert.True(tOk, tMsg);
            var t = (await env.Transplants.GetAsync(tid))!;
            Assert.Equal((20, 485m, 480m, 5m, "24 Cavity"), (t.OutputTrays, t.SelectedQuantity, t.TransplantedQuantity, t.LostQuantity, t.TargetCavityType));
            int output;
            if (trayBatchPath)
            {
                // 2026-10-13: a 20-tray growing batch in Sowing Approvals; Ready Stock only once it is approved.
                Assert.Null(t.OutputReadyStockId);
                var sw = (await env.Get<SeedSowingRepository>().GetByIdAsync(t.OutputSeedSowingId!.Value))!;
                Assert.Equal((SeedSowing.SourceTransplant, (int?)20, 480m, MpArea, env.MpPolyhouse, "Sown"), (sw.SourceType, sw.NumberOfTrays, sw.QuantitySown, sw.AreaId, sw.PolyhouseId, sw.Status));
                Assert.Equal(600m, (await env.BatchAsync(received.Value)).Qty + 480 + 5);
                Assert.Equal(trays0 - 20, await env.TraysAsync(MpP1, "24 Cavity"));
                Assert.Equal(owned0 - 480 - 5, await OwnedAsync(env, main, received.Value));     // 480 growing in trays, 5 lost
                Assert.Equal(AppClock.Today, sw.ExpectedReadyDate);                               // transplant date + Ready Days = today
                var (aOk2, aMsg2, _) = await env.Get<ReadyConfirmationRepository>().ConfirmAsync(sw.Id, 20, null, null, null, "transplant-test", Recorder, Any);
                Assert.True(aOk2, aMsg2);
                output = await env.ScalarAsync<int>("SELECT Id FROM dbo.ReadyStock WHERE SeedSowingId = @S", ("@S", sw.Id));
                var o2 = await env.BatchAsync(output);
                Assert.Equal((480m, 480m), (o2.Qty, o2.Available));
                Assert.Equal(owned0 - 5, await OwnedAsync(env, main, received.Value, output));   // nothing created; only the 5 lost left

                // STEP 8: Main Area sowing batch -> transfer -> received batch -> transplant tray batch -> approved batch.
                var lin = await env.Get<ReadyStockRepository>().GetLineageAsync(output);
                Assert.Equal(new[] { main, received.Value, output }, lin.Select(b => b.Id));
                Assert.Equal(new[] { ReadyStock.OriginSowing, ReadyStock.OriginTransfer, ReadyStock.OriginSowing }, lin.Select(b => b.OriginType));
                Assert.Equal((MpArea, env.MpPolyhouse, "24 Cavity", env.Species, (int?)sw.Id), (lin[2].AreaId, lin[2].PolyhouseId, lin[2].CavityType, lin[2].SpeciesId, lin[2].SeedSowingId));
            }
            else
            {
            output = t.OutputReadyStockId!.Value;
            // Source before = remaining + transplanted + lost; the output holds only the transplanted plants; 20 trays used.
            Assert.Equal(600m, (await env.BatchAsync(received.Value)).Qty + 480 + 5);
            var o1 = await env.BatchAsync(output);
            Assert.Equal((480m, 480m), (o1.Qty, o1.Available));
            Assert.Equal(trays0 - 20, await env.TraysAsync(MpP1, "24 Cavity"));
            Assert.Equal(owned0 - 5, await OwnedAsync(env, main, received.Value, output));        // nothing created; only the 5 lost left

            // STEP 8: traceability Main Area sowing batch -> transfer -> received batch -> transplant -> 24-cavity batch.
            var lineage = await env.Get<ReadyStockRepository>().GetLineageAsync(output);
            Assert.Equal(new[] { main, received.Value, output }, lineage.Select(b => b.Id));
            Assert.Equal(new[] { ReadyStock.OriginSowing, ReadyStock.OriginTransfer, ReadyStock.OriginTransplant }, lineage.Select(b => b.OriginType));
            Assert.Equal(lineage[0].SeedSowingId, lineage[2].SeedSowingId);
            var ob = lineage[2];
            Assert.Equal((MpArea, env.MpPolyhouse, "24 Cavity", env.Species), (ob.AreaId, ob.PolyhouseId, ob.CavityType, ob.SpeciesId));
            }

            // STEP 7: the transplanted stock follows the existing booking / dispatch workflow.
            var bookingC = await NewBookingAsync(env, 100);
            var (cOk, cMsg) = await fulfil.AllocateBatchAsync(bookingC, output, 100, null, BookingActor, Any);
            Assert.True(cOk, cMsg);
            var line = (await fulfil.GetAllocationsAsync(bookingC)).Single();
            var (dOk, dMsg) = await fulfil.DispatchAsync(bookingC, new[] { (line.Id, 100m) }, AppClock.Today, null, "acceptance dispatch", BookingActor, Any);
            Assert.True(dOk, dMsg);
            Assert.Equal((480m, 0m, 100m, 0m, 380m), await env.BatchAsync(output));
            Assert.Equal(owned0 - 5 - 100, await OwnedAsync(env, main, received.Value, output));
            // ... after which the transplanting can no longer be reversed.
            Assert.False((await env.Transplants.ReverseAsync(tid, "too late", Actor, Any)).Success);

            foreach (var b in new[] { main, received.Value, output }) await env.AssertLedgerExplainsAsync(b);
        }

        [SkippableTheory]
        [InlineData("9 Cavity", 100, 12, 1)]
        [InlineData("24 Cavity", 150, 7, 6)]     // the specification's example: 6 full trays + 1 tray of 6
        [InlineData("42 Cavity", 100, 3, 16)]
        [InlineData("102 Cavity", 230, 3, 26)]
        [InlineData("150 Cavity", 230, 2, 80)]
        public async Task Transplant_EverySupportedCavity_CeilingTrays_ExactPlants_PartialLastTray(string cavity, int plants, int trays, int lastTray)
        {
            var env = await OpenAsync();
            await SkipOnTrayBatchPathAsync(env);
            var source = await ReceivedBatchAsync(env, 240);
            await AddMpTraysAsync(env, MpP2, cavity, trays + 2);
            var trays0 = await env.TraysAsync(MpP2, cavity);
            var (ok, msg, id, _) = await env.Transplants.CreateAsync(new TransplantRepository.CreateRequest(source, MpArea, MpP2, cavity,
                plants, plants, 0, AppClock.Today, null, Guid.NewGuid()), Actor, Any);
            Assert.True(ok, msg);
            var t = (await env.Transplants.GetAsync(id))!;
            Assert.Equal((trays, (decimal)lastTray, cavity), (t.OutputTrays, t.PlantsInLastTray, t.TargetCavityType));
            Assert.Equal((decimal)plants, (await env.BatchAsync(t.OutputReadyStockId!.Value)).Qty);   // never rounded up to fill a tray
            Assert.Equal(240m - plants, (await env.BatchAsync(source)).Qty);
            Assert.Equal(trays0 - trays, await env.TraysAsync(MpP2, cavity));
            await env.AssertLedgerExplainsAsync(source);
            await env.AssertLedgerExplainsAsync(t.OutputReadyStockId.Value);
        }

        // A failure in the LAST write (the tray deduction, forced by a temporary trigger) rolls back the
        // transplanting row, the source movement and the output batch too: all or nothing.
        [SkippableFact]
        public async Task Transplant_FailureDuringTheTransaction_LeavesNothingBehind()
        {
            var env = await OpenAsync();
            var source = await ReceivedBatchAsync(env, 240);
            await AddMpTraysAsync(env, MpP1, "42 Cavity", 10);
            var trays0 = await env.TraysAsync(MpP1, "42 Cavity");
            var batches0 = await env.ScalarAsync<int>("SELECT COUNT(*) FROM dbo.ReadyStock");
            var sowings0 = await env.ScalarAsync<int>("SELECT COUNT(*) FROM dbo.SeedSowings");
            var ledger0 = await env.ScalarAsync<int>("SELECT COUNT(*) FROM dbo.ReadyStockTransactions WHERE ReadyStockId = @S", ("@S", source));
            var token = Guid.NewGuid();
            await env.ExecAsync(@"CREATE OR ALTER TRIGGER dbo.TR_E2E_FailTransplantTrays ON dbo.TrayStockTransactions AFTER INSERT AS
BEGIN IF EXISTS (SELECT 1 FROM inserted WHERE ReferenceType = N'ReadyStockTransplant') THROW 50999, 'E2E: simulated failure', 1; END");
            try
            {
                var (ok, _, _, _) = await env.Transplants.CreateAsync(new TransplantRepository.CreateRequest(source, MpArea, MpP1, "42 Cavity",
                    84, 84, 0, AppClock.Today, null, token), Actor, Any);
                Assert.False(ok);
            }
            finally { await env.ExecAsync("DROP TRIGGER IF EXISTS dbo.TR_E2E_FailTransplantTrays"); }

            Assert.Equal(0, await env.ScalarAsync<int>("SELECT COUNT(*) FROM dbo.ReadyStockTransplants WHERE SubmissionToken = @T", ("@T", token)));
            var s1 = await env.BatchAsync(source);
            Assert.Equal((240m, 240m), (s1.Qty, s1.Available));
            Assert.Equal(batches0, await env.ScalarAsync<int>("SELECT COUNT(*) FROM dbo.ReadyStock"));
            Assert.Equal(sowings0, await env.ScalarAsync<int>("SELECT COUNT(*) FROM dbo.SeedSowings"));   // no tray batch either
            Assert.Equal(ledger0, await env.ScalarAsync<int>("SELECT COUNT(*) FROM dbo.ReadyStockTransactions WHERE ReadyStockId = @S", ("@S", source)));
            Assert.Equal(trays0, await env.TraysAsync(MpP1, "42 Cavity"));

            // ... and the same form succeeds once the fault is gone (its token was never used).
            var (ok2, msg2, _, dup) = await env.Transplants.CreateAsync(new TransplantRepository.CreateRequest(source, MpArea, MpP1, "42 Cavity",
                84, 84, 0, AppClock.Today, null, token), Actor, Any);
            Assert.True(ok2, msg2);
            Assert.False(dup);
        }

        // Management totals stay consistent with the ledgers: per-Area physical Ready Stock + in transit
        // = company stock (approved - dispatched); a transfer moves plants, it never adds or doubles them.
        [SkippableFact]
        public async Task CompanyTotals_TransferMovesPlants_NeverDoubles_DashboardAreaFiguresReconcile()
        {
            var env = await OpenAsync();
            var dash = env.Get<ManagementDashboardRepository>();
            async Task<(decimal Company, decimal AreaPhysical, decimal InTransit)> Totals()
            {
                var company = await env.ScalarAsync<decimal>("SELECT ISNULL(SUM(Quantity - DispatchedQuantity), 0) FROM dbo.ReadyStock");
                var inTransit = await env.ScalarAsync<decimal>("SELECT ISNULL(SUM(InTransitQuantity), 0) FROM dbo.ReadyStock");
                var overview = await dash.GetOverviewAsync(DashboardPeriod.Resolve(DashboardPeriod.Today, null, null, AppClock.Today), AppClock.Today);
                return (company, overview.ReadyStock.Sum(s => s.Physical), inTransit);
            }
            var main = await ApprovedMainBatchAsync(env, 480);
            var t0 = await Totals();
            Assert.Equal(t0.Company, t0.AreaPhysical + t0.InTransit);

            var (ok, msg, transferId, _) = await env.Transfers.CreateAsync(Send(main, 240), Actor, Any);
            Assert.True(ok, msg);
            var t1 = await Totals();
            Assert.Equal(t0.Company, t1.Company);                                   // nothing created
            Assert.Equal(t0.AreaPhysical - 240, t1.AreaPhysical);                   // at neither location while in transit
            Assert.Equal(t1.Company, t1.AreaPhysical + t1.InTransit);

            Assert.True((await env.Transfers.ReceiveAsync(transferId, 230, "10 damaged on the way", Actor, Any)).Success);
            var t2 = await Totals();
            Assert.Equal(t0.Company - 10, t2.Company);                              // only the recorded transit loss leaves
            Assert.Equal(t2.Company, t2.AreaPhysical + t2.InTransit);
        }

        // ===== business dates on ledger rows (2026-10-10) ===================================================
        // TransactionDate = the operation's business date (at the recorded time of day);
        // CreatedAt = when it was recorded. Checked per row, independent of other data in the copy.

        private static async Task<List<(string Type, DateTime BusinessDay, DateTime RecordedDay, decimal Qty, string? Remarks)>> LedgerAsync(
            Env env, string table, string keyColumn, int key, string? referenceType = null, int? referenceId = null)
        {
            var list = new List<(string, DateTime, DateTime, decimal, string?)>();
            using var conn = new SqlConnection(env.ConnectionString);
            await conn.OpenAsync();
            using var cmd = new SqlCommand($@"SELECT TransactionType, TransactionDate, CreatedAt, Quantity, Remarks FROM dbo.{table}
WHERE {keyColumn} = @K AND (@RT IS NULL OR (ReferenceType = @RT AND ReferenceId = @RI)) ORDER BY Id", conn);
            cmd.Parameters.AddWithValue("@K", key);
            cmd.Parameters.Add("@RT", System.Data.SqlDbType.NVarChar, 100).Value = (object?)referenceType ?? DBNull.Value;
            cmd.Parameters.Add("@RI", System.Data.SqlDbType.Int).Value = (object?)referenceId ?? DBNull.Value;
            using var r = await cmd.ExecuteReaderAsync();
            while (await r.ReadAsync())
                list.Add((r.GetString(0), AppClock.ToIndiaTime(r.GetDateTime(1)).Date, AppClock.ToIndiaTime(r.GetDateTime(2)).Date,
                    r.GetDecimal(3), r.IsDBNull(4) ? null : r.GetString(4)));
            return list;
        }

        [SkippableFact]
        public async Task BusinessDate_TransferSentEarlier_ReceivedToday_EachRowOnItsOwnDay()
        {
            var env = await OpenAsync();
            var main = await ApprovedMainBatchAsync(env);
            var sentOn = AppClock.Today.AddDays(-2);
            var (ok, msg, id, _) = await env.Transfers.CreateAsync(Send(main, 240) with { TransferDate = sentOn }, Actor, Any);
            Assert.True(ok, msg);
            var hold = Assert.Single(await LedgerAsync(env, "ReadyStockTransactions", "ReadyStockId", main, MotherPlantTransferRules.ReferenceType, id));
            Assert.Equal(("TransferHold", sentOn, AppClock.Today, 240m), (hold.Type, hold.BusinessDay, hold.RecordedDay, hold.Qty));

            // Between sending and receipt: owned unchanged, physical and available reduced, counted once.
            var mid = await env.BatchAsync(main);
            Assert.Equal((480m, 240m, 240m), (mid.Qty - mid.Dispatched, mid.InTransit, mid.Available));

            var (rOk, rMsg, received) = await env.Transfers.ReceiveAsync(id, 240, null, Actor, Any);
            Assert.True(rOk, rMsg);
            var rows = await LedgerAsync(env, "ReadyStockTransactions", "ReadyStockId", main, MotherPlantTransferRules.ReferenceType, id);
            Assert.True(rows.Count > 1);
            Assert.All(rows.Where(r => r.Type != "TransferHold"), r => Assert.Equal(AppClock.Today, r.BusinessDay));   // receipt: today
            Assert.All(await LedgerAsync(env, "ReadyStockTransactions", "ReadyStockId", received!.Value), r => Assert.Equal(AppClock.Today, r.BusinessDay));
            var after = await env.BatchAsync(main);
            Assert.Equal((240m, 0m), (after.Qty - after.Dispatched, after.InTransit));
            Assert.Equal(240m, (await env.BatchAsync(received.Value)).Qty);
            await env.AssertLedgerExplainsAsync(main);
            await env.AssertLedgerExplainsAsync(received.Value);
        }

        [SkippableFact]
        public async Task BusinessDate_TransplantingDatedYesterday_ReadyAndTrayRowsFallOnThatDay()
        {
            var env = await OpenAsync();
            await SkipOnTrayBatchPathAsync(env);
            var received = await ReceivedBatchAsync(env);
            // Fixture: this received batch has been in the Mother Plant Area for three days.
            await env.ExecAsync("UPDATE dbo.ReadyStock SET FirstConfirmationDate = DATEADD(DAY, -3, SYSUTCDATETIME()) WHERE Id = @Id", ("@Id", received));
            await AddMpTraysAsync(env, MpP1, "9 Cavity", 20, AppClock.IndiaToUtc(AppClock.Today.AddDays(-2).AddHours(12)));   // there before the transplanting date
            var yesterday = AppClock.Today.AddDays(-1);
            var (ok, msg, id, _) = await env.Transplants.CreateAsync(Plant(received, 50, 45, 5) with { TransplantDate = yesterday }, Actor, Any);
            Assert.True(ok, msg);
            var output = (await env.Transplants.GetAsync(id))!.OutputReadyStockId!.Value;

            var source = await LedgerAsync(env, "ReadyStockTransactions", "ReadyStockId", received, TransplantRules.ReferenceType, id);
            Assert.Equal(new[] { -45m, -5m }, source.Select(r => r.Qty).OrderByDescending(q => Math.Abs(q)));
            Assert.All(source, r => Assert.Equal((yesterday, AppClock.Today), (r.BusinessDay, r.RecordedDay)));
            Assert.All(await LedgerAsync(env, "ReadyStockTransactions", "ReadyStockId", output), r => Assert.Equal(yesterday, r.BusinessDay));
            var trayPool = await env.ScalarAsync<int>("SELECT TrayStockId FROM dbo.ReadyStockTransplants WHERE Id = @Id", ("@Id", id));
            var tray = Assert.Single(await LedgerAsync(env, "TrayStockTransactions", "TrayStockId", trayPool, TransplantRules.ReferenceType, id));
            Assert.Equal(("Transplant", yesterday, AppClock.Today, -5m), (tray.Type, tray.BusinessDay, tray.RecordedDay, tray.Qty));   // CEILING(45 / 9)
            await env.AssertLedgerExplainsAsync(received);
            await env.AssertLedgerExplainsAsync(output);
        }

        [SkippableFact]
        public async Task BusinessDate_DispatchDatedBeforeReadyApproval_IsAllowedAndFlagged_ReleaseNeverBeforeReservation_FutureRefused()
        {
            var env = await OpenAsync();
            var main = await ApprovedMainBatchAsync(env);          // approved Ready today
            var repo = env.Get<ReadyStockRepository>();
            var dispatchedOn = AppClock.Today.AddDays(-3);
            using (var conn = new SqlConnection(env.ConnectionString))
            {
                await conn.OpenAsync();
                using var tx = conn.BeginTransaction();
                var (resOk, resMsg) = await repo.RecordReservationAsync(conn, tx, main, 24, "BusinessDateTest", 1, Recorder, "test reservation");
                Assert.True(resOk, resMsg);
                var (futOk, futMsg) = await repo.RecordDispatchAsync(conn, tx, main, 24, "BusinessDateTest", 1, Recorder, "test dispatch",
                    businessDateIndia: AppClock.Today.AddDays(1), reservedAtUtc: DateTime.UtcNow);
                Assert.False(futOk);
                Assert.Equal(LedgerDates.FutureDateMessage, futMsg);
                var (dOk, dMsg) = await repo.RecordDispatchAsync(conn, tx, main, 24, "BusinessDateTest", 1, Recorder, "test dispatch",
                    businessDateIndia: dispatchedOn, reservedAtUtc: DateTime.UtcNow);
                Assert.True(dOk, dMsg);
                tx.Commit();
            }
            var rows = await LedgerAsync(env, "ReadyStockTransactions", "ReadyStockId", main, "BusinessDateTest", 1);
            var dispatch = Assert.Single(rows, r => r.Type == "Dispatch");
            Assert.Equal((dispatchedOn, AppClock.Today, -24m), (dispatch.BusinessDay, dispatch.RecordedDay, dispatch.Qty));
            Assert.Contains("[dated before this batch was approved Ready on", dispatch.Remarks);
            var release = Assert.Single(rows, r => r.Type == "ReservationRelease");
            Assert.Equal(AppClock.Today, release.BusinessDay);       // reserved today: released no earlier than that
            var b = await env.BatchAsync(main);
            Assert.Equal((24m, 0m), (b.Dispatched, b.Reserved));
            await env.AssertLedgerExplainsAsync(main);
        }

        // ===== empty trays as of the transplanting date (2026-10-10 fix) ======================================
        // Each test gets its own scratch-only Mother Plant Area + Polyhouse, so the tray pool's history is
        // exactly what the test writes (deactivated at the end; never reused).

        private static async Task<(int Area, int Polyhouse)> FreshMotherPlantPolyhouseAsync(Env env)
        {
            var name = "ZZTEST-TRAYHIST " + Guid.NewGuid().ToString("N")[..8];
            var area = await env.ScalarAsync<int>("INSERT INTO dbo.Area (Name, IsActive, AreaType, Remarks) VALUES (@N, 1, N'MotherPlant', N'scratch test fixture'); SELECT CAST(SCOPE_IDENTITY() AS INT)", ("@N", name));
            var polyhouse = await env.ScalarAsync<int>("INSERT INTO dbo.Polyhouses (Name, AreaId) VALUES (N'ZZTEST P-1', @A); SELECT CAST(SCOPE_IDENTITY() AS INT)", ("@A", area));
            return (area, polyhouse);
        }

        private static Task RetireAsync(Env env, int area) => env.ExecAsync("UPDATE dbo.Area SET IsActive = 0 WHERE Id = @A", ("@A", area));

        private static DateTime NoonUtc(DateTime indiaDay) => AppClock.IndiaToUtc(indiaDay.Date.AddHours(12));

        private static async Task AddTraysAtAsync(Env env, int area, int polyhouse, string cavity, decimal trays, DateTime indiaDay)
        {
            var (ok, msg, _, _) = await env.Get<TrayStockRepository>().AddStockAsync(area, polyhouse, cavity, trays, NoonUtc(indiaDay),
                Random.Shared.Next(1, int.MaxValue), SupervisorId, "tray-history-test", "tray history test", Any);
            Assert.True(ok, msg);
        }

        private static async Task<List<TrayHistoryRules.NegativeStretch>> NegativeHistoryAsync(Env env, int area)
        {
            var rows = await env.Get<TrayStockRepository>().GetTransactionsAsync(area);
            return TrayHistoryRules.NegativeStretches(rows.Select(t => new TrayHistoryRules.Movement(t.TrayStockId, t.TraySize ?? "", t.TransactionDate, t.Id, t.Quantity)));
        }

        // A received batch usable since 30 days ago (fixture), so only the trays decide the transplanting date.
        private static async Task<int> LongReceivedBatchAsync(Env env)
        {
            var received = await ReceivedBatchAsync(env);
            await env.ExecAsync("UPDATE dbo.ReadyStock SET FirstConfirmationDate = DATEADD(DAY, -30, SYSUTCDATETIME()) WHERE Id = @Id", ("@Id", received));
            return received;
        }

        // 2026-10-14: these are Mother Plant Areas -- their ONE active Area pool once 2026-10-14_MotherPlantAreaTraysAndTransferPurpose.sql
        // is applied (inactive, so not counted, before it), the Polyhouse pool otherwise.
        private static Task<decimal> PoolAsync(Env env, int area, int polyhouse, string cavity)
            => env.ScalarAsync<decimal>("SELECT ISNULL(SUM(PhysicalQuantity), 0) FROM dbo.TrayStock WHERE AreaId = @A AND (PolyhouseId = @P OR (PolyhouseId IS NULL AND IsActive = 1)) AND TraySize = @C",
                ("@A", area), ("@P", polyhouse), ("@C", cavity));

        [SkippableFact]
        public async Task TrayHistory_TransplantDatedBeforeTheTraysArrived_IsRefused_AfterArrival_IsAccepted_HistoryNeverNegative()
        {
            var env = await OpenAsync();
            var (area, polyhouse) = await FreshMotherPlantPolyhouseAsync(env);
            try
            {
                var received = await LongReceivedBatchAsync(env);
                var today = AppClock.Today;
                await AddTraysAtAsync(env, area, polyhouse, "9 Cavity", 10, today.AddDays(-2));   // trays arrive two days ago
                var before = (await env.BatchAsync(received)).Qty;

                // dated BEFORE the trays arrived: refused, although 10 trays are in the pool today
                var early = Plant(received, 9, 9, 0, "9 Cavity", polyhouse) with { TargetAreaId = area, TransplantDate = today.AddDays(-5) };
                var (ok, msg, _, _) = await env.Transplants.CreateAsync(early, Actor, Any);
                Assert.False(ok);
                Assert.Contains($"on {today.AddDays(-5):dd/MM/yyyy}", msg);
                Assert.Equal(before, (await env.BatchAsync(received)).Qty);                        // nothing moved
                Assert.Equal(10m, await PoolAsync(env, area, polyhouse, "9 Cavity"));
                Assert.Equal(0, await env.ScalarAsync<int>("SELECT COUNT(*) FROM dbo.ReadyStockTransplants WHERE PolyhouseId = @P", ("@P", polyhouse)));

                // dated AFTER they arrived: accepted
                var (okLate, msgLate, _, _) = await env.Transplants.CreateAsync(
                    Plant(received, 18, 18, 0, "9 Cavity", polyhouse) with { TargetAreaId = area, TransplantDate = today.AddDays(-1) }, Actor, Any);
                Assert.True(okLate, msgLate);
                Assert.Equal(8m, await PoolAsync(env, area, polyhouse, "9 Cavity"));
                Assert.Empty(await NegativeHistoryAsync(env, area));
                await env.AssertLedgerExplainsAsync(received);
            }
            finally { await RetireAsync(env, area); }
        }

        [SkippableFact]
        public async Task TrayHistory_LaterConsumptionCounts_ABackDatedTransplantMayOnlyUseWhatIsLeftFromThatDateOn()
        {
            var env = await OpenAsync();
            var (area, polyhouse) = await FreshMotherPlantPolyhouseAsync(env);
            try
            {
                var received = await LongReceivedBatchAsync(env);
                var today = AppClock.Today;
                await AddTraysAtAsync(env, area, polyhouse, "9 Cavity", 10, today.AddDays(-6));
                // 8 of them are used up TODAY (a count correction); on day -3 there were 10, but only 2 remain for good
                var (aOk, aMsg, _, _) = await env.Get<TrayStockRepository>().AdjustAsync(area, polyhouse, "9 Cavity", -8, today,
                    Random.Shared.Next(1, int.MaxValue), "8 trays broken", Recorder, "tray-history-test", Any);
                Assert.True(aOk, aMsg);

                var (ok3, msg3, _, _) = await env.Transplants.CreateAsync(
                    Plant(received, 27, 27, 0, "9 Cavity", polyhouse) with { TargetAreaId = area, TransplantDate = today.AddDays(-3) }, Actor, Any);
                Assert.False(ok3);                                                              // 3 trays > the 2 left from day -3 on
                Assert.Contains("2 available from that date onward", msg3);
                var (ok2, msg2, _, _) = await env.Transplants.CreateAsync(
                    Plant(received, 18, 18, 0, "9 Cavity", polyhouse) with { TargetAreaId = area, TransplantDate = today.AddDays(-3) }, Actor, Any);
                Assert.True(ok2, msg2);
                Assert.Equal(0m, await PoolAsync(env, area, polyhouse, "9 Cavity"));
                Assert.Empty(await NegativeHistoryAsync(env, area));

                // and today, with no trays left, the ordinary check still refuses
                var (okToday, _, _, _) = await env.Transplants.CreateAsync(Plant(received, 9, 9, 0, "9 Cavity", polyhouse) with { TargetAreaId = area }, Actor, Any);
                Assert.False(okToday);
            }
            finally { await RetireAsync(env, area); }
        }

        [SkippableFact]
        public async Task TrayHistory_TransfersAndAdjustmentsWithDifferentBusinessDates_NeverTakeTraysBeforeTheyArrived()
        {
            var env = await OpenAsync();
            var (source, sourcePolyhouse) = await FreshMotherPlantPolyhouseAsync(env);
            var (dest, destPolyhouse) = await FreshMotherPlantPolyhouseAsync(env);
            try
            {
                var today = AppClock.Today;
                var transfers = env.Get<TrayTransferRepository>();
                await AddTraysAtAsync(env, source, sourcePolyhouse, "42 Cavity", 6, today.AddDays(-2));
                TrayTransferRequest Send(int qty, DateTime date) => new()
                {
                    SourceAreaId = source, SourcePolyhouseId = sourcePolyhouse, DestinationAreaId = dest, DestinationPolyhouseId = destPolyhouse,
                    TraySize = "42 Cavity", Quantity = qty, TransferDate = date, SubmissionToken = Guid.NewGuid(), Remarks = "tray history test",
                };

                // sent with a date before the trays arrived: refused
                var (e1, m1, _, _, _) = await transfers.CreateAsync(Send(4, today.AddDays(-4)), Recorder, "tray-history-test", Any, anyValidDestination: true);
                Assert.False(e1);
                Assert.Contains("Only 0 empty", m1);
                // a removal (adjustment) dated before they arrived: refused
                var (a1, am1, _, _) = await env.Get<TrayStockRepository>().AdjustAsync(source, sourcePolyhouse, "42 Cavity", -2, today.AddDays(-3),
                    Random.Shared.Next(1, int.MaxValue), "count before arrival", Recorder, "tray-history-test", Any);
                Assert.False(a1);
                Assert.Contains("below zero", am1);
                Assert.Equal(6m, await PoolAsync(env, source, sourcePolyhouse, "42 Cavity"));

                // sent the day after they arrived, received today: accepted on both sides
                var (ok, msg, id, _, _) = await transfers.CreateAsync(Send(4, today.AddDays(-1)), Recorder, "tray-history-test", Any, anyValidDestination: true);
                Assert.True(ok, msg);
                Assert.True((await transfers.ReceiveAsync(id, today, Recorder, "tray-history-test", Any)).Success);
                Assert.Equal(2m, await PoolAsync(env, source, sourcePolyhouse, "42 Cavity"));
                Assert.Equal(4m, await PoolAsync(env, dest, destPolyhouse, "42 Cavity"));

                // at the destination, the trays exist only from TODAY (receipt): a transplant dated yesterday cannot use them
                var received = await LongReceivedBatchAsync(env);
                var (tOk, tMsg, _, _) = await env.Transplants.CreateAsync(
                    Plant(received, 42, 42, 0, "42 Cavity", destPolyhouse) with { TargetAreaId = dest, TransplantDate = today.AddDays(-1) }, Actor, Any);
                Assert.False(tOk);
                Assert.Contains("available from that date onward", tMsg);
                var (tOk2, tMsg2, _, _) = await env.Transplants.CreateAsync(
                    Plant(received, 42, 42, 0, "42 Cavity", destPolyhouse) with { TargetAreaId = dest }, Actor, Any);
                Assert.True(tOk2, tMsg2);

                Assert.Empty(await NegativeHistoryAsync(env, source));
                Assert.Empty(await NegativeHistoryAsync(env, dest));
            }
            finally
            {
                await RetireAsync(env, source);
                await RetireAsync(env, dest);
            }
        }

        // ===== business receipt date of a Ready Stock transfer (2026-10-11) ==================================

        private static Task<bool> ReceiptDatesAppliedAsync(Env env)
            => env.ScalarAsync<int>("SELECT CASE WHEN COL_LENGTH('dbo.InternalTransfers', 'ReceivedDate') IS NULL THEN 0 ELSE 1 END").ContinueWith(t => t.Result == 1);

        [SkippableFact]
        public async Task Receipt_BackdatedWithAReason_DatedAtTheReceiptDate_RecordedTimeKept_NeverBeforeTheSend()
        {
            var env = await OpenAsync();
            Skip.IfNot(await ReceiptDatesAppliedAsync(env), "2026-10-11_ReadyStockReceiptDate.sql is not applied to this scratch database.");
            var main = await ApprovedMainBatchAsync(env);
            var sentOn = AppClock.Today.AddDays(-3);
            var receivedOn = AppClock.Today.AddDays(-1);
            var (ok, msg, id, _) = await env.Transfers.CreateAsync(Send(main, 240) with { TransferDate = sentOn }, Actor, Any);
            Assert.True(ok, msg);

            async Task<string?> Refused(DateTime date, string? reason)
            {
                var (rOk, rMsg, _) = await env.Transfers.ReceiveAsync(id, 240, null, Actor, Any, date, reason);
                Assert.False(rOk);
                return rMsg;
            }
            Assert.Contains("earlier than today", await Refused(receivedOn, null));                 // a late entry needs a reason
            Assert.Contains("before the transfer was sent", await Refused(sentOn.AddDays(-1), "x"));
            Assert.Contains("future", await Refused(AppClock.Today.AddDays(1), "x"));
            Assert.Equal(240m, (await env.BatchAsync(main)).InTransit);                                 // nothing moved

            var (okR, msgR, received) = await env.Transfers.ReceiveAsync(id, 230, "10 broken in the vehicle", Actor, Any, receivedOn, "received Tuesday, entered today");
            Assert.True(okR, msgR);
            var rows = await LedgerAsync(env, "ReadyStockTransactions", "ReadyStockId", main, MotherPlantTransferRules.ReferenceType, id);
            Assert.Equal(sentOn, Assert.Single(rows, r => r.Type == "TransferHold").BusinessDay);
            Assert.All(rows.Where(r => r.Type != "TransferHold"), r => Assert.Equal((receivedOn, AppClock.Today), (r.BusinessDay, r.RecordedDay)));
            Assert.Equal(new[] { "TransferHoldRelease", "TransferLoss", "TransferOut" }, rows.Where(r => r.Type != "TransferHold").Select(r => r.Type).OrderBy(t => t));
            var arrived = Assert.Single(await LedgerAsync(env, "ReadyStockTransactions", "ReadyStockId", received!.Value));
            Assert.Equal(("TransferIn", receivedOn, AppClock.Today, 230m), (arrived.Type, arrived.BusinessDay, arrived.RecordedDay, arrived.Qty));
            Assert.Contains("recorded later", arrived.Remarks);

            // stored: business receipt date + reason; the recorded time stays in ConfirmedDate
            Assert.Equal(receivedOn, await env.ScalarAsync<DateTime>("SELECT ReceivedDate FROM dbo.InternalTransfers WHERE Id = @Id", ("@Id", id)));
            Assert.Equal("received Tuesday, entered today", await env.ScalarAsync<string>("SELECT ReceiptDateReason FROM dbo.InternalTransfers WHERE Id = @Id", ("@Id", id)));
            Assert.Equal(AppClock.Today, AppClock.ToIndiaTime(await env.ScalarAsync<DateTime>("SELECT ConfirmedDate FROM dbo.InternalTransfers WHERE Id = @Id", ("@Id", id))).Date);
            // the received batch is usable from its receipt date: transplanting dated that day is accepted
            Assert.Equal(receivedOn, AppClock.ToIndiaTime(await env.ScalarAsync<DateTime>("SELECT FirstConfirmationDate FROM dbo.ReadyStock WHERE Id = @Id", ("@Id", received.Value))).Date);
            await AddMpTraysAsync(env, MpP1, "9 Cavity", 10, AppClock.IndiaToUtc(receivedOn.AddDays(-1).AddHours(12)));   // there before the transplanting date
            var (tOk, tMsg, _, _) = await env.Transplants.CreateAsync(Plant(received.Value, 9, 9, 0) with { TransplantDate = receivedOn }, Actor, Any);
            Assert.True(tOk, tMsg);

            var src = await env.BatchAsync(main);
            Assert.Equal((240m, 0m), (src.Qty, src.InTransit));                                           // 480 - 230 - 10, counted once
            await env.AssertLedgerExplainsAsync(main);
            await env.AssertLedgerExplainsAsync(received.Value);
        }

        [SkippableFact]
        public async Task Receipt_SameDayAsTheSend_IsNeverDatedBeforeTheHold_TodayNeedsNoReason()
        {
            var env = await OpenAsync();
            Skip.IfNot(await ReceiptDatesAppliedAsync(env), "2026-10-11_ReadyStockReceiptDate.sql is not applied to this scratch database.");
            var main = await ApprovedMainBatchAsync(env);
            var sentOn = AppClock.Today.AddDays(-2);
            var (_, _, id, _) = await env.Transfers.CreateAsync(Send(main, 48) with { TransferDate = sentOn }, Actor, Any);
            Assert.True((await env.Transfers.ReceiveAsync(id, 48, null, Actor, Any, sentOn, "arrived the day it was sent")).Success);
            var rows = await env.ScalarAsync<int>(@"SELECT COUNT(*) FROM dbo.ReadyStockTransactions r
WHERE r.ReferenceType = N'InternalTransfer' AND r.ReferenceId = @Id AND r.TransactionType <> N'TransferHold'
  AND r.TransactionDate < (SELECT h.TransactionDate FROM dbo.ReadyStockTransactions h WHERE h.ReferenceType = N'InternalTransfer' AND h.ReferenceId = @Id AND h.TransactionType = N'TransferHold')",
                ("@Id", id));
            Assert.Equal(0, rows);

            var main2 = await ApprovedMainBatchAsync(env);
            var (_, _, id2, _) = await env.Transfers.CreateAsync(Send(main2, 24), Actor, Any);
            Assert.True((await env.Transfers.ReceiveAsync(id2, 24, null, Actor, Any)).Success);                 // default: today, no reason
            Assert.Equal(AppClock.Today, await env.ScalarAsync<DateTime>("SELECT ReceivedDate FROM dbo.InternalTransfers WHERE Id = @Id", ("@Id", id2)));
            Assert.Null(await env.ScalarAsync<string>("SELECT ReceiptDateReason FROM dbo.InternalTransfers WHERE Id = @Id", ("@Id", id2)));
        }

        // ===== history across several days: every surface agrees (2026-10-11) ===============================
        // One Main Area batch through approval (5 days ago, fixture), a dispatch, a transfer sent on one day
        // and received two days later with a loss, a cancelled transfer, and a transplanting that is reversed.
        // Daily Operations is built for each day BEFORE and AFTER; the difference for the batch's Plant Type
        // must be exactly this scenario, day by day -- never today's stock under a past date.
        [SkippableFact]
        public async Task History_MultiDayScenario_EachDayShowsExactlyItsOwnMovements_AndSurfacesAgreeToday()
        {
            var env = await OpenAsync();
            Skip.IfNot(await ReceiptDatesAppliedAsync(env), "2026-10-11_ReadyStockReceiptDate.sql is not applied to this scratch database.");
            var reports = env.Get<DailyOperationsReportRepository>();
            var today = AppClock.Today;
            var from = today.AddDays(-6);
            var plantType = await env.ScalarAsync<string>("SELECT pt.Name FROM dbo.PlantSpecies ps JOIN dbo.PlantTypes pt ON pt.Id = ps.PlantTypeId WHERE ps.Id = @S", ("@S", env.Species));
            Skip.If(plantType == null, "The test variety has no Plant Type.");
            plantType = plantType!.Trim();

            async Task<Dictionary<DateTime, (decimal Owned, decimal Reserved, decimal InTransit, bool Available)>> SnapshotAsync()
            {
                var data = await reports.GetAsync(from, today);
                Skip.If(data.Coverage.ReadyStockUnreconciledBatches > 0, "This copy's Ready Stock ledger does not reconcile.");
                return DailyOperationsReportRules.BuildAll(data, today, AppClock.Now).ToDictionary(r => r.ReportDate.Date, r =>
                {
                    var l = r.ReadyStock.FirstOrDefault(x => x.PlantTypeName.Trim() == plantType);
                    return (l?.Owned ?? 0, l?.Reserved ?? 0, l?.InTransit ?? 0, r.ReadyStockAvailable);
                });
            }
            var before = await SnapshotAsync();

            // approved Ready 5 days ago (fixture: the approval rows and the Ready date of this new batch are moved back)
            var main = await ApprovedMainBatchAsync(env);
            await env.ExecAsync("UPDATE dbo.ReadyStockTransactions SET TransactionDate = DATEADD(DAY, -5, TransactionDate) WHERE ReadyStockId = @Id", ("@Id", main));
            await env.ExecAsync("UPDATE dbo.ReadyStock SET FirstConfirmationDate = DATEADD(DAY, -5, FirstConfirmationDate) WHERE Id = @Id", ("@Id", main));
            // a dispatch of 24 dated 3 days ago (reserved and released today)
            using (var conn = new SqlConnection(env.ConnectionString))
            {
                await conn.OpenAsync();
                using var tx = conn.BeginTransaction();
                var repo = env.Get<ReadyStockRepository>();
                Assert.True((await repo.RecordReservationAsync(conn, tx, main, 24, "HistoryTest", 1, Recorder, "history test")).Success);
                Assert.True((await repo.RecordDispatchAsync(conn, tx, main, 24, "HistoryTest", 1, Recorder, "history test",
                    businessDateIndia: today.AddDays(-3), reservedAtUtc: DateTime.UtcNow)).Success);
                tx.Commit();
            }
            // transfer A: 240 sent 3 days ago, received yesterday (230 + 10 lost)
            var (_, _, a, _) = await env.Transfers.CreateAsync(Send(main, 240) with { TransferDate = today.AddDays(-3) }, Actor, Any);
            var (aOk, aMsg, received) = await env.Transfers.ReceiveAsync(a, 230, "10 broken", Actor, Any, today.AddDays(-1), "entered a day late");
            Assert.True(aOk, aMsg);
            // transfer B: 48 sent 2 days ago, cancelled today
            var (_, _, bId, _) = await env.Transfers.CreateAsync(Send(main, 48) with { TransferDate = today.AddDays(-2) }, Actor, Any);
            Assert.True((await env.Transfers.CancelAsync(bId, "not needed", Actor, Any)).Success);
            // transplanting today (95 + 5 lost; 99 + 1 waste on the tray-batch path -- 100 processed make 11 complete 9-cavity
            // trays and the remainder is waste, 2026-10-16), reversed today
            await AddMpTraysAsync(env, MpP1, "9 Cavity", 15);
            var (moved, lostHere) = await TrayBatchPathAsync(env) ? (99m, 1m) : (95m, 5m);
            var (tOk, tMsg, tp, _) = await env.Transplants.CreateAsync(Plant(received!.Value, 100, moved, lostHere), Actor, Any);
            Assert.True(tOk, tMsg);
            Assert.True((await env.Transplants.ReverseAsync(tp, "entered on the wrong batch", Actor, Any)).Success);

            var after = await SnapshotAsync();
            (decimal O, decimal R, decimal T) Expected(DateTime d)
            {
                decimal o = 0, t = 0;
                if (d >= today.AddDays(-5)) o += 480;
                if (d >= today.AddDays(-3)) { o -= 24; t += 240; }
                if (d >= today.AddDays(-2)) t += 48;
                if (d >= today.AddDays(-1)) { o -= 10; t -= 240; }
                if (d >= today) t -= 48;
                return (o, 0, t);
            }
            for (var d = from; d <= today; d = d.AddDays(1))
            {
                Assert.True(after[d].Available, $"{d:yyyy-MM-dd} not available");
                var delta = (after[d].Owned - before[d].Owned, after[d].Reserved - before[d].Reserved, after[d].InTransit - before[d].InTransit);
                Assert.Equal(Expected(d), delta);
            }

            // today: Daily Operations, Dashboard, Ready Stock table and the WhatsApp source all give one Available figure
            var todayReport = DailyOperationsReportRules.Build(await reports.GetAsync(today, today), today, today, AppClock.Now);
            var table = await env.ScalarAsync<decimal>("SELECT ISNULL(SUM(AvailableQuantity), 0) FROM dbo.ReadyStock");
            var overview = await env.Get<ManagementDashboardRepository>().GetOverviewAsync(DashboardPeriod.Resolve(DashboardPeriod.Today, null, null, today), today);
            var whatsApp = (await env.Get<ManagementDashboardRepository>().GetReadyStockSummaryAsync(new ManagementDashboardFilters(), today))
                .ReadyStockQuantityByPlantType.Sum(p => p.Quantity);
            Assert.Equal(table, todayReport.ReadyStockAvailableTotal);
            Assert.Equal(table, ManagementDashboardCalculations.Totals(overview.ReadyStock).Available);
            Assert.Equal(table, whatsApp);
            await env.AssertLedgerExplainsAsync(main);
            await env.AssertLedgerExplainsAsync(received.Value);
        }

        // ===== tray-batch path (2026-10-13_TraysToSowingApprovals.sql) ======================================
        // Main Area Ready Stock -> transfer -> receipt -> "Transplant into Trays": the plants become a growing
        // tray batch in Sowing Approvals (complete trays only), Ready Date = transplant date + the variety's
        // Ready Days (snapshot), and Ready Stock exists only once the batch is approved.

        private static async Task<int> TrayBatchReadyDaysAsync(Env env)
        {
            Skip.IfNot(await TrayBatchPathAsync(env), "2026-10-13_TraysToSowingApprovals.sql is not applied to this scratch database.");
            var days = await env.ScalarAsync<int?>("SELECT ReadyStockDays FROM dbo.PlantSpecies WHERE Id = @S", ("@S", env.Species));
            Skip.If(days is not > 0, "The test variety has no Ready Days in this copy.");
            return days!.Value;
        }

        [SkippableFact]
        public async Task TrayBatch_ReceiptThenTransplant_GoesToSowingApprovals_NotApprovedBeforeItsReadyDate_ApprovedOnceOnIt()
        {
            var env = await OpenAsync();
            var days = await TrayBatchReadyDaysAsync(env);
            var received = await ReceivedBatchAsync(env);                                                    // 240 in P-1
            // fixture: the received batch is usable, and the trays were there, since before Ready Days ago
            await env.ExecAsync("UPDATE dbo.ReadyStock SET FirstConfirmationDate = DATEADD(DAY, -@D - 5, SYSUTCDATETIME()) WHERE Id = @Id", ("@D", days), ("@Id", received));
            await AddMpTraysAsync(env, MpP1, "24 Cavity", 20, AppClock.IndiaToUtc(AppClock.Today.AddDays(-days - 4).AddHours(12)));
            var trays0 = await env.TraysAsync(MpP1, "24 Cavity");
            var originalSowingDate = (await env.Get<ReadyStockRepository>().GetByIdAsync(received))!.SowingDate;
            var transplantDate = AppClock.Today.AddDays(-2);
            var token = Guid.NewGuid();

            var req = Plant(received, 125, 120, 5, "24 Cavity", token: token) with { TransplantDate = transplantDate };
            var (ok, msg, id, dup) = await env.Transplants.CreateAsync(req, Actor, Any);
            Assert.True(ok, msg);
            Assert.False(dup);
            Assert.Contains("Sowing Approvals", msg);
            var t = (await env.Transplants.GetAsync(id))!;
            Assert.Null(t.OutputReadyStockId);                                                               // no Ready Stock yet
            Assert.Equal((5, 120m, 5m, transplantDate.AddDays(days), (int?)days),
                (t.OutputTrays, t.TransplantedQuantity, t.LostQuantity, t.OutputReadyDate, t.OutputReadyDays));
            var sw = (await env.Get<SeedSowingRepository>().GetByIdAsync(t.OutputSeedSowingId!.Value))!;
            Assert.Equal((SeedSowing.SourceTransplant, (int?)null, (int?)5, 120m, 120m, "Sown"),
                (sw.SourceType, sw.SupervisorId, sw.NumberOfTrays, sw.QuantitySown, sw.SeedQuantity, sw.Status));
            Assert.Equal((MpArea, env.MpPolyhouse, "24 Cavity", env.Species, (int?)null), (sw.AreaId, sw.PolyhouseId, sw.CavityType, sw.SpeciesId, sw.GrowingMaterialTypeId));
            Assert.Equal((originalSowingDate.Date, transplantDate.AddDays(days), (int?)days), (sw.SowingDate.Date, sw.ExpectedReadyDate, sw.ReadyStockDays));
            Assert.Equal((int?)id, sw.TransplantId);
            Assert.True(sw.IsTransplantedBatch);

            // source: 240 - 120 transplanted - 5 lost; trays: 5 x 24 Cavity, dated the transplant date
            Assert.Equal(115m, (await env.BatchAsync(received)).Qty);
            Assert.Equal(trays0 - 5, await env.TraysAsync(MpP1, "24 Cavity"));
            var trayPool = await env.ScalarAsync<int>("SELECT TrayStockId FROM dbo.ReadyStockTransplants WHERE Id = @Id", ("@Id", id));
            var tray = Assert.Single(await LedgerAsync(env, "TrayStockTransactions", "TrayStockId", trayPool, TransplantRules.ReferenceType, id));
            Assert.Equal(("Transplant", transplantDate, AppClock.Today, -5m), (tray.Type, tray.BusinessDay, tray.RecordedDay, tray.Qty));
            Assert.Equal(0, await env.ScalarAsync<int>("SELECT COUNT(*) FROM dbo.ReadyStock WHERE SeedSowingId = @S", ("@S", sw.Id)));
            await env.AssertLedgerExplainsAsync(received);

            // the same form saved again: the same transplanting, nothing consumed twice
            var (ok2, _, id2, dup2) = await env.Transplants.CreateAsync(req, Actor, Any);
            Assert.True(ok2);
            Assert.True(dup2);
            Assert.Equal(id, id2);
            Assert.Equal(115m, (await env.BatchAsync(received)).Qty);
            Assert.Equal(1, await env.ScalarAsync<int>("SELECT COUNT(*) FROM dbo.ReadyStockTransplants WHERE SubmissionToken = @T", ("@T", token)));

            // a later change of the variety's Ready Days never rewrites this batch's Ready Date
            try
            {
                await env.ExecAsync("UPDATE dbo.PlantSpecies SET ReadyStockDays = @D WHERE Id = @S", ("@D", days + 7), ("@S", env.Species));
                Assert.Equal(transplantDate.AddDays(days), (await env.Get<SeedSowingRepository>().GetByIdAsync(sw.Id))!.ExpectedReadyDate);
            }
            finally { await env.ExecAsync("UPDATE dbo.PlantSpecies SET ReadyStockDays = @D WHERE Id = @S", ("@D", days), ("@S", env.Species)); }

            // BEFORE its Ready Date: refused by the server (inside the approval transaction), shown as "Approve
            // from <date>" by the same rule on the list / Confirm page, and refused by the database too.
            // 2026-10-20: not once 2026-10-20_TransplantApprovalAnyDate.sql is applied (any date; WorkflowCorrections1020E2ETests).
            var approvals = env.Get<ReadyConfirmationRepository>();
            if (!(await env.ScalarAsync<int>(WorkflowCorrectionsSchema.TransplantAnyDateProbeSql) == 1))
            {
            var expectedEarly = string.Format(BedSowingRules.TransplantNotReadyYetMessageFormat, transplantDate.AddDays(days));
            var (earlyOk, earlyMsg, _) = await approvals.ConfirmAsync(sw.Id, 5, null, null, null, "transplant-test", Recorder, Any);
            Assert.False(earlyOk);
            Assert.Equal(expectedEarly, earlyMsg);
            Assert.Equal((false, expectedEarly), BedSowingRules.ValidateApprovalReadyDate(sw.SourceType, sw.ExpectedReadyDate, AppClock.Today));
            var anyReadyStock = await env.ScalarAsync<int>("SELECT TOP 1 Id FROM dbo.ReadyStock ORDER BY Id");
            var forged = await Assert.ThrowsAsync<SqlException>(() => env.ExecAsync(@"
INSERT INTO dbo.ReadyConfirmations (ConfirmationCode, SeedSowingId, ReadyStockId, ActualTrayQuantity, ConfirmedQuantity, WastageQuantity, ApprovedById,
    ConfirmationDate, Status, SupervisorId, CreatedDate, CreatedBy)
VALUES (@C, @S, @R, 5, 120, 0, @U, SYSUTCDATETIME(), 'Confirmed', @U, SYSUTCDATETIME(), N'transplant-test')",
                ("@C", "ZZT-" + Guid.NewGuid().ToString("N")[..10]), ("@S", sw.Id), ("@R", anyReadyStock), ("@U", Recorder)));
            Assert.Contains("transplanted from Ready Stock cannot be approved before its Ready Date", forged.Message);
            }
            Assert.Equal(0, await env.ScalarAsync<int>("SELECT COUNT(*) FROM dbo.ReadyStock WHERE SeedSowingId = @S", ("@S", sw.Id)));
            Assert.Equal(0, await env.ScalarAsync<int>("SELECT COUNT(*) FROM dbo.ReadyConfirmations WHERE SeedSowingId = @S", ("@S", sw.Id)));

            // ON its Ready Date: a second transplant dated Ready Days ago is due today.
            var (dOk, dMsg, dueId, _) = await env.Transplants.CreateAsync(Plant(received, 48, 48, 0, "24 Cavity") with { TransplantDate = AppClock.Today.AddDays(-days) }, Actor, Any);
            Assert.True(dOk, dMsg);
            var due = (await env.Get<SeedSowingRepository>().GetByIdAsync((await env.Transplants.GetAsync(dueId))!.OutputSeedSowingId!.Value))!;
            Assert.Equal(AppClock.Today, due.ExpectedReadyDate);
            Assert.True(BedSowingRules.ValidateApprovalReadyDate(due.SourceType, due.ExpectedReadyDate, AppClock.Today).Ok);   // UI: "Approve"

            // never more than transplanted; approved once -> Ready Stock of exactly those plants
            var (overOk, overMsg, _) = await approvals.ConfirmAsync(due.Id, 3, null, null, null, "transplant-test", Recorder, Any);
            Assert.False(overOk);
            Assert.Contains("made by a transplant", overMsg);
            var (aOk, aMsg, _) = await approvals.ConfirmAsync(due.Id, 2, null, null, null, "transplant-test", Recorder, Any);
            Assert.True(aOk, aMsg);
            Assert.False((await approvals.ConfirmAsync(due.Id, 1, null, null, null, "transplant-test", Recorder, Any)).Success);
            var output = await env.ScalarAsync<int>("SELECT Id FROM dbo.ReadyStock WHERE SeedSowingId = @S", ("@S", due.Id));
            Assert.Equal(1, await env.ScalarAsync<int>("SELECT COUNT(*) FROM dbo.ReadyStock WHERE SeedSowingId = @S", ("@S", due.Id)));
            var o = await env.BatchAsync(output);
            Assert.Equal((48m, 48m), (o.Qty, o.Available));
            await env.AssertLedgerExplainsAsync(output);
            await env.AssertLedgerExplainsAsync(received);

            // lineage: Main Area sowing batch -> received batch -> approved tray batch
            var lineage = await env.Get<ReadyStockRepository>().GetLineageAsync(output);
            Assert.Equal(3, lineage.Count);
            Assert.Equal((MainArea, ReadyStock.OriginSowing), (lineage[0].AreaId, lineage[0].OriginType));
            Assert.Equal(new[] { received, output }, lineage.Skip(1).Select(b => b.Id));

            // approved: that transplanting can no longer be reversed
            var (rOk, rMsg) = await env.Transplants.ReverseAsync(dueId, "too late", Actor, Any);
            Assert.False(rOk);
            Assert.Contains("Supervisor Approval", rMsg);
        }

        [SkippableFact]
        public async Task TrayBatch_ReverseBeforeApproval_ReturnsPlantsAndTrays_CancelsTheBatch()
        {
            var env = await OpenAsync();
            await TrayBatchReadyDaysAsync(env);
            var received = await ReceivedBatchAsync(env);
            await AddMpTraysAsync(env, MpP1, "9 Cavity", 10);
            var trays0 = await env.TraysAsync(MpP1, "9 Cavity");
            var (ok, msg, id, _) = await env.Transplants.CreateAsync(Plant(received, 50, 45, 5), Actor, Any);
            Assert.True(ok, msg);
            var sowing = (await env.Transplants.GetAsync(id))!.OutputSeedSowingId!.Value;
            Assert.Equal(trays0 - 5, await env.TraysAsync(MpP1, "9 Cavity"));

            Assert.False((await env.Transplants.ReverseAsync(id, "", Actor, Any)).Success);            // reason required
            Assert.False((await env.Transplants.ReverseAsync(id, "x", Actor, _ => false)).Success);    // Area access
            var (rOk, rMsg) = await env.Transplants.ReverseAsync(id, "recorded against the wrong batch", Actor, Any);
            Assert.True(rOk, rMsg);
            Assert.Equal(240m, (await env.BatchAsync(received)).Qty);                                    // 45 + 5 returned
            Assert.Equal(trays0, await env.TraysAsync(MpP1, "9 Cavity"));
            Assert.Equal("Cancelled", (await env.Get<SeedSowingRepository>().GetByIdAsync(sowing))!.Status);
            Assert.Equal(TransplantRules.StatusReversed, (await env.Transplants.GetAsync(id))!.Status);
            Assert.False((await env.Transplants.ReverseAsync(id, "again", Actor, Any)).Success);       // replayed
            Assert.False((await env.Get<ReadyConfirmationRepository>().ConfirmAsync(sowing, 1, null, null, null, "transplant-test", Recorder, Any)).Success);
            Assert.Equal(0, await env.ScalarAsync<int>("SELECT COUNT(*) FROM dbo.ReadyStock WHERE SeedSowingId = @S", ("@S", sowing)));
            await env.AssertLedgerExplainsAsync(received);
        }

        [SkippableFact]
        public async Task TrayBatch_Refused_PartialTrays_MissingReadyDays_NotEnoughTrays_OrAccess_AndChangesNothing()
        {
            var env = await OpenAsync();
            var days = await TrayBatchReadyDaysAsync(env);
            var received = await ReceivedBatchAsync(env);
            await AddMpTraysAsync(env, MpP1, "24 Cavity", 10);
            var trays0 = await env.TraysAsync(MpP1, "24 Cavity");
            var sowings0 = await env.ScalarAsync<int>("SELECT COUNT(*) FROM dbo.SeedSowings");
            async Task Refused(TransplantRepository.CreateRequest req, string expect, Func<int, bool>? access = null)
            {
                var (ok, msg, _, _) = await env.Transplants.CreateAsync(req, Actor, access ?? Any);
                Assert.False(ok);
                Assert.Contains(expect, msg);
            }

            // no fabricated seedlings and no tray count that contradicts the quantity: 30 do not fill 24-cavity trays
            await Refused(Plant(received, 30, 30, 0, "24 Cavity"), "6 seedlings do not fill a complete tray and will be recorded as waste: enter the reason");
            await Refused(Plant(received, 24, 24, 0, "24 Cavity"), "not allowed", _ => false);          // forged Area access
            try
            {
                await env.ExecAsync("UPDATE dbo.PlantSpecies SET ReadyStockDays = NULL WHERE Id = @S", ("@S", env.Species));
                await Refused(Plant(received, 24, 24, 0, "24 Cavity"), "Ready Days are not configured");
            }
            finally { await env.ExecAsync("UPDATE dbo.PlantSpecies SET ReadyStockDays = @D WHERE Id = @S", ("@D", days), ("@S", env.Species)); }

            var (area, polyhouse) = await FreshMotherPlantPolyhouseAsync(env);
            try
            {
                await AddTraysAtAsync(env, area, polyhouse, "24 Cavity", 1, AppClock.Today.AddDays(-1));
                await Refused(Plant(received, 48, 48, 0, "24 Cavity", polyhouse) with { TargetAreaId = area }, "Not enough empty 24 Cavity trays");
                Assert.Equal(1m, await PoolAsync(env, area, polyhouse, "24 Cavity"));
            }
            finally { await RetireAsync(env, area); }

            Assert.Equal(240m, (await env.BatchAsync(received)).Qty);
            Assert.Equal(trays0, await env.TraysAsync(MpP1, "24 Cavity"));
            Assert.Equal(sowings0, await env.ScalarAsync<int>("SELECT COUNT(*) FROM dbo.SeedSowings"));
            Assert.Equal(0, await env.ScalarAsync<int>("SELECT COUNT(*) FROM dbo.ReadyStockTransplants WHERE SourceReadyStockId = @S", ("@S", received)));
        }

        // The Ready Date gate covers only tray batches made by a transplant: an ordinary Direct Seed Sowing
        // whose estimated Ready Date is still ahead is approved exactly as before (server and database).
        [SkippableFact]
        public async Task ReadyDateGate_DoesNotChangeDirectSeedSowingApproval()
        {
            var env = await OpenAsync();
            Skip.IfNot(await TrayBatchPathAsync(env), "2026-10-13_TraysToSowingApprovals.sql is not applied to this scratch database.");
            var (tOk, tMsg, _, _) = await env.Get<TrayStockRepository>().AddStockAsync(MainArea, MainPolyhouse, Cavity, 5, DateTime.UtcNow,
                Random.Shared.Next(1, int.MaxValue), SupervisorId, "transplant-test", "ready date gate test", Any);
            Assert.True(tOk, tMsg);
            var (mOk, mMsg, _) = await env.Get<MaterialStockRepository>().AdjustAsync(MainArea, MainPolyhouse, env.Cocopeat, 1, 50m, "ready date gate test", Guid.NewGuid(), Recorder, Any);
            Assert.True(mOk, mMsg);
            var sowing = new SeedSowing
            {
                SourceSeedStockId = env.SeedStockId, SeedQuantity = 48, CavityType = Cavity, SowingDate = AppClock.Today,
                PolyhouseId = MainPolyhouse, SupervisorId = SupervisorId, CreatedBy = "transplant-test", CreatedById = Recorder, GrowingMaterialTypeId = env.Cocopeat
            };
            var (ok, msg, sowingId) = await env.Get<SeedSowingRepository>().InsertAsync(sowing, Recorder, Any);
            Assert.True(ok, msg);
            var sw = (await env.Get<SeedSowingRepository>().GetByIdAsync(sowingId))!;
            Assert.Equal(SeedSowing.SourceSeed, sw.SourceType);
            Assert.True(sw.ExpectedReadyDate is null || sw.ExpectedReadyDate > AppClock.Today);   // not "ready" yet by its estimate
            Assert.True(BedSowingRules.ValidateApprovalReadyDate(sw.SourceType, sw.ExpectedReadyDate, AppClock.Today).Ok);
            var (cOk, cMsg, _) = await env.Get<ReadyConfirmationRepository>().ConfirmAsync(sowingId, 2, null, null, null, "transplant-test", SupervisorId, Any);
            Assert.True(cOk, cMsg);
            Assert.Equal(1, await env.ScalarAsync<int>("SELECT COUNT(*) FROM dbo.ReadyStock WHERE SeedSowingId = @S", ("@S", sowingId)));
        }
    }
}
