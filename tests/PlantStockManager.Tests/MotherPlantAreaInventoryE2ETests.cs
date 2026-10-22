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
    // 2026-10-14, end-to-end against a SCRATCH COPY (PlantsIMS2_Scratch_* only; SKIPPED without PSM_SCRATCH_CONNECTION):
    //   * Add Empty Pots to a Mother Plant / Cutting Area (no migration needed);
    //   * Add Empty Trays to a Mother Plant Area's own tray pool, a Ready Stock transfer to a Mother Plant AREA with
    //     its purpose recorded, and Transplant into Trays without a Polyhouse -- these need
    //     2026-10-14_MotherPlantAreaTraysAndTransferPurpose.sql on the copy and are SKIPPED without it.
    // Every test makes its own test variety (with or without Ready Days) and its own Main Area sowing.
    [Collection("ScratchDb")]
    public class MotherPlantAreaInventoryE2ETests
    {
        private const string ConnectionVariable = "PSM_SCRATCH_CONNECTION";
        private const string RequiredPrefix = "PlantsIMS2_Scratch_";
        private const int MainArea = 2, MainPolyhouse = 4;              // Main-Office-Areas / Facility-4 (as ReadyStockTransplantingE2ETests)
        private const int MpArea = 127, MpP1 = 14;                       // Green Bless Nursery (Mother Plant) / P-1
        private const int SupervisorId = 9, Recorder = 21;
        private const int ReadyDays = 30;
        private static readonly MotherPlantTransferRepository.Actor Actor = new(Recorder, "mp-area-test");
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
            public bool Migrated;
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

            // the Area tray pool of a Mother Plant Area (PolyhouseId NULL)
            public Task<decimal> AreaTraysAsync(int area, string cavity)
                => ScalarAsync<decimal>("SELECT ISNULL(SUM(PhysicalQuantity), 0) FROM dbo.TrayStock WHERE AreaId = @A AND PolyhouseId IS NULL AND TraySize = @C AND IsActive = 1",
                    ("@A", area), ("@C", cavity));

            public Task<decimal> PotsAsync(int area, string size)
                => ScalarAsync<decimal>("SELECT ISNULL(SUM(PhysicalQuantity), 0) FROM dbo.EmptyPotInventory WHERE AreaId = @A AND PotSize = @S", ("@A", area), ("@S", size));
        }

        private static async Task<Env> OpenAsync(bool needsMigration)
        {
            var cs = Environment.GetEnvironmentVariable(ConnectionVariable);
            Skip.If(string.IsNullOrWhiteSpace(cs), $"Set {ConnectionVariable} to a {RequiredPrefix}* database connection string to run these tests.");
            bool migrated;
            using (var conn = new SqlConnection(cs))
            {
                await conn.OpenAsync();
                using var cmd = new SqlCommand("SELECT DB_NAME()", conn);
                var name = (string)(await cmd.ExecuteScalarAsync())!;
                if (!name.StartsWith(RequiredPrefix, StringComparison.Ordinal))
                    throw new InvalidOperationException($"Refusing to run these tests against '{name}'. Only databases named {RequiredPrefix}* are allowed.");
                using var probe = new SqlCommand(MotherPlantAreaSchema.ProbeSql, conn);
                migrated = (int)(await probe.ExecuteScalarAsync())! == 1;
                using var traysProbe = new SqlCommand(BedSowingSchema.TraysProbeSql, conn);
                Skip.If(needsMigration && (int)(await traysProbe.ExecuteScalarAsync())! != 1, "2026-10-13_TraysToSowingApprovals.sql is not applied to this scratch database.");
            }
            Skip.If(needsMigration && !migrated, "2026-10-14_MotherPlantAreaTraysAndTransferPurpose.sql is not applied to this scratch database.");

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
            var env = new Env { ConnectionString = cs!, Provider = services.BuildServiceProvider(), Migrated = migrated };
            env.Cocopeat = await env.ScalarAsync<int>("SELECT Id FROM dbo.MaterialTypes WHERE Code = 'COCOPEAT'");
            return env;
        }

        private sealed record Variety(int SpeciesId, int SeedStockId);

        private static async Task<Variety> NewVarietyAsync(Env env, int? readyDays = ReadyDays)
        {
            var name = "ZZTEST MpArea " + Guid.NewGuid().ToString("N")[..10];
            var speciesId = await env.ScalarAsync<int>(@"INSERT INTO dbo.PlantSpecies (PlantTypeId, Name, ReadyStockDays)
VALUES ((SELECT MIN(Id) FROM dbo.PlantTypes), @N, @D); SELECT CAST(SCOPE_IDENTITY() AS INT)", ("@N", name), ("@D", readyDays));
            var lot = await env.ScalarAsync<int>(@"INSERT INTO dbo.SeedStock (SpeciesId, AreaId, BatchNo, Unit, PhysicalQuantity, CreatedBy)
VALUES (@S, @A, @B, N'Seeds', 100000, N'mp-area-test'); SELECT CAST(SCOPE_IDENTITY() AS INT)", ("@S", speciesId), ("@A", MainArea), ("@B", "LOT-" + name[^10..]));
            return new Variety(speciesId, lot);
        }

        // A Main Area sowing of `plants` 24 Cavity seedlings approved in full -> its Ready Stock batch id.
        private static async Task<int> ApprovedMainBatchAsync(Env env, Variety v, int plants = 480)
        {
            const string cavity = "24 Cavity";
            var (tOk, tMsg, _, _) = await env.Get<TrayStockRepository>().AddStockAsync(MainArea, MainPolyhouse, cavity, plants / 24 + 5, DateTime.UtcNow,
                Random.Shared.Next(1, int.MaxValue), SupervisorId, "mp-area-test", "mp area test setup", Any);
            Assert.True(tOk, tMsg);
            var (mOk, mMsg, _) = await env.Get<MaterialStockRepository>().AdjustAsync(MainArea, MainPolyhouse, env.Cocopeat, 1, 500m, "mp area test setup", Guid.NewGuid(), Recorder, Any);
            Assert.True(mOk, mMsg);
            var sowing = new SeedSowing
            {
                SourceSeedStockId = v.SeedStockId, SeedQuantity = plants, CavityType = cavity, SowingDate = AppClock.Today.AddDays(-40),
                PolyhouseId = MainPolyhouse, SupervisorId = SupervisorId, CreatedBy = "mp-area-test", CreatedById = Recorder, GrowingMaterialTypeId = env.Cocopeat
            };
            var (ok, msg, sowingId) = await env.Get<SeedSowingRepository>().InsertAsync(sowing, Recorder, Any);
            Assert.True(ok, msg);
            var (cOk, cMsg, _) = await env.Get<ReadyConfirmationRepository>().ConfirmAsync(sowingId, plants / 24, null, null, null, "mp-area-test", SupervisorId, Any);
            Assert.True(cOk, cMsg);
            return await env.ScalarAsync<int>("SELECT Id FROM dbo.ReadyStock WHERE SeedSowingId = @S AND OriginType = N'Sowing'", ("@S", sowingId));
        }

        private static MotherPlantTransferRepository.CreateRequest Send(int batch, decimal qty, string? purpose, Guid? token = null, int? polyhouse = null)
            => new(batch, qty, MpArea, polyhouse, AppClock.Today, "mp area test", token ?? Guid.NewGuid(), purpose);

        private static async Task<(int TransferId, int Received)> SentAndReceivedAsync(Env env, Variety v, string purpose, decimal plants = 240)
        {
            var main = await ApprovedMainBatchAsync(env, v);
            var (ok, msg, transferId, _) = await env.Transfers.CreateAsync(Send(main, plants, purpose), Actor, Any);
            Assert.True(ok, msg);
            var (rOk, rMsg, received) = await env.Transfers.ReceiveAsync(transferId, plants, null, Actor, Any);
            Assert.True(rOk, rMsg);
            return (transferId, received!.Value);
        }

        private static async Task AddAreaTraysAsync(Env env, string cavity, decimal trays, int area = MpArea)
        {
            var (ok, msg, _, _) = await env.Get<TrayStockRepository>().AddStockAsync(area, null, cavity, trays, DateTime.UtcNow.AddHours(-1),
                Random.Shared.Next(1, int.MaxValue), SupervisorId, "mp-area-test", "mp area test trays", Any);
            Assert.True(ok, msg);
        }

        private static TransplantRepository.CreateRequest Plant(int source, decimal transplanted, string cavity, Guid? token = null)
            => new(source, MpArea, null, cavity, transplanted, transplanted, 0, AppClock.Today, "mp area test transplant", token ?? Guid.NewGuid());

        // ===== Add Empty Pots (no migration needed) ===========================================================

        [SkippableFact]
        public async Task EmptyPots_AddedToAMotherPlantAndACuttingArea_StayInTheirArea_OtherAreasAndBadQuantitiesRefused()
        {
            var env = await OpenAsync(needsMigration: false);
            var cuttingArea = await env.ScalarAsync<int>(@"SELECT TOP 1 Id FROM dbo.Area WHERE AreaType = N'MotherPlant' AND IsActive = 1 AND Id <> @A
ORDER BY CASE WHEN Name LIKE N'%Cutting%' THEN 0 ELSE 1 END, Id", ("@A", MpArea));
            var outlet = await env.ScalarAsync<int>("SELECT TOP 1 Id FROM dbo.Area WHERE AreaType = N'Outlet' ORDER BY Id");
            var size = await env.ScalarAsync<string>("SELECT TOP 1 Name FROM dbo.PotSizes WHERE IsActive = 1 ORDER BY Id");
            Skip.If(cuttingArea == 0 || size == null, "No second Mother Plant Area or no pot size in this copy.");
            var repo = env.Get<EmptyPotInventoryRepository>();
            var mp0 = await env.PotsAsync(MpArea, size!);
            var cut0 = await env.PotsAsync(cuttingArea, size!);

            var (ok, msg, pool) = await repo.AddAreaStockAsync(MpArea, size, 7, AppClock.Today, "mp area test pots", Recorder, "mp-area-test", EmptyPotStockRules.MotherPlantAreaType);
            Assert.True(ok, msg);
            Assert.Equal(mp0 + 7, await env.PotsAsync(MpArea, size!));
            Assert.Equal(cut0, await env.PotsAsync(cuttingArea, size!));                         // never another Area
            Assert.Equal(MpArea, await env.ScalarAsync<int>("SELECT AreaId FROM dbo.EmptyPotInventory WHERE Id = @P", ("@P", pool)));
            Assert.Equal(1, await env.ScalarAsync<int>(@"SELECT COUNT(*) FROM dbo.EmptyPotInventoryTransactions
WHERE EmptyPotInventoryId = @P AND TransactionType = N'StockIn' AND ReferenceType = N'AreaStockEntry' AND Quantity = 7 AND Remarks = N'mp area test pots'", ("@P", pool)));

            var (cOk, cMsg, _) = await repo.AddAreaStockAsync(cuttingArea, size, 5, AppClock.Today, "mp area test pots (cutting)", Recorder, "mp-area-test", EmptyPotStockRules.MotherPlantAreaType);
            Assert.True(cOk, cMsg);
            Assert.Equal(cut0 + 5, await env.PotsAsync(cuttingArea, size!));
            Assert.Equal(mp0 + 7, await env.PotsAsync(MpArea, size!));

            // refused, nothing changes: the Main Office, an Outlet (this page stocks Mother Plant / Cutting Areas only), bad quantities
            var main0 = await env.PotsAsync(MainArea, size!);
            Assert.False((await repo.AddAreaStockAsync(MainArea, size, 5, AppClock.Today, null, Recorder, "mp-area-test", EmptyPotStockRules.MotherPlantAreaType)).Success);
            if (outlet > 0)
            {
                var outlet0 = await env.PotsAsync(outlet, size!);
                var (oOk, oMsg, _) = await repo.AddAreaStockAsync(outlet, size, 5, AppClock.Today, null, Recorder, "mp-area-test", EmptyPotStockRules.MotherPlantAreaType);
                Assert.False(oOk);
                Assert.Equal(EmptyPotStockRules.MotherPlantAreaOnlyMessage, oMsg);
                Assert.Equal(outlet0, await env.PotsAsync(outlet, size!));
            }
            foreach (var bad in new[] { 0m, -3m, 2.5m })
                Assert.False((await repo.AddAreaStockAsync(MpArea, size, bad, AppClock.Today, null, Recorder, "mp-area-test", EmptyPotStockRules.MotherPlantAreaType)).Success);
            Assert.False((await repo.AddAreaStockAsync(MpArea, size, 5, AppClock.Today.AddDays(1), null, Recorder, "mp-area-test", EmptyPotStockRules.MotherPlantAreaType)).Success);
            Assert.Equal(mp0 + 7, await env.PotsAsync(MpArea, size!));
            Assert.Equal(main0, await env.PotsAsync(MainArea, size!));
        }

        // ===== Add Empty Trays: the Mother Plant Area's own pool ==================================================

        [SkippableFact]
        public async Task EmptyTrays_AreaPool_NoPolyhouse_StayInTheirArea_DuplicateOnce_BadInputRefused()
        {
            var env = await OpenAsync(needsMigration: true);
            var otherMp = await env.ScalarAsync<int>("SELECT TOP 1 Id FROM dbo.Area WHERE AreaType = N'MotherPlant' AND IsActive = 1 AND Id <> @A ORDER BY Id", ("@A", MpArea));
            var repo = env.Get<TrayStockRepository>();
            const string cavity = "42 Cavity";
            var mp0 = await env.AreaTraysAsync(MpArea, cavity);
            var other0 = await env.AreaTraysAsync(otherMp, cavity);

            var token = Random.Shared.Next(1, int.MaxValue);
            var (ok, msg, dup, balance) = await repo.AddStockAsync(MpArea, null, cavity, 12, DateTime.UtcNow, token, SupervisorId, "mp-area-test", "mp area test", Any);
            Assert.True(ok, msg);
            Assert.False(dup);
            Assert.Equal(mp0 + 12, balance);                                                    // the balance shown after adding
            Assert.Equal(mp0 + 12, await env.AreaTraysAsync(MpArea, cavity));
            Assert.Equal(other0, await env.AreaTraysAsync(otherMp, cavity));
            // the same form again: nothing added twice
            var again = await repo.AddStockAsync(MpArea, null, cavity, 12, DateTime.UtcNow, token, SupervisorId, "mp-area-test", "mp area test", Any);
            Assert.True(again.Success);
            Assert.True(again.Duplicate);
            Assert.Equal(mp0 + 12, await env.AreaTraysAsync(MpArea, cavity));
            // a Polyhouse named for a Mother Plant Area still lands in the Area pool -- never a Polyhouse pool there
            Assert.True((await repo.AddStockAsync(MpArea, MpP1, cavity, 3, DateTime.UtcNow, Random.Shared.Next(1, int.MaxValue), SupervisorId, "mp-area-test", null, Any)).Success);
            Assert.Equal(mp0 + 15, await env.AreaTraysAsync(MpArea, cavity));
            Assert.Equal(0, await env.ScalarAsync<int>(@"SELECT COUNT(*) FROM dbo.TrayStock t JOIN dbo.Area a ON a.Id = t.AreaId
WHERE a.AreaType = N'MotherPlant' AND t.PolyhouseId IS NOT NULL AND t.IsActive = 1"));

            // refused, nothing changes: no access to the Area, bad quantities, a Main Office Area without a Polyhouse
            Assert.False((await repo.AddStockAsync(MpArea, null, cavity, 5, DateTime.UtcNow, Random.Shared.Next(1, int.MaxValue), SupervisorId, "x", null, _ => false)).Success);
            foreach (var bad in new[] { 0m, -2m, 1.5m })
                Assert.False((await repo.AddStockAsync(MpArea, null, cavity, bad, DateTime.UtcNow, Random.Shared.Next(1, int.MaxValue), SupervisorId, "x", null, Any)).Success);
            Assert.False((await repo.AddStockAsync(MpArea, null, "25 Cavity", 5, DateTime.UtcNow, Random.Shared.Next(1, int.MaxValue), SupervisorId, "x", null, Any)).Success);
            var (mOk, mMsg, _, _) = await repo.AddStockAsync(MainArea, null, cavity, 5, DateTime.UtcNow, Random.Shared.Next(1, int.MaxValue), SupervisorId, "x", null, Any);
            Assert.False(mOk);
            Assert.Equal("Polyhouse is required.", mMsg);
            Assert.Equal(mp0 + 15, await env.AreaTraysAsync(MpArea, cavity));
        }

        // ===== Ready Stock -> Mother Plant Area: purpose, no Polyhouse ============================================

        [SkippableFact]
        public async Task Transfer_BothPurposesPersisted_NoPolyhouse_PotProductionIsNeverTransplanted()
        {
            var env = await OpenAsync(needsMigration: true);
            var v = await NewVarietyAsync(env);
            await AddAreaTraysAsync(env, "9 Cavity", 40);

            // no purpose -> refused, nothing held
            var main = await ApprovedMainBatchAsync(env, v);
            var (nOk, nMsg, _, _) = await env.Transfers.CreateAsync(Send(main, 240, null), Actor, Any);
            Assert.False(nOk);
            Assert.Contains("purpose", nMsg, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(0m, await env.ScalarAsync<decimal>("SELECT InTransitQuantity FROM dbo.ReadyStock WHERE Id = @R", ("@R", main)));

            // Pot Production: saved with its purpose, no Polyhouse, received into the Area
            var (potTransfer, potBatch) = await SentAndReceivedAsync(env, v, MotherPlantTransferRules.PurposePotProduction);
            var pot = (await env.Transfers.GetAsync(potTransfer))!;
            Assert.Equal((MotherPlantTransferRules.PurposePotProduction, (int?)null, MotherPlantTransferRules.StatusCompleted),
                (pot.TransferPurpose, pot.DestinationPolyhouseId, pot.Status));
            Assert.False(pot.ForTrayTransplant);
            Assert.Null(await env.ScalarAsync<int?>("SELECT PolyhouseId FROM dbo.ReadyStock WHERE Id = @R", ("@R", potBatch)));
            Assert.Equal((MpArea, 240m), (await env.ScalarAsync<int>("SELECT AreaId FROM dbo.ReadyStock WHERE Id = @R", ("@R", potBatch)),
                                          await env.ScalarAsync<decimal>("SELECT Quantity FROM dbo.ReadyStock WHERE Id = @R", ("@R", potBatch))));
            Assert.Contains(potBatch, await env.Transfers.PotProductionBatchIdsAsync());

            // ... and it never enters the tray-transplant workflow: refused, nothing moves
            var trays0 = await env.AreaTraysAsync(MpArea, "9 Cavity");
            var batches0 = await env.ScalarAsync<int>("SELECT COUNT(*) FROM dbo.SeedSowings");
            var (tOk, tMsg, _, _) = await env.Transplants.CreateAsync(Plant(potBatch, 216, "9 Cavity"), Actor, Any);
            Assert.False(tOk);
            Assert.Equal(MotherPlantTransferRules.PotProductionNotTransplantedMessage, tMsg);
            Assert.Equal(240m, await env.ScalarAsync<decimal>("SELECT Quantity FROM dbo.ReadyStock WHERE Id = @R", ("@R", potBatch)));
            Assert.Equal(trays0, await env.AreaTraysAsync(MpArea, "9 Cavity"));
            Assert.Equal(batches0, await env.ScalarAsync<int>("SELECT COUNT(*) FROM dbo.SeedSowings"));
            Assert.Equal(0, await env.ScalarAsync<int>("SELECT COUNT(*) FROM dbo.ReadyStockTransplants WHERE SourceReadyStockId = @R", ("@R", potBatch)));

            // Transplant purpose: saved too, and offered for transplanting
            var (trTransfer, trBatch) = await SentAndReceivedAsync(env, v, MotherPlantTransferRules.PurposeTrayTransplant);
            var tr = (await env.Transfers.GetAsync(trTransfer))!;
            Assert.Equal((MotherPlantTransferRules.PurposeTrayTransplant, (int?)null), (tr.TransferPurpose, tr.DestinationPolyhouseId));
            Assert.True(tr.ForTrayTransplant);
            Assert.DoesNotContain(trBatch, await env.Transfers.PotProductionBatchIdsAsync());

            // the database keeps a purpose once recorded, and wants one on every new Mother Plant transfer
            var ex = await Assert.ThrowsAsync<SqlException>(() => env.ExecAsync("UPDATE dbo.InternalTransfers SET TransferPurpose = N'TrayTransplant' WHERE Id = @T", ("@T", potTransfer)));
            Assert.Contains("cannot be changed", ex.Message);
            Assert.Equal(MotherPlantTransferRules.PurposePotProduction, await env.ScalarAsync<string>("SELECT TransferPurpose FROM dbo.InternalTransfers WHERE Id = @T", ("@T", potTransfer)));
            await Assert.ThrowsAsync<SqlException>(() => env.ExecAsync("UPDATE dbo.InternalTransfers SET TransferPurpose = N'Sale' WHERE Id = @T", ("@T", trTransfer)));
        }

        // A Pot Production batch cannot reach the tray-transplant workflow through another door: the generic Ready Stock
        // Move refuses it (only sowing batches move), and anything ever derived from it is refused by its lineage.
        [SkippableFact]
        public async Task PotProduction_CannotBeMovedOrDerivedIntoTheTransplantWorkflow()
        {
            var env = await OpenAsync(needsMigration: true);
            var v = await NewVarietyAsync(env);
            await AddAreaTraysAsync(env, "24 Cavity", 20);
            var (_, potBatch) = await SentAndReceivedAsync(env, v, MotherPlantTransferRules.PurposePotProduction);
            var otherMp = await env.ScalarAsync<int>("SELECT TOP 1 Id FROM dbo.Area WHERE AreaType = N'MotherPlant' AND IsActive = 1 AND Id <> @A ORDER BY Id", ("@A", MpArea));

            // 1) generic Move (InternalTransfers 'ReadyStock') to another Area: refused, the batch stays where it is
            foreach (var dest in new[] { otherMp, MainArea })
            {
                var (mOk, mMsg, _) = await env.Get<InternalTransferRepository>().InsertAsync(new InternalTransfer
                {
                    StockType = "ReadyStock", SourceReadyStockId = potBatch, SourceAreaId = MpArea, DestinationAreaId = dest,
                    Quantity = 240, Status = "Completed", CreatedBy = "mp-area-test", Remarks = "mp area test move"
                }, Recorder);
                Assert.False(mOk);
                Assert.Contains("Only a sowing batch can be moved", mMsg);
            }
            Assert.Equal(MpArea, await env.ScalarAsync<int>("SELECT AreaId FROM dbo.ReadyStock WHERE Id = @R", ("@R", potBatch)));

            // 2) a batch derived from it (any future derivation path) is still refused for Transplant into Trays, server-side
            int child;
            using (var conn = new SqlConnection(env.ConnectionString))
            {
                await conn.OpenAsync();
                using var tx = conn.BeginTransaction();
                child = await env.Get<ReadyStockRepository>().CreateDerivedBatchAsync(conn, tx, potBatch, ReadyStock.OriginTransplant, potBatch, MpArea, null, "24 Cavity", "mp-area-test");
                tx.Commit();
            }
            Assert.Contains(child, await env.Transfers.PotProductionBatchIdsAsync());
            var trays0 = await env.AreaTraysAsync(MpArea, "24 Cavity");
            var (tOk, tMsg, _, _) = await env.Transplants.CreateAsync(Plant(child, 24, "24 Cavity"), Actor, Any);
            Assert.False(tOk);
            Assert.Equal(MotherPlantTransferRules.PotProductionNotTransplantedMessage, tMsg);
            Assert.Equal(trays0, await env.AreaTraysAsync(MpArea, "24 Cavity"));
            using (var conn = new SqlConnection(env.ConnectionString))
            {
                await conn.OpenAsync();
                Assert.Equal(MotherPlantTransferRules.PurposePotProduction, await MotherPlantTransferRepository.ReceivedBatchPurposeAsync(conn, null, child));
            }
        }

        [SkippableFact]
        public async Task TransplantPurpose_Receive_TransplantIntoAreaTrays_TrayBatchInSowingApprovals_ReadyDateFromTheTransplantDate()
        {
            var env = await OpenAsync(needsMigration: true);
            var v = await NewVarietyAsync(env);
            await AddAreaTraysAsync(env, "9 Cavity", 30);
            var (_, received) = await SentAndReceivedAsync(env, v, MotherPlantTransferRules.PurposeTrayTransplant);
            var trays0 = await env.AreaTraysAsync(MpArea, "9 Cavity");
            var readyStockRows = await env.ScalarAsync<int>("SELECT COUNT(*) FROM dbo.ReadyStock");

            // 216 of the 240 received plants into 24 x 9 Cavity trays (a different cavity), no Polyhouse
            var token = Guid.NewGuid();
            var (ok, msg, id, dup) = await env.Transplants.CreateAsync(Plant(received, 216, "9 Cavity", token), Actor, Any);
            Assert.True(ok, msg);
            Assert.False(dup);
            Assert.Contains($"Ready Date {AppClock.Today.AddDays(ReadyDays):dd-MM-yyyy}", msg);

            var t = (await env.Transplants.GetAsync(id))!;
            Assert.Null(t.PolyhouseId);
            Assert.Equal((MpArea, 24, 216m), (t.AreaId, t.OutputTrays, t.TransplantedQuantity));
            Assert.NotNull(t.OutputSeedSowingId);
            var sw = (await env.Get<SeedSowingRepository>().GetByIdAsync(t.OutputSeedSowingId!.Value))!;
            Assert.Equal((SeedSowing.SourceTransplant, "Sown", MpArea, (int?)null, "9 Cavity", (int?)24, 216m),
                (sw.SourceType, sw.Status, sw.AreaId, sw.PolyhouseId, sw.CavityType, sw.NumberOfTrays, sw.QuantitySown));
            Assert.Equal(AppClock.Today.AddDays(ReadyDays), sw.ExpectedReadyDate);            // actual transplant date + Ready Days
            Assert.Equal(ReadyDays, sw.ReadyStockDays);
            Assert.Contains(await env.Get<SeedSowingRepository>().GetReadyForConfirmationAsync(), s => s.Id == sw.Id);   // waits in Sowing Approvals
            Assert.Equal(readyStockRows, await env.ScalarAsync<int>("SELECT COUNT(*) FROM dbo.ReadyStock"));            // no Ready Stock before approval
            Assert.Equal(trays0 - 24, await env.AreaTraysAsync(MpArea, "9 Cavity"));
            Assert.Equal(24m, await env.ScalarAsync<decimal>("SELECT Quantity FROM dbo.ReadyStock WHERE Id = @R", ("@R", received)));

            // the approval gate: not before its Ready Date. 2026-10-20: with 2026-10-20_TransplantApprovalAnyDate.sql applied the
            // date no longer gates it, but this user is not an Area Supervisor of the batch's Area -- still refused, nothing posted.
            var (aOk, aMsg, _) = await env.Get<ReadyConfirmationRepository>().ConfirmAsync(sw.Id, 24, null, null, null, "mp-area-test", SupervisorId, Any);
            Assert.False(aOk);
            if (await env.ScalarAsync<int>(WorkflowCorrectionsSchema.TransplantAnyDateProbeSql) == 1)
                Assert.Contains("Area Supervisor of", aMsg);
            else
                Assert.Contains("not ready yet", aMsg, StringComparison.OrdinalIgnoreCase);

            // the same form again: recognised, nothing twice
            var (ok2, _, id2, dup2) = await env.Transplants.CreateAsync(Plant(received, 216, "9 Cavity", token), Actor, Any);
            Assert.True(ok2);
            Assert.True(dup2);
            Assert.Equal(id, id2);
            Assert.Equal(trays0 - 24, await env.AreaTraysAsync(MpArea, "9 Cavity"));

            // reversal gives the trays back to the Area pool and the plants to the received batch
            var (rOk, rMsg) = await env.Transplants.ReverseAsync(id, "mp area test reversal", Actor, Any);
            Assert.True(rOk, rMsg);
            Assert.Equal(trays0, await env.AreaTraysAsync(MpArea, "9 Cavity"));
            Assert.Equal(240m, await env.ScalarAsync<decimal>("SELECT Quantity FROM dbo.ReadyStock WHERE Id = @R", ("@R", received)));
            Assert.Equal("Cancelled", await env.ScalarAsync<string>("SELECT Status FROM dbo.SeedSowings WHERE Id = @S", ("@S", sw.Id)));
        }

        [SkippableFact]
        public async Task Transplant_NotEnoughAreaTrays_NoAccess_NoReadyDays_NotACompleteTray_ChangeNothing()
        {
            var env = await OpenAsync(needsMigration: true);
            var v = await NewVarietyAsync(env);
            var (_, received) = await SentAndReceivedAsync(env, v, MotherPlantTransferRules.PurposeTrayTransplant);
            const string cavity = "102 Cavity";
            var trays0 = await env.AreaTraysAsync(MpArea, cavity);
            var batches0 = await env.ScalarAsync<int>("SELECT COUNT(*) FROM dbo.SeedSowings");

            async Task Refused(TransplantRepository.CreateRequest req, string expected, Func<int, bool>? access = null)
            {
                var (ok, msg, _, _) = await env.Transplants.CreateAsync(req, Actor, access ?? Any);
                Assert.False(ok);
                Assert.Contains(expected, msg, StringComparison.OrdinalIgnoreCase);
                Assert.Equal(240m, await env.ScalarAsync<decimal>("SELECT Quantity FROM dbo.ReadyStock WHERE Id = @R", ("@R", received)));
                Assert.Equal(trays0, await env.AreaTraysAsync(MpArea, cavity));
                Assert.Equal(batches0, await env.ScalarAsync<int>("SELECT COUNT(*) FROM dbo.SeedSowings"));
                Assert.Equal(0, await env.ScalarAsync<int>("SELECT COUNT(*) FROM dbo.ReadyStockTransplants WHERE SourceReadyStockId = @R", ("@R", received)));
            }

            // 204 plants = 2 x 102 Cavity trays: more than the Area pool holds -> refused, rolled back
            if (trays0 < 2)
                await Refused(Plant(received, 204, cavity), "Not enough empty");
            await Refused(Plant(received, 204, cavity), "not allowed", _ => false);                        // no access to the Area
            await Refused(Plant(received, 150, cavity), "complete");                                        // not complete trays

            // a variety without Ready Days: refused, nothing moves (sown while it had them -- a sowing needs them --
            // and cleared before transplanting)
            var noDays = await NewVarietyAsync(env);
            var (_, receivedNoDays) = await SentAndReceivedAsync(env, noDays, MotherPlantTransferRules.PurposeTrayTransplant);
            await env.ExecAsync("UPDATE dbo.PlantSpecies SET ReadyStockDays = NULL WHERE Id = @S", ("@S", noDays.SpeciesId));
            await AddAreaTraysAsync(env, "24 Cavity", 15);
            var trays24 = await env.AreaTraysAsync(MpArea, "24 Cavity");
            var (dOk, dMsg, _, _) = await env.Transplants.CreateAsync(Plant(receivedNoDays, 240, "24 Cavity"), Actor, Any);
            Assert.False(dOk);
            Assert.Contains("Ready Days", dMsg);
            Assert.Equal(trays24, await env.AreaTraysAsync(MpArea, "24 Cavity"));
            Assert.Equal(240m, await env.ScalarAsync<decimal>("SELECT Quantity FROM dbo.ReadyStock WHERE Id = @R", ("@R", receivedNoDays)));
        }

        [SkippableFact]
        public async Task Transfer_SameFormTwice_OneTransferOneHold_AndThePoolLocationRuleIsInTheDatabase()
        {
            var env = await OpenAsync(needsMigration: true);
            var v = await NewVarietyAsync(env);
            var main = await ApprovedMainBatchAsync(env, v);
            var token = Guid.NewGuid();
            var first = await env.Transfers.CreateAsync(Send(main, 240, MotherPlantTransferRules.PurposePotProduction, token), Actor, Any);
            var second = await env.Transfers.CreateAsync(Send(main, 240, MotherPlantTransferRules.PurposeTrayTransplant, token), Actor, Any);
            Assert.True(first.Success, first.Message);
            Assert.True(second.Duplicate);
            Assert.Equal(first.Id, second.Id);
            Assert.Equal(240m, await env.ScalarAsync<decimal>("SELECT InTransitQuantity FROM dbo.ReadyStock WHERE Id = @R", ("@R", main)));
            Assert.Equal(MotherPlantTransferRules.PurposePotProduction, (await env.Transfers.GetAsync(first.Id))!.TransferPurpose);   // the first form's purpose

            // TR_TrayStock_PoolLocation: no active Area pool outside a Mother Plant Area, no active Polyhouse pool inside one
            var e1 = await Assert.ThrowsAsync<SqlException>(() => env.ExecAsync(
                "INSERT INTO dbo.TrayStock (AreaId, PolyhouseId, TraySize, PhysicalQuantity, IsActive, CreatedDate, CreatedBy) VALUES (@A, NULL, N'9 Cavity', 0, 1, SYSUTCDATETIME(), N'x')",
                ("@A", MainArea)));
            Assert.Contains("per Polyhouse", e1.Message);
            var freshPolyhouse = await env.ScalarAsync<int>("INSERT INTO dbo.Polyhouses (Name, AreaId) VALUES (@N, @A); SELECT CAST(SCOPE_IDENTITY() AS INT)",
                ("@N", "ZZTEST MP P " + Guid.NewGuid().ToString("N")[..8]), ("@A", MpArea));
            var e2 = await Assert.ThrowsAsync<SqlException>(() => env.ExecAsync(
                "INSERT INTO dbo.TrayStock (AreaId, PolyhouseId, TraySize, PhysicalQuantity, IsActive, CreatedDate, CreatedBy) VALUES (@A, @P, N'9 Cavity', 0, 1, SYSUTCDATETIME(), N'x')",
                ("@A", MpArea), ("@P", freshPolyhouse)));
            Assert.Contains("per Area", e2.Message);
            Assert.Equal(0, await env.ScalarAsync<int>("SELECT COUNT(*) FROM dbo.TrayStock WHERE PolyhouseId = @P", ("@P", freshPolyhouse)));
        }
        // ===== Pot Production from received Ready Stock (2026-10-15_PotProductionFromReadyStock.sql) ===========

        private sealed record PotFixture(Env Env, Variety V, int Transfer, int Received, int Confirmer, string Size);

        // A Pot Production transfer received in the Mother Plant Area, empty pots of one size in that Area, and a user
        // who may confirm READY there. Skipped without the 2026-10-15 migration.
        private static async Task<PotFixture> PotFixtureAsync(decimal plants = 240, decimal pots = 300)
        {
            var env = await OpenAsync(needsMigration: true);
            Skip.IfNot(await env.ScalarAsync<int>(PotBatchSchema.ProbeSql) == 1, "2026-10-15_PotProductionFromReadyStock.sql is not applied to this scratch database.");
            var confirmer = (await env.Get<UserRoleRepository>().GetReadyConfirmersAsync(MpArea)).Select(u => u.EmployeeID).FirstOrDefault();
            Skip.If(confirmer == 0, "No user may confirm pot batches READY in the Mother Plant Area of this copy.");
            var size = await env.ScalarAsync<string>("SELECT TOP 1 Name FROM dbo.PotSizes WHERE IsActive = 1 ORDER BY Id");
            Skip.If(size == null, "No active pot size in this copy.");
            var v = await NewVarietyAsync(env);
            var (transfer, received) = await SentAndReceivedAsync(env, v, MotherPlantTransferRules.PurposePotProduction, plants);
            var (ok, msg, _) = await env.Get<EmptyPotInventoryRepository>().AddAreaStockAsync(MpArea, size!, pots, AppClock.Today, "pots from ready stock test",
                Recorder, "mp-area-test", EmptyPotStockRules.MotherPlantAreaType);
            Assert.True(ok, msg);
            return new PotFixture(env, v, transfer, received, confirmer, size!);
        }

        private static PotProductionBatch PotEntry(PotFixture f, decimal seedlings, DateTime? start = null, int? source = null) => new()
        {
            SourceReadyStockId = source ?? f.Received, CuttingAllocated = seedlings, PotSize = f.Size,
            ProductionStartDate = start ?? AppClock.Today, ExpectedReadyDate = AppClock.Today.AddDays(30),
            SupervisorId = f.Confirmer, Remarks = "pots from ready stock test", CreatedBy = "mp-area-test"
        };

        private static async Task<(decimal Qty, decimal Available)> RsAsync(Env env, int id)
        {
            var b = (await env.Get<ReadyStockRepository>().GetByIdAsync(id))!;
            return (b.Quantity, b.AvailableQuantity);
        }

        // The batch's Ready Stock ledger explains its quantity (net of reservation / hold rows).
        private static async Task AssertReadyLedgerAsync(Env env, int id)
        {
            var b = (await env.Get<ReadyStockRepository>().GetByIdAsync(id))!;
            var net = await env.ScalarAsync<decimal>(@"SELECT ISNULL(SUM(Quantity), 0) FROM dbo.ReadyStockTransactions
WHERE ReadyStockId = @Id AND TransactionType NOT IN ('Reservation', 'ReservationRelease', 'TransferHold', 'TransferHoldRelease')", ("@Id", id));
            Assert.Equal(b.Quantity - b.DispatchedQuantity, net);
        }

        [SkippableFact]
        public async Task PotsFromReadyStock_ReceivedForPotProduction_SeedlingsAllocated_PotsMade_ReadyIntoPottedStock_UnusedReturned()
        {
            var f = await PotFixtureAsync();
            var env = f.Env;
            var pots0 = await env.PotsAsync(MpArea, f.Size);
            var potted0 = await env.ScalarAsync<decimal>("SELECT ISNULL(SUM(PhysicalQuantity), 0) FROM dbo.PottedPlantStock WHERE AreaId = @A AND SpeciesId = @S AND PotSize = @Z",
                ("@A", MpArea), ("@S", f.V.SpeciesId), ("@Z", f.Size));
            var pots = env.Get<PotBatchRepository>();

            // start: 200 of the 240 received seedlings leave the Ready Stock batch ('PotProductionOut', dated the start)
            var entry = PotEntry(f, 200);
            var (ok, msg, batchId) = await pots.CreateFromReadyStockAsync(entry, Recorder, Any);
            Assert.True(ok, msg);
            var b = (await pots.GetByIdAsync(batchId))!;
            Assert.True(b.IsFromReadyStock);
            Assert.Equal(((int?)null, (int?)f.Received, MpArea, f.V.SpeciesId, 200m, f.Size), (b.SourceCuttingStockId, b.SourceReadyStockId, b.AreaId, b.SpeciesId, b.CuttingAllocated, b.PotSize));
            Assert.Equal("seedlings", b.SourceUnit);
            Assert.Equal((40m, 40m), await RsAsync(env, f.Received));
            Assert.Equal(-200m, await env.ScalarAsync<decimal>(@"SELECT SUM(Quantity) FROM dbo.ReadyStockTransactions
WHERE ReadyStockId = @R AND TransactionType = N'PotProductionOut' AND ReferenceType = N'PotProductionBatch' AND ReferenceId = @B", ("@R", f.Received), ("@B", batchId)));
            await AssertReadyLedgerAsync(env, f.Received);

            // daily production uses the Area's own empty pots
            Assert.True((await pots.AddEntryAsync(batchId, AppClock.Today, 150, null, Recorder, "mp-area-test")).Success);
            Assert.Equal(pots0 - 150, await env.PotsAsync(MpArea, f.Size));

            // READY: 140 pots into Potted Plant Stock, 10 lost (reason), the 50 seedlings never potted back to the batch
            var (rOk, rMsg) = await pots.ConfirmReadyAsync(batchId, 140, "Damaged plants", PotBatchRules.ReturnedToStock, null, f.Confirmer, "mp-area-test");
            Assert.True(rOk, rMsg);
            Assert.Equal(PotBatchRules.Ready, (await pots.GetByIdAsync(batchId))!.Status);
            Assert.Equal(potted0 + 140, await env.ScalarAsync<decimal>("SELECT ISNULL(SUM(PhysicalQuantity), 0) FROM dbo.PottedPlantStock WHERE AreaId = @A AND SpeciesId = @S AND PotSize = @Z",
                ("@A", MpArea), ("@S", f.V.SpeciesId), ("@Z", f.Size)));
            Assert.Equal((90m, 90m), await RsAsync(env, f.Received));                       // 240 - 200 + 50
            await AssertReadyLedgerAsync(env, f.Received);
            // conservation: 240 received = 90 still Ready Stock + 140 potted + 10 pots lost
            Assert.Equal(240m, 90m + 140m + 10m);
            Assert.Contains(await env.Get<WastageRepository>().GetAsync(AppClock.Today, AppClock.Today, MpArea, b.BatchCode),
                w => w.ProductionType == "Pot production (pots lost)" && w.Quantity == 10);
        }

        [SkippableFact]
        public async Task PotsFromReadyStock_Refused_WrongPurpose_TooMany_OtherArea_BadDates_BadSize_ChangesNothing()
        {
            var f = await PotFixtureAsync();
            var env = f.Env;
            var pots = env.Get<PotBatchRepository>();
            var batches0 = await env.ScalarAsync<int>("SELECT COUNT(*) FROM dbo.PotProductionBatches");
            async Task Refused(PotProductionBatch e, string expect, Func<int, bool>? access = null)
            {
                var (ok, msg, _) = await pots.CreateFromReadyStockAsync(e, Recorder, access ?? Any);
                Assert.False(ok);
                Assert.Contains(expect, msg);
            }
            await Refused(PotEntry(f, 241), "Only 240 seedlings");
            await Refused(PotEntry(f, 0), "whole number greater than zero");
            await Refused(PotEntry(f, 10), "not authorized", _ => false);
            await Refused(PotEntry(f, 10, AppClock.Today.AddDays(1)), "future");
            await Refused(PotEntry(f, 10, AppClock.Today.AddDays(-1)), "on or after");   // before the batch was received
            var badSize = PotEntry(f, 10);
            badSize.PotSize = "ZZ no such size";
            await Refused(badSize, "pot size");

            // a batch received for transplanting into trays is not potted
            var (_, forTrays) = await SentAndReceivedAsync(env, f.V, MotherPlantTransferRules.PurposeTrayTransplant);
            await Refused(PotEntry(f, 10, source: forTrays), "Pot Production");
            // the Main Area batch itself (not received in a Mother Plant Area)
            var main = await env.ScalarAsync<int>("SELECT ParentReadyStockId FROM dbo.ReadyStock WHERE Id = @R", ("@R", f.Received));
            await Refused(PotEntry(f, 10, source: main), "Mother Plant Area");

            Assert.Equal((240m, 240m), await RsAsync(env, f.Received));
            Assert.Equal(batches0, await env.ScalarAsync<int>("SELECT COUNT(*) FROM dbo.PotProductionBatches"));
            await AssertReadyLedgerAsync(env, f.Received);

            // the database refuses a direct insert that takes Ready Stock of another Area, or two sources at once
            var cutting = await env.ScalarAsync<int>("SELECT TOP 1 Id FROM dbo.CuttingStock ORDER BY Id");
            var pool = await env.ScalarAsync<int>("SELECT TOP 1 Id FROM dbo.EmptyPotInventory WHERE AreaId = @A AND PotSize = @Z", ("@A", MpArea), ("@Z", f.Size));
            const string insert = @"INSERT INTO dbo.PotProductionBatches (BatchCode, SourceCuttingStockId, SourceReadyStockId, SpeciesId, AreaId, PotSize, EmptyPotInventoryId,
CuttingAllocated, ProductionStartDate, ExpectedReadyDate, SupervisorId, Status, CreatedById, CreatedBy)
VALUES (@Code, @Cut, @Rs, @S, @A, @Z, @Pool, 5, CAST(GETDATE() AS date), DATEADD(DAY, 30, CAST(GETDATE() AS date)), @Sup, N'InProduction', @U, N'mp-area-test')";
            var other = await Assert.ThrowsAsync<SqlException>(() => env.ExecAsync(insert, ("@Code", "ZZT-" + Guid.NewGuid().ToString("N")[..8]), ("@Cut", null),
                ("@Rs", main), ("@S", f.V.SpeciesId), ("@A", MpArea), ("@Z", f.Size), ("@Pool", pool), ("@Sup", f.Confirmer), ("@U", Recorder)));
            Assert.Contains("same Mother Plant Area", other.Message);
            var both = await Assert.ThrowsAsync<SqlException>(() => env.ExecAsync(insert, ("@Code", "ZZT-" + Guid.NewGuid().ToString("N")[..8]), ("@Cut", cutting),
                ("@Rs", f.Received), ("@S", f.V.SpeciesId), ("@A", MpArea), ("@Z", f.Size), ("@Pool", pool), ("@Sup", f.Confirmer), ("@U", Recorder)));
            Assert.Contains("CK_PotBatches_OneSource", both.Message);
        }

        [SkippableFact]
        public async Task PotsFromReadyStock_CancelReturnsEverySeedling_UnusedAsWastageIsLabelledSeedlings_SourceNeverChanges()
        {
            var f = await PotFixtureAsync();
            var env = f.Env;
            var pots = env.Get<PotBatchRepository>();

            // cancelled before any production: all 100 back to the batch
            var (ok, msg, cancelId) = await pots.CreateFromReadyStockAsync(PotEntry(f, 100), Recorder, Any);
            Assert.True(ok, msg);
            Assert.Equal((140m, 140m), await RsAsync(env, f.Received));
            var (cOk, cMsg) = await pots.CancelAsync(cancelId, Recorder, "mp-area-test");
            Assert.True(cOk, cMsg);
            Assert.Equal((240m, 240m), await RsAsync(env, f.Received));
            Assert.Equal("Cancelled", (await pots.GetByIdAsync(cancelId))!.Status);

            // the source of a batch never changes (TR_PotBatches_Update, NULL-safe for either source)
            var other = await SentAndReceivedAsync(env, f.V, MotherPlantTransferRules.PurposePotProduction);
            var ex = await Assert.ThrowsAsync<SqlException>(() => env.ExecAsync("UPDATE dbo.PotProductionBatches SET SourceReadyStockId = @R WHERE Id = @B",
                ("@R", other.Received), ("@B", cancelId)));
            Assert.Contains("cannot be changed", ex.Message);

            // seedlings never potted recorded as wastage: labelled as seedlings in the Wastage report
            var (wOk, wMsg, wasteId) = await pots.CreateFromReadyStockAsync(PotEntry(f, 60), Recorder, Any);
            Assert.True(wOk, wMsg);
            Assert.True((await pots.AddEntryAsync(wasteId, AppClock.Today, 40, null, Recorder, "mp-area-test")).Success);
            var (rOk, rMsg) = await pots.ConfirmReadyAsync(wasteId, 40, null, PotBatchRules.UnusedAsWastage, null, f.Confirmer, "mp-area-test");
            Assert.True(rOk, rMsg);
            Assert.Equal((180m, 180m), await RsAsync(env, f.Received));                      // 240 - 60; the 20 unused are wastage, not returned
            var code = (await pots.GetByIdAsync(wasteId))!.BatchCode;
            var row = Assert.Single(await env.Get<WastageRepository>().GetAsync(AppClock.Today, AppClock.Today, MpArea, code),
                w => w.ProductionType == "Pot production (seedlings not potted)");
            Assert.Equal(("Seedling", 20m), (row.Material, row.Quantity));
            await AssertReadyLedgerAsync(env, f.Received);
        }
    }
}
