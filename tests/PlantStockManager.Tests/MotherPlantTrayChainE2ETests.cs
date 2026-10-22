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
    // The whole Main Office -> Mother Plant Area tray chain on ONE batch, end-to-end against a SCRATCH COPY
    // (PlantsIMS2_Scratch_* only; SKIPPED without PSM_SCRATCH_CONNECTION or the migrations it needs):
    //   Main Office Ready Stock --transfer (purpose: tray transplant)--> in transit --receipt--> the Area's received batch
    //   --transplant (complete trays of the chosen cavity from the Area's tray pool, remainder = waste, no Cocopeat/Pitmos)-->
    //   a tray batch in Sowing Approvals --its Area Supervisor confirms (from the Ready Date)--> the Area's Ready Stock
    //   --> sold to a customer (Booking + allocation + reservation/release/dispatch) or sent whole to an Outlet.
    // Every step's refusals change nothing, and every ledger reconciles with its balance at the end.
    [Collection("ScratchDb")]
    public class MotherPlantTrayChainE2ETests
    {
        private const string ConnectionVariable = "PSM_SCRATCH_CONNECTION";
        private const string RequiredPrefix = "PlantsIMS2_Scratch_";
        private const int MainArea = 2, MainPolyhouse = 4, MpArea = 127, OutletArea = 124;
        private const int SowingSupervisor = 9, Recorder = 21, ReadyDays = 30;
        private static readonly MotherPlantTransferRepository.Actor Actor = new(Recorder, "chain-test");
        private static bool Any(int _) => true;
        private static bool OwnArea(int a) => a == MpArea;

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
            public int Cocopeat;
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

            public Task<decimal> D(string sql, params (string Name, object? Value)[] args) => ScalarAsync<decimal>(sql, args);
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
                foreach (var probe in new[] { BedSowingSchema.TraysProbeSql, MotherPlantAreaSchema.ProbeSql, WorkflowCorrectionsSchema.TransplantWasteProbeSql,
                             WorkflowCorrectionsSchema.TransplantAreaScopeProbeSql, WorkflowCorrectionsSchema.SeedlingSaleTokenProbeSql })
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
            var env = new Env { Cs = cs!, Provider = services.BuildServiceProvider() };
            env.Cocopeat = await env.ScalarAsync<int>("SELECT Id FROM dbo.MaterialTypes WHERE Code = 'COCOPEAT'");
            return env;
        }

        private sealed record Variety(int SpeciesId, int SeedStockId);

        private static async Task<Variety> NewVarietyAsync(Env env)
        {
            var name = "ZZTEST Chain " + Guid.NewGuid().ToString("N")[..10];
            var speciesId = await env.ScalarAsync<int>(@"INSERT INTO dbo.PlantSpecies (PlantTypeId, Name, ReadyStockDays)
VALUES ((SELECT MIN(Id) FROM dbo.PlantTypes), @N, @D); SELECT CAST(SCOPE_IDENTITY() AS INT)", ("@N", name), ("@D", ReadyDays));
            var lot = await env.ScalarAsync<int>(@"INSERT INTO dbo.SeedStock (SpeciesId, AreaId, BatchNo, Unit, PhysicalQuantity, CreatedBy)
VALUES (@S, @A, @B, N'Seeds', 100000, N'chain-test'); SELECT CAST(SCOPE_IDENTITY() AS INT)", ("@S", speciesId), ("@A", MainArea), ("@B", "LOT-" + name[^10..]));
            return new Variety(speciesId, lot);
        }

        // An approved Main Office sowing batch of `plants` (24 Cavity) sown 45 days ago -> its Ready Stock id.
        private static async Task<int> MainOfficeBatchAsync(Env env, Variety v, int plants)
        {
            Assert.True((await env.Get<TrayStockRepository>().AddStockAsync(MainArea, MainPolyhouse, "24 Cavity", plants / 24 + 5, DateTime.UtcNow.AddDays(-50),
                Random.Shared.Next(1, int.MaxValue), SowingSupervisor, "chain-test", "chain setup", Any)).Success);
            Assert.True((await env.Get<MaterialStockRepository>().AdjustAsync(MainArea, MainPolyhouse, env.Cocopeat, 1, 500m, "chain setup", Guid.NewGuid(), Recorder, Any,
                LedgerDates.Instant(AppClock.Today.AddDays(-50)).TransactionUtc)).Success);
            var (ok, msg, sowingId) = await env.Get<SeedSowingRepository>().InsertAsync(new SeedSowing
            {
                SourceSeedStockId = v.SeedStockId, SeedQuantity = plants, CavityType = "24 Cavity", SowingDate = AppClock.Today.AddDays(-45),
                PolyhouseId = MainPolyhouse, SupervisorId = SowingSupervisor, CreatedBy = "chain-test", CreatedById = Recorder, GrowingMaterialTypeId = env.Cocopeat
            }, Recorder, Any);
            Assert.True(ok, msg);
            Assert.True((await env.Get<ReadyConfirmationRepository>().ConfirmAsync(sowingId, plants / 24, null, null, null, "chain-test", SowingSupervisor, Any)).Success);
            return await env.ScalarAsync<int>("SELECT Id FROM dbo.ReadyStock WHERE SeedSowingId = @S AND OriginType = N'Sowing'", ("@S", sowingId));
        }

        private static async Task<int> AreaSupervisorAsync(Env env)
        {
            var id = await env.ScalarAsync<int>(@"SELECT TOP 1 ur.UserId FROM dbo.UserRoles ur JOIN dbo.IMSUsers u ON u.Id = ur.UserId AND u.IsActive = 1
JOIN dbo.RolePermissions rp ON rp.RoleId = ur.RoleId JOIN dbo.Permissions p ON p.Id = rp.PermissionId AND p.Code = N'ReadyStock.Confirm'
WHERE ur.AreaId = @A AND NOT EXISTS (SELECT 1 FROM dbo.UserRoles x JOIN dbo.Roles r ON r.Id = x.RoleId WHERE x.UserId = ur.UserId AND r.Name = N'System Administrator')
ORDER BY ur.UserId", ("@A", MpArea));
            Skip.If(id == 0, "The copy lacks an Area Supervisor of the Mother Plant Area.");
            return id;
        }

        private static Task<decimal> TrayPoolAsync(Env env, string size)
            => env.D("SELECT ISNULL(SUM(PhysicalQuantity), 0) FROM dbo.TrayStock WHERE IsActive = 1 AND AreaId = @A AND PolyhouseId IS NULL AND TraySize = @S", ("@A", MpArea), ("@S", size));

        private static Task<decimal> TrayLedgerAsync(Env env, string size)
            => env.D(@"SELECT ISNULL(SUM(x.Quantity), 0) FROM dbo.TrayStockTransactions x JOIN dbo.TrayStock t ON t.Id = x.TrayStockId
WHERE t.IsActive = 1 AND t.AreaId = @A AND t.PolyhouseId IS NULL AND t.TraySize = @S", ("@A", MpArea), ("@S", size));

        // Physical (Quantity - Dispatched) of a batch = its ledger without the reservation / hold bookkeeping rows.
        private static async Task AssertBatchReconcilesAsync(Env env, int rs)
        {
            var physical = await env.D("SELECT Quantity - DispatchedQuantity FROM dbo.ReadyStock WHERE Id = @R", ("@R", rs));
            Assert.Equal(physical, await env.D(@"SELECT ISNULL(SUM(Quantity), 0) FROM dbo.ReadyStockTransactions WHERE ReadyStockId = @R
AND TransactionType NOT IN ('Reservation', 'ReservationRelease', 'TransferHold', 'TransferHoldRelease')", ("@R", rs)));
            Assert.True(await env.D("SELECT Quantity - ReservedQuantity - DispatchedQuantity - InTransitQuantity FROM dbo.ReadyStock WHERE Id = @R", ("@R", rs)) >= 0);
        }

        private static SeedlingFulfilmentRepository.DirectTraySaleRequest Sale(int batch, int trays, Guid? token = null)
            => new(batch, trays, "ZZTEST Chain Customer", "9000000003", "chain address", "chain sale", token ?? Guid.NewGuid());

        // Main Office batch -> transfer + receipt dated `daysAgo` -> the Area's received batch (asserting the transfer step).
        private static async Task<(int Main, int Received)> TransferAndReceiveAsync(Env env, Variety v, int mainPlants, int sent, int daysAgo)
        {
            var main = await MainOfficeBatchAsync(env, v, mainPlants);
            var transfers = env.Get<MotherPlantTransferRepository>();
            var date = AppClock.Today.AddDays(-daysAgo);
            MotherPlantTransferRepository.CreateRequest Req(decimal q, string? purpose, Guid token) => new(main, q, MpArea, null, date, "chain", token, purpose);

            // refused, nothing held: no / an unknown purpose, more than the batch holds, an Area the user may not act for
            Assert.False((await transfers.CreateAsync(Req(sent, null, Guid.NewGuid()), Actor, Any)).Success);
            Assert.False((await transfers.CreateAsync(Req(sent, "Seedlings", Guid.NewGuid()), Actor, Any)).Success);
            Assert.False((await transfers.CreateAsync(Req(mainPlants + 24, MotherPlantTransferRules.PurposeTrayTransplant, Guid.NewGuid()), Actor, Any)).Success);
            Assert.False((await transfers.CreateAsync(Req(sent, MotherPlantTransferRules.PurposeTrayTransplant, Guid.NewGuid()), Actor, _ => false)).Success);
            Assert.Equal(0m, await env.D("SELECT InTransitQuantity FROM dbo.ReadyStock WHERE Id = @R", ("@R", main)));

            // the same form three times at once: ONE transfer, held once (in transit), nothing at the destination yet
            var token = Guid.NewGuid();
            var same = await Task.WhenAll(Enumerable.Range(0, 3).Select(_ => transfers.CreateAsync(Req(sent, MotherPlantTransferRules.PurposeTrayTransplant, token), Actor, Any)));
            foreach (var r in same) Assert.True(r.Success, r.Message);
            var transferId = Assert.Single(same.Select(r => r.Id).Distinct());
            Assert.Equal((decimal)sent, await env.D("SELECT InTransitQuantity FROM dbo.ReadyStock WHERE Id = @R", ("@R", main)));
            Assert.Equal(MotherPlantTransferRules.PurposeTrayTransplant, (await transfers.GetAsync(transferId))!.TransferPurpose);
            Assert.Null((await transfers.GetAsync(transferId))!.DestinationPolyhouseId);                             // the Area, never a Polyhouse
            Assert.Equal(0, await env.ScalarAsync<int>("SELECT COUNT(*) FROM dbo.ReadyStock WHERE AreaId = @A AND SpeciesId = @S", ("@A", MpArea), ("@S", v.SpeciesId)));

            var (ok, msg, received) = await transfers.ReceiveAsync(transferId, sent, null, Actor, Any, daysAgo > 0 ? date : null, daysAgo > 0 ? "chain back-dated receipt" : null);
            Assert.True(ok, msg);
            var again = await transfers.ReceiveAsync(transferId, sent, null, Actor, Any);                              // never received twice:
            Assert.True(!again.Success || again.ReceivedReadyStockId == received, again.Message);                        // refused, or the first receipt
            Assert.Equal(1, await env.ScalarAsync<int>("SELECT COUNT(*) FROM dbo.ReadyStock WHERE AreaId = @A AND SpeciesId = @S", ("@A", MpArea), ("@S", v.SpeciesId)));
            Assert.Equal((decimal)sent, await env.D("SELECT Quantity FROM dbo.ReadyStock WHERE Id = @R", ("@R", received!.Value)));
            Assert.Equal(MpArea, await env.ScalarAsync<int>("SELECT AreaId FROM dbo.ReadyStock WHERE Id = @R", ("@R", received.Value)));
            Assert.Equal((0m, mainPlants - (decimal)sent), (await env.D("SELECT InTransitQuantity FROM dbo.ReadyStock WHERE Id = @R", ("@R", main)),
                await env.D("SELECT Quantity - DispatchedQuantity FROM dbo.ReadyStock WHERE Id = @R", ("@R", main))));
            await AssertBatchReconcilesAsync(env, main);
            return (main, received.Value);
        }

        private static TransplantRepository.CreateRequest Transplant(int received, decimal available, decimal process, string cavity, DateTime date, Guid token, string? wasteReason)
        {
            var (ok, plan, error) = TransplantRules.PlanProcessing(available, process, cavity, false, wasteReason);
            Assert.True(ok, error);
            return new(received, MpArea, null, cavity, plan!.Processed, plan.Transplanted, plan.Waste, date, "chain transplant", token, wasteReason);
        }

        [SkippableFact]
        public async Task MainOfficeToAreaReadyStock_TransferTransplantConfirm_ThenSoldOnce_LedgersReconcile_PartlySoldBatchNotMoved()
        {
            var env = await OpenAsync();
            var supervisor = await AreaSupervisorAsync(env);
            var v = await NewVarietyAsync(env);
            var (main, received) = await TransferAndReceiveAsync(env, v, 480, 168, ReadyDays);       // whole Main Office trays: 7 x 24
            var date = AppClock.Today.AddDays(-ReadyDays);                                 // Ready Date = transplant date + Ready Days = today
            var trayRepo = env.Get<TrayStockRepository>();
            Task AddAreaTraysAsync(decimal n) => trayRepo.AddStockAsync(MpArea, null, "9 Cavity", n, DateTime.UtcNow.AddDays(-ReadyDays - 2),
                Random.Shared.Next(1, int.MaxValue), SowingSupervisor, "chain-test", "chain trays", Any);

            // the Area's 9 Cavity pool set to 17 empty trays -- one short of the 18 complete trays 168 seedlings make
            var before = await TrayPoolAsync(env, "9 Cavity");
            if (before > 0)
                Assert.True((await trayRepo.AdjustAsync(MpArea, null, "9 Cavity", -before, AppClock.Today, Random.Shared.Next(1, int.MaxValue),
                    "chain test: known starting balance", Recorder, "chain-test", Any)).Success);
            await AddAreaTraysAsync(17);
            var transplants = env.Get<TransplantRepository>();
            var materialRows0 = await env.ScalarAsync<int>("SELECT COUNT(*) FROM dbo.MaterialTransactions");

            // refused, nothing changes: not enough empty trays, more seedlings than the batch holds, a remainder without a
            // waste reason, an Area the user may not act for
            var shortTrays = await transplants.CreateAsync(Transplant(received, 168, 168, "9 Cavity", date, Guid.NewGuid(), "dead"), Actor, Any);
            Assert.False(shortTrays.Success);
            Assert.Contains("17", shortTrays.Message);
            await AddAreaTraysAsync(1);
            Assert.False(TransplantRules.PlanProcessing(168, 169, "9 Cavity", false, "dead").Ok);
            Assert.False((await transplants.CreateAsync(new TransplantRepository.CreateRequest(received, MpArea, null, "9 Cavity", 169, 162, 7, date, null, Guid.NewGuid(), "dead"), Actor, Any)).Success);
            Assert.False(TransplantRules.PlanProcessing(168, 168, "9 Cavity", false, null).Ok);
            Assert.False((await transplants.CreateAsync(Transplant(received, 168, 168, "9 Cavity", date, Guid.NewGuid(), "dead"), Actor, _ => false)).Success);
            Assert.Equal(168m, await env.D("SELECT Quantity - DispatchedQuantity FROM dbo.ReadyStock WHERE Id = @R", ("@R", received)));
            Assert.Equal(18m, await TrayPoolAsync(env, "9 Cavity"));

            // 168 seedlings into 9 Cavity: 18 complete trays (162) from the Area's pool, 6 wasted with the reason; the same form x3 = once
            var token = Guid.NewGuid();
            var same = await Task.WhenAll(Enumerable.Range(0, 3).Select(_ => transplants.CreateAsync(Transplant(received, 168, 168, "9 Cavity", date, token, "6 seedlings broken"), Actor, Any)));
            foreach (var r in same) Assert.True(r.Success, r.Message);
            var transplantId = Assert.Single(same.Select(r => r.Id).Distinct());
            Assert.Equal(0m, await TrayPoolAsync(env, "9 Cavity"));                                                  // exactly 18 taken, never below zero
            Assert.Equal(await TrayPoolAsync(env, "9 Cavity"), await TrayLedgerAsync(env, "9 Cavity"));
            Assert.Equal(materialRows0, await env.ScalarAsync<int>("SELECT COUNT(*) FROM dbo.MaterialTransactions"));   // no Cocopeat / Pitmos
            Assert.Equal((0m, -162m, -6m), (await env.D("SELECT Quantity - DispatchedQuantity FROM dbo.ReadyStock WHERE Id = @R", ("@R", received)),
                await env.D("SELECT SUM(Quantity) FROM dbo.ReadyStockTransactions WHERE ReadyStockId = @R AND TransactionType = N'TransplantOut'", ("@R", received)),
                await env.D("SELECT SUM(Quantity) FROM dbo.ReadyStockTransactions WHERE ReadyStockId = @R AND TransactionType = N'TransplantLoss'", ("@R", received))));
            Assert.Equal("6 seedlings broken", await env.ScalarAsync<string>("SELECT WasteReason FROM dbo.ReadyStockTransplants WHERE Id = @T", ("@T", transplantId)));
            Assert.Equal(date, await env.ScalarAsync<DateTime>("SELECT TransplantDate FROM dbo.ReadyStockTransplants WHERE Id = @T", ("@T", transplantId)));
            await AssertBatchReconcilesAsync(env, received);
            var sowing = await env.ScalarAsync<int>("SELECT OutputSeedSowingId FROM dbo.ReadyStockTransplants WHERE Id = @T", ("@T", transplantId));
            Assert.Equal((MpArea, "9 Cavity", 18m, 162m), (await env.ScalarAsync<int>("SELECT AreaId FROM dbo.SeedSowings WHERE Id = @S", ("@S", sowing)),
                await env.ScalarAsync<string>("SELECT CavityType FROM dbo.SeedSowings WHERE Id = @S", ("@S", sowing)),
                await env.D("SELECT NumberOfTrays FROM dbo.SeedSowings WHERE Id = @S", ("@S", sowing)),
                await env.D("SELECT QuantitySown FROM dbo.SeedSowings WHERE Id = @S", ("@S", sowing))));
            Assert.Null(await env.ScalarAsync<int?>("SELECT PolyhouseId FROM dbo.SeedSowings WHERE Id = @S", ("@S", sowing)));   // not a Main Office Polyhouse
            Assert.Equal(AppClock.Today, await env.ScalarAsync<DateTime>("SELECT ExpectedReadyDate FROM dbo.SeedSowings WHERE Id = @S", ("@S", sowing)));
            Assert.Equal(v.SpeciesId, await env.ScalarAsync<int>("SELECT SpeciesId FROM dbo.SeedSowings WHERE Id = @S", ("@S", sowing)));

            // not Ready Stock until its Area Supervisor confirms -- then exactly once, in this Area
            Assert.Equal(0, await env.ScalarAsync<int>("SELECT COUNT(*) FROM dbo.ReadyStock WHERE SeedSowingId = @S", ("@S", sowing)));
            var approvals = env.Get<ReadyConfirmationRepository>();
            var confirmed = await approvals.ConfirmAsync(sowing, 18, null, null, null, "chain", supervisor, Any);
            Assert.True(confirmed.Success, confirmed.Message);
            Assert.False((await approvals.ConfirmAsync(sowing, 18, null, null, null, "chain", supervisor, Any)).Success);
            var rs = await env.ScalarAsync<int>("SELECT Id FROM dbo.ReadyStock WHERE SeedSowingId = @S", ("@S", sowing));
            Assert.Equal(1, await env.ScalarAsync<int>("SELECT COUNT(*) FROM dbo.ReadyStock WHERE SeedSowingId = @S", ("@S", sowing)));
            Assert.Equal((MpArea, 162m), (await env.ScalarAsync<int>("SELECT AreaId FROM dbo.ReadyStock WHERE Id = @R", ("@R", rs)),
                await env.D("SELECT Quantity FROM dbo.ReadyStock WHERE Id = @R", ("@R", rs))));
            var areaRow = (await env.Get<ReadyStockRepository>().GetByIdAsync(rs))!;
            Assert.Equal(("9 Cavity", (int?)9, v.SpeciesId), (areaRow.CavityType, areaRow.CavitySize, areaRow.SpeciesId));

            // sold to a customer: 3 trays, once even when the form arrives three times at once; never more than is there
            var sales = env.Get<SeedlingFulfilmentRepository>();
            var seller = new SeedlingFulfilmentRepository.Actor("chain-seller", supervisor);
            Assert.False((await sales.DirectTraySaleAsync(Sale(rs, 19), seller, OwnArea)).Success);
            var saleToken = Guid.NewGuid();
            var sold = await Task.WhenAll(Enumerable.Range(0, 3).Select(_ => sales.DirectTraySaleAsync(Sale(rs, 3, saleToken), seller, OwnArea)));
            foreach (var x in sold) Assert.True(x.Success, x.Message);
            Assert.Single(sold.Select(s => s.DispatchCode).Distinct());
            var dispatch = await env.ScalarAsync<int>("SELECT Id FROM dbo.SeedlingDispatches WHERE SubmissionToken = @T", ("@T", saleToken));
            var booking = await env.ScalarAsync<int>("SELECT BookingId FROM dbo.SeedlingDispatches WHERE Id = @D", ("@D", dispatch));
            Assert.Equal((162m, 0m, 27m), (await env.D("SELECT Quantity FROM dbo.ReadyStock WHERE Id = @R", ("@R", rs)),
                await env.D("SELECT ReservedQuantity FROM dbo.ReadyStock WHERE Id = @R", ("@R", rs)),
                await env.D("SELECT DispatchedQuantity FROM dbo.ReadyStock WHERE Id = @R", ("@R", rs))));
            Assert.Equal(("Completed", 27m, 0m), (await env.ScalarAsync<string>("SELECT Status FROM dbo.Bookings WHERE Id = @B", ("@B", booking)),
                await env.D("SELECT DispatchedQuantity FROM dbo.Bookings WHERE Id = @B", ("@B", booking)),
                await env.D("SELECT ReservedQuantity FROM dbo.Bookings WHERE Id = @B", ("@B", booking))));
            Assert.Equal(("Closed", 27m), (await env.ScalarAsync<string>("SELECT Status FROM dbo.BookingBatchAllocations WHERE BookingId = @B AND ReadyStockId = @R", ("@B", booking), ("@R", rs)),
                await env.D("SELECT DispatchedQuantity FROM dbo.BookingBatchAllocations WHERE BookingId = @B AND ReadyStockId = @R", ("@B", booking), ("@R", rs))));
            Assert.Equal(27m, await env.D("SELECT SUM(Quantity) FROM dbo.SeedlingDispatchLines WHERE SeedlingDispatchId = @D", ("@D", dispatch)));
            Assert.Equal((27m, -27m, -27m), (
                await env.D("SELECT SUM(Quantity) FROM dbo.ReadyStockTransactions WHERE ReadyStockId = @R AND TransactionType = N'Reservation'", ("@R", rs)),
                await env.D("SELECT SUM(Quantity) FROM dbo.ReadyStockTransactions WHERE ReadyStockId = @R AND TransactionType = N'ReservationRelease'", ("@R", rs)),
                await env.D("SELECT SUM(Quantity) FROM dbo.ReadyStockTransactions WHERE ReadyStockId = @R AND TransactionType = N'Dispatch'", ("@R", rs))));
            await AssertBatchReconcilesAsync(env, rs);

            // the existing rule: a batch with trays already dispatched is never moved (whole batches only) -- nothing moves
            var move = await env.Get<InternalTransferRepository>().InsertAsync(new InternalTransfer
            {
                StockType = "ReadyStock", SourceReadyStockId = rs, DestinationAreaId = OutletArea, Quantity = 162, Remarks = "chain to outlet",
                CreatedBy = "chain-test", SubmissionToken = Guid.NewGuid()
            }, supervisor);
            Assert.False(move.Success);
            Assert.Equal(MpArea, await env.ScalarAsync<int>("SELECT AreaId FROM dbo.ReadyStock WHERE Id = @R", ("@R", rs)));
        }

        [SkippableFact]
        public async Task TransplantedBatch_NotConfirmableBeforeItsReadyDate_ThenSentWholeToAnOutletOnce_NeverSoldAfterItLeft()
        {
            var env = await OpenAsync();
            var supervisor = await AreaSupervisorAsync(env);
            var v = await NewVarietyAsync(env);
            var (_, received) = await TransferAndReceiveAsync(env, v, 240, 48, 0);
            await env.Get<TrayStockRepository>().AddStockAsync(MpArea, null, "24 Cavity", 2, DateTime.UtcNow.AddMinutes(-5),
                Random.Shared.Next(1, int.MaxValue), SowingSupervisor, "chain-test", "chain trays", Any);
            var (ok, msg, transplantId, _) = await env.Get<TransplantRepository>().CreateAsync(
                Transplant(received, 48, 48, "24 Cavity", AppClock.Today, Guid.NewGuid(), null), Actor, Any);
            Assert.True(ok, msg);
            var sowing = await env.ScalarAsync<int>("SELECT OutputSeedSowingId FROM dbo.ReadyStockTransplants WHERE Id = @T", ("@T", transplantId));
            Assert.Equal(AppClock.Today.AddDays(ReadyDays), await env.ScalarAsync<DateTime>("SELECT ExpectedReadyDate FROM dbo.SeedSowings WHERE Id = @S", ("@S", sowing)));

            // before its Ready Date: refused, and the Ready Date is not changed to let it through.
            // 2026-10-20: once 2026-10-20_TransplantApprovalAnyDate.sql is applied the Area Supervisor may approve it on any
            // date (WorkflowCorrections1020E2ETests) -- this early attempt is then not made, so the rest of the chain is unchanged.
            var approvals = env.Get<ReadyConfirmationRepository>();
            if (!(await env.ScalarAsync<int>(WorkflowCorrectionsSchema.TransplantAnyDateProbeSql) == 1))
            {
                var early = await approvals.ConfirmAsync(sowing, 2, null, null, null, "chain", supervisor, Any);
                Assert.False(early.Success);
                Assert.Contains("not ready yet", early.Message);
            }
            Assert.Equal(0, await env.ScalarAsync<int>("SELECT COUNT(*) FROM dbo.ReadyStock WHERE SeedSowingId = @S", ("@S", sowing)));
            Assert.Equal(AppClock.Today.AddDays(ReadyDays), await env.ScalarAsync<DateTime>("SELECT ExpectedReadyDate FROM dbo.SeedSowings WHERE Id = @S", ("@S", sowing)));

            // a batch transplanted Ready Days ago (Ready Date today): confirmed by its Area Supervisor -> Area Ready Stock
            var (_, receivedB) = await TransferAndReceiveAsync(env, await NewVarietyAsync(env), 240, 48, ReadyDays);
            await env.Get<TrayStockRepository>().AddStockAsync(MpArea, null, "24 Cavity", 2, DateTime.UtcNow.AddDays(-ReadyDays - 2),
                Random.Shared.Next(1, int.MaxValue), SowingSupervisor, "chain-test", "chain trays", Any);
            var b = await env.Get<TransplantRepository>().CreateAsync(
                Transplant(receivedB, 48, 48, "24 Cavity", AppClock.Today.AddDays(-ReadyDays), Guid.NewGuid(), null), Actor, Any);
            Assert.True(b.Success, b.Message);
            sowing = await env.ScalarAsync<int>("SELECT OutputSeedSowingId FROM dbo.ReadyStockTransplants WHERE Id = @T", ("@T", b.Id));
            var confirmed = await approvals.ConfirmAsync(sowing, 2, null, null, null, "chain", supervisor, Any);
            Assert.True(confirmed.Success, confirmed.Message);
            var rs = await env.ScalarAsync<int>("SELECT Id FROM dbo.ReadyStock WHERE SeedSowingId = @S", ("@S", sowing));
            Assert.Equal((MpArea, 48m), (await env.ScalarAsync<int>("SELECT AreaId FROM dbo.ReadyStock WHERE Id = @R", ("@R", rs)),
                await env.D("SELECT Quantity FROM dbo.ReadyStock WHERE Id = @R", ("@R", rs))));

            // sent whole to an Outlet: the same form x3 = one transfer, the batch row (and its lineage) is now the Outlet's
            var transfers = env.Get<InternalTransferRepository>();
            var token = Guid.NewGuid();
            InternalTransfer Send(Guid t) => new()
            {
                StockType = "ReadyStock", SourceReadyStockId = rs, DestinationAreaId = OutletArea, Quantity = 48, Remarks = "chain to outlet",
                CreatedBy = "chain-test", SubmissionToken = t
            };
            var same = await Task.WhenAll(Enumerable.Range(0, 3).Select(_ => transfers.InsertAsync(Send(token), supervisor)));
            Assert.All(same, r => Assert.True(r.Success, r.Message));
            Assert.Single(same.Select(r => r.Id).Distinct());
            Assert.False((await transfers.InsertAsync(Send(Guid.NewGuid()), supervisor)).Success);                   // never moved twice
            Assert.Equal((OutletArea, 48m, 0m), (await env.ScalarAsync<int>("SELECT AreaId FROM dbo.ReadyStock WHERE Id = @R", ("@R", rs)),
                await env.D("SELECT Quantity FROM dbo.ReadyStock WHERE Id = @R", ("@R", rs)),
                await env.D("SELECT DispatchedQuantity FROM dbo.ReadyStock WHERE Id = @R", ("@R", rs))));
            Assert.Equal(1, await env.ScalarAsync<int>("SELECT COUNT(*) FROM dbo.InternalTransfers WHERE SourceReadyStockId = @R AND StockType = N'ReadyStock'", ("@R", rs)));
            await AssertBatchReconcilesAsync(env, rs);

            // the trays have left the Mother Plant Area: they cannot be sold from it any more
            var sale = await env.Get<SeedlingFulfilmentRepository>().DirectTraySaleAsync(Sale(rs, 1), new SeedlingFulfilmentRepository.Actor("chain-seller", supervisor), _ => true);
            Assert.False(sale.Success);
            Assert.Contains("active Mother Plant Area", sale.Message);
            Assert.Equal(0m, await env.D("SELECT DispatchedQuantity FROM dbo.ReadyStock WHERE Id = @R", ("@R", rs)));
        }
    }
}
