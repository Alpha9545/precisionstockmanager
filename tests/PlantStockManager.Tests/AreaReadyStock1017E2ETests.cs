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
    // 2026-10-17, end-to-end against a SCRATCH COPY (PlantsIMS2_Scratch_* only; SKIPPED without PSM_SCRATCH_CONNECTION and,
    // per test, without the migrations it needs):
    //   1. A tray batch transplanted in a Mother Plant Area is that Area's Ready Stock only after ITS Area Supervisor approves
    //      it -- exactly once, in that Area; another Area's supervisor / a Main Office supervisor is refused (server + trigger).
    //   2. Issuing that Area's Ready Stock: trays to an Outlet (whole batch), pots to a customer / an Outlet -- each once,
    //      never twice on a replayed form, never overdrawn by concurrent forms.
    //   3. Bed Sowing tray batches: the designated approver may approve them on ANY date; nothing else about who approves.
    [Collection("ScratchDb")]
    public class AreaReadyStock1017E2ETests
    {
        private const string ConnectionVariable = "PSM_SCRATCH_CONNECTION";
        private const string RequiredPrefix = "PlantsIMS2_Scratch_";
        private const int MainArea = 2, MainPolyhouse = 4, MpArea = 127, OutletArea = 124;
        private const int SowingSupervisor = 9, Recorder = 21, ReadyDays = 30;
        private static readonly MotherPlantTransferRepository.Actor Actor = new(Recorder, "wf1017-test");
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

            public Task ExecAsync(string sql, params (string Name, object? Value)[] args) => ScalarAsync<int>(sql + "; SELECT 1", args);
        }

        private static async Task<Env> OpenAsync(params string[] probes)
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
                foreach (var probe in new[] { BedSowingSchema.TraysProbeSql, MotherPlantAreaSchema.ProbeSql, WorkflowCorrectionsSchema.TransplantWasteProbeSql }.Concat(probes))
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

        // ---- fixtures -------------------------------------------------------------------------------------------------

        private sealed record Variety(int SpeciesId, int SeedStockId);

        private static async Task<Variety> NewVarietyAsync(Env env, int area = MainArea)
        {
            var name = "ZZTEST Wf17 " + Guid.NewGuid().ToString("N")[..10];
            var speciesId = await env.ScalarAsync<int>(@"INSERT INTO dbo.PlantSpecies (PlantTypeId, Name, ReadyStockDays)
VALUES ((SELECT MIN(Id) FROM dbo.PlantTypes), @N, @D); SELECT CAST(SCOPE_IDENTITY() AS INT)", ("@N", name), ("@D", ReadyDays));
            var lot = await env.ScalarAsync<int>(@"INSERT INTO dbo.SeedStock (SpeciesId, AreaId, BatchNo, Unit, PhysicalQuantity, CreatedBy)
VALUES (@S, @A, @B, N'Seeds', 100000, N'wf1017-test'); SELECT CAST(SCOPE_IDENTITY() AS INT)", ("@S", speciesId), ("@A", area), ("@B", "LOT-" + name[^10..]));
            return new Variety(speciesId, lot);
        }

        private static async Task<int> ApprovedMainBatchAsync(Env env, Variety v, int plants = 480)
        {
            Assert.True((await env.Get<TrayStockRepository>().AddStockAsync(MainArea, MainPolyhouse, "24 Cavity", plants / 24 + 5, DateTime.UtcNow.AddDays(-45),
                Random.Shared.Next(1, int.MaxValue), SowingSupervisor, "wf1017-test", "wf1017 setup", Any)).Success);
            Assert.True((await env.Get<MaterialStockRepository>().AdjustAsync(MainArea, MainPolyhouse, env.Cocopeat, 1, 500m, "wf1017 setup", Guid.NewGuid(), Recorder, Any,
                LedgerDates.Instant(AppClock.Today.AddDays(-45)).TransactionUtc)).Success);
            var sowing = new SeedSowing
            {
                SourceSeedStockId = v.SeedStockId, SeedQuantity = plants, CavityType = "24 Cavity", SowingDate = AppClock.Today.AddDays(-40),
                PolyhouseId = MainPolyhouse, SupervisorId = SowingSupervisor, CreatedBy = "wf1017-test", CreatedById = Recorder, GrowingMaterialTypeId = env.Cocopeat
            };
            var (ok, msg, sowingId) = await env.Get<SeedSowingRepository>().InsertAsync(sowing, Recorder, Any);
            Assert.True(ok, msg);
            Assert.True((await env.Get<ReadyConfirmationRepository>().ConfirmAsync(sowingId, plants / 24, null, null, null, "wf1017-test", SowingSupervisor, Any)).Success);
            return await env.ScalarAsync<int>("SELECT Id FROM dbo.ReadyStock WHERE SeedSowingId = @S AND OriginType = N'Sowing'", ("@S", sowingId));
        }

        // Received in the Mother Plant Area `daysAgo` days ago (business dates) for a purpose -> the received batch.
        private static async Task<int> ReceivedAsync(Env env, Variety v, string purpose, decimal plants, int daysAgo)
        {
            var main = await ApprovedMainBatchAsync(env, v);
            var transfers = env.Get<MotherPlantTransferRepository>();
            var (ok, msg, transferId, _) = await transfers.CreateAsync(new MotherPlantTransferRepository.CreateRequest(main, plants, MpArea, null,
                AppClock.Today.AddDays(-daysAgo), "wf1017", Guid.NewGuid(), purpose), Actor, Any);
            Assert.True(ok, msg);
            var (rOk, rMsg, batch) = await transfers.ReceiveAsync(transferId, plants, null, Actor, Any,
                daysAgo > 0 ? AppClock.Today.AddDays(-daysAgo) : null, daysAgo > 0 ? "wf1017 back-dated receipt" : null);
            Assert.True(rOk, rMsg);
            return batch!.Value;
        }

        // A tray batch transplanted in the Mother Plant Area `daysAgo` days ago (Ready Date = that + Ready Days) -> sowing id.
        private static async Task<int> TransplantedTrayBatchAsync(Env env, Variety v, decimal plants, int daysAgo)
        {
            var received = await ReceivedAsync(env, v, MotherPlantTransferRules.PurposeTrayTransplant, plants, daysAgo);
            Assert.True((await env.Get<TrayStockRepository>().AddStockAsync(MpArea, null, "24 Cavity", plants / 24 + 1, DateTime.UtcNow.AddDays(-daysAgo - 1),
                Random.Shared.Next(1, int.MaxValue), SowingSupervisor, "wf1017-test", "wf1017 trays", Any)).Success);
            var (ok, msg, id, _) = await env.Get<TransplantRepository>().CreateAsync(new TransplantRepository.CreateRequest(received, MpArea, null, "24 Cavity",
                plants, plants, 0, AppClock.Today.AddDays(-daysAgo), "wf1017 transplant", Guid.NewGuid()), Actor, Any);
            Assert.True(ok, msg);
            return await env.ScalarAsync<int>("SELECT OutputSeedSowingId FROM dbo.ReadyStockTransplants WHERE Id = @T", ("@T", id));
        }

        // Users: an Area Supervisor of the Mother Plant Area, one of ANOTHER Mother Plant Area, a Main Office Sowing Supervisor
        // (not an administrator), a System Administrator.
        private sealed record Users(int AreaSupervisor, int OtherAreaSupervisor, int MainSupervisor, int Admin);

        private static async Task<Users> UsersAsync(Env env)
        {
            const string confirmIn = @"SELECT TOP 1 ur.UserId FROM dbo.UserRoles ur JOIN dbo.IMSUsers u ON u.Id = ur.UserId AND u.IsActive = 1
JOIN dbo.RolePermissions rp ON rp.RoleId = ur.RoleId JOIN dbo.Permissions p ON p.Id = rp.PermissionId AND p.Code = N'ReadyStock.Confirm'
WHERE ur.AreaId = @A AND NOT EXISTS (SELECT 1 FROM dbo.UserRoles x JOIN dbo.Roles r ON r.Id = x.RoleId WHERE x.UserId = ur.UserId AND r.Name = N'System Administrator')
  AND NOT EXISTS (SELECT 1 FROM dbo.UserRoles y WHERE y.UserId = ur.UserId AND y.AreaId = @Not) ORDER BY ur.UserId";
            var area = await env.ScalarAsync<int>(confirmIn, ("@A", MpArea), ("@Not", -1));
            var otherArea = await env.ScalarAsync<int>(confirmIn.Replace("ur.AreaId = @A", "ur.AreaId <> @A AND ur.AreaId IN (SELECT Id FROM dbo.Area WHERE AreaType = N'MotherPlant')"),
                ("@A", MpArea), ("@Not", MpArea));
            var main = await env.ScalarAsync<int>(confirmIn, ("@A", MainArea), ("@Not", MpArea));
            var admin = await env.ScalarAsync<int>(@"SELECT TOP 1 ur.UserId FROM dbo.UserRoles ur JOIN dbo.Roles r ON r.Id = ur.RoleId JOIN dbo.IMSUsers u ON u.Id = ur.UserId AND u.IsActive = 1
WHERE r.Name = N'System Administrator' AND u.Id NOT IN (SELECT UserId FROM dbo.BedSowingApprover) ORDER BY ur.UserId");
            Skip.If(area == 0 || otherArea == 0 || main == 0 || admin == 0, "The copy lacks an Area Supervisor of each kind or an administrator.");
            return new Users(area, otherArea, main, admin);
        }

        // =====================================================================================================
        // 1. Area Supervisor confirmation -> Area Ready Stock
        // =====================================================================================================

        [SkippableFact]
        public async Task TransplantBatch_IsAreaReadyStockOnlyAfterItsAreaSupervisorConfirms_ExactlyOnce_InThatArea()
        {
            var env = await OpenAsync(WorkflowCorrectionsSchema.TransplantAreaScopeProbeSql);
            var u = await UsersAsync(env);
            var v = await NewVarietyAsync(env);
            var batch = await TransplantedTrayBatchAsync(env, v, 240, ReadyDays);     // Ready Date = today
            var approvals = env.Get<ReadyConfirmationRepository>();
            Assert.Equal(0, await env.ScalarAsync<int>("SELECT COUNT(*) FROM dbo.ReadyStock WHERE SeedSowingId = @S", ("@S", batch)));   // not Ready Stock yet

            // another Mother Plant Area's supervisor, and a Main Office supervisor: refused, nothing posted
            foreach (var wrong in new[] { u.OtherAreaSupervisor, u.MainSupervisor })
            {
                var (ok, msg, _) = await approvals.ConfirmAsync(batch, 10, null, null, null, "wf1017", wrong, Any);
                Assert.False(ok);
                Assert.Contains("Area Supervisor of", msg);
            }
            Assert.Equal(0, await env.ScalarAsync<int>("SELECT COUNT(*) FROM dbo.ReadyConfirmations WHERE SeedSowingId = @S", ("@S", batch)));
            Assert.True(await approvals.IsAreaApproverAsync(u.Admin, MpArea));                 // an administrator may
            Assert.False(await approvals.IsAreaApproverAsync(u.OtherAreaSupervisor, MpArea));

            // the Area Supervisor confirms -- three simultaneous clicks: exactly one approval, one Ready Stock batch
            var clicks = await Task.WhenAll(Enumerable.Range(0, 3).Select(_ => approvals.ConfirmAsync(batch, 10, null, null, null, "wf1017", u.AreaSupervisor, Any)));
            Assert.Equal(1, clicks.Count(c => c.Success));
            Assert.Equal(1, await env.ScalarAsync<int>("SELECT COUNT(*) FROM dbo.ReadyConfirmations WHERE SeedSowingId = @S AND Status = 'Confirmed'", ("@S", batch)));
            Assert.Equal(1, await env.ScalarAsync<int>("SELECT COUNT(*) FROM dbo.ReadyStock WHERE SeedSowingId = @S", ("@S", batch)));
            var rs = await env.ScalarAsync<int>("SELECT Id FROM dbo.ReadyStock WHERE SeedSowingId = @S", ("@S", batch));
            var rb = (await env.Get<ReadyStockRepository>().GetByIdAsync(rs))!;
            Assert.Equal((MpArea, (int?)null, v.SpeciesId, "24 Cavity", 240m, ReadyStock.OriginSowing),
                (rb.AreaId, rb.PolyhouseId, rb.SpeciesId, rb.CavityType, rb.Quantity, rb.OriginType));                          // same Area, no implicit move
            Assert.Equal(240m, await env.ScalarAsync<decimal>("SELECT SUM(Quantity) FROM dbo.ReadyStockTransactions WHERE ReadyStockId = @R AND TransactionType = N'Confirmed'", ("@R", rs)));
            Assert.Equal(u.AreaSupervisor, await env.ScalarAsync<int>("SELECT ApprovedById FROM dbo.ReadyConfirmations WHERE SeedSowingId = @S", ("@S", batch)));
            Assert.Equal("Completed", await env.ScalarAsync<string>("SELECT Status FROM dbo.SeedSowings WHERE Id = @S", ("@S", batch)));
            // a replay after it closed: refused
            Assert.False((await approvals.ConfirmAsync(batch, 10, null, null, null, "wf1017", u.AreaSupervisor, Any)).Success);

            // the database refuses another Area's supervisor too (direct insert on a second batch)
            var second = await TransplantedTrayBatchAsync(env, v, 48, ReadyDays);
            var ex = await Assert.ThrowsAsync<SqlException>(() => env.ExecAsync(@"
INSERT INTO dbo.ReadyConfirmations (ConfirmationCode, SeedSowingId, ReadyStockId, ActualTrayQuantity, ConfirmedQuantity, WastageQuantity, ApprovedById,
    ConfirmationDate, Status, SupervisorId, CreatedDate, CreatedBy)
VALUES (@C, @S, @R, 2, 48, 0, @U, SYSUTCDATETIME(), 'Confirmed', @U, SYSUTCDATETIME(), N'wf1017')",
                ("@C", "ZZT-" + Guid.NewGuid().ToString("N")[..10]), ("@S", second), ("@R", rs), ("@U", u.OtherAreaSupervisor)));
            Assert.Contains("Only the assigned supervisor", ex.Message);
        }

        // =====================================================================================================
        // 2. Issuing the Area's Ready Stock
        // =====================================================================================================

        [SkippableFact]
        public async Task ReadyTrays_ToAnOutlet_WholeBatchOnce_ReplayAndConcurrentSendsNeverDuplicate()
        {
            var env = await OpenAsync(WorkflowCorrectionsSchema.TransplantAreaScopeProbeSql);
            var u = await UsersAsync(env);
            var v = await NewVarietyAsync(env);
            var batch = await TransplantedTrayBatchAsync(env, v, 96, ReadyDays);
            Assert.True((await env.Get<ReadyConfirmationRepository>().ConfirmAsync(batch, 4, null, null, null, "wf1017", u.AreaSupervisor, Any)).Success);
            var rs = await env.ScalarAsync<int>("SELECT Id FROM dbo.ReadyStock WHERE SeedSowingId = @S", ("@S", batch));
            var transfers = env.Get<InternalTransferRepository>();
            InternalTransfer Send(Guid token) => new()
            {
                StockType = "ReadyStock", SourceReadyStockId = rs, DestinationAreaId = OutletArea, Quantity = 96, Remarks = "wf1017 to outlet",
                CreatedBy = "wf1017-test", SubmissionToken = token
            };

            var token = Guid.NewGuid();
            var same = await Task.WhenAll(Enumerable.Range(0, 3).Select(_ => transfers.InsertAsync(Send(token), u.AreaSupervisor)));   // the same form x3
            Assert.All(same, r => Assert.True(r.Success, r.Message));
            Assert.Single(same.Select(r => r.Id).Distinct());
            Assert.Equal(1, await env.ScalarAsync<int>("SELECT COUNT(*) FROM dbo.InternalTransfers WHERE SourceReadyStockId = @R AND StockType = N'ReadyStock'", ("@R", rs)));
            Assert.Equal(OutletArea, await env.ScalarAsync<int>("SELECT AreaId FROM dbo.ReadyStock WHERE Id = @R", ("@R", rs)));        // lineage kept: same batch row
            Assert.Equal((96m, 0m), (await env.ScalarAsync<decimal>("SELECT Quantity FROM dbo.ReadyStock WHERE Id = @R", ("@R", rs)),
                await env.ScalarAsync<decimal>("SELECT DispatchedQuantity FROM dbo.ReadyStock WHERE Id = @R", ("@R", rs))));
            // a new form for the batch now at the Outlet: refused (already there), nothing moves again
            Assert.False((await transfers.InsertAsync(Send(Guid.NewGuid()), u.AreaSupervisor)).Success);
            Assert.Equal(1, await env.ScalarAsync<int>("SELECT COUNT(*) FROM dbo.InternalTransfers WHERE SourceReadyStockId = @R AND StockType = N'ReadyStock'", ("@R", rs)));
        }

        private static async Task<(int Pool, string Size, int Confirmer)> ReadyPotsAsync(Env env, Variety v, decimal pots)
        {
            Skip.IfNot(await env.ScalarAsync<int>(PotBatchSchema.ProbeSql) == 1, "2026-10-15_PotProductionFromReadyStock.sql is not applied.");
            var confirmer = (await env.Get<UserRoleRepository>().GetReadyConfirmersAsync(MpArea)).Select(x => x.EmployeeID).FirstOrDefault();
            var size = await env.ScalarAsync<string>("SELECT TOP 1 Name FROM dbo.PotSizes WHERE IsActive = 1 ORDER BY Id");
            Skip.If(confirmer == 0 || size == null, "No READY confirmer or pot size in this copy.");
            var received = await ReceivedAsync(env, v, MotherPlantTransferRules.PurposePotProduction, 96, 0);
            Assert.True((await env.Get<EmptyPotInventoryRepository>().AddAreaStockAsync(MpArea, size!, pots, AppClock.Today, "wf1017 pots", Recorder, "wf1017-test",
                EmptyPotStockRules.MotherPlantAreaType)).Success);
            var potsRepo = env.Get<PotBatchRepository>();
            var (ok, msg, batchId) = await potsRepo.CreateFromReadyStockAsync(new PotProductionBatch
            {
                SourceReadyStockId = received, CuttingAllocated = pots, PotSize = size!, ProductionStartDate = AppClock.Today,
                ExpectedReadyDate = AppClock.Today.AddDays(30), SupervisorId = confirmer, CreatedBy = "wf1017-test", SubmissionToken = Guid.NewGuid()
            }, Recorder, Any);
            Assert.True(ok, msg);
            Assert.True((await potsRepo.AddEntryAsync(batchId, AppClock.Today, pots, null, Recorder, "wf1017-test")).Success);
            var (rOk, rMsg) = await potsRepo.ConfirmReadyAsync(batchId, pots, null, PotBatchRules.ReturnedToStock, null, confirmer, "wf1017-test");
            Assert.True(rOk, rMsg);
            var pool = await env.ScalarAsync<int>("SELECT PottedPlantStockId FROM dbo.PotProductionBatches WHERE Id = @B", ("@B", batchId));
            var sources = await env.Get<PottedPlantStockRepository>().GetReadySourcesAsync(new[] { pool });
            Assert.Contains(sources, s => s.BatchId == batchId && s.ReadyQuantity == pots && s.FromReadyStock);                    // the pot pool's lineage
            return (pool, size!, confirmer);
        }

        [SkippableFact]
        public async Task ReadyPots_ToACustomerAndAnOutlet_DeductOnce_ReplayAndConcurrentFormsNeverOverdraw()
        {
            var env = await OpenAsync(WorkflowCorrectionsSchema.PottedSaleTokenProbeSql);
            var u = await UsersAsync(env);
            var v = await NewVarietyAsync(env);
            var (pool, _, _) = await ReadyPotsAsync(env, v, 40);
            async Task<decimal> Physical() => await env.ScalarAsync<decimal>("SELECT PhysicalQuantity FROM dbo.PottedPlantStock WHERE Id = @P", ("@P", pool));
            var p0 = await Physical();
            var dispatches = env.Get<DispatchRepository>();

            // customer sale: the same form three times at once -> one sale, one dispatch, one deduction
            var token = Guid.NewGuid();
            var sale = await Task.WhenAll(Enumerable.Range(0, 3).Select(_ => dispatches.DirectSaleAsync(pool, 10, "ZZTEST Customer", null, "wf1017", u.AreaSupervisor, "wf1017-test", token)));
            Assert.All(sale, s => Assert.True(s.Success, s.Message));
            Assert.Single(sale.Select(s => s.DispatchCode).Distinct());
            Assert.Equal(p0 - 10, await Physical());
            Assert.Equal(1, await env.ScalarAsync<int>("SELECT COUNT(*) FROM dbo.Dispatches WHERE SubmissionToken = @T", ("@T", token)));
            Assert.Equal(-10m, await env.ScalarAsync<decimal>(@"SELECT SUM(t.Quantity) FROM dbo.PottedPlantStockTransactions t JOIN dbo.Dispatches d ON t.ReferenceType = N'Dispatch' AND d.Id = t.ReferenceId
WHERE t.PottedPlantStockId = @P AND d.SubmissionToken = @T", ("@P", pool), ("@T", token)));

            // an Outlet transfer: replayed form sends once; the Outlet receives exactly that in its own pool
            var transfers = env.Get<InternalTransferRepository>();
            var tToken = Guid.NewGuid();
            InternalTransfer ToOutlet(decimal q, Guid tk) => new() { StockType = "PottedPlant", SourcePottedPlantStockId = pool, DestinationAreaId = OutletArea,
                Quantity = q, Remarks = "wf1017 pots to outlet", CreatedBy = "wf1017-test", SubmissionToken = tk };
            Assert.True((await transfers.InsertAsync(ToOutlet(8, tToken), u.AreaSupervisor)).Success);
            var again = await transfers.InsertAsync(ToOutlet(8, tToken), u.AreaSupervisor);
            Assert.Equal((true, InternalTransferRepository.AlreadySentMessage), (again.Success, again.Message));
            Assert.Equal(p0 - 18, await Physical());
            Assert.Equal(8m, await env.ScalarAsync<decimal>(@"SELECT PhysicalQuantity FROM dbo.PottedPlantStock WHERE AreaId = @O AND SpeciesId = @S",
                ("@O", OutletArea), ("@S", v.SpeciesId)));

            // concurrent DIFFERENT forms that together exceed what is left: never below zero, never more than available
            var left = await Physical();
            var each = decimal.Floor(left / 2) + 1;
            var race = await Task.WhenAll(Enumerable.Range(0, 3).Select(_ => dispatches.DirectSaleAsync(pool, each, "ZZTEST Racer", null, null, u.AreaSupervisor, "wf1017-test", Guid.NewGuid())));
            Assert.Equal(1, race.Count(r => r.Success));
            Assert.Equal(left - each, await Physical());
            Assert.True(await Physical() >= 0);
        }

        // =====================================================================================================
        // 2b. Direct tray sale to a customer (2026-10-18_SeedlingDirectSaleSubmissionToken.sql)
        // =====================================================================================================

        private static SeedlingFulfilmentRepository.DirectTraySaleRequest Sale(int batch, int trays, Guid? token = null, string? customer = "ZZTEST Tray Customer",
            string? contact = "9000000000") => new(batch, trays, customer, contact, "wf1018 address", "wf1018", token ?? Guid.NewGuid());

        [SkippableFact]
        public async Task DirectTraySale_BookingAllocationDispatchRecorded_DeductsOnce_ReplayOnce_ConcurrentNeverOverdraws_Refusals()
        {
            var env = await OpenAsync(WorkflowCorrectionsSchema.TransplantAreaScopeProbeSql, WorkflowCorrectionsSchema.SeedlingSaleTokenProbeSql);
            var u = await UsersAsync(env);
            var v = await NewVarietyAsync(env);
            var sowing = await TransplantedTrayBatchAsync(env, v, 240, ReadyDays);                    // 10 x 24 Cavity
            Assert.True((await env.Get<ReadyConfirmationRepository>().ConfirmAsync(sowing, 10, null, null, null, "wf1018", u.AreaSupervisor, Any)).Success);
            var rs = await env.ScalarAsync<int>("SELECT Id FROM dbo.ReadyStock WHERE SeedSowingId = @S", ("@S", sowing));
            var sales = env.Get<SeedlingFulfilmentRepository>();
            var seller = new SeedlingFulfilmentRepository.Actor("wf1018-seller", u.AreaSupervisor);
            bool OwnArea(int a) => a == MpArea;
            async Task<(decimal Qty, decimal Reserved, decimal Dispatched)> Batch() => (
                await env.ScalarAsync<decimal>("SELECT Quantity FROM dbo.ReadyStock WHERE Id = @R", ("@R", rs)),
                await env.ScalarAsync<decimal>("SELECT ReservedQuantity FROM dbo.ReadyStock WHERE Id = @R", ("@R", rs)),
                await env.ScalarAsync<decimal>("SELECT DispatchedQuantity FROM dbo.ReadyStock WHERE Id = @R", ("@R", rs)));

            // refused, nothing recorded: no customer, no contact, 0 trays, more than available, another Area, a Main Office batch
            async Task Refused(SeedlingFulfilmentRepository.DirectTraySaleRequest r, string expect, Func<int, bool>? access = null)
            {
                var (ok, msg, _) = await sales.DirectTraySaleAsync(r, seller, access ?? OwnArea);
                Assert.False(ok);
                Assert.Contains(expect, msg);
            }
            await Refused(Sale(rs, 1, customer: " "), "Customer name");
            await Refused(Sale(rs, 1, contact: null), "contact");
            await Refused(Sale(rs, 0), "greater than zero");
            await Refused(Sale(rs, 11), "Only 240 plants (10 complete 24 Cavity trays)");
            await Refused(Sale(rs, 1), "not authorized", _ => false);
            var mainBatch = await ApprovedMainBatchAsync(env, v);
            await Refused(Sale(mainBatch, 1), "active Mother Plant Area", _ => true);
            Assert.Equal((240m, 0m, 0m), await Batch());

            // the same form three times at once: one sale, the plants leave the batch once
            var token = Guid.NewGuid();
            var same = await Task.WhenAll(Enumerable.Range(0, 3).Select(_ => sales.DirectTraySaleAsync(Sale(rs, 3, token), seller, OwnArea)));
            Assert.All(same, s => Assert.True(s.Success, s.Message));
            var code = Assert.Single(same.Select(s => s.DispatchCode).Distinct());
            Assert.Equal((240m, 0m, 72m), await Batch());                                                  // reserved back to 0, 72 dispatched
            var dispatchId = await env.ScalarAsync<int>("SELECT Id FROM dbo.SeedlingDispatches WHERE SubmissionToken = @T", ("@T", token));
            var bookingId = await env.ScalarAsync<int>("SELECT BookingId FROM dbo.SeedlingDispatches WHERE Id = @D", ("@D", dispatchId));
            Assert.Equal(("Completed", "ReadyStock", 72, 72m, 0m, "ZZTEST Tray Customer", "9000000000"), (
                await env.ScalarAsync<string>("SELECT Status FROM dbo.Bookings WHERE Id = @B", ("@B", bookingId)),
                await env.ScalarAsync<string>("SELECT FulfilmentSource FROM dbo.Bookings WHERE Id = @B", ("@B", bookingId)),
                await env.ScalarAsync<int>("SELECT Quantity FROM dbo.Bookings WHERE Id = @B", ("@B", bookingId)),
                await env.ScalarAsync<decimal>("SELECT DispatchedQuantity FROM dbo.Bookings WHERE Id = @B", ("@B", bookingId)),
                await env.ScalarAsync<decimal>("SELECT ReservedQuantity FROM dbo.Bookings WHERE Id = @B", ("@B", bookingId)),
                await env.ScalarAsync<string>("SELECT CustomerName FROM dbo.Bookings WHERE Id = @B", ("@B", bookingId)),
                await env.ScalarAsync<string>("SELECT Contact FROM dbo.Bookings WHERE Id = @B", ("@B", bookingId))));
            Assert.Equal(("Closed", 72m), (await env.ScalarAsync<string>("SELECT Status FROM dbo.BookingBatchAllocations WHERE BookingId = @B AND ReadyStockId = @R", ("@B", bookingId), ("@R", rs)),
                await env.ScalarAsync<decimal>("SELECT DispatchedQuantity FROM dbo.BookingBatchAllocations WHERE BookingId = @B", ("@B", bookingId))));
            Assert.Equal(72m, await env.ScalarAsync<decimal>("SELECT SUM(Quantity) FROM dbo.SeedlingDispatchLines WHERE SeedlingDispatchId = @D AND ReadyStockId = @R", ("@D", dispatchId), ("@R", rs)));
            // ledger: Reservation +72, ReservationRelease -72, Dispatch -72 -- the batch's ledger explains it exactly
            Assert.Equal(-72m, await env.ScalarAsync<decimal>("SELECT SUM(Quantity) FROM dbo.ReadyStockTransactions WHERE ReadyStockId = @R AND TransactionType = N'Dispatch' AND ReferenceType = N'SeedlingDispatch' AND ReferenceId = @D", ("@R", rs), ("@D", dispatchId)));
            Assert.Equal(0m, await env.ScalarAsync<decimal>("SELECT ISNULL(SUM(Quantity), 0) FROM dbo.ReadyStockTransactions WHERE ReadyStockId = @R AND TransactionType IN (N'Reservation', N'ReservationRelease')", ("@R", rs)));
            var (q, _, d) = await Batch();
            Assert.Equal(q - d, await env.ScalarAsync<decimal>(@"SELECT SUM(Quantity) FROM dbo.ReadyStockTransactions WHERE ReadyStockId = @R
AND TransactionType NOT IN ('Reservation', 'ReservationRelease', 'TransferHold', 'TransferHoldRelease')", ("@R", rs)));

            // three DIFFERENT forms at once, 4 trays each, 7 left: exactly one succeeds; never below zero
            var race = await Task.WhenAll(Enumerable.Range(0, 3).Select(_ => sales.DirectTraySaleAsync(Sale(rs, 4), seller, OwnArea)));
            Assert.Equal(1, race.Count(r => r.Success));
            Assert.Equal((240m, 0m, 168m), await Batch());
            Assert.All(race.Where(r => !r.Success), r => Assert.Contains("Only 72 plants", r.Message));

            // stock received for Pot Production is never sold as trays
            var potBatch = await ReceivedAsync(env, v, MotherPlantTransferRules.PurposePotProduction, 48, 0);
            await Refused(Sale(potBatch, 1), "Pot Production");
        }

        [SkippableFact]
        public async Task DirectTraySale_WithoutTheTokenMigration_IsRefused_NothingRecorded()
        {
            var env = await OpenAsync();
            Skip.If(await env.ScalarAsync<int>(WorkflowCorrectionsSchema.SeedlingSaleTokenProbeSql) == 1, "2026-10-18_SeedlingDirectSaleSubmissionToken.sql is applied.");
            var any = await env.ScalarAsync<int>("SELECT TOP 1 rs.Id FROM dbo.ReadyStock rs JOIN dbo.Area a ON a.Id = rs.AreaId WHERE a.AreaType = N'MotherPlant' ORDER BY rs.Id");
            Skip.If(any == 0, "No Mother Plant Area Ready Stock in this copy.");
            var dispatches0 = await env.ScalarAsync<int>("SELECT COUNT(*) FROM dbo.SeedlingDispatches");
            var (ok, msg, _) = await env.Get<SeedlingFulfilmentRepository>().DirectTraySaleAsync(Sale(any, 1), new SeedlingFulfilmentRepository.Actor("x", Recorder), Any);
            Assert.Equal((false, SeedlingFulfilmentRepository.DirectSaleNeedsMigrationMessage), (ok, msg));
            Assert.Equal(dispatches0, await env.ScalarAsync<int>("SELECT COUNT(*) FROM dbo.SeedlingDispatches"));
        }

        // =====================================================================================================
        // 3. Bed Sowing tray batches: approved on any date, still only by the designated approver
        // =====================================================================================================

        [SkippableFact]
        public async Task BedSowingBatch_ApprovedBeforeItsReadyDate_ByTheDesignatedApproverOnly_ReadyDateUnchanged_OtherRulesKept()
        {
            var env = await OpenAsync(BedSowingSchema.ProbeSql, WorkflowCorrectionsSchema.BedApproverProbeSql, WorkflowCorrectionsSchema.BedAnyDateProbeSql);
            var approvals = env.Get<ReadyConfirmationRepository>();
            var approver = await approvals.GetApprovalSettingsAsync();
            Assert.True(approver.BedAnyDate);
            Skip.If(approver.Bed.UserId is null, "No active Bed Sowing approver in this copy.");
            var maya = approver.Bed.UserId!.Value;
            var bedPolyhouse = await env.ScalarAsync<int>("SELECT TOP 1 PolyhouseId FROM dbo.BedSowingLocations WHERE IsActive = 1 ORDER BY PolyhouseId");
            var bedArea = await env.ScalarAsync<int>("SELECT AreaId FROM dbo.BedSowingLocations WHERE PolyhouseId = @P", ("@P", bedPolyhouse));
            var other = await env.ScalarAsync<int>(@"SELECT TOP 1 u.Id FROM dbo.UserRoles ur JOIN dbo.Roles r ON r.Id = ur.RoleId JOIN dbo.IMSUsers u ON u.Id = ur.UserId AND u.IsActive = 1
WHERE r.Name = N'Sowing Supervisor' AND u.Id <> @M ORDER BY u.Id", ("@M", maya));
            var admin = await env.ScalarAsync<int>(@"SELECT TOP 1 ur.UserId FROM dbo.UserRoles ur JOIN dbo.Roles r ON r.Id = ur.RoleId JOIN dbo.IMSUsers u ON u.Id = ur.UserId AND u.IsActive = 1
WHERE r.Name = N'System Administrator' AND u.Id <> @M ORDER BY ur.UserId", ("@M", maya));
            Skip.If(bedPolyhouse == 0 || other == 0 || admin == 0, "No bed location, second supervisor or administrator in this copy.");

            var v = await NewVarietyAsync(env, bedArea);
            Assert.True((await env.Get<TrayStockRepository>().AddStockAsync(bedArea, bedPolyhouse, "24 Cavity", 20, DateTime.UtcNow.AddDays(-10),
                Random.Shared.Next(1, int.MaxValue), other, "wf1017-test", "wf1017 bed trays", Any)).Success);
            Assert.True((await env.Get<MaterialStockRepository>().AdjustAsync(bedArea, bedPolyhouse, env.Cocopeat, 1, 100m, "wf1017 bed", Guid.NewGuid(), other, Any,
                LedgerDates.Instant(AppClock.Today.AddDays(-10)).TransactionUtc)).Success);
            var beds = env.Get<BedSowingRepository>();
            var bed = new BedSowing { SpeciesId = v.SpeciesId, SeedStockId = v.SeedStockId, PolyhouseId = bedPolyhouse, Quantity = 240,
                SowingDate = AppClock.Today.AddDays(-5), Remarks = "wf1017 bed", SubmissionToken = Guid.NewGuid() };
            Assert.True((await beds.CreateAsync(bed, new BedSowingRepository.Actor(other, "wf1017-sower", false), Any)).Success);
            var (tOk, tMsg, _, batch, _) = await beds.TransplantAsync(new BedTransplantRequest
            {
                BedSowingId = bed.Id, TargetPolyhouseId = bedPolyhouse, CavityType = "24 Cavity", Quantity = 240, TransplantDate = AppClock.Today,
                GrowingMaterialTypeId = env.Cocopeat, Remarks = "wf1017", SubmissionToken = Guid.NewGuid()
            }, new BedSowingRepository.Actor(maya, "wf1017-maya", false));
            Assert.True(tOk, tMsg);
            var readyDate = await env.ScalarAsync<DateTime>("SELECT ExpectedReadyDate FROM dbo.SeedSowings WHERE Id = @S", ("@S", batch));
            Assert.True(readyDate.Date > AppClock.Today);                                                       // not due yet
            Assert.True(BedSowingRules.ValidateApprovalReadyDate(SeedSowing.SourceBedSowing, readyDate, AppClock.Today, bedAnyDate: true).Ok);

            // still only the designated approver: another supervisor, an administrator, a forged (unknown) batch -- refused
            Assert.Equal(DirectSowingRules.NotBedApproverMessage(approver.Bed.Name), (await approvals.ConfirmAsync(batch, 10, null, null, null, "wf1017", other, Any)).Message);
            Assert.False((await approvals.ConfirmAsync(batch, 10, null, null, null, "wf1017", admin, Any)).Success);
            Assert.False((await approvals.ConfirmAsync(int.MaxValue, 10, null, null, null, "wf1017", maya, Any)).Success);
            var anyRs = await env.ScalarAsync<int>("SELECT TOP 1 Id FROM dbo.ReadyStock ORDER BY Id");
            var ex = await Assert.ThrowsAsync<SqlException>(() => env.ExecAsync(@"
INSERT INTO dbo.ReadyConfirmations (ConfirmationCode, SeedSowingId, ReadyStockId, ActualTrayQuantity, ConfirmedQuantity, WastageQuantity, ApprovedById,
    ConfirmationDate, Status, SupervisorId, CreatedDate, CreatedBy)
VALUES (@C, @S, @R, 10, 240, 0, @U, SYSUTCDATETIME(), 'Confirmed', @U, SYSUTCDATETIME(), N'wf1017')",
                ("@C", "ZZT-" + Guid.NewGuid().ToString("N")[..10]), ("@S", batch), ("@R", anyRs), ("@U", other)));
            Assert.Contains("designated Bed Sowing approver", ex.Message);
            Assert.Equal(0, await env.ScalarAsync<int>("SELECT COUNT(*) FROM dbo.ReadyConfirmations WHERE SeedSowingId = @S", ("@S", batch)));

            // Maya approves it today -- before its Ready Date -- once; the Ready Date stays as recorded
            var (ok, msg, _) = await approvals.ConfirmAsync(batch, 10, null, null, null, "wf1017", maya, Any);
            Assert.True(ok, msg);
            Assert.Equal(readyDate, await env.ScalarAsync<DateTime>("SELECT ExpectedReadyDate FROM dbo.SeedSowings WHERE Id = @S", ("@S", batch)));
            Assert.Equal(240m, await env.ScalarAsync<decimal>("SELECT Quantity FROM dbo.ReadyStock WHERE SeedSowingId = @S", ("@S", batch)));
            Assert.False((await approvals.ConfirmAsync(batch, 1, null, null, null, "wf1017", maya, Any)).Success);   // no second approval

            // a Transplant tray batch keeps its Ready Date rule (and a Main Office user is not its Area Supervisor)
            Assert.False(BedSowingRules.ValidateApprovalReadyDate(SeedSowing.SourceTransplant, AppClock.Today.AddDays(1), AppClock.Today, bedAnyDate: true).Ok);
        }
    }
}
