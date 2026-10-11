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
    // 2026-10-12 Bed Sowing, end-to-end against a SCRATCH COPY with 2026-10-12_BedSowing.sql applied
    // (PlantsIMS2_Scratch_* only; SKIPPED without PSM_SCRATCH_CONNECTION or the migration).
    // Nothing is hard-coded: the Bed Sowing Polyhouse comes from dbo.BedSowingLocations, the
    // transplanter is the user holding the "Bed Sowing Transplanter" role there, the approver an
    // active Sowing Supervisor of that Area other than the transplanter. Every test sows its OWN
    // test variety and seed lot, so the figures it checks are its own.
    [Collection("ScratchDb")]
    public class BedSowingE2ETests
    {
        private const string ConnectionVariable = "PSM_SCRATCH_CONNECTION";
        private const string RequiredPrefix = "PlantsIMS2_Scratch_";
        private const string Cavity = "24 Cavity";
        private const int ReadyDays = 30;
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
            public int AreaId, PolyhouseId, OtherMainPolyhouseId, Transplanter, Supervisor, Cocopeat;
            public T Get<T>() where T : notnull => Provider.GetRequiredService<T>();
            public BedSowingRepository Beds => Get<BedSowingRepository>();
            public BedSowingRepository.Actor Maya => new(Transplanter, "bed-test-transplanter", false);
            public BedSowingRepository.Actor NotTransplanter => new(Supervisor, "bed-test-supervisor", false);

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

            public Task<decimal> TraysAsync(string cavity = Cavity)
                => ScalarAsync<decimal>("SELECT ISNULL(SUM(PhysicalQuantity), 0) FROM dbo.TrayStock WHERE AreaId = @A AND PolyhouseId = @P AND TraySize = @C AND IsActive = 1",
                    ("@A", AreaId), ("@P", PolyhouseId), ("@C", cavity));

            public Task<decimal> CocopeatAsync()
                => ScalarAsync<decimal>("SELECT ISNULL(SUM(CurrentQuantity), 0) FROM dbo.MaterialStock WHERE MaterialTypeId = @M AND AreaId = @A AND PolyhouseId = @P",
                    ("@M", Cocopeat), ("@A", AreaId), ("@P", PolyhouseId));

            public Task<decimal> LotAsync(int lot) => ScalarAsync<decimal>("SELECT PhysicalQuantity FROM dbo.SeedStock WHERE Id = @Id", ("@Id", lot));
        }

        private sealed record Variety(int SpeciesId, int SeedStockId);

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
                using var mig = new SqlCommand(BedSowingSchema.ProbeSql, conn);
                Skip.If((int)(await mig.ExecuteScalarAsync())! != 1, "2026-10-12_BedSowing.sql is not applied to this scratch database.");
                using var mig13 = new SqlCommand(BedSowingSchema.TraysProbeSql, conn);
                Skip.If((int)(await mig13.ExecuteScalarAsync())! != 1, "2026-10-13_TraysToSowingApprovals.sql is not applied to this scratch database.");
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
            env.PolyhouseId = await env.ScalarAsync<int>("SELECT TOP 1 PolyhouseId FROM dbo.BedSowingLocations WHERE IsActive = 1 ORDER BY PolyhouseId");
            env.AreaId = await env.ScalarAsync<int>("SELECT AreaId FROM dbo.BedSowingLocations WHERE PolyhouseId = @P", ("@P", env.PolyhouseId));
            env.OtherMainPolyhouseId = await env.ScalarAsync<int>(@"SELECT TOP 1 p.Id FROM dbo.Polyhouses p JOIN dbo.Area a ON a.Id = p.AreaId
WHERE a.AreaType = N'MainOffice' AND a.IsActive = 1 AND p.Id NOT IN (SELECT PolyhouseId FROM dbo.BedSowingLocations) ORDER BY p.Id");
            env.Transplanter = await env.ScalarAsync<int>(@"SELECT TOP 1 ur.UserId FROM dbo.UserRoles ur JOIN dbo.Roles r ON r.Id = ur.RoleId
WHERE COALESCE(NULLIF(LTRIM(RTRIM(r.Name)), N''), r.RoleName) = N'Bed Sowing Transplanter' AND ur.AreaId = @A", ("@A", env.AreaId));
            env.Supervisor = await env.ScalarAsync<int>(@"SELECT TOP 1 u.Id FROM dbo.UserRoles ur JOIN dbo.Roles r ON r.Id = ur.RoleId
JOIN dbo.IMSUsers u ON u.Id = ur.UserId AND u.IsActive = 1
WHERE COALESCE(NULLIF(LTRIM(RTRIM(r.Name)), N''), r.RoleName) = N'Sowing Supervisor' AND ur.AreaId = @A AND u.Id <> @T
  AND NOT EXISTS (SELECT 1 FROM dbo.UserRoles x JOIN dbo.RolePermissions rp ON rp.RoleId = x.RoleId JOIN dbo.Permissions p ON p.Id = rp.PermissionId
                  WHERE x.UserId = u.Id AND p.Code = N'BedSowing.Transplant')
ORDER BY u.Id", ("@A", env.AreaId), ("@T", env.Transplanter));
            env.Cocopeat = await env.ScalarAsync<int>("SELECT Id FROM dbo.MaterialTypes WHERE Code = 'COCOPEAT'");
            Skip.If(env.PolyhouseId == 0 || env.Transplanter == 0 || env.Supervisor == 0,
                "The scratch copy has no Bed Sowing location, transplanter or second Sowing Supervisor.");
            return env;
        }

        // A fresh test variety (Ready Days set, or null) with its own Main Office seed lot.
        private static async Task<Variety> NewVarietyAsync(Env env, int? readyDays = ReadyDays, decimal seeds = 100000)
        {
            var name = "ZZTEST Bed " + Guid.NewGuid().ToString("N")[..10];
            var speciesId = await env.ScalarAsync<int>(@"INSERT INTO dbo.PlantSpecies (PlantTypeId, Name, ReadyStockDays)
VALUES ((SELECT MIN(Id) FROM dbo.PlantTypes), @N, @D); SELECT CAST(SCOPE_IDENTITY() AS INT)", ("@N", name), ("@D", readyDays));
            var lot = await env.ScalarAsync<int>(@"INSERT INTO dbo.SeedStock (SpeciesId, AreaId, BatchNo, Unit, PhysicalQuantity, CreatedBy)
VALUES (@S, @A, @B, N'Seeds', @Q, N'bed-test'); SELECT CAST(SCOPE_IDENTITY() AS INT)", ("@S", speciesId), ("@A", env.AreaId), ("@B", "LOT-" + name[^10..]), ("@Q", seeds));
            return new Variety(speciesId, lot);
        }

        // Empty trays and Cocopeat at the Bed Sowing Polyhouse, received `daysAgo` days ago.
        private static async Task StockTraysAsync(Env env, decimal trays, int daysAgo = 10, string cavity = Cavity, int? polyhouse = null, decimal? cocopeatKg = null)
        {
            var (ok, msg, _, _) = await env.Get<TrayStockRepository>().AddStockAsync(env.AreaId, polyhouse ?? env.PolyhouseId, cavity, trays,
                DateTime.UtcNow.AddDays(-daysAgo), Random.Shared.Next(1, int.MaxValue), env.Supervisor, "bed-test", "bed test trays", Any);
            Assert.True(ok, msg);
            await StockCocopeatAsync(env, cocopeatKg ?? trays * 2m, daysAgo, polyhouse);
        }

        // Cocopeat received `daysAgo` days ago (business date; recorded now). daysAgo 0 = today.
        private static async Task StockCocopeatAsync(Env env, decimal kg, int daysAgo, int? polyhouse = null)
        {
            var (mOk, mMsg, _) = await env.Get<MaterialStockRepository>().AdjustAsync(env.AreaId, polyhouse ?? env.PolyhouseId, env.Cocopeat, 1, kg,
                "bed test cocopeat", Guid.NewGuid(), env.Supervisor, Any, LedgerDates.Instant(AppClock.Today.AddDays(-daysAgo)).TransactionUtc);
            Assert.True(mOk, mMsg);
        }

        // A scratch-only Bed Sowing Polyhouse in the same Main Office Area, with empty pools, so date
        // tests see only their own movements. Deactivated by the caller when done.
        private static async Task<int> FreshBedPolyhouseAsync(Env env)
        {
            var id = await env.ScalarAsync<int>("INSERT INTO dbo.Polyhouses (Name, AreaId) VALUES (@N, @A); SELECT CAST(SCOPE_IDENTITY() AS INT)",
                ("@N", "ZZTEST Bed P " + Guid.NewGuid().ToString("N")[..8]), ("@A", env.AreaId));
            await env.ExecAsync("INSERT INTO dbo.BedSowingLocations (PolyhouseId, AreaId, IsActive, Notes, CreatedBy) VALUES (@P, @A, 1, N'scratch test', N'bed-test')",
                ("@P", id), ("@A", env.AreaId));
            return id;
        }

        private static async Task<BedSowing> SowAsync(Env env, Variety v, decimal qty = 1000, int daysAgo = 12, Guid? token = null, int? polyhouse = null)
        {
            var bed = new BedSowing
            {
                SpeciesId = v.SpeciesId, SeedStockId = v.SeedStockId, PolyhouseId = polyhouse ?? env.PolyhouseId, Quantity = qty,
                SowingDate = AppClock.Today.AddDays(-daysAgo), Remarks = "bed test", SubmissionToken = token ?? Guid.NewGuid()
            };
            var (ok, msg, _) = await env.Beds.CreateAsync(bed, new BedSowingRepository.Actor(env.Supervisor, "bed-test-sower", false), Any);
            Assert.True(ok, msg);
            return bed;
        }

        private static BedTransplantRequest Move(Env env, BedSowing bed, decimal qty, int daysAgo = 2, Guid? token = null, string cavity = Cavity, int? trays = null)
            => new()
            {
                BedSowingId = bed.Id, TargetPolyhouseId = bed.PolyhouseId, CavityType = cavity, Quantity = qty, OutputTrays = trays,
                TransplantDate = AppClock.Today.AddDays(-daysAgo), GrowingMaterialTypeId = env.Cocopeat,
                Remarks = "bed test transplant", SubmissionToken = token ?? Guid.NewGuid()
            };

        private static async Task<BedSowing> BedAsync(Env env, int id) => (await env.Beds.GetAsync(id))!;

        private static void AssertConserved(BedSowing b)
            => Assert.True(BedSowingRules.IsConserved(b.Quantity, b.TransplantedQuantity, b.LostQuantity, b.RemainingQuantity),
                $"{b.BedSowingCode}: {b.Quantity} != {b.RemainingQuantity} + {b.TransplantedQuantity} + {b.LostQuantity}");

        // ===== bed sowing ==============================================================================

        [SkippableFact]
        public async Task BedSowing_DeductsTheSeedLot_NoTraysNoMaterialNoReadyStock_ReadyDateFromTheVariety()
        {
            var env = await OpenAsync();
            var v = await NewVarietyAsync(env);
            var trays = await env.TraysAsync();
            var readyStockRows = await env.ScalarAsync<int>("SELECT COUNT(*) FROM dbo.ReadyStock");

            var bed = await SowAsync(env, v, qty: 1000, daysAgo: 12);

            Assert.Matches(@"^BS-\d{4}-\d{5}$", bed.BedSowingCode);
            var saved = await BedAsync(env, bed.Id);
            Assert.Equal(BedSowing.StatusPending, saved.Status);
            Assert.Equal(env.PolyhouseId, saved.PolyhouseId);
            Assert.Equal(env.AreaId, saved.AreaId);
            Assert.Null(saved.ReadyStockDays);                 // no Ready Date at bed stage
            Assert.Null(saved.ExpectedReadyDate);
            Assert.Equal(99000, await env.LotAsync(v.SeedStockId));
            Assert.Equal(-1000, await env.ScalarAsync<decimal>(
                "SELECT SUM(Quantity) FROM dbo.SeedStockTransactions WHERE SeedStockId = @L AND TransactionType = N'Sown' AND ReferenceType = N'BedSowing' AND ReferenceId = @B",
                ("@L", v.SeedStockId), ("@B", bed.Id)));
            Assert.Equal(trays, await env.TraysAsync());
            Assert.Equal(readyStockRows, await env.ScalarAsync<int>("SELECT COUNT(*) FROM dbo.ReadyStock"));
            Assert.Equal(0, await env.ScalarAsync<int>("SELECT COUNT(*) FROM dbo.SeedSowings WHERE BedSowingId = @B", ("@B", bed.Id)));
            AssertConserved(saved);
        }

        [SkippableFact]
        public async Task BedSowing_OnlyAtTheBedSowingPolyhouse_WithReadyDays_NotInTheFuture_Once()
        {
            var env = await OpenAsync();
            var v = await NewVarietyAsync(env);
            var actor = new BedSowingRepository.Actor(env.Supervisor, "bed-test", false);

            if (env.OtherMainPolyhouseId > 0)
            {
                var elsewhere = new BedSowing { SpeciesId = v.SpeciesId, SeedStockId = v.SeedStockId, PolyhouseId = env.OtherMainPolyhouseId, Quantity = 100, SowingDate = AppClock.Today, SubmissionToken = Guid.NewGuid() };
                var (ok, msg, _) = await env.Beds.CreateAsync(elsewhere, actor, Any);
                Assert.False(ok);
                Assert.Equal(BedSowingRules.NotBedLocationMessage, msg);
            }

            var future = new BedSowing { SpeciesId = v.SpeciesId, SeedStockId = v.SeedStockId, PolyhouseId = env.PolyhouseId, Quantity = 100, SowingDate = AppClock.Today.AddDays(1), SubmissionToken = Guid.NewGuid() };
            Assert.False((await env.Beds.CreateAsync(future, actor, Any)).Success);

            var outOfArea = new BedSowing { SpeciesId = v.SpeciesId, SeedStockId = v.SeedStockId, PolyhouseId = env.PolyhouseId, Quantity = 100, SowingDate = AppClock.Today, SubmissionToken = Guid.NewGuid() };
            Assert.False((await env.Beds.CreateAsync(outOfArea, actor, _ => false)).Success);

            var tooMuch = new BedSowing { SpeciesId = v.SpeciesId, SeedStockId = v.SeedStockId, PolyhouseId = env.PolyhouseId, Quantity = 100001, SowingDate = AppClock.Today, SubmissionToken = Guid.NewGuid() };
            Assert.False((await env.Beds.CreateAsync(tooMuch, actor, Any)).Success);

            // A variety without Ready Days can be sown into a bed (no Ready Date yet), but it cannot be
            // transplanted into trays: refused with a clear message, nothing moves.
            await StockTraysAsync(env, 20);
            var noDays = await NewVarietyAsync(env, readyDays: null);
            var missingBed = await SowAsync(env, noDays, qty: 480);
            var trays = await env.TraysAsync();
            var (mOk, mMsg, _, _, _) = await env.Beds.TransplantAsync(Move(env, missingBed, 240), env.Maya);
            Assert.False(mOk);
            Assert.Contains("Ready Days are not configured", mMsg);
            Assert.Equal(0, (await BedAsync(env, missingBed.Id)).TransplantedQuantity);
            Assert.Equal(trays, await env.TraysAsync());
            Assert.Equal(0, await env.ScalarAsync<int>("SELECT COUNT(*) FROM dbo.SeedSowings WHERE BedSowingId = @B", ("@B", missingBed.Id)));
            Assert.Equal(100000, await env.LotAsync(v.SeedStockId));

            // The same form twice: one bed, one deduction.
            var token = Guid.NewGuid();
            var first = await SowAsync(env, v, qty: 500, token: token);
            var again = new BedSowing { SpeciesId = v.SpeciesId, SeedStockId = v.SeedStockId, PolyhouseId = env.PolyhouseId, Quantity = 500, SowingDate = AppClock.Today.AddDays(-12), SubmissionToken = token };
            var (dOk, _, dId) = await env.Beds.CreateAsync(again, actor, Any);
            Assert.True(dOk);
            Assert.True(again.IsDuplicateSubmission);
            Assert.Equal(first.Id, dId);
            Assert.Equal(99500, await env.LotAsync(v.SeedStockId));
        }

        // ===== transplanting ============================================================================

        [SkippableFact]
        public async Task Transplant_ByTheTransplanter_MakesOneGrowingTrayBatch_TakesTraysAndMaterial_KeepsTheBedsReadyDate()
        {
            var env = await OpenAsync();
            var v = await NewVarietyAsync(env);
            await StockTraysAsync(env, 40);
            var bed = await SowAsync(env, v, qty: 1000, daysAgo: 12);
            var trays = await env.TraysAsync();
            var cocopeat = await env.CocopeatAsync();
            var lot = await env.LotAsync(v.SeedStockId);
            var readyStockRows = await env.ScalarAsync<int>("SELECT COUNT(*) FROM dbo.ReadyStock");

            var (ok, msg, opId, sowingId, dup) = await env.Beds.TransplantAsync(Move(env, bed, 240, daysAgo: 2), env.Maya);
            Assert.True(ok, msg);
            Assert.False(dup);
            Assert.Equal($"240 seedlings were transplanted into 10 24 Cavity trays. Ready Date {AppClock.Today.AddDays(-2 + ReadyDays):dd-MM-yyyy} (transplant date + {ReadyDays} days).", msg);

            var b = await BedAsync(env, bed.Id);
            Assert.Equal(240, b.TransplantedQuantity);
            Assert.Equal(760, b.RemainingQuantity);
            Assert.Equal(BedSowing.StatusPartial, b.Status);
            AssertConserved(b);

            var sw = (await env.Get<SeedSowingRepository>().GetByIdAsync(sowingId))!;
            Assert.Equal(SeedSowing.SourceBedSowing, sw.SourceType);
            Assert.Equal(bed.Id, sw.BedSowingId);
            Assert.Equal(bed.BedSowingCode, sw.BedSowingCode);
            Assert.Equal(AppClock.Today.AddDays(-2), sw.BedTransplantDate);
            Assert.Equal(240, sw.QuantitySown);
            Assert.Equal(10, sw.NumberOfTrays);
            Assert.Equal(Cavity, sw.CavityType);
            Assert.Equal(b.SowingDate, sw.SowingDate.Date);                 // the original bed sowing date is kept
            Assert.Equal(AppClock.Today.AddDays(-2 + ReadyDays), sw.ExpectedReadyDate);   // Ready Date = TRANSPLANT date + Ready Days
            Assert.Equal(ReadyDays, sw.ReadyStockDays);
            Assert.Null(sw.SupervisorId);                                   // no supervisor is chosen
            Assert.Null(b.ExpectedReadyDate);
            Assert.Equal("Sown", sw.Status);
            Assert.Contains(sw, await env.Get<SeedSowingRepository>().GetReadyForConfirmationAsync(), new IdComparer());

            Assert.Equal(trays - 10, await env.TraysAsync());
            Assert.Equal(AppClock.Today.AddDays(-2), AppClock.ToIndiaTime(await env.ScalarAsync<DateTime>(
                "SELECT TransactionDate FROM dbo.TrayStockTransactions WHERE ReferenceType = N'SeedSowing' AND ReferenceId = @S", ("@S", sowingId))).Date);
            Assert.Equal(cocopeat - 15m, await env.CocopeatAsync());           // 10 trays x 1.5 kg (existing rule)
            Assert.Equal(lot, await env.LotAsync(v.SeedStockId));             // the seed was taken when the BED was sown
            Assert.Equal(readyStockRows, await env.ScalarAsync<int>("SELECT COUNT(*) FROM dbo.ReadyStock"));
            Assert.Equal(sowingId, await env.ScalarAsync<int>("SELECT SeedSowingId FROM dbo.BedSowingOperations WHERE Id = @O", ("@O", opId)));

            // A later change of the variety's Ready Days never rewrites the batch's Ready Date.
            await env.ExecAsync("UPDATE dbo.PlantSpecies SET ReadyStockDays = 45 WHERE Id = @S", ("@S", v.SpeciesId));
            var again = (await env.Get<SeedSowingRepository>().GetByIdAsync(sowingId))!;
            Assert.Equal(AppClock.Today.AddDays(-2 + ReadyDays), again.ExpectedReadyDate);
            Assert.Equal(ReadyDays, again.ReadyStockDays);
        }

        private sealed class IdComparer : IEqualityComparer<SeedSowing>
        {
            public bool Equals(SeedSowing? x, SeedSowing? y) => x?.Id == y?.Id;
            public int GetHashCode(SeedSowing obj) => obj.Id;
        }

        [SkippableFact]
        public async Task Transplant_OnlyTheTransplanterOfTheArea_NeverAnAdministrator()
        {
            var env = await OpenAsync();
            var v = await NewVarietyAsync(env);
            await StockTraysAsync(env, 20);
            var bed = await SowAsync(env, v, qty: 480);

            var (ok, msg, _, _, _) = await env.Beds.TransplantAsync(Move(env, bed, 240), env.NotTransplanter);
            Assert.False(ok);
            Assert.Equal(BedSowingRules.NotTransplanterMessage, msg);
            var (lossOk, lossMsg, _, _) = await env.Beds.RecordLossAsync(bed.Id, 10, AppClock.Today, "test", null, Guid.NewGuid(), env.NotTransplanter);
            Assert.False(lossOk);
            Assert.Equal(BedSowingRules.NotTransplanterMessage, lossMsg);
            Assert.Equal(0, (await BedAsync(env, bed.Id)).TransplantedQuantity + (await BedAsync(env, bed.Id)).LostQuantity);

            using (var conn = new SqlConnection(env.ConnectionString))
            {
                await conn.OpenAsync();
                Assert.True(await BedSowingRepository.IsTransplanterAsync(conn, null, env.Transplanter, env.AreaId));
                Assert.False(await BedSowingRepository.IsTransplanterAsync(conn, null, env.Supervisor, env.AreaId));
                Assert.False(await BedSowingRepository.IsTransplanterAsync(conn, null, env.Transplanter, env.AreaId + 100000));   // another Area
            }

            // Maya-only: a System Administrator cannot transplant or record a loss -- there is no exception
            // path. Nothing moves: no operation, no tray batch, no trays, no material.
            var admin = new BedSowingRepository.Actor(env.Supervisor, "bed-test-admin", true);
            var trays = await env.TraysAsync();
            var cocopeat = await env.CocopeatAsync();
            var byAdmin = Move(env, bed, 240);
            var (aOk, aMsg, _, _, _) = await env.Beds.TransplantAsync(byAdmin, admin);
            Assert.False(aOk);
            Assert.Equal(BedSowingRules.NotTransplanterMessage, aMsg);
            var (alOk, alMsg, _, _) = await env.Beds.RecordLossAsync(bed.Id, 10, AppClock.Today, "test", null, Guid.NewGuid(), admin);
            Assert.False(alOk);
            Assert.Equal(BedSowingRules.NotTransplanterMessage, alMsg);
            var after = await BedAsync(env, bed.Id);
            Assert.Equal(0, after.TransplantedQuantity + after.LostQuantity);
            Assert.Equal(0, await env.ScalarAsync<int>("SELECT COUNT(*) FROM dbo.BedSowingOperations WHERE BedSowingId = @B", ("@B", bed.Id)));
            Assert.Equal(0, await env.ScalarAsync<int>("SELECT COUNT(*) FROM dbo.SeedSowings WHERE BedSowingId = @B", ("@B", bed.Id)));
            Assert.Equal(trays, await env.TraysAsync());
            Assert.Equal(cocopeat, await env.CocopeatAsync());

            // The database refuses it too (TR_BedSowingOperations_Transplanter): a loss row written directly
            // under any other user -- or with no user -- is refused.
            foreach (var who in new object?[] { env.Supervisor, null })
            {
                var ex = await Assert.ThrowsAsync<SqlException>(() => env.ExecAsync(@"
INSERT INTO dbo.BedSowingOperations (OperationCode, BedSowingId, OperationType, OperationDate, Quantity, Reason, CreatedBy, CreatedById)
VALUES (@C, @B, N'Loss', CAST(SYSDATETIME() AS date), 10, N'direct insert', N'bed-test', @U)",
                    ("@C", "ZZT-" + Guid.NewGuid().ToString("N")[..10]), ("@B", bed.Id), ("@U", who)));
                Assert.Contains("Only the Bed Sowing Transplanter", ex.Message);
            }

            // Maya herself records it normally.
            var (mOk, mMsg, _, _, _) = await env.Beds.TransplantAsync(Move(env, bed, 240), env.Maya);
            Assert.True(mOk, mMsg);
            var (lOk, lMsg, _, _) = await env.Beds.RecordLossAsync(bed.Id, 24, AppClock.Today, "damping off", null, Guid.NewGuid(), env.Maya);
            Assert.True(lOk, lMsg);
            Assert.Equal(2, (await env.Beds.GetOperationsAsync(bed.Id)).Count);
            Assert.All(await env.Beds.GetOperationsAsync(bed.Id), o => Assert.Equal(env.Transplanter, o.CreatedById));
        }

        [SkippableFact]
        public async Task Transplant_NeverMoreThanRemaining_CompleteTraysOnly_AndAllOrNothing()
        {
            var env = await OpenAsync();
            var v = await NewVarietyAsync(env);
            var bed = await SowAsync(env, v, qty: 480);
            await StockTraysAsync(env, 30);

            Assert.False((await env.Beds.TransplantAsync(Move(env, bed, 504), env.Maya)).Success);        // > remaining
            Assert.False((await env.Beds.TransplantAsync(Move(env, bed, 250), env.Maya)).Success);        // not whole trays
            Assert.False((await env.Beds.TransplantAsync(Move(env, bed, 240, trays: 11), env.Maya)).Success);   // trays disagree
            Assert.False((await env.Beds.TransplantAsync(Move(env, bed, 240, daysAgo: 13), env.Maya)).Success); // before the bed
            Assert.False((await env.Beds.TransplantAsync(Move(env, bed, 240, daysAgo: -1), env.Maya)).Success); // future
            var noMaterial = Move(env, bed, 240);
            noMaterial.GrowingMaterialTypeId = null;                                                          // Main Office: material required
            Assert.False((await env.Beds.TransplantAsync(noMaterial, env.Maya)).Success);

            // Not enough empty 9 Cavity trays: refused, and NOTHING moved (bed, batch, trays, material).
            var trays9 = await env.TraysAsync("9 Cavity");
            var cocopeat = await env.CocopeatAsync();
            var batches = await env.ScalarAsync<int>("SELECT COUNT(*) FROM dbo.SeedSowings WHERE BedSowingId = @B", ("@B", bed.Id));
            var tooMany = Move(env, bed, 9 * (trays9 + 1), cavity: "9 Cavity");
            if (tooMany.Quantity <= 480)
            {
                var (ok, msg, _, _, _) = await env.Beds.TransplantAsync(tooMany, env.Maya);
                Assert.False(ok);
                Assert.Contains("tray", msg, StringComparison.OrdinalIgnoreCase);
            }
            var b = await BedAsync(env, bed.Id);
            Assert.Equal(0, b.TransplantedQuantity);
            Assert.Equal(BedSowing.StatusPending, b.Status);
            Assert.Equal(batches, await env.ScalarAsync<int>("SELECT COUNT(*) FROM dbo.SeedSowings WHERE BedSowingId = @B", ("@B", bed.Id)));
            Assert.Equal(cocopeat, await env.CocopeatAsync());
            Assert.Equal(trays9, await env.TraysAsync("9 Cavity"));
            Assert.Equal(0, await env.ScalarAsync<int>("SELECT COUNT(*) FROM dbo.BedSowingOperations WHERE BedSowingId = @B", ("@B", bed.Id)));
        }

        [SkippableFact]
        public async Task Transplant_TraysMustHaveBeenThereOnTheTransplantDate()
        {
            var env = await OpenAsync();
            var v = await NewVarietyAsync(env);
            var bed = await SowAsync(env, v, qty: 24 * 2000, daysAgo: 12);
            // Use every 24 Cavity tray there is, then add a few TODAY only: a transplant dated
            // 2 days ago cannot use trays that arrived today.
            var available = await env.TraysAsync();
            if (available > 0)
            {
                var (ok, msg, _, _, _) = await env.Beds.TransplantAsync(Move(env, bed, 24 * available, daysAgo: 0), env.Maya);
                if (!ok && 24 * available > bed.Quantity) Skip.If(true, "Too many trays in the scratch copy for this test.");
                Assert.True(ok, msg);
            }
            await StockTraysAsync(env, 5, daysAgo: 0);
            var (bOk, bMsg, _, _, _) = await env.Beds.TransplantAsync(Move(env, bed, 24 * 5, daysAgo: 2), env.Maya);
            Assert.False(bOk);
            Assert.Contains("onward", bMsg);
            var (tOk, tMsg, _, _, _) = await env.Beds.TransplantAsync(Move(env, bed, 24 * 5, daysAgo: 0), env.Maya);
            Assert.True(tOk, tMsg);
        }

        [SkippableFact]
        public async Task Transplant_SameFormTwice_RecordsOnce()
        {
            var env = await OpenAsync();
            var v = await NewVarietyAsync(env);
            await StockTraysAsync(env, 20);
            var bed = await SowAsync(env, v, qty: 480);
            var trays = await env.TraysAsync();
            var token = Guid.NewGuid();

            var first = await env.Beds.TransplantAsync(Move(env, bed, 240, token: token), env.Maya);
            var second = await env.Beds.TransplantAsync(Move(env, bed, 240, token: token), env.Maya);
            Assert.True(first.Success, first.Message);
            Assert.True(second.Success);
            Assert.True(second.Duplicate);
            Assert.Equal(first.OperationId, second.OperationId);
            Assert.Equal(first.SeedSowingId, second.SeedSowingId);
            Assert.Equal(240, (await BedAsync(env, bed.Id)).TransplantedQuantity);
            Assert.Equal(trays - 10, await env.TraysAsync());
        }

        [SkippableFact]
        public async Task Transplant_ConcurrentRequests_NeverExceedTheBed()
        {
            var env = await OpenAsync();
            var v = await NewVarietyAsync(env);
            await StockTraysAsync(env, 60);
            var bed = await SowAsync(env, v, qty: 480);
            var trays = await env.TraysAsync();

            // 6 parallel transplants of 120 (5 trays each) against 480: exactly 4 can succeed.
            var results = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => env.Beds.TransplantAsync(Move(env, bed, 120), env.Maya)));
            Assert.Equal(4, results.Count(r => r.Success));
            var b = await BedAsync(env, bed.Id);
            Assert.Equal(480, b.TransplantedQuantity);
            Assert.Equal(0, b.RemainingQuantity);
            Assert.Equal(BedSowing.StatusCompleted, b.Status);
            AssertConserved(b);
            Assert.Equal(4, await env.ScalarAsync<int>("SELECT COUNT(*) FROM dbo.SeedSowings WHERE BedSowingId = @B AND Status <> 'Cancelled'", ("@B", bed.Id)));
            Assert.Equal(trays - 20, await env.TraysAsync());
            Assert.False((await env.Beds.TransplantAsync(Move(env, bed, 24), env.Maya)).Success);   // nothing left
        }

        // ===== approval -> Ready Stock ===================================================================

        [SkippableFact]
        public async Task Approval_CreatesReadyStockOnce_NeverMoreThanTransplanted_ApproverNotTheTransplanter()
        {
            var env = await OpenAsync();
            var v = await NewVarietyAsync(env);
            await StockTraysAsync(env, 20, daysAgo: ReadyDays + 10);
            var bed = await SowAsync(env, v, qty: 480, daysAgo: ReadyDays + 5);
            var (ok, msg, _, sowingId, _) = await env.Beds.TransplantAsync(Move(env, bed, 240, daysAgo: ReadyDays + 1), env.Maya);   // ready since yesterday
            Assert.True(ok, msg);
            var approvals = env.Get<ReadyConfirmationRepository>();

            Assert.False((await approvals.ConfirmAsync(sowingId, 10, null, null, null, "bed-test", env.Transplanter, Any)).Success);   // the recorder
            var (overOk, overMsg, _) = await approvals.ConfirmAsync(sowingId, 11, null, null, null, "bed-test", env.Supervisor, Any);
            Assert.False(overOk);                                                                                                      // more than transplanted
            Assert.Contains("made by a transplant", overMsg);

            var (aOk, aMsg, _) = await approvals.ConfirmAsync(sowingId, 8, "Damaged plants", null, null, "bed-test", env.Supervisor, Any);
            Assert.True(aOk, aMsg);
            var rows = await env.ScalarAsync<int>("SELECT COUNT(*) FROM dbo.ReadyStock WHERE SeedSowingId = @S", ("@S", sowingId));
            Assert.Equal(1, rows);
            Assert.Equal(192, await env.ScalarAsync<decimal>("SELECT Quantity FROM dbo.ReadyStock WHERE SeedSowingId = @S", ("@S", sowingId)));
            Assert.Equal(192, await env.ScalarAsync<decimal>(@"SELECT SUM(t.Quantity) FROM dbo.ReadyStockTransactions t JOIN dbo.ReadyStock r ON r.Id = t.ReadyStockId
WHERE r.SeedSowingId = @S AND t.TransactionType = N'Confirmed'", ("@S", sowingId)));
            Assert.Equal(AppClock.Today.AddDays(-1),     // transplant date (ReadyDays + 1 days ago) + ReadyDays
                (await env.ScalarAsync<DateTime>("SELECT ExpectedReadyDate FROM dbo.SeedSowings WHERE Id = @S", ("@S", sowingId))).Date);
            Assert.Equal("Completed", await env.ScalarAsync<string>("SELECT Status FROM dbo.SeedSowings WHERE Id = @S", ("@S", sowingId)));
            // The bed is untouched by the approval; Ready Stock never exceeds what was transplanted.
            var bedNow = await BedAsync(env, bed.Id);
            Assert.Equal(240, bedNow.TransplantedQuantity);
            AssertConserved(bedNow);
            Assert.True(await env.ScalarAsync<decimal>("SELECT ISNULL(SUM(Quantity), 0) FROM dbo.ReadyStock rs JOIN dbo.SeedSowings sw ON sw.Id = rs.SeedSowingId WHERE sw.BedSowingId = @B", ("@B", bed.Id))
                        <= bedNow.TransplantedQuantity);

            // After the approval the transplant can no longer be reversed (cancel the approval first).
            var opId = await env.ScalarAsync<int>("SELECT Id FROM dbo.BedSowingOperations WHERE SeedSowingId = @S", ("@S", sowingId));
            Assert.False((await env.Beds.ReverseOperationAsync(opId, "test", env.Maya)).Success);
        }

        // ===== reversal, loss, cancel ===================================================================

        [SkippableFact]
        public async Task ReverseTransplant_ReturnsTraysAndMaterial_SeedlingsBackInTheBed_RowKept()
        {
            var env = await OpenAsync();
            var v = await NewVarietyAsync(env);
            await StockTraysAsync(env, 20);
            var bed = await SowAsync(env, v, qty: 480);
            var trays = await env.TraysAsync();
            var cocopeat = await env.CocopeatAsync();
            var (ok, msg, opId, sowingId, _) = await env.Beds.TransplantAsync(Move(env, bed, 240), env.Maya);
            Assert.True(ok, msg);

            // The generic sowing cancel is not the way back for a bed tray batch.
            var (cOk, cMsg) = await env.Get<SeedSowingRepository>().CancelAsync(sowingId, "bed-test", env.Supervisor);
            Assert.False(cOk);
            Assert.Equal(BedSowingRules.GenericCancelBlockedMessage, cMsg);

            Assert.False((await env.Beds.ReverseOperationAsync(opId, " ", env.Maya)).Success);                 // reason required
            Assert.False((await env.Beds.ReverseOperationAsync(opId, "wrong", env.NotTransplanter)).Success);  // not the transplanter
            var (rOk, rMsg) = await env.Beds.ReverseOperationAsync(opId, "entered on the wrong bed", env.Maya);
            Assert.True(rOk, rMsg);

            var b = await BedAsync(env, bed.Id);
            Assert.Equal(0, b.TransplantedQuantity);
            Assert.Equal(BedSowing.StatusPending, b.Status);
            AssertConserved(b);
            Assert.Equal("Cancelled", await env.ScalarAsync<string>("SELECT Status FROM dbo.SeedSowings WHERE Id = @S", ("@S", sowingId)));
            Assert.Equal(trays, await env.TraysAsync());
            Assert.Equal(cocopeat, await env.CocopeatAsync());
            Assert.Equal("Reversed", await env.ScalarAsync<string>("SELECT Status FROM dbo.BedSowingOperations WHERE Id = @O", ("@O", opId)));
            Assert.Equal("entered on the wrong bed", await env.ScalarAsync<string>("SELECT ReversalReason FROM dbo.BedSowingOperations WHERE Id = @O", ("@O", opId)));
            Assert.False((await env.Beds.ReverseOperationAsync(opId, "again", env.Maya)).Success);             // once only
        }

        [SkippableFact]
        public async Task Losses_CloseTheBed_Conserved_ReversalReopens_CancelOnlyUntouched()
        {
            var env = await OpenAsync();
            var v = await NewVarietyAsync(env);
            await StockTraysAsync(env, 20);
            var bed = await SowAsync(env, v, qty: 500);

            Assert.False((await env.Beds.RecordLossAsync(bed.Id, 20, AppClock.Today, "", null, Guid.NewGuid(), env.Maya)).Success);  // reason
            var (ok, msg, _, _, _) = await env.Beds.TransplantAsync(Move(env, bed, 480), env.Maya);
            Assert.True(ok, msg);
            var token = Guid.NewGuid();
            var (lOk, lMsg, lossId, _) = await env.Beds.RecordLossAsync(bed.Id, 20, AppClock.Today, "damping off", null, token, env.Maya);
            Assert.True(lOk, lMsg);
            Assert.True((await env.Beds.RecordLossAsync(bed.Id, 20, AppClock.Today, "damping off", null, token, env.Maya)).Duplicate);

            var b = await BedAsync(env, bed.Id);
            Assert.Equal(BedSowing.StatusCompleted, b.Status);
            Assert.Equal(20, b.LostQuantity);
            Assert.Equal(0, b.RemainingQuantity);
            AssertConserved(b);
            Assert.Contains((await env.Get<WastageRepository>().GetAsync(AppClock.Today, AppClock.Today, env.AreaId, b.BedSowingCode)),
                w => w.ProductionType == WastageRepository.BedLossProductionType && w.Quantity == 20);

            Assert.False((await env.Beds.CancelAsync(bed.Id, "test", env.Maya, Any)).Success);               // not untouched
            Assert.True((await env.Beds.ReverseOperationAsync(lossId, "counted twice", env.Maya)).Success);
            b = await BedAsync(env, bed.Id);
            Assert.Equal(BedSowing.StatusPartial, b.Status);
            Assert.Equal(20, b.RemainingQuantity);
            AssertConserved(b);

            // An untouched bed can be cancelled: the seed goes back to the lot.
            var other = await SowAsync(env, v, qty: 300);
            var lot = await env.LotAsync(v.SeedStockId);
            Assert.False((await env.Beds.CancelAsync(other.Id, " ", env.Maya, Any)).Success);                // reason
            var (xOk, xMsg) = await env.Beds.CancelAsync(other.Id, "sown on the wrong date", env.Maya, Any);
            Assert.True(xOk, xMsg);
            Assert.Equal(lot + 300, await env.LotAsync(v.SeedStockId));
            Assert.Equal(BedSowing.StatusCancelled, (await BedAsync(env, other.Id)).Status);
            Assert.False((await env.Beds.TransplantAsync(Move(env, other, 24), env.Maya)).Success);
        }

        [SkippableFact]
        public async Task Database_RefusesEditsDeletesAndTotalsThatDoNotMatchTheOperations()
        {
            var env = await OpenAsync();
            var v = await NewVarietyAsync(env);
            await StockTraysAsync(env, 20);
            var bed = await SowAsync(env, v, qty: 480);
            var (ok, msg, opId, _, _) = await env.Beds.TransplantAsync(Move(env, bed, 240), env.Maya);
            Assert.True(ok, msg);

            await Assert.ThrowsAsync<SqlException>(() => env.ExecAsync("UPDATE dbo.BedSowings SET Quantity = 999 WHERE Id = @B", ("@B", bed.Id)));
            await Assert.ThrowsAsync<SqlException>(() => env.ExecAsync("UPDATE dbo.BedSowings SET TransplantedQuantity = 0, Status = N'PendingTransplant' WHERE Id = @B", ("@B", bed.Id)));
            await Assert.ThrowsAsync<SqlException>(() => env.ExecAsync("UPDATE dbo.BedSowings SET LostQuantity = 300 WHERE Id = @B", ("@B", bed.Id)));
            await Assert.ThrowsAsync<SqlException>(() => env.ExecAsync("DELETE FROM dbo.BedSowingOperations WHERE Id = @O", ("@O", opId)));
            await Assert.ThrowsAsync<SqlException>(() => env.ExecAsync("UPDATE dbo.BedSowingOperations SET Quantity = 24 WHERE Id = @O", ("@O", opId)));
            await Assert.ThrowsAsync<SqlException>(() => env.ExecAsync("UPDATE dbo.SeedSowings SET BedSowingId = NULL, SourceType = N'Seed' WHERE BedSowingId = @B", ("@B", bed.Id)));
            Assert.Equal(240, (await BedAsync(env, bed.Id)).TransplantedQuantity);
        }

        // ===== into a Mother Plant Area ==================================================================

        [SkippableFact]
        public async Task Transplant_IntoAMotherPlantAreaPolyhouse_NoMaterial_ApprovedByAnApproverButNeverTheTransplanter()
        {
            var env = await OpenAsync();
            var (mpArea, mpPolyhouse) = (await env.ScalarAsync<int>(@"SELECT TOP 1 a.Id FROM dbo.Area a JOIN dbo.Polyhouses p ON p.AreaId = a.Id
WHERE a.AreaType = N'MotherPlant' AND a.IsActive = 1 ORDER BY a.Id"), 0);
            mpPolyhouse = await env.ScalarAsync<int>("SELECT TOP 1 Id FROM dbo.Polyhouses WHERE AreaId = @A ORDER BY Id", ("@A", mpArea));
            Skip.If(mpArea == 0, "No active Mother Plant Area with a Polyhouse in this copy.");
            var (tOk, tMsg, _, _) = await env.Get<TrayStockRepository>().AddStockAsync(mpArea, mpPolyhouse, Cavity, 20, DateTime.UtcNow.AddDays(-ReadyDays - 10),
                Random.Shared.Next(1, int.MaxValue), env.Supervisor, "bed-test", "bed test MP trays", Any);
            Assert.True(tOk, tMsg);
            var traysBefore = await env.ScalarAsync<decimal>("SELECT PhysicalQuantity FROM dbo.TrayStock WHERE PolyhouseId = @P AND TraySize = @C AND IsActive = 1", ("@P", mpPolyhouse), ("@C", Cavity));
            var v = await NewVarietyAsync(env);
            var bed = await SowAsync(env, v, qty: 480, daysAgo: ReadyDays + 5);

            var req = Move(env, bed, 240, daysAgo: ReadyDays);
            req.TargetPolyhouseId = mpPolyhouse;
            req.GrowingMaterialTypeId = null;          // material stock exists only in Main Office Polyhouses (existing rule)
            var (ok, msg, _, sowingId, _) = await env.Beds.TransplantAsync(req, env.Maya);
            Assert.True(ok, msg);
            var sw = (await env.Get<SeedSowingRepository>().GetByIdAsync(sowingId))!;
            Assert.Equal(mpArea, sw.AreaId);
            Assert.Equal(mpPolyhouse, sw.PolyhouseId);
            Assert.Null(sw.GrowingMaterialTypeId);
            Assert.Equal(AppClock.Today, sw.ExpectedReadyDate);
            Assert.Equal(traysBefore - 10, await env.ScalarAsync<decimal>("SELECT PhysicalQuantity FROM dbo.TrayStock WHERE PolyhouseId = @P AND TraySize = @C AND IsActive = 1", ("@P", mpPolyhouse), ("@C", Cavity)));

            // Never the person who recorded the transplant; any other approver may approve.
            var approvals = env.Get<ReadyConfirmationRepository>();
            var (selfOk, selfMsg, _) = await approvals.ConfirmAsync(sowingId, 10, null, null, null, "bed-test", env.Transplanter, Any);
            Assert.False(selfOk);
            Assert.Equal(DirectSowingRules.OwnSowingMessage, selfMsg);
            var (aOk, aMsg, approvalId) = await approvals.ConfirmAsync(sowingId, 10, null, null, null, "bed-test", env.Supervisor, Any);
            Assert.True(aOk, aMsg);
            Assert.Equal(240, await env.ScalarAsync<decimal>("SELECT Quantity FROM dbo.ReadyStock WHERE SeedSowingId = @S", ("@S", sowingId)));
            // The approval is cancelled only by whoever made it.
            Assert.False((await approvals.CancelAsync(approvalId, "bed-test", env.Transplanter)).Success);
            Assert.True((await approvals.CancelAsync(approvalId, "bed-test", env.Supervisor)).Success);
        }

        // ===== Ready Date gate ===========================================================================

        [SkippableFact]
        public async Task ReadyDate_BedTrayBatch_IsNotApprovedBeforeIt_ServerAndDatabase()
        {
            var env = await OpenAsync();
            var v = await NewVarietyAsync(env);
            await StockTraysAsync(env, 20, daysAgo: ReadyDays + 10);
            // Sown long ago, but transplanted TODAY: the Ready Date is today + Ready Days (not bed date + days).
            var early = await SowAsync(env, v, qty: 480, daysAgo: ReadyDays + 5);
            var (ok, msg, _, sowingId, _) = await env.Beds.TransplantAsync(Move(env, early, 240, daysAgo: 0), env.Maya);
            Assert.True(ok, msg);

            var (cOk, cMsg, _) = await env.Get<ReadyConfirmationRepository>().ConfirmAsync(sowingId, 10, null, null, null, "bed-test", env.Supervisor, Any);
            Assert.False(cOk);
            Assert.Equal(string.Format(BedSowingRules.NotReadyYetMessageFormat, AppClock.Today.AddDays(ReadyDays)), cMsg);
            // The approval list / Confirm page use the same rule on the stored batch: "Approve from <Ready Date>".
            var early1 = (await env.Get<SeedSowingRepository>().GetByIdAsync(sowingId))!;
            Assert.Equal((false, cMsg), BedSowingRules.ValidateApprovalReadyDate(early1.SourceType, early1.ExpectedReadyDate, AppClock.Today));
            Assert.Equal(0, await env.ScalarAsync<int>("SELECT COUNT(*) FROM dbo.ReadyStock WHERE SeedSowingId = @S", ("@S", sowingId)));
            Assert.Equal(0, await env.ScalarAsync<int>("SELECT COUNT(*) FROM dbo.ReadyConfirmations WHERE SeedSowingId = @S", ("@S", sowingId)));

            // A direct database insert is refused too (TR_ReadyConfirmations_BedReadyDate).
            var anyReadyStock = await env.ScalarAsync<int>("SELECT TOP 1 Id FROM dbo.ReadyStock ORDER BY Id");
            var ex = await Assert.ThrowsAsync<SqlException>(() => env.ExecAsync(@"
INSERT INTO dbo.ReadyConfirmations (ConfirmationCode, SeedSowingId, ReadyStockId, ActualTrayQuantity, ConfirmedQuantity, WastageQuantity, ApprovedById,
    ConfirmationDate, Status, SupervisorId, CreatedDate, CreatedBy)
VALUES (@C, @S, @R, 10, 240, 0, @U, SYSUTCDATETIME(), 'Confirmed', @U, SYSUTCDATETIME(), N'bed-test')",
                ("@C", "ZZT-" + Guid.NewGuid().ToString("N")[..10]), ("@S", sowingId), ("@R", anyReadyStock), ("@U", env.Supervisor)));
            Assert.Contains("before its Ready Date", ex.Message);

            // On the Ready Date itself it is approved normally.
            var due = await SowAsync(env, v, qty: 480, daysAgo: ReadyDays + 2);
            var (dOk, dMsg, _, dueSowing, _) = await env.Beds.TransplantAsync(Move(env, due, 240, daysAgo: ReadyDays), env.Maya);
            Assert.True(dOk, dMsg);
            var dueBatch = (await env.Get<SeedSowingRepository>().GetByIdAsync(dueSowing))!;
            Assert.Equal(AppClock.Today, dueBatch.ExpectedReadyDate);
            Assert.True(BedSowingRules.ValidateApprovalReadyDate(dueBatch.SourceType, dueBatch.ExpectedReadyDate, AppClock.Today).Ok);   // UI: "Approve"
            var (aOk, aMsg, _) = await env.Get<ReadyConfirmationRepository>().ConfirmAsync(dueSowing, 10, null, null, null, "bed-test", env.Supervisor, Any);
            Assert.True(aOk, aMsg);
            // linked to its bed, one Ready Stock batch only
            Assert.False((await env.Get<ReadyConfirmationRepository>().ConfirmAsync(dueSowing, 1, null, null, null, "bed-test", env.Supervisor, Any)).Success);
            Assert.Equal(1, await env.ScalarAsync<int>("SELECT COUNT(*) FROM dbo.ReadyStock WHERE SeedSowingId = @S", ("@S", dueSowing)));
            Assert.Equal((int?)due.Id, (await env.Get<SeedSowingRepository>().GetByIdAsync(dueSowing))!.BedSowingId);
        }

        // ===== business dates ==========================================================================

        // Lowest balance of a ledger in BUSINESS-date order (seed lot / tray pool / material balance).
        private static Task<decimal> MinHistorySeedAsync(Env env, int lot) => env.ScalarAsync<decimal>(@"
SELECT ISNULL(MIN(r), 0) FROM (SELECT SUM(Quantity) OVER (ORDER BY TransactionDate, Id ROWS UNBOUNDED PRECEDING) r
FROM dbo.SeedStockTransactions WHERE SeedStockId = @L) x", ("@L", lot));
        private static Task<decimal> MinHistoryTraysAsync(Env env, int polyhouse) => env.ScalarAsync<decimal>(@"
SELECT ISNULL(MIN(r), 0) FROM (SELECT SUM(t.Quantity) OVER (PARTITION BY t.TrayStockId ORDER BY t.TransactionDate, t.Id ROWS UNBOUNDED PRECEDING) r
FROM dbo.TrayStockTransactions t JOIN dbo.TrayStock s ON s.Id = t.TrayStockId WHERE s.PolyhouseId = @P) x", ("@P", polyhouse));
        private static Task<decimal> MinHistoryMaterialAsync(Env env, int polyhouse) => env.ScalarAsync<decimal>(@"
SELECT ISNULL(MIN(r), 0) FROM (SELECT SUM(t.Direction * t.StockQuantity) OVER (PARTITION BY t.MaterialStockId ORDER BY COALESCE(t.TransactionDate, t.CreatedAt), t.Id ROWS UNBOUNDED PRECEDING) r
FROM dbo.MaterialTransactions t JOIN dbo.MaterialStock s ON s.Id = t.MaterialStockId WHERE s.PolyhouseId = @P) x", ("@P", polyhouse));

        private static async Task ReceiveSeedAsync(Env env, int lot, decimal qty, int daysAgo)
        {
            using var conn = new SqlConnection(env.ConnectionString);
            await conn.OpenAsync();
            using var tx = conn.BeginTransaction();
            var (ok, msg) = await env.Get<SeedStockRepository>().RecordTransactionAsync(conn, tx, lot, qty, "StockIn", "SeedStock", lot, env.Supervisor,
                "bed test receipt", DateTime.UtcNow.AddDays(-daysAgo));
            Assert.True(ok, msg);
            tx.Commit();
        }

        [SkippableFact]
        public async Task Dates_SeedTraysAndMaterial_OnTheBusinessDate_RecordedTimeKept_NoNegativeHistory()
        {
            var env = await OpenAsync();
            var p = await FreshBedPolyhouseAsync(env);
            try
            {
                // Seed: received TODAY -> a bed sown 5 days ago cannot use it; sown today it can.
                var late = await NewVarietyAsync(env, seeds: 0);
                await ReceiveSeedAsync(env, late.SeedStockId, 1000, daysAgo: 0);
                var actor = new BedSowingRepository.Actor(env.Supervisor, "bed-test", false);
                var backDated = new BedSowing { SpeciesId = late.SpeciesId, SeedStockId = late.SeedStockId, PolyhouseId = p, Quantity = 500, SowingDate = AppClock.Today.AddDays(-5), SubmissionToken = Guid.NewGuid() };
                var (bOk, bMsg, _) = await env.Beds.CreateAsync(backDated, actor, Any);
                Assert.False(bOk);
                Assert.Contains("onward", bMsg);
                Assert.Equal(1000, await env.LotAsync(late.SeedStockId));
                Assert.Equal(0, await env.ScalarAsync<int>("SELECT COUNT(*) FROM dbo.BedSowings WHERE SeedStockId = @L", ("@L", late.SeedStockId)));
                await SowAsync(env, late, qty: 500, daysAgo: 0, polyhouse: p);

                // Seed received 20 days ago -> a bed sown 10 days ago: the ledger row is on the BED's date,
                // CreatedAt is when it was recorded.
                var v = await NewVarietyAsync(env, seeds: 0);
                await ReceiveSeedAsync(env, v.SeedStockId, 5000, daysAgo: 20);
                var bed = await SowAsync(env, v, qty: 960, daysAgo: 10, polyhouse: p);
                var (seedDay, seedRecorded) = (await env.ScalarAsync<DateTime>("SELECT TransactionDate FROM dbo.SeedStockTransactions WHERE ReferenceType = N'BedSowing' AND ReferenceId = @B", ("@B", bed.Id)),
                                               await env.ScalarAsync<DateTime>("SELECT CreatedAt FROM dbo.SeedStockTransactions WHERE ReferenceType = N'BedSowing' AND ReferenceId = @B", ("@B", bed.Id)));
                Assert.Equal(AppClock.Today.AddDays(-10), AppClock.ToIndiaTime(seedDay).Date);
                Assert.Equal(AppClock.Today, AppClock.ToIndiaTime(seedRecorded).Date);

                // Trays and Cocopeat that arrived TODAY cannot serve a transplant dated 3 days ago -- nothing moves.
                await StockTraysAsync(env, 20, daysAgo: 0, polyhouse: p);
                var (t1, t1Msg, _, _, _) = await env.Beds.TransplantAsync(Move(env, bed, 240, daysAgo: 3), env.Maya);
                Assert.False(t1);
                Assert.Contains("onward", t1Msg);
                // Trays from 7 days ago, Cocopeat still only today: refused on the material, all or nothing.
                await env.Get<TrayStockRepository>().AddStockAsync(env.AreaId, p, Cavity, 20, DateTime.UtcNow.AddDays(-7),
                    Random.Shared.Next(1, int.MaxValue), env.Supervisor, "bed-test", "bed test trays", Any);
                var traysBefore = await env.ScalarAsync<decimal>("SELECT PhysicalQuantity FROM dbo.TrayStock WHERE PolyhouseId = @P AND TraySize = @C", ("@P", p), ("@C", Cavity));
                var (t2, t2Msg, _, _, _) = await env.Beds.TransplantAsync(Move(env, bed, 240, daysAgo: 3), env.Maya);
                Assert.False(t2);
                Assert.Contains("Cocopeat", t2Msg);
                Assert.Contains("onward", t2Msg);
                Assert.Equal(traysBefore, await env.ScalarAsync<decimal>("SELECT PhysicalQuantity FROM dbo.TrayStock WHERE PolyhouseId = @P AND TraySize = @C", ("@P", p), ("@C", Cavity)));
                Assert.Equal(0, (await BedAsync(env, bed.Id)).TransplantedQuantity);
                // Cocopeat from 7 days ago -> accepted; trays AND material dated 3 days ago, recorded today.
                await StockCocopeatAsync(env, 40, daysAgo: 7, polyhouse: p);
                var (t3, t3Msg, _, sowingId, _) = await env.Beds.TransplantAsync(Move(env, bed, 240, daysAgo: 3), env.Maya);
                Assert.True(t3, t3Msg);
                var trayRow = (await env.ScalarAsync<DateTime>("SELECT TransactionDate FROM dbo.TrayStockTransactions WHERE ReferenceType = N'SeedSowing' AND ReferenceId = @S", ("@S", sowingId)),
                               await env.ScalarAsync<DateTime>("SELECT CreatedAt FROM dbo.TrayStockTransactions WHERE ReferenceType = N'SeedSowing' AND ReferenceId = @S", ("@S", sowingId)));
                var materialRow = (await env.ScalarAsync<DateTime>("SELECT TransactionDate FROM dbo.MaterialTransactions WHERE ReferenceType = N'SeedSowing' AND ReferenceId = @S AND TransactionType = 'CONSUME'", ("@S", sowingId)),
                                   await env.ScalarAsync<DateTime>("SELECT CreatedAt FROM dbo.MaterialTransactions WHERE ReferenceType = N'SeedSowing' AND ReferenceId = @S AND TransactionType = 'CONSUME'", ("@S", sowingId)));
                Assert.Equal(AppClock.Today.AddDays(-3), AppClock.ToIndiaTime(trayRow.Item1).Date);
                Assert.Equal(AppClock.Today.AddDays(-3), AppClock.ToIndiaTime(materialRow.Item1).Date);
                Assert.Equal(AppClock.Today, AppClock.ToIndiaTime(trayRow.Item2).Date);
                Assert.Equal(AppClock.Today, AppClock.ToIndiaTime(materialRow.Item2).Date);

                // Material History shows the transplant on its business date, with the recorded time kept.
                var (rows, _) = await env.Get<MaterialStockRepository>().GetTransactionsAsync(
                    new MaterialTransactionFilter { PolyhouseId = p, FromDate = AppClock.Today.AddDays(-3), ToDate = AppClock.Today.AddDays(-3), PageSize = 50 }, null);
                var used = Assert.Single(rows, r => r.TransactionType == "CONSUME");
                Assert.True(used.IsBackDated);

                // A reversal returns them now; no history ever goes below zero.
                var opId = await env.ScalarAsync<int>("SELECT Id FROM dbo.BedSowingOperations WHERE SeedSowingId = @S", ("@S", sowingId));
                Assert.True((await env.Beds.ReverseOperationAsync(opId, "test reversal", env.Maya)).Success);
                Assert.True(await MinHistorySeedAsync(env, late.SeedStockId) >= 0);
                Assert.True(await MinHistorySeedAsync(env, v.SeedStockId) >= 0);
                Assert.True(await MinHistoryTraysAsync(env, p) >= 0);
                Assert.True(await MinHistoryMaterialAsync(env, p) >= 0);
            }
            finally
            {
                await env.ExecAsync("UPDATE dbo.BedSowingLocations SET IsActive = 0 WHERE PolyhouseId = @P", ("@P", p));
            }
        }

        // ===== one named Transplanter ===================================================================

        [SkippableFact]
        public async Task RoleGuard_OneTransplanter_PermissionOnlyOnItsRole_EveryChangeAudited()
        {
            var env = await OpenAsync();
            var guard = new BedSowingRoleGuard(env.Get<DatabaseHelper>());
            var roleId = await env.ScalarAsync<int>("SELECT Id FROM dbo.Roles WHERE COALESCE(NULLIF(LTRIM(RTRIM(Name)), N''), RoleName) = N'Bed Sowing Transplanter'");
            var supervisorRole = await env.ScalarAsync<int>("SELECT Id FROM dbo.Roles WHERE COALESCE(NULLIF(LTRIM(RTRIM(Name)), N''), RoleName) = N'Sowing Supervisor'");
            var permissionId = (await guard.TransplantPermissionIdAsync())!.Value;

            // The pages' rules (Admin > User Roles / Users / Roles).
            Assert.True(await guard.RoleCarriesTransplantAsync(roleId));
            Assert.False(await guard.RoleCarriesTransplantAsync(supervisorRole));
            Assert.True(await guard.UserHoldsTransplantRoleAsync(env.Transplanter));
            Assert.False(await guard.UserHoldsTransplantRoleAsync(env.Supervisor));
            Assert.Equal(BedSowingRules.ProtectedRoleMessage, await guard.CheckRolePermissionChangeAsync(supervisorRole, new[] { permissionId }));
            Assert.Equal(BedSowingRules.ProtectedRoleMessage, await guard.CheckRolePermissionChangeAsync(roleId, new[] { permissionId }));
            Assert.Null(await guard.CheckRolePermissionChangeAsync(supervisorRole, Array.Empty<int>()));

            // The database backstop: no second holder, no other role with the permission.
            var e1 = await Assert.ThrowsAsync<SqlException>(() => env.ExecAsync("INSERT INTO dbo.UserRoles (UserId, RoleId, AreaId) VALUES (@U, @R, @A)",
                ("@U", env.Supervisor), ("@R", roleId), ("@A", env.AreaId)));
            Assert.Contains("one named person", e1.Message);
            var e2 = await Assert.ThrowsAsync<SqlException>(() => env.ExecAsync("INSERT INTO dbo.RolePermissions (RoleId, PermissionId) VALUES (@R, @P)",
                ("@R", supervisorRole), ("@P", permissionId)));
            Assert.Contains("only to the Bed Sowing Transplanter role", e2.Message);
            Assert.Equal(0, await env.ScalarAsync<int>("SELECT COUNT(*) FROM dbo.UserRoles WHERE UserId = @U AND RoleId = @R", ("@U", env.Supervisor), ("@R", roleId)));
            Assert.Equal(1, await env.ScalarAsync<int>(@"SELECT COUNT(DISTINCT ur.UserId) FROM dbo.UserRoles ur
JOIN dbo.RolePermissions rp ON rp.RoleId = ur.RoleId WHERE rp.PermissionId = @P", ("@P", permissionId)));

            // The migration's own grant and assignment are in the audit log, which is append-only.
            Assert.True(await env.ScalarAsync<int>("SELECT COUNT(*) FROM dbo.BedSowingAuditLog WHERE Action = N'TransplanterRoleAssigned' AND TargetUserId = @U", ("@U", env.Transplanter)) >= 1);
            await Assert.ThrowsAsync<SqlException>(() => env.ExecAsync("UPDATE dbo.BedSowingAuditLog SET Reason = N'x'"));
            await Assert.ThrowsAsync<SqlException>(() => env.ExecAsync("DELETE FROM dbo.BedSowingAuditLog"));
        }

        [SkippableFact]
        public async Task AdminReversal_IsAllowed_AndAudited()
        {
            var env = await OpenAsync();
            var v = await NewVarietyAsync(env);
            await StockTraysAsync(env, 20);
            var bed = await SowAsync(env, v, qty: 480);
            var (ok, msg, opId, _, _) = await env.Beds.TransplantAsync(Move(env, bed, 240), env.Maya);
            Assert.True(ok, msg);
            var admin = new BedSowingRepository.Actor(env.Supervisor, "bed-test-admin", true);
            var (rOk, rMsg) = await env.Beds.ReverseOperationAsync(opId, "wrong cavity entered", admin);
            Assert.True(rOk, rMsg);
            Assert.Equal(1, await env.ScalarAsync<int>(
                "SELECT COUNT(*) FROM dbo.BedSowingAuditLog WHERE Action = N'AdminReversal' AND OperationId = @O AND UserId = @U AND Reason = N'wrong cavity entered'",
                ("@O", opId), ("@U", env.Supervisor)));
            // The Transplanter's own reversal is ordinary work, not an audited exception.
            var (ok2, msg2, opId2, _, _) = await env.Beds.TransplantAsync(Move(env, bed, 240), env.Maya);
            Assert.True(ok2, msg2);
            Assert.True((await env.Beds.ReverseOperationAsync(opId2, "test", env.Maya)).Success);
            Assert.Equal(0, await env.ScalarAsync<int>("SELECT COUNT(*) FROM dbo.BedSowingAuditLog WHERE OperationId = @O", ("@O", opId2)));
        }

        // ===== reads used by the pages ===================================================================

        [SkippableFact]
        public async Task Reads_ListsHistoryLocationsAndTransplantAreas()
        {
            var env = await OpenAsync();
            var v = await NewVarietyAsync(env);
            await StockTraysAsync(env, 20);
            var open = await SowAsync(env, v, qty: 480);
            var closed = await SowAsync(env, v, qty: 240);
            var (ok, msg, _, _, _) = await env.Beds.TransplantAsync(Move(env, open, 240), env.Maya);
            Assert.True(ok, msg);
            Assert.True((await env.Beds.TransplantAsync(Move(env, closed, 240), env.Maya)).Success);
            Assert.True((await env.Beds.RecordLossAsync(open.Id, 24, AppClock.Today, "test loss", null, Guid.NewGuid(), env.Maya)).Success);

            var all = await env.Beds.ListAsync(null);
            Assert.Contains(all, b => b.Id == open.Id);
            Assert.Contains(all, b => b.Id == closed.Id && b.Status == BedSowing.StatusCompleted);
            var waiting = await env.Beds.ListAsync(new[] { env.AreaId }, openOnly: true);
            Assert.Contains(waiting, b => b.Id == open.Id && b.RemainingQuantity == 216 && b.PolyhouseName != null && b.SpeciesName.StartsWith("ZZTEST Bed"));
            Assert.DoesNotContain(waiting, b => b.Id == closed.Id);
            Assert.Empty(await env.Beds.ListAsync(Array.Empty<int>()));                         // no Area -> nothing

            var ops = await env.Beds.GetOperationsAsync(open.Id);
            Assert.Equal(2, ops.Count);
            var t = Assert.Single(ops, o => o.IsTransplant);
            Assert.Equal(10, t.OutputTrays);
            Assert.NotNull(t.SowingCode);
            Assert.Equal("Sown", t.SowingStatus);
            Assert.Equal("Cocopeat", t.GrowingMaterialName, ignoreCase: true);
            Assert.Equal("test loss", Assert.Single(ops, o => !o.IsTransplant).Reason);

            var history = await env.Beds.GetHistoryAsync(new[] { env.AreaId }, AppClock.Today.AddDays(-30), AppClock.Today);
            Assert.Contains(history, o => o.BedSowingId == open.Id && o.IsTransplant);
            Assert.Contains(history, o => o.BedSowingId == closed.Id);
            Assert.Empty(await env.Beds.GetHistoryAsync(new[] { env.AreaId }, AppClock.Today.AddDays(1), AppClock.Today.AddDays(2)));

            Assert.Contains(await env.Beds.GetLocationsAsync(), l => l.PolyhouseId == env.PolyhouseId && l.AreaId == env.AreaId);
            Assert.Contains(env.AreaId, (await env.Beds.TransplantAreaIdsAsync(env.Transplanter, false))!);
            Assert.DoesNotContain(env.AreaId, (await env.Beds.TransplantAreaIdsAsync(env.Supervisor, false))!);
            Assert.Null(await env.Beds.TransplantAreaIdsAsync(env.Supervisor, true));            // administrator: all Areas
        }

        // ===== reports ==================================================================================

        [SkippableFact]
        public async Task Reports_CountTheBedSowingOnce_NotItsTrayBatchesAgain()
        {
            var env = await OpenAsync();
            var v = await NewVarietyAsync(env);
            await StockTraysAsync(env, 20);
            var bed = await SowAsync(env, v, qty: 1000, daysAgo: 5);
            var day = AppClock.Today.AddDays(-5);
            var (ok, msg, _, _, _) = await env.Beds.TransplantAsync(Move(env, bed, 240, daysAgo: 1), env.Maya);
            Assert.True(ok, msg);

            // Management Dashboard seed summary for this variety: the bed's 1000 seeds, once.
            var summary = await env.Get<ManagementDashboardRepository>().GetSeedSummaryAsync(
                new ManagementDashboardFilters { FromDate = day, ToDate = day, SpeciesId = v.SpeciesId }, new List<UnitQuantity>());
            Assert.Equal(1, summary.SowingActivityCountInRange);
            Assert.Equal(1000, summary.SeedSownQuantityInRange);

            // Monthly sowing pivot: 1000 on the bed's day for this variety.
            var pivot = await env.Get<SeedSowingRepository>().GetMonthlyPivotAsync(day.Month, day.Year, null, null, null);
            Assert.Equal(1000, pivot.Where(r => r.SpeciesId == v.SpeciesId).Sum(r => r.Quantity));

            // Daily Operations Report: the bed is a 'BedSowing' sowing figure; the tray batch adds nothing.
            var data = await env.Get<DailyOperationsReportRepository>().GetAsync(day, day);
            var bedsThatDay = await env.ScalarAsync<decimal>("SELECT ISNULL(SUM(Quantity), 0) FROM dbo.BedSowings WHERE SowingDate = @D AND Status <> N'Cancelled'", ("@D", day));
            Assert.Equal(bedsThatDay, data.Sowing.Where(f => f.Date == day && f.Key == SeedSowing.SourceBedSowing).Sum(f => f.Quantity));
            var directThatDay = await env.ScalarAsync<decimal>(
                "SELECT ISNULL(SUM(QuantitySown), 0) FROM dbo.SeedSowings WHERE CAST(SowingDate AS date) = @D AND Status <> 'Cancelled' AND SourceType <> N'BedSowing'", ("@D", day));
            Assert.Equal(bedsThatDay + directThatDay, data.Sowing.Where(f => f.Date == day).Sum(f => f.Quantity));
        }
        // ===== transplanting ALL the remaining seedlings: complete trays + the remainder as waste (2026-10-11) =====
        // N remaining, cavity C: FullTrays = N / C (integer), Transplanted = FullTrays x C, Waste = N - Transplanted,
        // recorded as one system-generated bed loss in the same transaction; the bed closes.

        // A fresh Bed Sowing Polyhouse (own tray / material pools) with `trays` empty trays of `cavity`, and a
        // bed of `qty` seedlings sown there 12 days ago.
        private static async Task<(int Polyhouse, Variety V, BedSowing Bed)> RemainderSetupAsync(Env env, decimal qty, string cavity, decimal trays,
            int? readyDays = ReadyDays, decimal cocopeatKg = 200)
        {
            var p = await FreshBedPolyhouseAsync(env);
            var v = await NewVarietyAsync(env, readyDays);
            if (trays > 0)
            {
                var (ok, msg, _, _) = await env.Get<TrayStockRepository>().AddStockAsync(env.AreaId, p, cavity, trays,
                    DateTime.UtcNow.AddDays(-10), Random.Shared.Next(1, int.MaxValue), env.Supervisor, "bed-test", "bed test trays", Any);
                Assert.True(ok, msg);
            }
            if (cocopeatKg > 0) await StockCocopeatAsync(env, cocopeatKg, 10, p);
            var bed = await SowAsync(env, v, qty: qty, daysAgo: 12, polyhouse: p);
            return (p, v, bed);
        }

        private static Task RetireBedPolyhouseAsync(Env env, int p)
            => env.ExecAsync("UPDATE dbo.BedSowingLocations SET IsActive = 0 WHERE PolyhouseId = @P", ("@P", p));

        private static Task<decimal> PoolTraysAsync(Env env, int p, string cavity)
            => env.ScalarAsync<decimal>("SELECT ISNULL(SUM(PhysicalQuantity), 0) FROM dbo.TrayStock WHERE PolyhouseId = @P AND TraySize = @C", ("@P", p), ("@C", cavity));

        private static Task<decimal> PoolCocopeatAsync(Env env, int p)
            => env.ScalarAsync<decimal>("SELECT ISNULL(SUM(CurrentQuantity), 0) FROM dbo.MaterialStock WHERE PolyhouseId = @P", ("@P", p));

        private sealed record Op(string Type, decimal Qty, int? Trays, int? SowingId, string? Reason, DateTime Date, Guid? Token, string Status);

        private static async Task<List<Op>> OpsAsync(Env env, int bedId)
        {
            var list = new List<Op>();
            using var conn = new SqlConnection(env.ConnectionString);
            await conn.OpenAsync();
            using var cmd = new SqlCommand(@"SELECT OperationType, Quantity, OutputTrays, SeedSowingId, Reason, OperationDate, SubmissionToken, Status
FROM dbo.BedSowingOperations WHERE BedSowingId = @B ORDER BY Id", conn);
            cmd.Parameters.AddWithValue("@B", bedId);
            using var r = await cmd.ExecuteReaderAsync();
            while (await r.ReadAsync())
                list.Add(new Op(r.GetString(0), r.GetDecimal(1), r.IsDBNull(2) ? null : r.GetInt32(2), r.IsDBNull(3) ? null : r.GetInt32(3),
                    r.IsDBNull(4) ? null : r.GetString(4), r.GetDateTime(5), r.IsDBNull(6) ? null : r.GetGuid(6), r.GetString(7)));
            return list;
        }

        [SkippableTheory]
        [InlineData(500, "150 Cavity", 3, 450, 50)]
        [InlineData(450, "150 Cavity", 3, 450, 0)]
        [InlineData(500, "102 Cavity", 4, 408, 92)]
        public async Task TransplantAll_CompleteTrays_RemainderRecordedAsWaste_BedClosed(int remaining, string cavity, int trays, int transplanted, int waste)
        {
            var env = await OpenAsync();
            var (p, _, bed) = await RemainderSetupAsync(env, remaining, cavity, trays + 2);
            try
            {
                var trays0 = await PoolTraysAsync(env, p, cavity);
                var req = Move(env, bed, remaining, daysAgo: 2, cavity: cavity);
                var (ok, msg, opId, sowingId, dup) = await env.Beds.TransplantAsync(req, env.Maya);
                Assert.True(ok, msg);
                Assert.False(dup);
                var day = AppClock.Today.AddDays(-2);
                var plan = new BedSowingRules.TransplantPlan(trays, transplanted, waste);
                Assert.StartsWith(plan.Summary(cavity, done: true), msg);
                Assert.Contains($"Ready Date {day.AddDays(ReadyDays):dd-MM-yyyy}", msg);

                // the bed: Original = Remaining + Transplanted + Lost, closed
                var b = await BedAsync(env, bed.Id);
                Assert.Equal((0m, (decimal)transplanted, (decimal)waste, BedSowing.StatusCompleted), (b.RemainingQuantity, b.TransplantedQuantity, b.LostQuantity, b.Status));
                AssertConserved(b);

                // one tray batch of the complete trays only, in Sowing Approvals (no Ready Stock)
                var sw = (await env.Get<SeedSowingRepository>().GetByIdAsync(sowingId))!;
                Assert.Equal((SeedSowing.SourceBedSowing, (int?)trays, (decimal)transplanted, "Sown", day.AddDays(ReadyDays)),
                    (sw.SourceType, sw.NumberOfTrays, sw.QuantitySown, sw.Status, sw.ExpectedReadyDate));
                Assert.Equal(0, await env.ScalarAsync<int>("SELECT COUNT(*) FROM dbo.ReadyStock WHERE SeedSowingId = @S", ("@S", sowingId)));
                Assert.Equal(trays0 - trays, await PoolTraysAsync(env, p, cavity));

                // the operations: the transplant (holding the form's token) + the system-generated loss on the transplant date
                var ops = await OpsAsync(env, bed.Id);
                var t = Assert.Single(ops, o => o.Type == "Transplant");
                Assert.Equal(((decimal)transplanted, (int?)trays, (int?)sowingId, day, req.SubmissionToken), (t.Qty, t.Trays, t.SowingId, t.Date, t.Token));
                Assert.Equal(opId, await env.ScalarAsync<int>("SELECT Id FROM dbo.BedSowingOperations WHERE BedSowingId = @B AND OperationType = N'Transplant'", ("@B", bed.Id)));
                if (waste > 0)
                {
                    var l = Assert.Single(ops, o => o.Type == "Loss");
                    Assert.Equal(((decimal)waste, BedSowingRules.RemainderLossReason, day, (Guid?)null, "Active"), (l.Qty, l.Reason, l.Date, l.Token, l.Status));
                }
                else
                    Assert.DoesNotContain(ops, o => o.Type == "Loss");
            }
            finally { await RetireBedPolyhouseAsync(env, p); }
        }

        [SkippableFact]
        public async Task TransplantAll_FewerThanOneTray_NoTrayBatch_AllRecordedAsWaste_BedClosed_DuplicateIgnored()
        {
            var env = await OpenAsync();
            var (p, _, bed) = await RemainderSetupAsync(env, 100, "150 Cavity", 5);
            try
            {
                var trays0 = await PoolTraysAsync(env, p, "150 Cavity");
                var cocopeat0 = await PoolCocopeatAsync(env, p);
                var batches0 = await env.ScalarAsync<int>("SELECT COUNT(*) FROM dbo.SeedSowings");
                var req = Move(env, bed, 100, daysAgo: 1, cavity: "150 Cavity");
                var (ok, msg, opId, sowingId, _) = await env.Beds.TransplantAsync(req, env.Maya);
                Assert.True(ok, msg);
                Assert.Equal("Not enough seedlings for one 150 Cavity tray: no tray batch was created, and 100 seedlings were recorded as waste.", msg);
                Assert.Equal(0, sowingId);
                Assert.Equal(batches0, await env.ScalarAsync<int>("SELECT COUNT(*) FROM dbo.SeedSowings"));
                Assert.Equal(trays0, await PoolTraysAsync(env, p, "150 Cavity"));
                Assert.Equal(cocopeat0, await PoolCocopeatAsync(env, p));
                var b = await BedAsync(env, bed.Id);
                Assert.Equal((0m, 0m, 100m, BedSowing.StatusCompleted), (b.RemainingQuantity, b.TransplantedQuantity, b.LostQuantity, b.Status));
                AssertConserved(b);
                var l = Assert.Single(await OpsAsync(env, bed.Id));
                Assert.Equal(("Loss", 100m, BedSowingRules.RemainderLossReason, AppClock.Today.AddDays(-1), req.SubmissionToken), (l.Type, l.Qty, l.Reason, l.Date, l.Token));

                // the same form again: recognised, nothing recorded twice
                var (ok2, _, opId2, _, dup2) = await env.Beds.TransplantAsync(req, env.Maya);
                Assert.True(ok2);
                Assert.True(dup2);
                Assert.Equal(opId, opId2);
                Assert.Single(await OpsAsync(env, bed.Id));
            }
            finally { await RetireBedPolyhouseAsync(env, p); }
        }

        [SkippableFact]
        public async Task TransplantAll_MissingOrZeroReadyDays_Refused_NothingChanges()
        {
            var env = await OpenAsync();
            // Zero Ready Days cannot even be stored (CK_PlantSpecies_ReadyStockDays); the rule refuses it too
            // (BedSowingRulesTests: ReadyDate(.., 0) is null). Missing Ready Days: refused end to end below.
            var zero = await Assert.ThrowsAsync<SqlException>(() => NewVarietyAsync(env, readyDays: 0));
            Assert.Contains("CK_PlantSpecies_ReadyStockDays", zero.Message);
            Assert.Null(TrayBatchRules.ReadyDate(AppClock.Today, 0));
            foreach (int? days in new int?[] { null })
            {
                var (p, _, bed) = await RemainderSetupAsync(env, 500, "150 Cavity", 5, readyDays: days);
                try
                {
                    var trays0 = await PoolTraysAsync(env, p, "150 Cavity");
                    var cocopeat0 = await PoolCocopeatAsync(env, p);
                    var (ok, msg, _, _, _) = await env.Beds.TransplantAsync(Move(env, bed, 500, cavity: "150 Cavity"), env.Maya);
                    Assert.False(ok);
                    Assert.Contains("Ready Days are not configured", msg);
                    var b = await BedAsync(env, bed.Id);
                    Assert.Equal((500m, 0m, 0m, BedSowing.StatusPending), (b.RemainingQuantity, b.TransplantedQuantity, b.LostQuantity, b.Status));
                    Assert.Empty(await OpsAsync(env, bed.Id));
                    Assert.Equal(0, await env.ScalarAsync<int>("SELECT COUNT(*) FROM dbo.SeedSowings WHERE BedSowingId = @B", ("@B", bed.Id)));
                    Assert.Equal(trays0, await PoolTraysAsync(env, p, "150 Cavity"));
                    Assert.Equal(cocopeat0, await PoolCocopeatAsync(env, p));
                }
                finally { await RetireBedPolyhouseAsync(env, p); }
            }
        }

        [SkippableFact]
        public async Task TransplantAll_NotEnoughTraysOrMaterial_WholeOperationRefused_NoLossRecorded()
        {
            var env = await OpenAsync();
            // 2 trays for a 3-tray transplant
            var (p, _, bed) = await RemainderSetupAsync(env, 500, "150 Cavity", 2);
            try
            {
                var (ok, msg, _, _, _) = await env.Beds.TransplantAsync(Move(env, bed, 500, cavity: "150 Cavity"), env.Maya);
                Assert.False(ok);
                Assert.Contains("tray", msg, StringComparison.OrdinalIgnoreCase);
                Assert.Empty(await OpsAsync(env, bed.Id));
                Assert.Equal(500m, (await BedAsync(env, bed.Id)).RemainingQuantity);
                Assert.Equal(2m, await PoolTraysAsync(env, p, "150 Cavity"));
            }
            finally { await RetireBedPolyhouseAsync(env, p); }

            // trays enough, growing material not
            var (p2, _, bed2) = await RemainderSetupAsync(env, 500, "150 Cavity", 5, cocopeatKg: 0.5m);
            try
            {
                var (ok, msg, _, _, _) = await env.Beds.TransplantAsync(Move(env, bed2, 500, cavity: "150 Cavity"), env.Maya);
                Assert.False(ok);
                Assert.Contains("Cocopeat", msg, StringComparison.OrdinalIgnoreCase);
                Assert.Empty(await OpsAsync(env, bed2.Id));
                Assert.Equal(500m, (await BedAsync(env, bed2.Id)).RemainingQuantity);
                Assert.Equal(5m, await PoolTraysAsync(env, p2, "150 Cavity"));
                Assert.Equal(0.5m, await PoolCocopeatAsync(env, p2));
                Assert.Equal(0, await env.ScalarAsync<int>("SELECT COUNT(*) FROM dbo.SeedSowings WHERE BedSowingId = @B", ("@B", bed2.Id)));
            }
            finally { await RetireBedPolyhouseAsync(env, p2); }
        }

        // A failure in the LAST write (the tray deduction, forced by a temporary trigger) rolls back the tray
        // batch, the transplant, the remainder loss and the bed totals: all or nothing.
        [SkippableFact]
        public async Task TransplantAll_DatabaseFailureMidway_EverythingRolledBack()
        {
            var env = await OpenAsync();
            var (p, _, bed) = await RemainderSetupAsync(env, 500, "150 Cavity", 5);
            await env.ExecAsync($@"CREATE OR ALTER TRIGGER dbo.TR_E2E_FailBedRemainder ON dbo.TrayStockTransactions AFTER INSERT AS
BEGIN IF EXISTS (SELECT 1 FROM inserted i JOIN dbo.TrayStock s ON s.Id = i.TrayStockId WHERE s.PolyhouseId = {p}) THROW 50999, 'E2E: simulated failure', 1; END");
            try
            {
                var batches0 = await env.ScalarAsync<int>("SELECT COUNT(*) FROM dbo.SeedSowings");
                var req = Move(env, bed, 500, cavity: "150 Cavity");
                var (ok, _, _, _, _) = await env.Beds.TransplantAsync(req, env.Maya);
                Assert.False(ok);
                Assert.Empty(await OpsAsync(env, bed.Id));
                var b = await BedAsync(env, bed.Id);
                Assert.Equal((500m, 0m, 0m, BedSowing.StatusPending), (b.RemainingQuantity, b.TransplantedQuantity, b.LostQuantity, b.Status));
                Assert.Equal(batches0, await env.ScalarAsync<int>("SELECT COUNT(*) FROM dbo.SeedSowings"));
                Assert.Equal(5m, await PoolTraysAsync(env, p, "150 Cavity"));
                Assert.Equal(200m, await PoolCocopeatAsync(env, p));

                await env.ExecAsync("DROP TRIGGER IF EXISTS dbo.TR_E2E_FailBedRemainder");
                var (ok2, msg2, _, _, dup2) = await env.Beds.TransplantAsync(req, env.Maya);       // the same form, once the fault is gone
                Assert.True(ok2, msg2);
                Assert.False(dup2);
                Assert.Equal(2, (await OpsAsync(env, bed.Id)).Count);
            }
            finally
            {
                await env.ExecAsync("DROP TRIGGER IF EXISTS dbo.TR_E2E_FailBedRemainder");
                await RetireBedPolyhouseAsync(env, p);
            }
        }

        [SkippableFact]
        public async Task TransplantAll_SameFormSubmittedConcurrently_OneBatchOneLossOneDeduction()
        {
            var env = await OpenAsync();
            var (p, _, bed) = await RemainderSetupAsync(env, 500, "150 Cavity", 6);
            try
            {
                var req = Move(env, bed, 500, cavity: "150 Cavity");
                var results = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => env.Beds.TransplantAsync(req, env.Maya)));
                Assert.All(results, r => Assert.True(r.Success, r.Message));
                Assert.Single(results.Select(r => r.OperationId).Distinct());
                Assert.Single(results, r => !r.Duplicate);
                var ops = await OpsAsync(env, bed.Id);
                Assert.Equal(new[] { "Transplant", "Loss" }, ops.Select(o => o.Type));
                Assert.Equal(1, await env.ScalarAsync<int>("SELECT COUNT(*) FROM dbo.SeedSowings WHERE BedSowingId = @B", ("@B", bed.Id)));
                Assert.Equal(3m, await PoolTraysAsync(env, p, "150 Cavity"));
                var b = await BedAsync(env, bed.Id);
                Assert.Equal((0m, 450m, 50m), (b.RemainingQuantity, b.TransplantedQuantity, b.LostQuantity));
            }
            finally { await RetireBedPolyhouseAsync(env, p); }
        }

        [SkippableFact]
        public async Task TransplantAll_OnlyTheTransplanter_NeverAnotherUserOrAnAdministrator()
        {
            var env = await OpenAsync();
            var (p, _, bed) = await RemainderSetupAsync(env, 500, "150 Cavity", 5);
            try
            {
                var admin = await env.ScalarAsync<int>(@"SELECT TOP 1 u.Id FROM dbo.IMSUsers u JOIN dbo.UserRoles ur ON ur.UserId = u.Id JOIN dbo.Roles r ON r.Id = ur.RoleId
WHERE u.IsActive = 1 AND COALESCE(NULLIF(LTRIM(RTRIM(r.Name)), N''), r.RoleName) = N'System Administrator' AND u.Id <> @T ORDER BY u.Id", ("@T", env.Transplanter));
                foreach (var actor in new[] { env.NotTransplanter, new BedSowingRepository.Actor(admin, "bed-test-admin", true) })
                {
                    var (ok, msg, _, _, _) = await env.Beds.TransplantAsync(Move(env, bed, 500, cavity: "150 Cavity"), actor);
                    Assert.False(ok);
                    Assert.Equal(BedSowingRules.NotTransplanterMessage, msg);
                }
                Assert.Empty(await OpsAsync(env, bed.Id));
                Assert.Equal(500m, (await BedAsync(env, bed.Id)).RemainingQuantity);
            }
            finally { await RetireBedPolyhouseAsync(env, p); }
        }

        [SkippableFact]
        public async Task TransplantAll_RemainderLoss_InHistoryWastageAndDailyOperations_OnTheTransplantDate()
        {
            var env = await OpenAsync();
            var (p, _, bed) = await RemainderSetupAsync(env, 500, "150 Cavity", 5);
            try
            {
                var day = AppClock.Today.AddDays(-3);
                var ops = env.Get<DailyOperationsReportRepository>();
                async Task<decimal> WastageOn(DateTime d) => (await ops.GetAsync(d, d)).Wastage.Where(f => f.Date == d).Sum(f => f.Quantity);
                var before = await WastageOn(day);
                var beforeToday = await WastageOn(AppClock.Today);

                var (ok, msg, _, _, _) = await env.Beds.TransplantAsync(Move(env, bed, 500, daysAgo: 3, cavity: "150 Cavity"), env.Maya);
                Assert.True(ok, msg);

                // Wastage report: the bed loss on the transplant date (not today)
                var w = Assert.Single(await env.Get<WastageRepository>().GetAsync(day, day, env.AreaId, bed.BedSowingCode),
                    x => x.ProductionType == WastageRepository.BedLossProductionType);
                Assert.Equal(50m, w.Quantity);
                Assert.DoesNotContain(await env.Get<WastageRepository>().GetAsync(AppClock.Today, AppClock.Today, env.AreaId, bed.BedSowingCode),
                    x => x.ProductionType == WastageRepository.BedLossProductionType);
                // Daily Operations: +50 wastage on that day, nothing on the recorded day
                Assert.Equal(before + 50, await WastageOn(day));
                Assert.Equal(beforeToday, await WastageOn(AppClock.Today));
                // Bed history: the loss row with its business date and the recorded time kept separately
                var loss = Assert.Single(await OpsAsync(env, bed.Id), o => o.Type == "Loss");
                Assert.Equal(day, loss.Date);
                Assert.Equal(AppClock.Today, AppClock.ToIndiaTime(await env.ScalarAsync<DateTime>(
                    "SELECT CreatedDate FROM dbo.BedSowingOperations WHERE BedSowingId = @B AND OperationType = N'Loss'", ("@B", bed.Id))).Date);
                // tray / material histories never negative
                Assert.True(await MinHistoryTraysAsync(env, p) >= 0);
                Assert.True(await MinHistoryMaterialAsync(env, p) >= 0);
            }
            finally { await RetireBedPolyhouseAsync(env, p); }
        }
    }
}
