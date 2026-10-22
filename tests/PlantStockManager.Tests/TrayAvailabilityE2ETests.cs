using System.Reflection;
using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using PlantStockManager.Authorization;
using PlantStockManager.Data;
using PlantStockManager.Models;
using PlantStockManager.Pages.Production.SeedSowing;
using PlantStockManager.Services;
using SeedSowing = PlantStockManager.Models.SeedSowing;

namespace PlantStockManager.Tests
{
    // Empty trays available per cavity on the sowing pages (TrayStockRepository.GetCavityAvailabilityAsync +
    // TrayAvailabilityForm), end-to-end against a SCRATCH COPY (PlantsIMS2_Scratch_* only; SKIPPED without
    // PSM_SCRATCH_CONNECTION or the 2026-10-14 Mother Plant Area tray migration):
    //   - every Main Office Polyhouse and every Mother Plant Area, every cavity: the balance shown = the pool's balance =
    //     its ledger; a location without a pool shows 0; a Mother Plant Area is ONE row (its Area pool) however many of
    //     its Polyhouses are offered -- nothing counted twice;
    //   - only the locations the page offers the user are listed (the page handlers, with a real user's Area claims);
    //     a Polyhouse of another Area and a chosen location outside the list are ignored; a cavity change re-reads;
    //   - concurrent sowings never take more trays than the pool holds; the panel then shows what is left.
    [Collection("ScratchDb")]
    public class TrayAvailabilityE2ETests
    {
        private const string ConnectionVariable = "PSM_SCRATCH_CONNECTION";
        private const string RequiredPrefix = "PlantsIMS2_Scratch_";
        private const int MainArea = 2, MpArea = 127;
        private const int SowingSupervisor = 9, Recorder = 21;
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

            public async Task<List<int>> IdsAsync(string sql)
            {
                var list = new List<int>();
                using var conn = new SqlConnection(Cs);
                await conn.OpenAsync();
                using var cmd = new SqlCommand(sql, conn);
                using var r = await cmd.ExecuteReaderAsync();
                while (await r.ReadAsync()) list.Add(r.GetInt32(0));
                return list;
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
                using var p = new SqlCommand(MotherPlantAreaSchema.ProbeSql, conn);
                Skip.If((int)(await p.ExecuteScalarAsync())! != 1, "2026-10-14_MotherPlantAreaTraysAndTransferPurpose.sql is not applied to this scratch database.");
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
            return new Env { Cs = cs!, Provider = services.BuildServiceProvider() };
        }

        private static async Task<int> NewPolyhouseAsync(Env env, int areaId)
            => await env.ScalarAsync<int>("INSERT INTO dbo.Polyhouses (Name, AreaId) VALUES (@N, @A); SELECT CAST(SCOPE_IDENTITY() AS INT)",
                ("@N", "ZZTEST TA " + Guid.NewGuid().ToString("N")[..8]), ("@A", areaId));

        private static async Task AddTraysAsync(Env env, int areaId, int? polyhouseId, string size, decimal trays)
            => Assert.True((await env.Get<TrayStockRepository>().AddStockAsync(areaId, polyhouseId, size, trays, DateTime.UtcNow.AddMinutes(-5),
                Random.Shared.Next(1, int.MaxValue), SowingSupervisor, "tray-avail-test", "tray availability test", Any)).Success);

        // the pool a location's sowing would use: (balance, ledger total)
        private static async Task<(decimal Balance, decimal Ledger)> PoolAsync(Env env, int areaId, int? polyhouseId, string size)
        {
            const string where = "t.IsActive = 1 AND t.AreaId = @A AND t.TraySize = @S AND ((@P IS NULL AND t.PolyhouseId IS NULL) OR t.PolyhouseId = @P)";
            return (await env.ScalarAsync<decimal>($"SELECT ISNULL(SUM(t.PhysicalQuantity), 0) FROM dbo.TrayStock t WHERE {where}", ("@A", areaId), ("@P", polyhouseId), ("@S", size)),
                await env.ScalarAsync<decimal>($"SELECT ISNULL(SUM(x.Quantity), 0) FROM dbo.TrayStockTransactions x JOIN dbo.TrayStock t ON t.Id = x.TrayStockId WHERE {where}",
                    ("@A", areaId), ("@P", polyhouseId), ("@S", size)));
        }

        private static object? Prop(object o, string name) => o.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance)!.GetValue(o);
        private static List<object> Rows(object result) => ((System.Collections.IEnumerable)Prop(result, "rows")!).Cast<object>().ToList();

        // =====================================================================================================

        [SkippableFact]
        public async Task EveryMainOfficePolyhouseAndMotherPlantArea_EveryCavity_EqualsPoolAndLedger_ZeroShown_AreaCountedOnce()
        {
            var env = await OpenAsync();
            var repo = env.Get<TrayStockRepository>();
            var fresh = await NewPolyhouseAsync(env, MainArea);           // no pool yet: 0 for every cavity
            var stocked = await NewPolyhouseAsync(env, MainArea);
            await AddTraysAsync(env, MainArea, stocked, "150 Cavity", 7);
            var mpExtra1 = await NewPolyhouseAsync(env, MpArea);
            var mpExtra2 = await NewPolyhouseAsync(env, MpArea);
            await AddTraysAsync(env, MpArea, null, "42 Cavity", 3);

            var mainPolyhouses = await env.IdsAsync(@"SELECT p.Id FROM dbo.Polyhouses p JOIN dbo.Area a ON a.Id = p.AreaId
WHERE a.IsActive = 1 AND a.AreaType = N'MainOffice' ORDER BY p.Id");
            var mainAreaOf = new Dictionary<int, int>();
            foreach (var p in mainPolyhouses) mainAreaOf[p] = await env.ScalarAsync<int>("SELECT AreaId FROM dbo.Polyhouses WHERE Id = @P", ("@P", p));
            var mpAreas = await env.IdsAsync("SELECT Id FROM dbo.Area WHERE IsActive = 1 AND AreaType = N'MotherPlant' ORDER BY Id");
            var candidates = mainPolyhouses.Select(p => new TrayLocation(mainAreaOf[p], p))
                .Concat(new[] { new TrayLocation(MpArea, mpExtra1), new TrayLocation(MpArea, mpExtra2) })     // two Polyhouses of one MP Area...
                .Concat(mpAreas.Select(a => new TrayLocation(a, null)))                                         // ...and the Area itself
                .ToList();

            foreach (var size in DirectSowingRules.CavityTypes)
            {
                var rows = await repo.GetCavityAvailabilityAsync(size, candidates);
                Assert.Equal(rows.Count, rows.Select(r => r.Key).Distinct().Count());                       // nothing listed twice
                Assert.Equal(mainPolyhouses.Count + mpAreas.Count, rows.Count);
                Assert.Single(rows, r => r.AreaId == MpArea);                                                 // ONE row for the MP Area
                Assert.All(rows, r => Assert.Equal(size, r.TraySize));
                foreach (var r in rows)
                {
                    Assert.Equal(mpAreas.Contains(r.AreaId), r.AreaPool);
                    var (balance, ledger) = await PoolAsync(env, r.AreaId, r.AreaPool ? null : r.PolyhouseId, size);
                    Assert.Equal(balance, r.Available);
                    Assert.Equal(ledger, r.Available);                                                         // the ledger explains every balance
                }
                Assert.Equal(0m, rows.Single(r => r.PolyhouseId == fresh).Available);
                Assert.Equal(size == "150 Cavity" ? 7m : 0m, rows.Single(r => r.PolyhouseId == stocked).Available);
                // the MP Area row is its Area pool only -- never a sum with (retired) Polyhouse pools of that Area
                Assert.Equal((await PoolAsync(env, MpArea, null, size)).Balance, rows.Single(r => r.AreaId == MpArea).Available);
                Assert.Equal(0m, await env.ScalarAsync<decimal>("SELECT ISNULL(SUM(PhysicalQuantity), 0) FROM dbo.TrayStock WHERE IsActive = 1 AND AreaId = @A AND PolyhouseId IS NOT NULL AND TraySize = @S",
                    ("@A", MpArea), ("@S", size)));
            }
        }

        [SkippableFact]
        public async Task OnlyOfferedLocations_ForeignPolyhouseLeftOut_OutsideSelectionIgnored_CavityChangeReReads_ShortageFlagged()
        {
            var env = await OpenAsync();
            var repo = env.Get<TrayStockRepository>();
            var p1 = await NewPolyhouseAsync(env, MainArea);
            var p2 = await NewPolyhouseAsync(env, MainArea);
            var mpPolyhouse = await NewPolyhouseAsync(env, MpArea);
            await AddTraysAsync(env, MainArea, p1, "150 Cavity", 7);

            // a Polyhouse named with an Area it does not belong to is left out
            Assert.Empty(await repo.GetCavityAvailabilityAsync("150 Cavity", new[] { new TrayLocation(MainArea, mpPolyhouse) }));
            // a non-Mother-Plant location without a Polyhouse has no pool
            Assert.Empty(await repo.GetCavityAvailabilityAsync("150 Cavity", new[] { new TrayLocation(MainArea, null) }));

            var eligible = new List<TrayLocation> { new(MainArea, p1) };
            var outside = await TrayAvailabilityForm.BuildAsync(repo, "150 Cavity", eligible, new TrayLocation(MainArea, p2), 1);
            Assert.Null(Prop(outside, "selectedKey"));                                                       // p2 was not offered
            Assert.Equal(new[] { $"p{p1}" }, Rows(outside).Select(r => (string)Prop(r, "key")!));

            var mine = new TrayLocation(MainArea, p1);
            var enough = await TrayAvailabilityForm.BuildAsync(repo, "150 Cavity", eligible, mine, 7);
            Assert.Equal((true, 7m, $"p{p1}"), ((bool)Prop(enough, "enough")!, (decimal)Prop(enough, "selectedAvailable")!, (string?)Prop(enough, "selectedKey")));
            Assert.False((bool)Prop(await TrayAvailabilityForm.BuildAsync(repo, "150 Cavity", eligible, mine, 8), "enough")!);
            // the user changes the cavity after entering a quantity: re-read, now short
            var changed = await TrayAvailabilityForm.BuildAsync(repo, "9 Cavity", eligible, mine, 1);
            Assert.Equal((0m, false), ((decimal)Prop(changed, "selectedAvailable")!, (bool)Prop(changed, "enough")!));
            Assert.Contains("No empty 9 Cavity trays", (string?)Prop(changed, "message"));
            var none = await TrayAvailabilityForm.BuildAsync(repo, "10 Cavity", eligible, mine, 1);
            Assert.False((bool)Prop(none, "ok")!);
            Assert.Empty(Rows(none));
        }

        private static T Page<T>(Env env, params int[] areas) where T : PageModel
        {
            var page = ActivatorUtilities.CreateInstance<T>(env.Provider);
            var identity = new ClaimsIdentity(areas.Select(a => new Claim(AreaAccessService.AreaAccessClaimType, a.ToString())), "test");
            page.PageContext = new PageContext { HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(identity) } };
            return page;
        }

        [SkippableFact]
        public async Task PageHandlers_ListOnlyTheUsersOwnLocations_MainOfficeByPolyhouse_MotherPlantByArea()
        {
            var env = await OpenAsync();
            var p = await NewPolyhouseAsync(env, MainArea);
            await AddTraysAsync(env, MainArea, p, "102 Cavity", 4);

            // Confirm Cutting Sowing: Main Office Area only -> its Polyhouses, no Mother Plant Area
            var mainUser = Page<CreateFromCuttingModel>(env, MainArea);
            var r1 = (await mainUser.OnGetTrayAvailabilityAsync("102 Cavity", p, 102 * 3)).Value!;
            var keys1 = Rows(r1).Select(x => (string)Prop(x, "key")!).ToList();
            Assert.Contains($"p{p}", keys1);
            Assert.DoesNotContain($"a{MpArea}", keys1);
            Assert.All(Rows(r1), x => Assert.False((bool)Prop(x, "areaPool")!));
            Assert.Equal((4m, 3m, true), ((decimal)Prop(r1, "selectedAvailable")!, (decimal)Prop(r1, "needed")!, (bool)Prop(r1, "enough")!));

            // Mother Plant Area only -> ONE Area row, none of the Main Office Polyhouses; a Main Office Polyhouse posted as the
            // selection is not one of this user's -> not selected
            var mpUser = Page<CreateFromCuttingModel>(env, MpArea);
            var r2 = (await mpUser.OnGetTrayAvailabilityAsync("102 Cavity", p, 102)).Value!;
            Assert.Equal(new[] { $"a{MpArea}" }, Rows(r2).Select(x => (string)Prop(x, "key")!));
            Assert.Null(Prop(r2, "selectedKey"));

            // Direct Seed Sowing: Main Office Polyhouses only, even for a user of the Mother Plant Area
            var seedUser = Page<CreateModel>(env, MainArea, MpArea);
            var r3 = (await seedUser.OnGetTrayAvailabilityAsync("102 Cavity", p, 103)).Value!;
            Assert.DoesNotContain(Rows(r3), x => (bool)Prop(x, "areaPool")!);
            Assert.Equal(2m, (decimal)Prop(r3, "needed")!);                                                  // CEILING(103 / 102), as the save

            // Transplant into Trays: the batch's own Mother Plant Area only; a user of another Area gets nothing
            var mpBatch = await env.ScalarAsync<int>("SELECT TOP 1 Id FROM dbo.ReadyStock WHERE AreaId = @A ORDER BY Id", ("@A", MpArea));
            Skip.If(mpBatch == 0, "No Ready Stock batch in the Mother Plant Area of this copy.");
            var outsider = Page<PlantStockManager.Pages.Production.Transplanting.CreateModel>(env, MainArea);
            var r4 = Assert.IsType<JsonResult>(await outsider.OnGetTrayAvailabilityAsync(mpBatch, "102 Cavity", null, 204, false)).Value!;
            Assert.False((bool)Prop(r4, "ok")!);
            Assert.Empty(Rows(r4));
            var insider = Page<PlantStockManager.Pages.Production.Transplanting.CreateModel>(env, MpArea);
            var r5 = Assert.IsType<JsonResult>(await insider.OnGetTrayAvailabilityAsync(mpBatch, "102 Cavity", null, 250, false)).Value!;
            Assert.Equal(new[] { $"a{MpArea}" }, Rows(r5).Select(x => (string)Prop(x, "key")!));
            Assert.Equal(($"a{MpArea}", 2m), ((string?)Prop(r5, "selectedKey"), (decimal)Prop(r5, "needed")!));   // complete trays only: FLOOR(250 / 102)
            var r6 = Assert.IsType<JsonResult>(await insider.OnGetTrayAvailabilityAsync(mpBatch, "102 Cavity", null, 250, true)).Value!;
            Assert.Equal(0m, (decimal)Prop(r6, "needed")!);                                                  // waste-only: no trays
        }

        [SkippableFact]
        public async Task ConcurrentSowings_NeverTakeMoreTraysThanThePoolHolds_PanelShowsWhatIsLeft()
        {
            var env = await OpenAsync();
            var p = await NewPolyhouseAsync(env, MainArea);
            await AddTraysAsync(env, MainArea, p, "24 Cavity", 5);
            var cocopeat = await env.ScalarAsync<int>("SELECT Id FROM dbo.MaterialTypes WHERE Code = 'COCOPEAT'");
            Assert.True((await env.Get<MaterialStockRepository>().AdjustAsync(MainArea, p, cocopeat, 1, 500m, "tray-avail setup", Guid.NewGuid(), Recorder, Any,
                DateTime.UtcNow.AddMinutes(-5))).Success);
            var name = "ZZTEST TA " + Guid.NewGuid().ToString("N")[..10];
            var species = await env.ScalarAsync<int>(@"INSERT INTO dbo.PlantSpecies (PlantTypeId, Name, ReadyStockDays)
VALUES ((SELECT MIN(Id) FROM dbo.PlantTypes), @N, 30); SELECT CAST(SCOPE_IDENTITY() AS INT)", ("@N", name));
            var lot = await env.ScalarAsync<int>(@"INSERT INTO dbo.SeedStock (SpeciesId, AreaId, BatchNo, Unit, PhysicalQuantity, CreatedBy)
VALUES (@S, @A, @B, N'Seeds', 100000, N'tray-avail-test'); SELECT CAST(SCOPE_IDENTITY() AS INT)", ("@S", species), ("@A", MainArea), ("@B", "LOT-" + name[^10..]));

            // four sowings at once, 2 trays each (48 seeds of 24 Cavity), 5 trays in the pool: exactly two succeed
            var results = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => env.Get<SeedSowingRepository>().InsertAsync(new SeedSowing
            {
                SourceSeedStockId = lot, SeedQuantity = 48, CavityType = "24 Cavity", SowingDate = AppClock.Today, PolyhouseId = p,
                SupervisorId = SowingSupervisor, CreatedBy = "tray-avail-test", CreatedById = Recorder, GrowingMaterialTypeId = cocopeat,
                SubmissionToken = Guid.NewGuid()
            }, Recorder, Any)));
            Assert.Equal(2, results.Count(r => r.Success));
            Assert.All(results.Where(r => !r.Success), r => Assert.Contains("Insufficient tray stock", r.Message));
            Assert.Equal((1m, 1m), await PoolAsync(env, MainArea, p, "24 Cavity"));                           // never below zero; ledger agrees

            var left = await TrayAvailabilityForm.BuildAsync(env.Get<TrayStockRepository>(), "24 Cavity", new List<TrayLocation> { new(MainArea, p) },
                new TrayLocation(MainArea, p), DirectSowingRules.PhysicalTraysRequired(48, "24 Cavity"));
            Assert.Equal((1m, false), ((decimal)Prop(left, "selectedAvailable")!, (bool)Prop(left, "enough")!));
        }
    }
}
