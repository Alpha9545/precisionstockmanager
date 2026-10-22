using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
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
using PlantStockManager.Services;
using GiveToMainOfficePage = PlantStockManager.Pages.Production.CuttingStock.GiveToMainOfficeModel;
using PotBatchDetailsPage = PlantStockManager.Pages.Production.PotBatch.DetailsModel;

namespace PlantStockManager.Tests
{
    // 2026-10-21 security review (third round), against a SCRATCH COPY only (PSM_SCRATCH_CONNECTION, database name
    // PlantsIMS2_Scratch_*; SKIPPED otherwise). Every test builds its own variety and stock through the real repositories.
    //   * Cutting Stock > Send to Main Office reserved the cuttings AGAIN for every repeat of the same form. It now carries
    //     the page's one-time token into dbo.InternalTransfers.SubmissionToken (unique; durable): one delivery per form,
    //     a post without the token is refused.
    //   * Pot Batch > production entry recorded the pots (and took the empty pots) AGAIN for every repeat of the same
    //     form. It now takes a one-time SubmissionTokenGuard token.
    [Collection("ScratchDb")]
    public class CuttingSendAndPotEntryReplayE2ETests
    {
        private const string ConnectionVariable = "PSM_SCRATCH_CONNECTION";
        private const string RequiredPrefix = "PlantsIMS2_Scratch_";
        private const int MainArea = 2, MainPolyhouse = 4;              // as MotherPlantAreaInventoryE2ETests
        private const int MpArea = 127;
        private const int SupervisorId = 9, Recorder = 21;
        private static readonly MotherPlantTransferRepository.Actor Actor = new(Recorder, "replay-test");
        private static bool Any(int _) => true;

        private sealed class NullTempData : ITempDataProvider
        {
            public IDictionary<string, object> LoadTempData(HttpContext context) => new Dictionary<string, object>();
            public void SaveTempData(HttpContext context, IDictionary<string, object> values) { }
        }

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
            public ClaimsPrincipal Admin = null!;
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

            public T Page<T>() where T : PageModel
            {
                var http = new DefaultHttpContext { User = Admin, RequestServices = Provider };
                var page = ActivatorUtilities.CreateInstance<T>(Provider);
                page.PageContext = new PageContext(new ActionContext(http, new Microsoft.AspNetCore.Routing.RouteData(), new CompiledPageActionDescriptor()));
                page.TempData = new TempDataDictionary(http, new NullTempData());
                return page;
            }

            public Task<decimal> PotsAsync(int area, string size)
                => ScalarAsync<decimal>("SELECT ISNULL(SUM(PhysicalQuantity), 0) FROM dbo.EmptyPotInventory WHERE AreaId = @A AND PotSize = @S", ("@A", area), ("@S", size));
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
            }
            var services = new ServiceCollection();
            services.AddSingleton<IConfiguration>(new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:DefaultConnection"] = cs }).Build());
            services.AddLogging();
            services.AddSingleton<DatabaseHelper>();
            services.AddSingleton<AreaAccessService>();
            services.AddSingleton<IOptions<SecurityOptions>>(Options.Create(new SecurityOptions { FullAccessRoleNames = new[] { "System Administrator" } }));
            services.AddSingleton<IOptionsMonitor<SeedlingWorkflowOptions>>(new StaticMonitor<SeedlingWorkflowOptions>(new SeedlingWorkflowOptions()));
            services.AddSingleton<SeedlingAreaScope>();
            services.AddSingleton<SubmissionTokenGuard>();
            services.AddScoped<UserClaimsFactory>();
            services.AddSingleton<IAuthorizationPolicyProvider, PermissionAuthorizationPolicyProvider>();
            services.AddScoped<IAuthorizationHandler, MinimumAuthorizationLevelHandler>();
            services.AddAuthorization();
            foreach (var t in typeof(DatabaseHelper).Assembly.GetTypes().Where(t => t.Namespace == "PlantStockManager.Data" && t.IsClass && t.Name.EndsWith("Repository")))
                services.AddScoped(t);
            var env = new Env { Cs = cs!, Provider = services.BuildServiceProvider() };
            env.Cocopeat = await env.ScalarAsync<int>("SELECT Id FROM dbo.MaterialTypes WHERE Code = 'COCOPEAT'");

            var admin = await env.ScalarAsync<int>(@"SELECT TOP 1 ur.UserId FROM dbo.UserRoles ur JOIN dbo.Roles r ON r.Id = ur.RoleId
JOIN dbo.IMSUsers u ON u.Id = ur.UserId AND u.IsActive = 1 WHERE r.Name = N'System Administrator' ORDER BY ur.UserId");
            Skip.If(admin == 0, "No active System Administrator in this copy.");
            using var scope = env.Provider.CreateScope();
            var factory = scope.ServiceProvider.GetRequiredService<UserClaimsFactory>();
            env.Admin = await factory.CreatePrincipalAsync((await factory.GetActiveUserAsync(admin))!);
            return env;
        }

        private static async Task<int> NewSpeciesAsync(Env env, string tag)
            => await env.ScalarAsync<int>(@"INSERT INTO dbo.PlantSpecies (PlantTypeId, Name, ReadyStockDays)
VALUES ((SELECT MIN(Id) FROM dbo.PlantTypes), @N, 30); SELECT CAST(SCOPE_IDENTITY() AS INT)", ("@N", tag));

        private static string Tag() => "ZZTEST Replay3 " + Guid.NewGuid().ToString("N")[..10];

        // ===== Cutting Stock > Send to Main Office ====================================================================

        // `quantity` cuttings of a NEW variety in the Mother Plant Area's Cutting Stock (as a harvest records them).
        private static async Task<int> CuttingPoolAsync(Env env, string tag, decimal quantity)
        {
            var species = await NewSpeciesAsync(env, tag);
            var repo = env.Get<CuttingStockRepository>();
            using var conn = new SqlConnection(env.Cs);
            await conn.OpenAsync();
            using var tx = conn.BeginTransaction();
            var pool = await repo.GetOrCreateLockedAsync(conn, tx, species, MpArea, "replay-test");
            var (ok, msg) = await repo.RecordTransactionAsync(conn, tx, pool, quantity, "Harvest", "CuttingProduction", null, Recorder, tag);
            Assert.True(ok, msg);
            tx.Commit();
            return pool;
        }

        private static async Task<(int Deliveries, decimal InTransit, decimal Physical)> CuttingStateAsync(Env env, int pool) => (
            await env.ScalarAsync<int>("SELECT COUNT(*) FROM dbo.InternalTransfers WHERE StockType = N'Cutting' AND SourceCuttingStockId = @P", ("@P", pool)),
            await env.ScalarAsync<decimal>("SELECT InTransitQuantity FROM dbo.CuttingStock WHERE Id = @P", ("@P", pool)),
            await env.ScalarAsync<decimal>("SELECT PhysicalQuantity FROM dbo.CuttingStock WHERE Id = @P", ("@P", pool)));

        private static async Task<GiveToMainOfficePage> SendAsync(Env env, int pool, Guid token, decimal quantity = 10m)
        {
            var page = env.Page<GiveToMainOfficePage>();
            page.SubmissionToken = token;
            await page.OnPostSendAsync(pool, quantity, MainArea, "replay-test", MpArea);
            return page;
        }

        [SkippableFact]
        public async Task SendCuttings_SameFormTwice_OrThreeAtOnce_ReservesOnce_AndAPostWithoutTokenSendsNothing()
        {
            var env = await OpenAsync();
            var pool = await CuttingPoolAsync(env, Tag(), 100m);
            Assert.Equal((0, 0m, 100m), await CuttingStateAsync(env, pool));

            // the GET hands the form a token
            var get = env.Page<GiveToMainOfficePage>();
            await get.OnGetAsync(MpArea);
            Assert.NotEqual(Guid.Empty, get.SubmissionToken);

            // the same form twice in a row: ONE delivery, 10 reserved; the repeat says so
            await SendAsync(env, pool, get.SubmissionToken);
            var replay = await SendAsync(env, pool, get.SubmissionToken);
            Assert.Equal((1, 10m, 100m), await CuttingStateAsync(env, pool));
            Assert.Equal(InternalTransferRepository.AlreadySentMessage, replay.TempData["Success"]);

            // a second form submitted three times at once: ONE more delivery
            var second = Guid.NewGuid();
            await Task.WhenAll(Enumerable.Range(0, 3).Select(_ => Task.Run(() => SendAsync(env, pool, second))));
            Assert.Equal((2, 20m, 100m), await CuttingStateAsync(env, pool));

            // a post without the form's token (or with an unreadable one -- it binds as Guid.Empty): nothing is sent
            var forged = await SendAsync(env, pool, Guid.Empty);
            Assert.Equal((2, 20m, 100m), await CuttingStateAsync(env, pool));
            Assert.NotNull(forged.TempData["Error"]);

            // every delivery is still pending; the reservation matches the deliveries exactly
            Assert.Equal(20m, await env.ScalarAsync<decimal>(@"SELECT SUM(Quantity) FROM dbo.InternalTransfers
WHERE StockType = N'Cutting' AND SourceCuttingStockId = @P AND Status = N'PendingConfirmation'", ("@P", pool)));
        }

        [SkippableFact]
        public async Task SendCuttings_AReplayAfterTheStockRanOut_StillReportsAlreadySent_NotAShortage()
        {
            var env = await OpenAsync();
            var pool = await CuttingPoolAsync(env, Tag(), 10m);
            var token = Guid.NewGuid();
            await SendAsync(env, pool, token, 10m);                 // every cutting is now reserved
            var replay = await SendAsync(env, pool, token, 10m);
            Assert.Equal(InternalTransferRepository.AlreadySentMessage, replay.TempData["Success"]);
            Assert.Equal((1, 10m, 10m), await CuttingStateAsync(env, pool));
        }

        // ===== Pot Batch > production entry ===========================================================================

        // A Main Area sowing approved into Ready Stock, sent to and received in the Mother Plant Area for Pot Production, empty
        // pots in that Area, and a pot batch of 200 seedlings started from it (as MotherPlantAreaInventoryE2ETests).
        private static async Task<(int Batch, string Size)> PotBatchAsync(Env env, string tag)
        {
            Skip.IfNot(await env.ScalarAsync<int>(PotBatchSchema.ProbeSql) == 1, "2026-10-15_PotProductionFromReadyStock.sql is not applied to this scratch database.");
            var confirmer = (await env.Get<UserRoleRepository>().GetReadyConfirmersAsync(MpArea)).Select(u => u.EmployeeID).FirstOrDefault();
            Skip.If(confirmer == 0, "No user may confirm pot batches READY in the Mother Plant Area of this copy.");
            var size = await env.ScalarAsync<string>("SELECT TOP 1 Name FROM dbo.PotSizes WHERE IsActive = 1 ORDER BY Id");
            Skip.If(size == null, "No active pot size in this copy.");

            var species = await NewSpeciesAsync(env, tag);
            var lot = await env.ScalarAsync<int>(@"INSERT INTO dbo.SeedStock (SpeciesId, AreaId, BatchNo, Unit, PhysicalQuantity, CreatedBy)
VALUES (@S, @A, @B, N'Seeds', 100000, N'replay-test'); SELECT CAST(SCOPE_IDENTITY() AS INT)", ("@S", species), ("@A", MainArea), ("@B", "LOT-" + tag[^10..]));

            const string cavity = "24 Cavity";
            const int plants = 480;
            var (tOk, tMsg, _, _) = await env.Get<TrayStockRepository>().AddStockAsync(MainArea, MainPolyhouse, cavity, plants / 24 + 5, DateTime.UtcNow,
                Random.Shared.Next(1, int.MaxValue), SupervisorId, "replay-test", "replay test setup", Any);
            Assert.True(tOk, tMsg);
            var (mOk, mMsg, _) = await env.Get<MaterialStockRepository>().AdjustAsync(MainArea, MainPolyhouse, env.Cocopeat, 1, 500m, "replay test setup", Guid.NewGuid(), Recorder, Any);
            Assert.True(mOk, mMsg);
            var sowing = new SeedSowing
            {
                SourceSeedStockId = lot, SeedQuantity = plants, CavityType = cavity, SowingDate = AppClock.Today.AddDays(-40),
                PolyhouseId = MainPolyhouse, SupervisorId = SupervisorId, CreatedBy = "replay-test", CreatedById = Recorder, GrowingMaterialTypeId = env.Cocopeat
            };
            var (sOk, sMsg, sowingId) = await env.Get<SeedSowingRepository>().InsertAsync(sowing, Recorder, Any);
            Assert.True(sOk, sMsg);
            var (cOk, cMsg, _) = await env.Get<ReadyConfirmationRepository>().ConfirmAsync(sowingId, plants / 24, null, null, null, "replay-test", SupervisorId, Any);
            Assert.True(cOk, cMsg);
            var main = await env.ScalarAsync<int>("SELECT Id FROM dbo.ReadyStock WHERE SeedSowingId = @S AND OriginType = N'Sowing'", ("@S", sowingId));

            var transfers = env.Get<MotherPlantTransferRepository>();
            var (trOk, trMsg, transferId, _) = await transfers.CreateAsync(
                new(main, 240, MpArea, null, AppClock.Today, "replay test", Guid.NewGuid(), MotherPlantTransferRules.PurposePotProduction), Actor, Any);
            Assert.True(trOk, trMsg);
            var (rOk, rMsg, received) = await transfers.ReceiveAsync(transferId, 240, null, Actor, Any);
            Assert.True(rOk, rMsg);

            var (pOk, pMsg, _) = await env.Get<EmptyPotInventoryRepository>().AddAreaStockAsync(MpArea, size!, 300, AppClock.Today, "replay test pots",
                Recorder, "replay-test", EmptyPotStockRules.MotherPlantAreaType);
            Assert.True(pOk, pMsg);

            var (bOk, bMsg, batch) = await env.Get<PotBatchRepository>().CreateFromReadyStockAsync(new PotProductionBatch
            {
                SourceReadyStockId = received!.Value, CuttingAllocated = 200, PotSize = size!, ProductionStartDate = AppClock.Today,
                ExpectedReadyDate = AppClock.Today.AddDays(30), SupervisorId = confirmer, Remarks = tag, CreatedBy = "replay-test"
            }, Recorder, Any);
            Assert.True(bOk, bMsg);
            return (batch, size!);
        }

        private static async Task<(int Entries, decimal Produced)> EntriesAsync(Env env, int batch) => (
            await env.ScalarAsync<int>("SELECT COUNT(*) FROM dbo.PotProductionEntries WHERE BatchId = @B", ("@B", batch)),
            await env.ScalarAsync<decimal>("SELECT ISNULL(SUM(Quantity), 0) FROM dbo.PotProductionEntries WHERE BatchId = @B", ("@B", batch)));

        private static async Task<PotBatchDetailsPage> EntryAsync(Env env, int batch, string? token)
        {
            var page = env.Page<PotBatchDetailsPage>();
            (page.EntryDate, page.EntryQuantity, page.EntryRemarks, page.SubmitToken) = (AppClock.Today, 10m, "replay-test", token);
            await page.OnPostEntryAsync(batch);
            return page;
        }

        [SkippableFact]
        public async Task PotEntry_SameFormTwice_OrThreeAtOnce_RecordsOnce_AndAPostWithoutTokenRecordsNothing()
        {
            var env = await OpenAsync();
            var (batch, size) = await PotBatchAsync(env, Tag());
            var pots0 = await env.PotsAsync(MpArea, size);

            var get = env.Page<PotBatchDetailsPage>();
            Assert.IsType<PageResult>(await get.OnGetAsync(batch));
            Assert.False(string.IsNullOrWhiteSpace(get.SubmitToken));

            await EntryAsync(env, batch, get.SubmitToken);
            var replay = await EntryAsync(env, batch, get.SubmitToken);
            Assert.Equal((1, 10m), await EntriesAsync(env, batch));
            Assert.Equal(pots0 - 10, await env.PotsAsync(MpArea, size));
            Assert.NotNull(replay.TempData["Error"]);

            var second = SubmissionTokenGuard.NewToken();
            await Task.WhenAll(Enumerable.Range(0, 3).Select(_ => Task.Run(() => EntryAsync(env, batch, second))));
            Assert.Equal((2, 20m), await EntriesAsync(env, batch));

            await EntryAsync(env, batch, null);
            await EntryAsync(env, batch, "");
            Assert.Equal((2, 20m), await EntriesAsync(env, batch));
            Assert.Equal(pots0 - 20, await env.PotsAsync(MpArea, size));
        }
    }
}
