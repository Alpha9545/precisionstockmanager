using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using PlantStockManager.Authorization;
using PlantStockManager.Data;
using PlantStockManager.Models;
using PlantStockManager.Pages.Production.ReadyConfirmation;

namespace PlantStockManager.Tests
{
    // CORRECTION #2 -- end-to-end test of the REAL Supervisor Approval code (page model + repositories)
    // for Cutting Tray Sowing against a SCRATCH COPY of the test database. Same safety rules as
    // CuttingEntryDestinationE2ETests: runs only when PSM_SCRATCH_CONNECTION points at a database named
    // PlantsIMS2_Scratch_*, refuses every other database (PlantsIMS2_Test, PlantsIMS2, PlantsIMS...) and is
    // SKIPPED otherwise. The two E2E classes share one collection, so they never run at the same time.
    //
    // UNITS (used throughout): the supervisor enters TRAYS; every stock figure is in CUTTINGS.
    // Cutting Stock arithmetic (24-cavity trays; a sowing of 480 cuttings = 20 trays):
    //   the sowing's own 480 CUTTINGS leave the pool when it is recorded;
    //   ready trays above 20 take only (ready trays - 20) x 24 extra CUTTINGS, and the TOTAL ready cuttings
    //   (ready trays x 24) may not be more than the pool's Available stock (Physical - In-Transit, in cuttings)
    //   at that moment. E.g. 25 ready trays = 600 cuttings = 5 extra trays = 120 extra cuttings.
    [Collection("ScratchDb")]
    public class CuttingSowingOverageE2ETests
    {
        private const string ConnectionVariable = "PSM_SCRATCH_CONNECTION";
        private const string RequiredPrefix = "PlantsIMS2_Scratch_";

        // real rows of the copy: Mother Plant 114 (Area 1, species 3144) and its Area 1 users
        private const int MpA = 114, SupA = 115, AreaA = 1;   // SupA = an active Area 1 user who may approve sowings
        private const int SupB = 101, AreaB = 122;             // an Area 122 user (another Area)
        private const int Recorder = 21;                       // records sowings; NOT the assigned supervisor
        private const int Cavity = 24;

        private sealed class StaticMonitor<T> : IOptionsMonitor<T>
        {
            public StaticMonitor(T value) => CurrentValue = value;
            public T CurrentValue { get; }
            public T Get(string? name) => CurrentValue;
            public IDisposable? OnChange(Action<T, string?> listener) => null;
        }

        private sealed class NullTempData : ITempDataProvider
        {
            public IDictionary<string, object> LoadTempData(HttpContext context) => new Dictionary<string, object>();
            public void SaveTempData(HttpContext context, IDictionary<string, object> values) { }
        }

        private sealed class Env
        {
            public string ConnectionString { get; init; } = "";
            public ServiceProvider Provider { get; init; } = null!;
            public int Species { get; init; }
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

            // (re)creates the (species, Area) pool with the given stock -- scratch copy only
            public async Task<int> ResetPoolAsync(int area, decimal physical, decimal inTransit, int? species = null)
            {
                var s = species ?? Species;
                await ExecAsync(@"
IF NOT EXISTS (SELECT 1 FROM dbo.CuttingStock WHERE SpeciesId = @S AND AreaId = @A)
    INSERT INTO dbo.CuttingStock (SpeciesId, AreaId, PhysicalQuantity, CreatedDate, CreatedBy) VALUES (@S, @A, 0, SYSUTCDATETIME(), N'e2e-test');
UPDATE dbo.CuttingStock SET InTransitQuantity = 0 WHERE SpeciesId = @S AND AreaId = @A;
UPDATE dbo.CuttingStock SET PhysicalQuantity = @P WHERE SpeciesId = @S AND AreaId = @A;
UPDATE dbo.CuttingStock SET InTransitQuantity = @T WHERE SpeciesId = @S AND AreaId = @A", ("@S", s), ("@A", area), ("@P", physical), ("@T", inTransit));
                var id = await ScalarAsync<int>("SELECT Id FROM dbo.CuttingStock WHERE SpeciesId = @S AND AreaId = @A", ("@S", s), ("@A", area));
                // each test starts this pool with a clean ledger (scratch copy only), so ledger counts are per test
                await ExecAsync("DELETE FROM dbo.CuttingStockTransactions WHERE CuttingStockId = @P", ("@P", id));
                return id;
            }

            public async Task<(decimal Physical, decimal InTransit, decimal Available)> PoolAsync(int poolId)
            {
                using var conn = new SqlConnection(ConnectionString);
                await conn.OpenAsync();
                using var cmd = new SqlCommand("SELECT PhysicalQuantity, InTransitQuantity, AvailableQuantity FROM dbo.CuttingStock WHERE Id = @I", conn);
                cmd.Parameters.AddWithValue("@I", poolId);
                using var r = await cmd.ExecuteReaderAsync();
                await r.ReadAsync();
                return (r.GetDecimal(0), r.GetDecimal(1), r.GetDecimal(2));
            }

            // every pool except the given one, as one comparable string
            public Task<string> OtherPoolsAsync(int poolId)
                => ScalarAsync<string>("SELECT CONCAT(ISNULL(SUM(PhysicalQuantity), 0), '/', ISNULL(SUM(InTransitQuantity), 0), '/', COUNT(*)) FROM dbo.CuttingStock WHERE Id <> @I", ("@I", poolId));

            public Task<int> ApprovalLedgerRowsAsync(int poolId, int? confirmationId = null)
                => ScalarAsync<int>("SELECT COUNT(*) FROM dbo.CuttingStockTransactions WHERE CuttingStockId = @P AND ReferenceType = N'ReadyConfirmation' AND (@C IS NULL OR ReferenceId = @C)", ("@P", poolId), ("@C", confirmationId));

            public Task<decimal> ReadyStockAsync(int sowingId)
                => ScalarAsync<decimal>("SELECT ISNULL((SELECT Quantity FROM dbo.ReadyStock WHERE SeedSowingId = @S), 0)", ("@S", sowingId));

            // a fingerprint of everything that existed before these tests (historical approvals and the two open cutting sowings)
            public Task<string> HistoryAsync()
                => ScalarAsync<string>(@"SELECT CONCAT((SELECT CHECKSUM_AGG(CHECKSUM(*)) FROM dbo.ReadyConfirmations WHERE Id <= 393), '/',
                                                      (SELECT CHECKSUM_AGG(CHECKSUM(*)) FROM dbo.SeedSowings WHERE Id IN (3, 234, 634, 645, 653, 936, 937, 938, 939)))");
        }

        private static async Task<Env> OpenAsync()
        {
            var cs = Environment.GetEnvironmentVariable(ConnectionVariable);
            Skip.If(string.IsNullOrWhiteSpace(cs), $"Set {ConnectionVariable} to a {RequiredPrefix}* database connection string to run the end-to-end tests.");

            string name;
            using (var conn = new SqlConnection(cs))
            {
                await conn.OpenAsync();
                using var cmd = new SqlCommand("SELECT DB_NAME()", conn);
                name = (string)(await cmd.ExecuteScalarAsync())!;
            }
            if (!name.StartsWith(RequiredPrefix, StringComparison.Ordinal))
                throw new InvalidOperationException($"Refusing to run end-to-end tests against '{name}'. Only databases named {RequiredPrefix}* are allowed.");

            var services = new ServiceCollection();
            services.AddSingleton<IConfiguration>(new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:DefaultConnection"] = cs }).Build());
            services.AddSingleton<DatabaseHelper>();
            services.AddSingleton<AreaAccessService>();
            services.AddSingleton<IOptions<SecurityOptions>>(Options.Create(new SecurityOptions()));
            services.AddSingleton<IOptionsMonitor<SeedlingWorkflowOptions>>(new StaticMonitor<SeedlingWorkflowOptions>(new SeedlingWorkflowOptions()));   // Area scope ON
            services.AddSingleton<SeedlingAreaScope>();
            foreach (var t in typeof(DatabaseHelper).Assembly.GetTypes().Where(t => t.Namespace == "PlantStockManager.Data" && t.IsClass && t.Name.EndsWith("Repository")))
                services.AddScoped(t);
            var provider = services.BuildServiceProvider();

            var env = new Env { ConnectionString = cs!, Provider = provider };
            var species = await env.ScalarAsync<int>("SELECT SpeciesId FROM dbo.MotherPlants WHERE Id = @M", ("@M", MpA));
            // test setup on the scratch copy only: this species has no growing days configured in the data
            await env.ExecAsync("UPDATE dbo.PlantSpecies SET ReadyStockDays = 60 WHERE Id = @S AND ReadyStockDays IS NULL", ("@S", species));
            return new Env { ConnectionString = cs!, Provider = provider, Species = species };
        }

        // records a Cutting Tray Sowing of `cuttings` (a whole number of 24-cavity trays) from the pool; returns its id
        private static async Task<int> SowAsync(Env env, int pool, decimal cuttings, int supervisor = SupA, int recordedBy = Recorder)
        {
            var sowing = new SeedSowing
            {
                SourceCuttingStockId = pool, SeedQuantity = cuttings, CavityType = $"{Cavity} Cavity", SowingDate = DateTime.Today, AreaId = AreaA,
                SupervisorId = supervisor, CreatedBy = "e2e-test", CreatedById = recordedBy
            };
            var (ok, message, id) = await env.Get<SeedSowingRepository>().InsertFromCuttingAsync(sowing, recordedBy, _ => true, _ => true);
            Assert.True(ok, message);
            return id;
        }

        private static Task<(bool Success, string? Message, int Id)> ApproveAsync(Env env, int sowingId, decimal trays, int userId = SupA,
            string? reason = null, Func<int, bool>? canUseSourceArea = null)
            => env.Get<ReadyConfirmationRepository>().ConfirmAsync(sowingId, trays, reason, null, "e2e", "e2e-approver", userId, canUseSourceArea ?? (_ => true));

        // ---- A. only the EXTRA is deducted; ledger, balances and Ready Stock reconcile ----

        [SkippableFact]
        public async Task Extra_OnlyTheExtraIsDeducted_TheSowingsOwnCuttingsAreNotTakenAgain_LedgerAndBalancesReconcile()
        {
            var env = await OpenAsync();
            var pool = await env.ResetPoolAsync(AreaA, 3000, 0);
            var sowingId = await SowAsync(env, pool, 480);                                   // 20 trays; pool 3,000 -> 2,520
            Assert.Equal(2520m, (await env.PoolAsync(pool)).Physical);
            var others = await env.OtherPoolsAsync(pool);
            var history = await env.HistoryAsync();

            var (ok, message, confirmationId) = await ApproveAsync(env, sowingId, 30);       // 30 trays = 720; extra = 10 trays x 24 = 240
            Assert.True(ok, message);

            var after = await env.PoolAsync(pool);
            Assert.Equal(2520m - 240m, after.Physical);                                      // NOT 2,520 - 720 and NOT 2,520 - 480 - 240
            Assert.Equal(0m, after.InTransit);
            Assert.Equal(after.Physical, after.Available);
            Assert.Equal(others, await env.OtherPoolsAsync(pool));                           // no other pool moved

            // exactly one approval ledger row: 'Sown', -240, before 2,520, for THIS approval; the sowing's own -480 row is still the only original one
            Assert.Equal(1, await env.ApprovalLedgerRowsAsync(pool));
            Assert.Equal(1, await env.ScalarAsync<int>(@"SELECT COUNT(*) FROM dbo.CuttingStockTransactions WHERE CuttingStockId = @P AND TransactionType = N'Sown'
                AND ReferenceType = N'ReadyConfirmation' AND ReferenceId = @C AND Quantity = -240 AND BeforeQuantity = 2520 AND AfterQuantity = 2280", ("@P", pool), ("@C", confirmationId)));
            Assert.Equal(1, await env.ScalarAsync<int>(@"SELECT COUNT(*) FROM dbo.CuttingStockTransactions WHERE CuttingStockId = @P AND TransactionType = N'Sown'
                AND ReferenceType = N'SeedSowing' AND ReferenceId = @S AND Quantity = -480", ("@P", pool), ("@S", sowingId)));
            Assert.Equal(-480m - 240m, await env.ScalarAsync<decimal>(@"SELECT SUM(Quantity) FROM dbo.CuttingStockTransactions WHERE CuttingStockId = @P AND TransactionType = N'Sown'
                AND ((ReferenceType = N'SeedSowing' AND ReferenceId = @S) OR (ReferenceType = N'ReadyConfirmation' AND ReferenceId = @C))", ("@P", pool), ("@S", sowingId), ("@C", confirmationId)));

            // Ready Stock got trays x cavity, the batch closed with zero wastage
            Assert.Equal(720m, await env.ReadyStockAsync(sowingId));
            Assert.Equal(720m, await env.ScalarAsync<decimal>("SELECT ConfirmedQuantity FROM dbo.ReadyConfirmations WHERE Id = @C", ("@C", confirmationId)));
            Assert.Equal(30m, await env.ScalarAsync<decimal>("SELECT ActualTrayQuantity FROM dbo.ReadyConfirmations WHERE Id = @C", ("@C", confirmationId)));
            Assert.Equal(0m, await env.ScalarAsync<decimal>("SELECT WastageQuantity FROM dbo.ReadyConfirmations WHERE Id = @C", ("@C", confirmationId)));
            Assert.Equal("Completed", await env.ScalarAsync<string>("SELECT Status FROM dbo.SeedSowings WHERE Id = @S", ("@S", sowingId)));
            // reconciliation: cuttings taken (480 + 240) = seedlings produced (720)
            Assert.Equal(720m, 480m + 240m);
            Assert.Equal(history, await env.HistoryAsync());                                 // nothing that existed before changed
        }

        // ---- B. boundaries of the cap: TOTAL ready CUTTINGS (ready trays x 24) against AVAILABLE cuttings ----
        // The supervisor enters TRAYS. The sowing is 20 trays = 480 cuttings; the pool has 1,008 CUTTINGS available after it
        // was recorded (= 42 trays of 24). The scaled tray-level analogue of "500 / 600 / 1,000 / 1,001":
        //   20 trays (480 cuttings)   as sown, no extra          -> allowed
        //   25 trays (600 cuttings)   5 extra trays = 120 cuttings -> allowed
        //   42 trays (1,008 cuttings) exactly the available stock -> allowed
        //   43 trays (1,032 cuttings) more than available         -> rejected

        [SkippableTheory]
        [InlineData(20, true, 0)]        // as sown: allowed, nothing extra
        [InlineData(25, true, 120)]      // 600 ready cuttings, extra 120
        [InlineData(42, true, 528)]      // 1,008 = exactly the available stock: allowed
        [InlineData(43, false, 0)]       // 1,032 > 1,008: rejected, nothing changes
        public async Task TotalReadyQuantity_AgainstAvailableStock_Boundaries(int readyTrays, bool allowed, int expectedExtra)
        {
            var env = await OpenAsync();
            var pool = await env.ResetPoolAsync(AreaA, 480 + 1008, 0);
            var sowingId = await SowAsync(env, pool, 480);
            Assert.Equal(1008m, (await env.PoolAsync(pool)).Available);
            var before = await env.PoolAsync(pool);
            var others = await env.OtherPoolsAsync(pool);

            var (ok, message, confirmationId) = await ApproveAsync(env, sowingId, readyTrays);
            var after = await env.PoolAsync(pool);
            if (allowed)
            {
                Assert.True(ok, message);
                Assert.Equal(before.Physical - expectedExtra, after.Physical);
                Assert.True(after.Physical >= 0 && after.Available >= 0);
                Assert.Equal(expectedExtra > 0 ? 1 : 0, await env.ApprovalLedgerRowsAsync(pool, confirmationId));
                Assert.Equal(readyTrays * (decimal)Cavity, await env.ReadyStockAsync(sowingId));
            }
            else
            {
                Assert.False(ok);
                Assert.Contains("available Cutting Stock", message);
                Assert.Equal(before, after);
                Assert.Equal(0, await env.ApprovalLedgerRowsAsync(pool));
                Assert.Equal(0m, await env.ReadyStockAsync(sowingId));
                Assert.Equal("Sown", await env.ScalarAsync<string>("SELECT Status FROM dbo.SeedSowings WHERE Id = @S", ("@S", sowingId)));   // still open
            }
            Assert.Equal(others, await env.OtherPoolsAsync(pool));
        }

        [SkippableTheory]
        [InlineData(0)]
        [InlineData(-1)]
        [InlineData(2.5)]
        public async Task ZeroNegativeAndFractionalTrays_AreRejected_NothingChanges(double trays)
        {
            var env = await OpenAsync();
            var pool = await env.ResetPoolAsync(AreaA, 3000, 0);
            var sowingId = await SowAsync(env, pool, 480);
            var before = await env.PoolAsync(pool);
            var (ok, message, _) = await ApproveAsync(env, sowingId, (decimal)trays);
            Assert.False(ok);
            Assert.NotNull(message);
            Assert.Equal(before, await env.PoolAsync(pool));
            Assert.Equal(0, await env.ApprovalLedgerRowsAsync(pool));
            Assert.Equal("Sown", await env.ScalarAsync<string>("SELECT Status FROM dbo.SeedSowings WHERE Id = @S", ("@S", sowingId)));
        }

        // ---- C. In-Transit stock can never be consumed ----

        [SkippableFact]
        public async Task InTransitCuttings_CannotBeUsedForTheExtra()
        {
            var env = await OpenAsync();
            var pool = await env.ResetPoolAsync(AreaA, 1500, 500);                            // 1,000 available before the sowing
            var sowingId = await SowAsync(env, pool, 480);                                    // physical 1,020, in transit 500 -> available 520
            var before = await env.PoolAsync(pool);
            Assert.Equal((1020m, 500m, 520m), before);

            // 22 trays = 528 > 520 available although 1,020 is physically there
            var (tooMany, message, _) = await ApproveAsync(env, sowingId, 22);
            Assert.False(tooMany);
            Assert.Contains("available Cutting Stock", message);
            Assert.Equal(before, await env.PoolAsync(pool));

            // 21 trays = 504 <= 520: allowed; extra 24; in transit untouched
            var (ok, okMessage, _) = await ApproveAsync(env, sowingId, 21);
            Assert.True(ok, okMessage);
            Assert.Equal((996m, 500m, 496m), await env.PoolAsync(pool));
        }

        // ---- D. Area isolation: only the sowing's own pool; a user who may not use that Area is refused ----

        [SkippableFact]
        public async Task ExtraNeverComesFromAnotherAreasPool()
        {
            var env = await OpenAsync();
            var poolA = await env.ResetPoolAsync(AreaA, 500, 0);
            var poolB = await env.ResetPoolAsync(AreaB, 50000, 0);                            // plenty of the same variety in ANOTHER Area
            var sowingId = await SowAsync(env, poolA, 240);                                   // pool A: 260 left, 10 trays sown
            var beforeA = await env.PoolAsync(poolA);
            var beforeB = await env.PoolAsync(poolB);

            // 25 trays = 600 > 260 available in the sowing's own pool: refused although Area 122 holds far more
            var (ok, message, _) = await ApproveAsync(env, sowingId, 25);
            Assert.False(ok);
            Assert.Contains("available Cutting Stock", message);
            Assert.Equal(beforeA, await env.PoolAsync(poolA));
            Assert.Equal(beforeB, await env.PoolAsync(poolB));

            // a user who may not use cuttings held in that pool's Area is refused before anything is taken
            var (denied, deniedMessage, _) = await ApproveAsync(env, sowingId, 12, canUseSourceArea: _ => false);
            Assert.False(denied);
            Assert.Contains("not authorized", deniedMessage);
            Assert.Equal(beforeA, await env.PoolAsync(poolA));

            // an approval that needs NO extra never consults the pool's Area, and never touches any pool
            var (normal, normalMessage, _) = await ApproveAsync(env, sowingId, 10, canUseSourceArea: _ => false);
            Assert.True(normal, normalMessage);
            Assert.Equal(beforeA, await env.PoolAsync(poolA));
            Assert.Equal(beforeB, await env.PoolAsync(poolB));
        }

        // ---- E. ordinary approvals (ready <= sown) are exactly as before: no cutting stock movement ----

        [SkippableFact]
        public async Task NormalApproval_TakesNothingFromCuttingStock_AndPartialReadyStillRecordsWastage()
        {
            var env = await OpenAsync();
            var pool = await env.ResetPoolAsync(AreaA, 3000, 0);
            var sowingId = await SowAsync(env, pool, 480);
            var before = await env.PoolAsync(pool);

            // 15 of 20 trays ready -> 5 trays (120) wastage, reason required
            var (noReason, reasonMessage, _) = await ApproveAsync(env, sowingId, 15);
            Assert.False(noReason);
            Assert.Contains("Wastage Reason", reasonMessage);
            var (ok, message, confirmationId) = await ApproveAsync(env, sowingId, 15, reason: "Disease");
            Assert.True(ok, message);
            Assert.Equal(120m, await env.ScalarAsync<decimal>("SELECT WastageQuantity FROM dbo.ReadyConfirmations WHERE Id = @C", ("@C", confirmationId)));
            Assert.Equal(360m, await env.ReadyStockAsync(sowingId));
            Assert.Equal(before, await env.PoolAsync(pool));
            Assert.Equal(0, await env.ApprovalLedgerRowsAsync(pool));

            // and cancelling it returns nothing to Cutting Stock (nothing was taken)
            var (cancelled, cancelMessage) = await env.Get<ReadyConfirmationRepository>().CancelAsync(confirmationId, "e2e", SupA);
            Assert.True(cancelled, cancelMessage);
            Assert.Equal(before, await env.PoolAsync(pool));
            Assert.Equal(0, await env.ApprovalLedgerRowsAsync(pool));
        }

        // ---- F. cancellation returns ONLY the extra, once ----

        [SkippableFact]
        public async Task Cancel_ReturnsOnlyTheExtra_NotTheSowingsOwnCuttings_AndNeverTwice()
        {
            var env = await OpenAsync();
            var pool = await env.ResetPoolAsync(AreaA, 3000, 0);
            var sowingId = await SowAsync(env, pool, 480);                                    // 3,000 -> 2,520
            var (ok, message, confirmationId) = await ApproveAsync(env, sowingId, 30);        // extra 240 -> 2,280
            Assert.True(ok, message);
            Assert.Equal(2280m, (await env.PoolAsync(pool)).Physical);
            var others = await env.OtherPoolsAsync(pool);
            var repo = env.Get<ReadyConfirmationRepository>();

            // somebody who is not the sowing's supervisor cannot cancel it: nothing changes
            var (denied, deniedMessage) = await repo.CancelAsync(confirmationId, "e2e", Recorder);
            Assert.False(denied);
            Assert.Contains("assigned", deniedMessage);
            Assert.Equal(2280m, (await env.PoolAsync(pool)).Physical);

            var (cancelled, cancelMessage) = await repo.CancelAsync(confirmationId, "e2e", SupA);
            Assert.True(cancelled, cancelMessage);
            Assert.Equal(2520m, (await env.PoolAsync(pool)).Physical);                        // +240 only: the sowing's 480 stay out (2,520, not 3,000)
            Assert.Equal(others, await env.OtherPoolsAsync(pool));
            Assert.Equal(1, await env.ScalarAsync<int>(@"SELECT COUNT(*) FROM dbo.CuttingStockTransactions WHERE CuttingStockId = @P AND TransactionType = N'ReversalReturn'
                AND ReferenceType = N'ReadyConfirmation' AND ReferenceId = @C AND Quantity = 240", ("@P", pool), ("@C", confirmationId)));
            Assert.Equal(0, await env.ScalarAsync<int>("SELECT COUNT(*) FROM dbo.CuttingStockTransactions WHERE CuttingStockId = @P AND TransactionType = N'ReversalReturn' AND ReferenceType = N'SeedSowing'", ("@P", pool)));
            Assert.Equal(0m, await env.ReadyStockAsync(sowingId));                            // the Ready Stock was reversed too
            Assert.Equal("Sown", await env.ScalarAsync<string>("SELECT Status FROM dbo.SeedSowings WHERE Id = @S", ("@S", sowingId)));   // re-opened

            // cancelling again returns nothing more
            var (again, againMessage) = await repo.CancelAsync(confirmationId, "e2e", SupA);
            Assert.False(again);
            Assert.Contains("already Cancelled", againMessage);
            Assert.Equal(2520m, (await env.PoolAsync(pool)).Physical);
            Assert.Equal(2, await env.ApprovalLedgerRowsAsync(pool, confirmationId));         // one 'Sown', one 'ReversalReturn'

            // the re-opened sowing can be approved again (extra taken again, exactly once)
            var (reApproved, reMessage, secondId) = await ApproveAsync(env, sowingId, 22);    // 528 -> extra 48
            Assert.True(reApproved, reMessage);
            Assert.Equal(2520m - 48m, (await env.PoolAsync(pool)).Physical);
            Assert.Equal(1, await env.ApprovalLedgerRowsAsync(pool, secondId));
        }

        [SkippableFact]
        public async Task Cancel_IsIdempotent_UnderConcurrentCancellations()
        {
            var env = await OpenAsync();
            var pool = await env.ResetPoolAsync(AreaA, 3000, 0);
            var sowingId = await SowAsync(env, pool, 480);
            var (ok, message, confirmationId) = await ApproveAsync(env, sowingId, 30);
            Assert.True(ok, message);
            var repo = env.Get<ReadyConfirmationRepository>();

            var results = await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => Task.Run(() => env.Get<ReadyConfirmationRepository>().CancelAsync(confirmationId, "e2e", SupA))));
            Assert.Equal(1, results.Count(r => r.Success));
            Assert.Equal(2520m, (await env.PoolAsync(pool)).Physical);                        // returned exactly once
            Assert.Equal(1, await env.ScalarAsync<int>("SELECT COUNT(*) FROM dbo.CuttingStockTransactions WHERE CuttingStockId = @P AND TransactionType = N'ReversalReturn' AND ReferenceId = @C AND ReferenceType = N'ReadyConfirmation'", ("@P", pool), ("@C", confirmationId)));
        }

        // ---- G. concurrency and duplicates ----

        [SkippableFact]
        public async Task SameSowingApprovedSixTimesAtOnce_ConsumesTheExtraOnce()
        {
            var env = await OpenAsync();
            var pool = await env.ResetPoolAsync(AreaA, 3000, 0);
            var sowingId = await SowAsync(env, pool, 480);
            var before = await env.PoolAsync(pool);

            var results = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => Task.Run(() => ApproveAsync(env, sowingId, 30))));
            Assert.Equal(1, results.Count(r => r.Success));
            Assert.All(results.Where(r => !r.Success), r => Assert.NotNull(r.Message));
            Assert.Equal(before.Physical - 240, (await env.PoolAsync(pool)).Physical);
            Assert.Equal(1, await env.ScalarAsync<int>("SELECT COUNT(*) FROM dbo.ReadyConfirmations WHERE SeedSowingId = @S", ("@S", sowingId)));
            Assert.Equal(1, await env.ApprovalLedgerRowsAsync(pool));
            Assert.Equal(720m, await env.ReadyStockAsync(sowingId));
        }

        [SkippableFact]
        public async Task TwoSowingsCompetingForTheSamePool_OnlyValidAvailableStockIsConsumed()
        {
            var env = await OpenAsync();
            var pool = await env.ResetPoolAsync(AreaA, 960 + 600, 0);                         // two sowings of 480 leave 600 available
            var first = await SowAsync(env, pool, 480);
            var second = await SowAsync(env, pool, 480);
            Assert.Equal(600m, (await env.PoolAsync(pool)).Available);

            // each alone is fine (25 trays = 600 <= 600 available, extra 120); together only one can be
            var results = await Task.WhenAll(new[] { first, second }.Select(id => Task.Run(() => ApproveAsync(env, id, 25))));
            Assert.Equal(1, results.Count(r => r.Success));
            var loser = results.Single(r => !r.Success);
            Assert.Contains("available Cutting Stock", loser.Message);
            var after = await env.PoolAsync(pool);
            Assert.Equal(600m - 120m, after.Physical);
            Assert.True(after.Physical >= 0 && after.Available >= 0);
            Assert.Equal(1, await env.ApprovalLedgerRowsAsync(pool));
            Assert.Equal(1, await env.ScalarAsync<int>("SELECT COUNT(*) FROM dbo.ReadyConfirmations WHERE SeedSowingId IN (@A, @B)", ("@A", first), ("@B", second)));
        }

        // ---- H. authorization ----

        [SkippableFact]
        public async Task OnlyTheAssignedSupervisorApproves_TheRecorderCanBeThem_OtherAreasUsersAndOthersAreRefused()
        {
            var env = await OpenAsync();
            var pool = await env.ResetPoolAsync(AreaA, 3000, 0);
            var before = await env.PoolAsync(pool);

            // recorded by user 21, assigned to SupA
            var sowingId = await SowAsync(env, pool, 480);
            foreach (var stranger in new[] { Recorder, SupB })                                // the recorder (not assigned) and a user of another Area
            {
                var (ok, message, _) = await ApproveAsync(env, sowingId, 30, userId: stranger);
                Assert.False(ok);
                Assert.Contains("assigned", message);
            }
            Assert.Equal(before.Physical - 480, (await env.PoolAsync(pool)).Physical);        // only the sowing's own 480; nothing extra
            Assert.Equal(0, await env.ApprovalLedgerRowsAsync(pool));

            // the assigned supervisor is accepted
            var (accepted, acceptedMessage, _) = await ApproveAsync(env, sowingId, 30);
            Assert.True(accepted, acceptedMessage);

            // the recorder chosen as supervisor of their OWN cutting sowing may approve it, extra included
            var own = await SowAsync(env, pool, 480, supervisor: SupA, recordedBy: SupA);
            var (selfOk, selfMessage, _) = await ApproveAsync(env, own, 22, userId: SupA);
            Assert.True(selfOk, selfMessage);
        }

        [SkippableFact]
        public async Task InactiveUser_CannotBeAssignedAsSowingSupervisor()
        {
            var env = await OpenAsync();
            var pool = await env.ResetPoolAsync(AreaA, 3000, 0);
            await env.ExecAsync("UPDATE dbo.IMSUsers SET IsActive = 0 WHERE Id = @U", ("@U", SupA));
            try
            {
                var sowing = new SeedSowing
                {
                    SourceCuttingStockId = pool, SeedQuantity = 480, CavityType = "24 Cavity", SowingDate = DateTime.Today, AreaId = AreaA,
                    SupervisorId = SupA, CreatedBy = "e2e-test", CreatedById = Recorder
                };
                var (ok, message, _) = await env.Get<SeedSowingRepository>().InsertFromCuttingAsync(sowing, Recorder, _ => true, _ => true);
                Assert.False(ok);
                Assert.NotNull(message);
                Assert.Equal(3000m, (await env.PoolAsync(pool)).Physical);
            }
            finally
            {
                await env.ExecAsync("UPDATE dbo.IMSUsers SET IsActive = 1 WHERE Id = @U", ("@U", SupA));
            }
        }

        // ---- I. Direct Seed Sowing and existing records are exactly as before ----

        [SkippableFact]
        public async Task DirectSeedSowing_ApprovalIsUnchanged_AndNeverTouchesCuttingStock()
        {
            var env = await OpenAsync();
            // sowing 634: an open SEED sowing (42-cavity, 11 trays) assigned to user 6, recorded by user 21
            Skip.IfNot(await env.ScalarAsync<string>("SELECT Status FROM dbo.SeedSowings WHERE Id = 634 AND SourceType = N'Seed'") == "Sown", "sowing 634 is not an open seed sowing in this copy");
            var poolsBefore = await env.ScalarAsync<string>("SELECT CONCAT(SUM(PhysicalQuantity), '/', SUM(InTransitQuantity), '/', COUNT(*)) FROM dbo.CuttingStock");
            var ledgerBefore = await env.ScalarAsync<int>("SELECT COUNT(*) FROM dbo.CuttingStockTransactions");
            var history = await env.HistoryAsync();
            var historyWithout634 = await env.ScalarAsync<string>("SELECT CONCAT((SELECT CHECKSUM_AGG(CHECKSUM(*)) FROM dbo.SeedSowings WHERE Id IN (3, 234, 645, 653, 936, 937, 938, 939)), '')");

            // the seed recorder still cannot approve their own seed sowing, and only the assigned supervisor may
            var (own, ownMessage, _) = await env.Get<ReadyConfirmationRepository>().ConfirmAsync(634, 5, "Disease", null, null, "e2e", 21, _ => true);
            Assert.False(own);
            Assert.Contains("assigned", ownMessage);

            // 12 trays against 11 sown: the server accepted this for seed sowings before (Phase G) and still does;
            // it never takes cuttings and needs no source-Area rule
            var (ok, message, confirmationId) = await env.Get<ReadyConfirmationRepository>().ConfirmAsync(634, 12, null, null, null, "e2e", 6, _ => false);
            Assert.True(ok, message);
            Assert.Equal(12m * 42m, await env.ScalarAsync<decimal>("SELECT ConfirmedQuantity FROM dbo.ReadyConfirmations WHERE Id = @C", ("@C", confirmationId)));
            Assert.Equal(0m, await env.ScalarAsync<decimal>("SELECT WastageQuantity FROM dbo.ReadyConfirmations WHERE Id = @C", ("@C", confirmationId)));
            Assert.Equal(poolsBefore, await env.ScalarAsync<string>("SELECT CONCAT(SUM(PhysicalQuantity), '/', SUM(InTransitQuantity), '/', COUNT(*)) FROM dbo.CuttingStock"));
            Assert.Equal(ledgerBefore, await env.ScalarAsync<int>("SELECT COUNT(*) FROM dbo.CuttingStockTransactions"));

            var (cancelled, cancelMessage) = await env.Get<ReadyConfirmationRepository>().CancelAsync(confirmationId, "e2e", 6);
            Assert.True(cancelled, cancelMessage);
            Assert.Equal(poolsBefore, await env.ScalarAsync<string>("SELECT CONCAT(SUM(PhysicalQuantity), '/', SUM(InTransitQuantity), '/', COUNT(*)) FROM dbo.CuttingStock"));
            Assert.Equal(ledgerBefore, await env.ScalarAsync<int>("SELECT COUNT(*) FROM dbo.CuttingStockTransactions"));
            // every other pre-existing sowing and every historical approval is byte-identical
            Assert.Equal(historyWithout634, await env.ScalarAsync<string>("SELECT CONCAT((SELECT CHECKSUM_AGG(CHECKSUM(*)) FROM dbo.SeedSowings WHERE Id IN (3, 234, 645, 653, 936, 937, 938, 939)), '')"));
            Assert.NotNull(history);
        }

        [SkippableFact]
        public async Task ExistingCuttingSowings_AndHistoricalApprovals_AreUntouched_ByAllOfTheAbove()
        {
            var env = await OpenAsync();
            var history = await env.HistoryAsync();
            var pool = await env.ResetPoolAsync(AreaA, 3000, 0);
            var sowingId = await SowAsync(env, pool, 480);
            Assert.True((await ApproveAsync(env, sowingId, 30)).Success);
            // 653 and 936 (open cutting sowings recorded before this correction) and every historical approval: unchanged
            var open = await env.ScalarAsync<int>("SELECT COUNT(*) FROM dbo.SeedSowings WHERE Id IN (653, 936) AND Status = N'Sown' AND ConfirmedReadyQuantity = 0 AND WastageQuantity = 0");
            Assert.Equal(2, open);
            Assert.Equal(await env.ScalarAsync<string>("SELECT CONCAT((SELECT CHECKSUM_AGG(CHECKSUM(*)) FROM dbo.ReadyConfirmations WHERE Id <= 393), '')"),
                         history.Split('/')[0]);
        }

        // ---- J. the approval PAGE ----

        private static T Page<T>(Env env, int userId, params int[] areas) where T : PageModel
        {
            var claims = new List<Claim>
            {
                new("UserId", userId.ToString()), new(ClaimTypes.Name, "e2e-approver"),
                new(MinimumAuthorizationLevelHandler.PermissionClaimType, "ReadyStock.Confirm"),
                new(MinimumAuthorizationLevelHandler.PermissionClaimType, "ReadyStock.View")
            };
            claims.AddRange(areas.Select(a => new Claim(AreaAccessService.AreaAccessClaimType, a.ToString())));
            var http = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(claims, "test", ClaimTypes.Name, ClaimTypes.Role)) };
            var page = ActivatorUtilities.CreateInstance<T>(env.Provider);
            page.PageContext = new PageContext(new ActionContext(http, new Microsoft.AspNetCore.Routing.RouteData(), new CompiledPageActionDescriptor()));
            page.TempData = new TempDataDictionary(http, new NullTempData());
            return page;
        }

        [SkippableFact]
        public async Task ApprovalPage_EndToEnd_ShowsAvailableStock_PreviewsTheExtra_RejectsTooMany_AndApprovesWithinStock()
        {
            var env = await OpenAsync();
            var pool = await env.ResetPoolAsync(AreaA, 480 + 1008, 0);
            var sowingId = await SowAsync(env, pool, 480);
            var before = await env.PoolAsync(pool);

            // GET: the assigned supervisor sees the available Cutting Stock of the sowing's pool
            var page = Page<ConfirmModel>(env, SupA, AreaA);
            Assert.IsType<PageResult>(await page.OnGetAsync(sowingId));
            Assert.Equal(1008m, page.AvailableCuttingStock);                                 // CUTTINGS
            Assert.Equal(20, page.SeedSowing.NumberOfTrays);                                 // TRAYS (20 trays x 24 = 480 cuttings)
            Assert.Equal(42m, page.MaxReadyTrays);                                           // TRAYS: floor(1,008 cuttings / 24)

            // preview: 25 trays -> 600 seedlings, extra 120, stock covers it; 43 trays -> flagged, the numbers are still shown
            static JsonElement Json(JsonResult r) => JsonSerializer.SerializeToElement(r.Value);
            var okPreview = Json(await page.OnGetTrayPreviewAsync(sowingId, 25));
            Assert.True(okPreview.GetProperty("ok").GetBoolean());
            Assert.Equal(600m, okPreview.GetProperty("seedlings").GetDecimal());             // 25 trays x 24 = 600 cuttings
            Assert.Equal(5m, okPreview.GetProperty("extraTrays").GetDecimal());              // 25 - 20 sown trays
            Assert.Equal(120m, okPreview.GetProperty("extraCuttings").GetDecimal());         // 5 extra trays x 24
            Assert.Equal(1008m, okPreview.GetProperty("availableCuttings").GetDecimal());
            Assert.Equal(42m, okPreview.GetProperty("maxReadyTrays").GetDecimal());
            var badPreview = Json(await page.OnGetTrayPreviewAsync(sowingId, 43));
            Assert.False(badPreview.GetProperty("ok").GetBoolean());
            var badError = badPreview.GetProperty("error").GetString();
            Assert.Contains("43 Actual Ready Trays x 24-cavity = 1,032 cuttings", badError);
            Assert.Contains("available Cutting Stock (1,008 cuttings, in-transit excluded)", badError);
            var normalPreview = Json(await page.OnGetTrayPreviewAsync(sowingId, 20));
            Assert.True(normalPreview.GetProperty("ok").GetBoolean());
            Assert.Equal(0m, normalPreview.GetProperty("extraTrays").GetDecimal());
            Assert.Equal(0m, normalPreview.GetProperty("extraCuttings").GetDecimal());

            // POST 43 trays: refused on the page, nothing saved
            var tooMany = Page<ConfirmModel>(env, SupA, AreaA);
            tooMany.SeedSowingId = sowingId; tooMany.ActualReadyTrays = 43;
            Assert.IsType<PageResult>(await tooMany.OnPostAsync());
            Assert.Contains(tooMany.ModelState.Values.SelectMany(v => v.Errors), e => e.ErrorMessage.Contains("available Cutting Stock"));
            Assert.Equal(before, await env.PoolAsync(pool));
            Assert.Equal(0m, await env.ReadyStockAsync(sowingId));

            // POST 25 trays: approved, extra 120 taken, the message says so
            var good = Page<ConfirmModel>(env, SupA, AreaA);
            good.SeedSowingId = sowingId; good.ActualReadyTrays = 25;
            Assert.IsType<RedirectToPageResult>(await good.OnPostAsync());
            Assert.Contains("5 extra trays = 120 extra cuttings were taken from Cutting Stock", (string)good.TempData["Success"]!);
            Assert.Equal(1, await env.ScalarAsync<int>("SELECT COUNT(*) FROM dbo.CuttingStockTransactions WHERE CuttingStockId = @P AND ReferenceType = N'ReadyConfirmation' AND Remarks LIKE N'Extra 5 trays (120 cuttings)%'", ("@P", pool)));
            Assert.Equal(before.Physical - 120, (await env.PoolAsync(pool)).Physical);
            Assert.Equal(600m, await env.ReadyStockAsync(sowingId));

            // a repeated submit of the same form: the sowing is already Completed, nothing is taken twice
            var repeat = Page<ConfirmModel>(env, SupA, AreaA);
            repeat.SeedSowingId = sowingId; repeat.ActualReadyTrays = 25;
            Assert.IsType<PageResult>(await repeat.OnPostAsync());
            Assert.Contains(repeat.ModelState.Values.SelectMany(v => v.Errors), e => e.ErrorMessage.Contains("already been fully approved"));
            Assert.Equal(before.Physical - 120, (await env.PoolAsync(pool)).Physical);
            Assert.Equal(1, await env.ApprovalLedgerRowsAsync(pool));
        }

        [SkippableFact]
        public async Task ApprovalPage_AUserOfAnotherArea_IsRefused_AndDirectSeedPageShowsNoCuttingStock()
        {
            var env = await OpenAsync();
            var pool = await env.ResetPoolAsync(AreaA, 3000, 0);
            var sowingId = await SowAsync(env, pool, 480);
            var before = await env.PoolAsync(pool);

            var foreign = Page<ConfirmModel>(env, SupB, AreaB);
            Assert.IsType<RedirectToPageResult>(await foreign.OnGetAsync(sowingId));
            Assert.NotNull(foreign.TempData["Error"]);
            foreign.SeedSowingId = sowingId; foreign.ActualReadyTrays = 30;
            Assert.IsType<RedirectToPageResult>(await foreign.OnPostAsync());
            Assert.Equal(before, await env.PoolAsync(pool));
            Assert.Equal(0, await env.ApprovalLedgerRowsAsync(pool));

            // an open SEED sowing: the page never reports Cutting Stock
            if (await env.ScalarAsync<string>("SELECT Status FROM dbo.SeedSowings WHERE Id = 634 AND SourceType = N'Seed'") == "Sown")
            {
                var seedPage = Page<ConfirmModel>(env, 6, 2);
                Assert.IsType<PageResult>(await seedPage.OnGetAsync(634));
                Assert.Null(seedPage.AvailableCuttingStock);
                var preview = JsonSerializer.SerializeToElement(((JsonResult)await seedPage.OnGetTrayPreviewAsync(634, 5)).Value);
                Assert.Equal(0m, preview.GetProperty("extraCuttings").GetDecimal());
                Assert.Equal(JsonValueKind.Null, preview.GetProperty("availableCuttings").ValueKind);
            }
        }
    }
}
