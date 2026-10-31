using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using PlantStockManager.Authorization;
using PlantStockManager.Data;
using PlantStockManager.Models;
using PlantStockManager.Pages.Production.SeedSowing;
using PlantStockManager.Services;

namespace PlantStockManager.Tests
{
    // 2026-10-31: the REAL Cutting Tray Sowing -> Supervisor Approval -> Ready Stock code for a quantity under one
    // complete tray (62 cuttings at 150 Cavity), on a SCRATCH COPY that has 2026-10-31_CuttingPartialTraySowing.sql.
    // Runs only when PSM_SCRATCH_CONNECTION names a PlantsIMS2_Scratch_* database (refuses any other); SKIPPED otherwise,
    // and when the migration is not on the copy. Its rows are discovered from the copy (an active Mother Plant Area with a
    // Polyhouse and a cutting-sowing supervisor, a variety with growing days); a Mother Plant Area uses no Cocopeat / Pitmos.
    [Collection("ScratchDb")]
    public partial class PartialTrayCuttingSowingE2ETests
    {
        private const string ConnectionVariable = "PSM_SCRATCH_CONNECTION";
        private const string RequiredPrefix = "PlantsIMS2_Scratch_";
        private const string C150 = "150 Cavity";

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
            public int Area { get; set; }
            public int Polyhouse { get; set; }
            public int Supervisor { get; set; }
            public int OtherUser { get; set; }
            public int Species { get; set; }
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

            // (re)creates this test's (variety, Area) Cutting Stock pool with a clean ledger -- scratch copy only
            public async Task<int> ResetPoolAsync(decimal physical)
            {
                await ExecAsync(@"
IF NOT EXISTS (SELECT 1 FROM dbo.CuttingStock WHERE SpeciesId = @S AND AreaId = @A)
    INSERT INTO dbo.CuttingStock (SpeciesId, AreaId, PhysicalQuantity, CreatedDate, CreatedBy) VALUES (@S, @A, 0, SYSUTCDATETIME(), N'e2e-partial-tray');
UPDATE dbo.CuttingStock SET InTransitQuantity = 0, PhysicalQuantity = @P WHERE SpeciesId = @S AND AreaId = @A", ("@S", Species), ("@A", Area), ("@P", physical));
                var id = await ScalarAsync<int>("SELECT Id FROM dbo.CuttingStock WHERE SpeciesId = @S AND AreaId = @A", ("@S", Species), ("@A", Area));
                await ExecAsync("DELETE FROM dbo.CuttingStockTransactions WHERE CuttingStockId = @P", ("@P", id));
                return id;
            }

            public Task<decimal> PoolAsync(int pool) => ScalarAsync<decimal>("SELECT PhysicalQuantity FROM dbo.CuttingStock WHERE Id = @P", ("@P", pool));
            public Task<decimal> TraysAsync(string cavity)
                => ScalarAsync<decimal>("SELECT ISNULL(SUM(PhysicalQuantity), 0) FROM dbo.TrayStock WHERE AreaId = @A AND TraySize = @C", ("@A", Area), ("@C", cavity));
            public Task<decimal> TrayLedgerAsync(string referenceType, int referenceId)
                => ScalarAsync<decimal>("SELECT ISNULL(SUM(Quantity), 0) FROM dbo.TrayStockTransactions WHERE ReferenceType = @T AND ReferenceId = @R", ("@T", referenceType), ("@R", referenceId));
            public Task<decimal> CuttingLedgerAsync(int pool, string type, string referenceType, int referenceId)
                => ScalarAsync<decimal>(@"SELECT ISNULL(SUM(Quantity), 0) FROM dbo.CuttingStockTransactions
                    WHERE CuttingStockId = @P AND TransactionType = @Ty AND ReferenceType = @T AND ReferenceId = @R", ("@P", pool), ("@Ty", type), ("@T", referenceType), ("@R", referenceId));
            public Task<int> CuttingLedgerRowsAsync(int pool) => ScalarAsync<int>("SELECT COUNT(*) FROM dbo.CuttingStockTransactions WHERE CuttingStockId = @P", ("@P", pool));
            public Task<decimal> ReadyStockAsync(int sowing)
                => ScalarAsync<decimal>("SELECT ISNULL((SELECT Quantity FROM dbo.ReadyStock WHERE SeedSowingId = @S), 0)", ("@S", sowing));
            public Task<int> ApprovalsAsync(int sowing, string status = "Confirmed")
                => ScalarAsync<int>("SELECT COUNT(*) FROM dbo.ReadyConfirmations WHERE SeedSowingId = @S AND Status = @St", ("@S", sowing), ("@St", status));
        }

        private static async Task<Env> OpenAsync(bool migrated = true)
        {
            var cs = Environment.GetEnvironmentVariable(ConnectionVariable);
            Skip.If(string.IsNullOrWhiteSpace(cs), $"Set {ConnectionVariable} to a {RequiredPrefix}* database connection string to run the end-to-end tests.");
            using (var conn = new SqlConnection(cs))
            {
                await conn.OpenAsync();
                var name = (string)(await new SqlCommand("SELECT DB_NAME()", conn).ExecuteScalarAsync())!;
                if (!name.StartsWith(RequiredPrefix, StringComparison.Ordinal))
                    throw new InvalidOperationException($"Refusing to run end-to-end tests against '{name}'. Only databases named {RequiredPrefix}* are allowed.");
                var applied = (int)(await new SqlCommand(WorkflowCorrectionsSchema.CuttingPartialTrayProbeSql, conn).ExecuteScalarAsync())! == 1;
                Skip.If(migrated && !applied, "2026-10-31_CuttingPartialTraySowing.sql is not applied to this scratch copy.");
                Skip.If(!migrated && applied, "This test needs a scratch copy WITHOUT 2026-10-31_CuttingPartialTraySowing.sql.");
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

            // an active Mother Plant Area with a Polyhouse and a user who may supervise cutting sowings there
            var candidates = new List<(int Area, int Polyhouse)>();
            using (var conn = new SqlConnection(cs))
            {
                await conn.OpenAsync();
                using var cmd = new SqlCommand(@"SELECT a.Id, MIN(p.Id) FROM dbo.Area a JOIN dbo.Polyhouses p ON p.AreaId = a.Id
                    WHERE a.AreaType = N'MotherPlant' AND a.IsActive = 1 GROUP BY a.Id ORDER BY a.Id", conn);
                using var r = await cmd.ExecuteReaderAsync();
                while (await r.ReadAsync()) candidates.Add((r.GetInt32(0), r.GetInt32(1)));
            }
            foreach (var (area, polyhouse) in candidates)
            {
                var supervisors = await env.Get<UserRoleRepository>().GetCuttingSowingSupervisorsAsync(area);
                if (supervisors.Count == 0) continue;
                env.Area = area; env.Polyhouse = polyhouse; env.Supervisor = supervisors[0].EmployeeID;
                break;
            }
            Skip.If(env.Area == 0, "No active Mother Plant Area with a Polyhouse and a cutting-sowing supervisor on this copy.");
            env.OtherUser = await env.ScalarAsync<int>("SELECT TOP 1 Id FROM dbo.IMSUsers WHERE IsActive = 1 AND Id <> @U ORDER BY Id", ("@U", env.Supervisor));
            env.Species = await env.ScalarAsync<int>("SELECT TOP 1 Id FROM dbo.PlantSpecies WHERE ReadyStockDays > 0 AND IsActive = 1 ORDER BY Id DESC");
            Skip.If(env.Species == 0, "No variety with growing days on this copy.");

            // empty 150-cavity trays in this Area (scratch copy only); balances are compared before/after, never absolute
            if (await env.TraysAsync(C150) < 50)
            {
                var (ok, msg, _, _) = await env.Get<TrayStockRepository>().AddStockAsync(env.Area, env.Polyhouse, C150, 200, DateTime.UtcNow,
                    Random.Shared.Next(1, int.MaxValue), env.Supervisor, "e2e-partial-tray", "partial-tray test trays", _ => true);
                Assert.True(ok, msg);
            }
            return env;
        }

        private static SeedSowing Entry(Env env, int pool, decimal cuttings, string cavity = C150, Guid? token = null) => new()
        {
            SourceCuttingStockId = pool, SeedQuantity = cuttings, CavityType = cavity, SowingDate = AppClock.Today,
            AreaId = env.Area, PolyhouseId = env.Polyhouse, SupervisorId = env.Supervisor,
            CreatedBy = "e2e-partial-tray", CreatedById = env.Supervisor, SubmissionToken = token ?? Guid.NewGuid()
        };

        private static Task<(bool Success, string? Message, int Id)> SowAsync(Env env, SeedSowing entry, Func<int, bool>? canAccess = null)
            => env.Get<SeedSowingRepository>().InsertFromCuttingAsync(entry, env.Supervisor, canAccess ?? (_ => true), _ => true);

        private static Task<(bool Success, string? Message, int Id)> ApproveAsync(Env env, int sowing, decimal trays, int? userId = null)
            => env.Get<ReadyConfirmationRepository>().ConfirmAsync(sowing, trays, null, null, "e2e", "e2e-approver", userId ?? env.Supervisor, _ => true);

        // ---- 1. 62 cuttings / 150 cavity -----------------------------------

        [SkippableFact]
        public async Task Cuttings62_WarningShown_Sown62_OneTray_Approved62_NoWastage()
        {
            var env = await OpenAsync();
            var pool = await env.ResetPoolAsync(500);
            var traysBefore = await env.TraysAsync(C150);

            // the page's live preview: OK (the button stays usable) with the yellow warning, not an error
            var page = new CreateFromCuttingModel(env.Get<SeedSowingRepository>(), env.Get<CuttingStockRepository>(), env.Get<UserRoleRepository>(),
                env.Get<PolyhouseRepository>(), env.Get<InternalTransferRepository>(), env.Get<AreaAccessService>(), env.Get<MaterialStockRepository>(),
                env.Get<TrayStockRepository>());
            var preview = JsonSerializer.SerializeToElement(((JsonResult)await page.OnGetTrayCalculationAsync(62, C150)).Value);
            Assert.True(preview.GetProperty("ok").GetBoolean());
            Assert.Equal(1, preview.GetProperty("trays").GetInt32());
            Assert.Equal(62m, preview.GetProperty("used").GetDecimal());
            Assert.Equal(PartialTrayRules.SowingWarning(62, C150), preview.GetProperty("warning").GetString());
            Assert.Equal(JsonValueKind.Null, preview.GetProperty("error").ValueKind);

            var (ok, message, sowing) = await SowAsync(env, Entry(env, pool, 62));
            Assert.True(ok, message);
            Assert.Equal(1, await env.ScalarAsync<int>("SELECT NumberOfTrays FROM dbo.SeedSowings WHERE Id = @S", ("@S", sowing)));
            Assert.Equal(62m, await env.ScalarAsync<decimal>("SELECT QuantitySown FROM dbo.SeedSowings WHERE Id = @S", ("@S", sowing)));
            Assert.Equal(62m, await env.ScalarAsync<decimal>("SELECT SeedQuantity FROM dbo.SeedSowings WHERE Id = @S", ("@S", sowing)));
            Assert.Equal(438m, await env.PoolAsync(pool));                                                    // 500 - 62, never - 150
            Assert.Equal(-62m, await env.CuttingLedgerAsync(pool, "Sown", "SeedSowing", sowing));
            Assert.Equal(0m, await env.CuttingLedgerAsync(pool, "Wastage", "SeedSowing", sowing));           // nothing left over to waste
            Assert.Equal(1, await env.CuttingLedgerRowsAsync(pool));
            Assert.Equal(-1m, await env.TrayLedgerAsync("SeedSowing", sowing));                              // exactly one tray
            Assert.Equal(traysBefore - 1, await env.TraysAsync(C150));

            var (aOk, aMessage, approval) = await ApproveAsync(env, sowing, 1);
            Assert.True(aOk, aMessage);
            Assert.Equal(62m, await env.ScalarAsync<decimal>("SELECT ConfirmedQuantity FROM dbo.ReadyConfirmations WHERE Id = @C", ("@C", approval)));
            Assert.Equal(0m, await env.ScalarAsync<decimal>("SELECT WastageQuantity FROM dbo.ReadyConfirmations WHERE Id = @C", ("@C", approval)));
            Assert.Equal(62m, await env.ReadyStockAsync(sowing));
            Assert.Equal("Completed", await env.ScalarAsync<string>("SELECT Status FROM dbo.SeedSowings WHERE Id = @S", ("@S", sowing)));
            Assert.Equal(438m, await env.PoolAsync(pool));                                                    // no extra cuttings
            Assert.Equal(0m, await env.TrayLedgerAsync("ReadyConfirmation", approval));                      // no extra trays
            Assert.Equal(traysBefore - 1, await env.TraysAsync(C150));

            // the batch is one tray of its 62 plants
            var batch = (await env.Get<ReadyStockRepository>().GetByIdAsync(
                await env.ScalarAsync<int>("SELECT Id FROM dbo.ReadyStock WHERE SeedSowingId = @S", ("@S", sowing))))!;
            Assert.True(batch.IsPartialTrayBatch);
            Assert.Equal(62, batch.TraySize);
            Assert.Equal(1, AreaReadyStockActionRules.AvailableCompleteTrays(batch));
        }

        [SkippableFact]
        public async Task Cuttings62_ApprovingMoreThanOneTray_IsRefused_NothingChanges()
        {
            var env = await OpenAsync();
            var pool = await env.ResetPoolAsync(500);
            var (ok, message, sowing) = await SowAsync(env, Entry(env, pool, 62));
            Assert.True(ok, message);
            var (aOk, aMessage, _) = await ApproveAsync(env, sowing, 2);
            Assert.False(aOk);
            Assert.Equal(PartialTrayRules.ApprovalTrayMessage(62, C150), aMessage);
            Assert.Equal(0, await env.ApprovalsAsync(sowing));
            Assert.Equal(0m, await env.ReadyStockAsync(sowing));
            Assert.Equal(438m, await env.PoolAsync(pool));
        }

        // Without the migration the database still refuses the shape, so the application refuses it first -- unchanged.
        [SkippableFact]
        public async Task WithoutTheMigration_62IsRefusedAsBefore_NothingChanges()
        {
            var env = await OpenAsync(migrated: false);
            var pool = await env.ResetPoolAsync(500);
            var traysBefore = await env.TraysAsync(C150);
            var (ok, message, _) = await SowAsync(env, Entry(env, pool, 62));
            Assert.False(ok);
            Assert.Equal("Cutting Quantity (62) is less than one complete 150-cavity tray.", message);
            Assert.Equal(500m, await env.PoolAsync(pool));
            Assert.Equal(0, await env.CuttingLedgerRowsAsync(pool));
            Assert.Equal(traysBefore, await env.TraysAsync(C150));
        }

        // ---- 2 + 3. full trays: unchanged ----------------------------------

        [SkippableTheory]
        [InlineData(150, 1)]
        [InlineData(300, 2)]
        public async Task FullTrays_NoWarning_NormalDeductionAndApproval(int cuttings, int trays)
        {
            var env = await OpenAsync();
            var pool = await env.ResetPoolAsync(1000);
            var traysBefore = await env.TraysAsync(C150);
            var page = new CreateFromCuttingModel(env.Get<SeedSowingRepository>(), env.Get<CuttingStockRepository>(), env.Get<UserRoleRepository>(),
                env.Get<PolyhouseRepository>(), env.Get<InternalTransferRepository>(), env.Get<AreaAccessService>(), env.Get<MaterialStockRepository>(),
                env.Get<TrayStockRepository>());
            var preview = JsonSerializer.SerializeToElement(((JsonResult)await page.OnGetTrayCalculationAsync(cuttings, C150)).Value);
            Assert.Equal(JsonValueKind.Null, preview.GetProperty("warning").ValueKind);

            var (ok, message, sowing) = await SowAsync(env, Entry(env, pool, cuttings));
            Assert.True(ok, message);
            Assert.Equal(trays, await env.ScalarAsync<int>("SELECT NumberOfTrays FROM dbo.SeedSowings WHERE Id = @S", ("@S", sowing)));
            Assert.Equal(1000m - cuttings, await env.PoolAsync(pool));
            Assert.Equal(-trays, await env.TrayLedgerAsync("SeedSowing", sowing));
            Assert.Equal(traysBefore - trays, await env.TraysAsync(C150));

            var (aOk, aMessage, _) = await ApproveAsync(env, sowing, trays);
            Assert.True(aOk, aMessage);
            Assert.Equal((decimal)cuttings, await env.ReadyStockAsync(sowing));
            Assert.Equal(1000m - cuttings, await env.PoolAsync(pool));
        }

        // ---- 4 + 5. invalid quantity / not enough stock --------------------

        [SkippableTheory]
        [InlineData(0)]
        [InlineData(-5)]
        public async Task ZeroOrNegative_IsRefused_NothingChanges(int cuttings)
        {
            var env = await OpenAsync();
            var pool = await env.ResetPoolAsync(500);
            var sowings = await env.ScalarAsync<int>("SELECT COUNT(*) FROM dbo.SeedSowings");
            var (ok, message, _) = await SowAsync(env, Entry(env, pool, cuttings));
            Assert.False(ok);
            Assert.Equal("Cutting Quantity must be greater than zero.", message);
            Assert.Equal(sowings, await env.ScalarAsync<int>("SELECT COUNT(*) FROM dbo.SeedSowings"));
            Assert.Equal(500m, await env.PoolAsync(pool));
        }

        [SkippableFact]
        public async Task InsufficientCuttingStock_IsRefused_NothingChanges()
        {
            var env = await OpenAsync();
            var pool = await env.ResetPoolAsync(40);
            var traysBefore = await env.TraysAsync(C150);
            var (ok, message, _) = await SowAsync(env, Entry(env, pool, 62));
            Assert.False(ok);
            Assert.Equal("Insufficient Cutting Stock (available 40, requested 62).", message);
            Assert.Equal(40m, await env.PoolAsync(pool));
            Assert.Equal(0, await env.CuttingLedgerRowsAsync(pool));
            Assert.Equal(traysBefore, await env.TraysAsync(C150));
        }

        // The cuttings ledger row is written BEFORE the trays are taken, so this also proves the whole sowing rolls back.
        [SkippableFact]
        public async Task NoEmptyTray_IsRefused_AndTheCuttingDeductionRollsBack()
        {
            var env = await OpenAsync();
            const string c42 = "42 Cavity";
            Skip.IfNot(await env.TraysAsync(c42) == 0, "This copy has 42-cavity trays in the test Area.");
            var pool = await env.ResetPoolAsync(500);
            var sowings = await env.ScalarAsync<int>("SELECT COUNT(*) FROM dbo.SeedSowings");
            var (ok, message, _) = await SowAsync(env, Entry(env, pool, 30, c42));   // a partial 42-cavity tray
            Assert.False(ok, "sowing without an empty tray must be refused");
            Assert.False(string.IsNullOrWhiteSpace(message));
            Assert.Equal(500m, await env.PoolAsync(pool));
            Assert.Equal(0, await env.CuttingLedgerRowsAsync(pool));
            Assert.Equal(sowings, await env.ScalarAsync<int>("SELECT COUNT(*) FROM dbo.SeedSowings"));
        }

        // ---- 6. unauthorized -----------------------------------------------

        [SkippableFact]
        public async Task Unauthorized_SowingAndApproval_AreRefused_NothingChanges()
        {
            var env = await OpenAsync();
            var pool = await env.ResetPoolAsync(500);
            var (denied, deniedMessage, _) = await SowAsync(env, Entry(env, pool, 62), canAccess: _ => false);
            Assert.False(denied);
            Assert.Equal("You are not authorized to sow in this Area.", deniedMessage);
            Assert.Equal(500m, await env.PoolAsync(pool));

            var (ok, message, sowing) = await SowAsync(env, Entry(env, pool, 62));
            Assert.True(ok, message);
            var (aOk, aMessage, _) = await ApproveAsync(env, sowing, 1, userId: env.OtherUser);   // not the assigned supervisor
            Assert.False(aOk);
            Assert.False(string.IsNullOrWhiteSpace(aMessage));
            Assert.Equal(0, await env.ApprovalsAsync(sowing));
            Assert.Equal(0m, await env.ReadyStockAsync(sowing));
            Assert.Equal("Sown", await env.ScalarAsync<string>("SELECT Status FROM dbo.SeedSowings WHERE Id = @S", ("@S", sowing)));
        }

        // ---- 7. a database failure inside the approval ---------------------

        [SkippableFact]
        public async Task DatabaseFailureDuringApproval_RollsEverythingBack()
        {
            var env = await OpenAsync();
            var pool = await env.ResetPoolAsync(500);
            var (ok, message, sowing) = await SowAsync(env, Entry(env, pool, 62));
            Assert.True(ok, message);
            // scratch copy only: a trigger that fails the Ready Stock ledger write of THIS sowing's approval -- after the
            // approval row and the Ready Stock row were already written in the same transaction
            await env.ExecAsync($@"EXEC (N'CREATE TRIGGER dbo.TR_ZZTEST_PartialTrayFailure ON dbo.ReadyStockTransactions AFTER INSERT AS
BEGIN
    IF EXISTS (SELECT 1 FROM inserted i JOIN dbo.ReadyStock rs ON rs.Id = i.ReadyStockId WHERE rs.SeedSowingId = {sowing})
        THROW 59031, ''e2e: simulated failure'', 1;
END')");
            try
            {
                var (aOk, _, _) = await ApproveAsync(env, sowing, 1);
                Assert.False(aOk);
            }
            finally
            {
                await env.ExecAsync("DROP TRIGGER IF EXISTS dbo.TR_ZZTEST_PartialTrayFailure");
            }
            Assert.Equal(0, await env.ScalarAsync<int>("SELECT COUNT(*) FROM dbo.ReadyConfirmations WHERE SeedSowingId = @S", ("@S", sowing)));
            Assert.Equal(0m, await env.ReadyStockAsync(sowing));
            Assert.Equal("Sown", await env.ScalarAsync<string>("SELECT Status FROM dbo.SeedSowings WHERE Id = @S", ("@S", sowing)));
            Assert.Equal(0m, await env.ScalarAsync<decimal>("SELECT ConfirmedReadyQuantity FROM dbo.SeedSowings WHERE Id = @S", ("@S", sowing)));
            Assert.Equal(438m, await env.PoolAsync(pool));

            // and once the failure is gone the same approval succeeds normally
            var (retryOk, retryMessage, _) = await ApproveAsync(env, sowing, 1);
            Assert.True(retryOk, retryMessage);
            Assert.Equal(62m, await env.ReadyStockAsync(sowing));
        }

        // ---- 8. repeated / concurrent --------------------------------------

        [SkippableFact]
        public async Task RepeatedSowingSubmit_DeductsOnce()
        {
            var env = await OpenAsync();
            var pool = await env.ResetPoolAsync(500);
            var token = Guid.NewGuid();
            var first = Entry(env, pool, 62, token: token);
            var (ok1, m1, id1) = await SowAsync(env, first);
            var second = Entry(env, pool, 62, token: token);
            var (ok2, m2, id2) = await SowAsync(env, second);
            Assert.True(ok1, m1);
            Assert.True(ok2, m2);
            Assert.True(second.IsDuplicateSubmission);
            Assert.Equal(id1, second.Id);
            Assert.Equal(1, await env.ScalarAsync<int>("SELECT COUNT(*) FROM dbo.SeedSowings WHERE SubmissionToken = @T", ("@T", token)));
            Assert.Equal(438m, await env.PoolAsync(pool));
            Assert.Equal(1, await env.CuttingLedgerRowsAsync(pool));
            Assert.Equal(-1m, await env.TrayLedgerAsync("SeedSowing", id1));
        }

        [SkippableFact]
        public async Task ConcurrentApprovals_OnlyOneSucceeds_ReadyStockAddedOnce()
        {
            var env = await OpenAsync();
            var pool = await env.ResetPoolAsync(500);
            var (ok, message, sowing) = await SowAsync(env, Entry(env, pool, 62));
            Assert.True(ok, message);
            var results = await Task.WhenAll(ApproveAsync(env, sowing, 1), ApproveAsync(env, sowing, 1), ApproveAsync(env, sowing, 1));
            Assert.Equal(1, results.Count(r => r.Success));
            Assert.Equal(1, await env.ApprovalsAsync(sowing));
            Assert.Equal(62m, await env.ReadyStockAsync(sowing));
            Assert.Equal(62m, await env.ScalarAsync<decimal>(@"SELECT ISNULL(SUM(t.Quantity), 0) FROM dbo.ReadyStockTransactions t
                JOIN dbo.ReadyStock rs ON rs.Id = t.ReadyStockId WHERE rs.SeedSowingId = @S", ("@S", sowing)));
            Assert.Equal(438m, await env.PoolAsync(pool));
        }

        // ---- cancel round trip and the whole-batch sale --------------------

        [SkippableFact]
        public async Task CancelApprovalThenSowing_ReturnsExactly62CuttingsAndOneTray()
        {
            var env = await OpenAsync();
            var pool = await env.ResetPoolAsync(500);
            var traysBefore = await env.TraysAsync(C150);
            var (ok, message, sowing) = await SowAsync(env, Entry(env, pool, 62));
            Assert.True(ok, message);
            var (aOk, aMessage, approval) = await ApproveAsync(env, sowing, 1);
            Assert.True(aOk, aMessage);

            var (cOk, cMessage) = await env.Get<ReadyConfirmationRepository>().CancelAsync(approval, "e2e", env.Supervisor);
            Assert.True(cOk, cMessage);
            Assert.Equal(0m, await env.ReadyStockAsync(sowing));
            var (sOk, sMessage) = await env.Get<SeedSowingRepository>().CancelAsync(sowing, "e2e", env.Supervisor);
            Assert.True(sOk, sMessage);
            Assert.Equal(500m, await env.PoolAsync(pool));
            Assert.Equal(traysBefore, await env.TraysAsync(C150));
        }

        [SkippableFact]
        public async Task PartialBatch_IsSoldOnlyAsAWhole_OneTrayIs62Plants()
        {
            var env = await OpenAsync();
            var pool = await env.ResetPoolAsync(500);
            var (ok, message, sowing) = await SowAsync(env, Entry(env, pool, 62));
            Assert.True(ok, message);
            var (aOk, aMessage, _) = await ApproveAsync(env, sowing, 1);
            Assert.True(aOk, aMessage);
            var batchId = await env.ScalarAsync<int>("SELECT Id FROM dbo.ReadyStock WHERE SeedSowingId = @S", ("@S", sowing));
            var repo = env.Get<SeedlingFulfilmentRepository>();
            var actor = new SeedlingFulfilmentRepository.Actor("e2e", env.Supervisor);

            var (twoOk, twoMessage, _) = await repo.DirectTraySaleAsync(
                new SeedlingFulfilmentRepository.DirectTraySaleRequest(batchId, 2, "E2E Customer", "9999999999", null, null, Guid.NewGuid()), actor, _ => true);
            Assert.False(twoOk);
            Assert.Equal(PartialTrayRules.WholeBatchMessage(62, C150), twoMessage);

            var (sold, soldMessage, _) = await repo.DirectTraySaleAsync(
                new SeedlingFulfilmentRepository.DirectTraySaleRequest(batchId, 1, "E2E Customer", "9999999999", null, null, Guid.NewGuid()), actor, _ => true);
            Assert.True(sold, soldMessage);
            Assert.Equal(62m, await env.ScalarAsync<decimal>("SELECT DispatchedQuantity FROM dbo.ReadyStock WHERE Id = @B", ("@B", batchId)));
            Assert.Equal(0m, await env.ScalarAsync<decimal>("SELECT AvailableQuantity FROM dbo.ReadyStock WHERE Id = @B", ("@B", batchId)));

            var (again, _, _) = await repo.DirectTraySaleAsync(
                new SeedlingFulfilmentRepository.DirectTraySaleRequest(batchId, 1, "E2E Customer", "9999999999", null, null, Guid.NewGuid()), actor, _ => true);
            Assert.False(again);
        }
    }
}
