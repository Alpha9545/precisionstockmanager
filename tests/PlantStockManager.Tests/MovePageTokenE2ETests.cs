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
using PottedMovePage = PlantStockManager.Pages.Production.PottedPlantStock.MoveModel;
using ReadyMovePage = PlantStockManager.Pages.Production.ReadyStock.MoveModel;

namespace PlantStockManager.Tests
{
    // 2026-10-21 security review (fourth round), against a SCRATCH COPY only (PSM_SCRATCH_CONNECTION, database name
    // PlantsIMS2_Scratch_*; SKIPPED otherwise). Potted Plant Stock > Move and Ready Stock > Move passed a missing token
    // (missing / empty / not a GUID -- all bind as Guid.Empty) on as "no token", so the movement ran WITHOUT replay
    // protection. Both pages now refuse such a post before anything is sent or sold. With the form's token: the same
    // form twice, or three times at once, moves the stock once.
    [Collection("ScratchDb")]
    public class MovePageTokenE2ETests
    {
        private const string ConnectionVariable = "PSM_SCRATCH_CONNECTION";
        private const string RequiredPrefix = "PlantsIMS2_Scratch_";
        private const int MainArea = 2, MainPolyhouse = 4;              // as MotherPlantAreaInventoryE2ETests
        private const int SupervisorId = 9, Recorder = 21;
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
            public int AdminId, OutletId, Cocopeat;
            public string PotSize = "";
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

            env.AdminId = await env.ScalarAsync<int>(@"SELECT TOP 1 ur.UserId FROM dbo.UserRoles ur JOIN dbo.Roles r ON r.Id = ur.RoleId
JOIN dbo.IMSUsers u ON u.Id = ur.UserId AND u.IsActive = 1 WHERE r.Name = N'System Administrator' ORDER BY ur.UserId");
            Skip.If(env.AdminId == 0, "No active System Administrator in this copy.");
            env.OutletId = await env.ScalarAsync<int>("SELECT TOP 1 Id FROM dbo.Area WHERE AreaType = N'Outlet' AND IsActive = 1 ORDER BY Id");
            Skip.If(env.OutletId == 0, "No active Outlet Area in this copy.");
            env.PotSize = (await env.ScalarAsync<string>("SELECT TOP 1 Name FROM dbo.PotSizes WHERE IsActive = 1 ORDER BY SortOrder, Id")) ?? "";
            env.Cocopeat = await env.ScalarAsync<int>("SELECT Id FROM dbo.MaterialTypes WHERE Code = 'COCOPEAT'");
            using var scope = env.Provider.CreateScope();
            var factory = scope.ServiceProvider.GetRequiredService<UserClaimsFactory>();
            env.Admin = await factory.CreatePrincipalAsync((await factory.GetActiveUserAsync(env.AdminId))!);
            return env;
        }

        private static string Tag() => "ZZTEST Move " + Guid.NewGuid().ToString("N")[..10];

        private static async Task<int> NewSpeciesAsync(Env env, string tag)
            => await env.ScalarAsync<int>(@"INSERT INTO dbo.PlantSpecies (PlantTypeId, Name, ReadyStockDays)
VALUES ((SELECT MIN(Id) FROM dbo.PlantTypes), @N, 30); SELECT CAST(SCOPE_IDENTITY() AS INT)", ("@N", tag));

        // ===== Potted Plant Stock > Move ==============================================================================

        // 100 potted plants of a NEW variety at the Outlet (an Outlet purchase).
        private static async Task<int> PottedPoolAsync(Env env, string tag)
        {
            var species = await NewSpeciesAsync(env, tag);
            var (ok, msg, _) = await env.Get<OutletPurchaseRepository>().InsertAsync(new OutletPurchase
            {
                PurchaseDate = DateTime.Today, SupplierName = tag, OutletAreaId = env.OutletId, SpeciesId = species,
                PotSize = env.PotSize, Quantity = 100, Remarks = tag, CreatedBy = "move-test"
            }, env.AdminId);
            Assert.True(ok, msg);
            return await env.ScalarAsync<int>("SELECT Id FROM dbo.PottedPlantStock WHERE SpeciesId = @S AND AreaId = @A", ("@S", species), ("@A", env.OutletId));
        }

        private static async Task<(int Transfers, int Sales, decimal Physical, decimal InTransit)> PottedStateAsync(Env env, int pool) => (
            await env.ScalarAsync<int>("SELECT COUNT(*) FROM dbo.InternalTransfers WHERE SourcePottedPlantStockId = @P", ("@P", pool)),
            await env.ScalarAsync<int>("SELECT COUNT(*) FROM dbo.PottedPlantStockTransactions WHERE PottedPlantStockId = @P AND Quantity < 0", ("@P", pool)),
            await env.ScalarAsync<decimal>("SELECT PhysicalQuantity FROM dbo.PottedPlantStock WHERE Id = @P", ("@P", pool)),
            await env.ScalarAsync<decimal>("SELECT InTransitQuantity FROM dbo.PottedPlantStock WHERE Id = @P", ("@P", pool)));

        private static async Task<PottedMovePage> PottedPostAsync(Env env, int pool, Guid token, string destination)
        {
            var p = env.Page<PottedMovePage>();
            (p.Destination, p.Quantity, p.Remarks, p.SubmissionToken) = (destination, 5m, "move-test", token);
            if (destination == PottedMovePage.ToCustomer)
                (p.CustomerName, p.CustomerContact) = ("ZZTEST Move customer", "9000000000");
            else
                p.DestinationAreaId = await env.ScalarAsync<int>("SELECT TOP 1 Id FROM dbo.Area WHERE AreaType = N'MainOffice' AND IsActive = 1 ORDER BY Id");
            await p.OnPostAsync(pool);
            return p;
        }

        private static IEnumerable<string> Errors(PageModel p) => p.ModelState.Values.SelectMany(v => v.Errors).Select(e => e.ErrorMessage);

        [SkippableFact]
        public async Task PottedMove_SendToMainOffice_MissingTokenRefused_ReplayAndSimultaneousSendOnce()
        {
            var env = await OpenAsync();
            var pool = await PottedPoolAsync(env, Tag());
            var start = await PottedStateAsync(env, pool);

            // missing / empty / unreadable token: refused, nothing moves, a fresh token is handed back
            var missing = await PottedPostAsync(env, pool, Guid.Empty, PottedMovePage.ToMainOffice);
            Assert.Contains(PottedMovePage.MissingTokenMessage, Errors(missing));
            Assert.NotEqual(Guid.Empty, missing.SubmissionToken);
            Assert.Equal(start, await PottedStateAsync(env, pool));

            // the same form twice: one partial send
            var token = Guid.NewGuid();
            await PottedPostAsync(env, pool, token, PottedMovePage.ToMainOffice);
            var replay = await PottedPostAsync(env, pool, token, PottedMovePage.ToMainOffice);
            Assert.Equal(InternalTransferRepository.AlreadySentMessage, replay.TempData["Success"]);
            var once = await PottedStateAsync(env, pool);
            Assert.Equal(start.Transfers + 1, once.Transfers);

            // another form three times at once: one more send
            var second = Guid.NewGuid();
            await Task.WhenAll(Enumerable.Range(0, 3).Select(_ => Task.Run(() => PottedPostAsync(env, pool, second, PottedMovePage.ToMainOffice))));
            Assert.Equal(start.Transfers + 2, (await PottedStateAsync(env, pool)).Transfers);
            Assert.Equal(10m, await env.ScalarAsync<decimal>("SELECT SUM(Quantity) FROM dbo.InternalTransfers WHERE SourcePottedPlantStockId = @P", ("@P", pool)));
        }

        [SkippableFact]
        public async Task PottedMove_SellToCustomer_MissingTokenRefused_ReplayAndSimultaneousSellOnce()
        {
            var env = await OpenAsync();
            var pool = await PottedPoolAsync(env, Tag());
            var start = await PottedStateAsync(env, pool);

            var missing = await PottedPostAsync(env, pool, Guid.Empty, PottedMovePage.ToCustomer);
            Assert.Contains(PottedMovePage.MissingTokenMessage, Errors(missing));
            Assert.Equal(start, await PottedStateAsync(env, pool));

            var token = Guid.NewGuid();
            await PottedPostAsync(env, pool, token, PottedMovePage.ToCustomer);
            await PottedPostAsync(env, pool, token, PottedMovePage.ToCustomer);
            Assert.Equal(start.Physical - 5, (await PottedStateAsync(env, pool)).Physical);

            var second = Guid.NewGuid();
            await Task.WhenAll(Enumerable.Range(0, 3).Select(_ => Task.Run(() => PottedPostAsync(env, pool, second, PottedMovePage.ToCustomer))));
            Assert.Equal(start.Physical - 10, (await PottedStateAsync(env, pool)).Physical);
        }

        // ===== Ready Stock > Move =====================================================================================

        // A Main Area sowing of 480 24 Cavity seedlings approved in full -> its Ready Stock batch (as MotherPlantAreaInventoryE2ETests).
        private static async Task<int> ApprovedMainBatchAsync(Env env, string tag)
        {
            var species = await NewSpeciesAsync(env, tag);
            var lot = await env.ScalarAsync<int>(@"INSERT INTO dbo.SeedStock (SpeciesId, AreaId, BatchNo, Unit, PhysicalQuantity, CreatedBy)
VALUES (@S, @A, @B, N'Seeds', 100000, N'move-test'); SELECT CAST(SCOPE_IDENTITY() AS INT)", ("@S", species), ("@A", MainArea), ("@B", "LOT-" + tag[^10..]));
            const string cavity = "24 Cavity";
            const int plants = 480;
            var (tOk, tMsg, _, _) = await env.Get<TrayStockRepository>().AddStockAsync(MainArea, MainPolyhouse, cavity, plants / 24 + 5, DateTime.UtcNow,
                Random.Shared.Next(1, int.MaxValue), SupervisorId, "move-test", "move test setup", Any);
            Assert.True(tOk, tMsg);
            var (mOk, mMsg, _) = await env.Get<MaterialStockRepository>().AdjustAsync(MainArea, MainPolyhouse, env.Cocopeat, 1, 500m, "move test setup", Guid.NewGuid(), Recorder, Any);
            Assert.True(mOk, mMsg);
            var (sOk, sMsg, sowingId) = await env.Get<SeedSowingRepository>().InsertAsync(new SeedSowing
            {
                SourceSeedStockId = lot, SeedQuantity = plants, CavityType = cavity, SowingDate = AppClock.Today.AddDays(-40),
                PolyhouseId = MainPolyhouse, SupervisorId = SupervisorId, CreatedBy = "move-test", CreatedById = Recorder, GrowingMaterialTypeId = env.Cocopeat
            }, Recorder, Any);
            Assert.True(sOk, sMsg);
            var (cOk, cMsg, _) = await env.Get<ReadyConfirmationRepository>().ConfirmAsync(sowingId, plants / 24, null, null, null, "move-test", SupervisorId, Any);
            Assert.True(cOk, cMsg);
            return await env.ScalarAsync<int>("SELECT Id FROM dbo.ReadyStock WHERE SeedSowingId = @S AND OriginType = N'Sowing'", ("@S", sowingId));
        }

        private static async Task<(int Transfers, decimal Available)> ReadyStateAsync(Env env, int batch) => (
            await env.ScalarAsync<int>("SELECT COUNT(*) FROM dbo.InternalTransfers WHERE SourceReadyStockId = @B", ("@B", batch)),
            (await env.Get<ReadyStockRepository>().GetByIdAsync(batch))!.AvailableQuantity);

        private static async Task<ReadyMovePage> ReadyPostAsync(Env env, int batch, Guid token)
        {
            var p = env.Page<ReadyMovePage>();
            (p.Destination, p.DestinationAreaId, p.Remarks, p.SubmissionToken) = (ReadyMovePage.ToOutlet, env.OutletId, "move-test", token);
            await p.OnPostAsync(batch);
            return p;
        }

        [SkippableFact]
        public async Task ReadyMove_SendToOutlet_MissingTokenRefused_ReplayAndSimultaneousSendOnce()
        {
            var env = await OpenAsync();
            var batch = await ApprovedMainBatchAsync(env, Tag());
            var start = await ReadyStateAsync(env, batch);
            Assert.Equal(0, start.Transfers);

            var missing = await ReadyPostAsync(env, batch, Guid.Empty);
            Assert.Contains(ReadyMovePage.MissingTokenMessage, Errors(missing));
            Assert.NotEqual(Guid.Empty, missing.SubmissionToken);
            Assert.Equal(start, await ReadyStateAsync(env, batch));

            var token = Guid.NewGuid();
            var first = await ReadyPostAsync(env, batch, token);
            Assert.True(first.ModelState.IsValid, string.Join(" | ", Errors(first)));
            var afterFirst = await ReadyStateAsync(env, batch);
            Assert.Equal(1, afterFirst.Transfers);
            // the replay sends nothing more (the batch now belongs to the destination, so the page itself refuses it
            // before the token is even needed; the token is the backstop)
            await ReadyPostAsync(env, batch, token);
            Assert.Equal(afterFirst, await ReadyStateAsync(env, batch));

            // a second batch, its form submitted three times at once: sent once
            var other = await ApprovedMainBatchAsync(env, Tag());
            var second = Guid.NewGuid();
            await Task.WhenAll(Enumerable.Range(0, 3).Select(_ => Task.Run(() => ReadyPostAsync(env, other, second))));
            Assert.Equal(1, (await ReadyStateAsync(env, other)).Transfers);
        }
    }
}
