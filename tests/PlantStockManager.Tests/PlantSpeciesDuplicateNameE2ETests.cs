using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using PlantStockManager.Data;
using PlantStockManager.Pages.Admin;

namespace PlantStockManager.Tests
{
    // Administration > Plant Master "Add variety" refuses a duplicate name, end-to-end against a SCRATCH COPY
    // (PlantsIMS2_Scratch_* only; SKIPPED without PSM_SCRATCH_CONNECTION):
    //   - a new name is added exactly once; the same name again in other capitals / spacing / line breaks, or under
    //     another Plant Type, is refused with a clear error and nothing is inserted;
    //   - an existing master name in other capitals is refused; names differing in punctuation or spelling are both added;
    //   - simultaneous adds of one new name insert it once; the existing Ready Stock Days check still applies;
    //   - Edit: keeping a variety's own name (any capitals/spacing) is saved, also for one copy of an existing duplicate
    //     pair; renaming to another variety's name is refused and nothing is saved; simultaneous renames of several
    //     varieties to one name save only one.
    // Every row these tests add is named "ZZTEST DUP ..." and removed at the end of each test.
    [Collection("ScratchDb")]
    public class PlantSpeciesDuplicateNameE2ETests
    {
        private const string ConnectionVariable = "PSM_SCRATCH_CONNECTION";
        private const string RequiredPrefix = "PlantsIMS2_Scratch_";

        private sealed class NullTempData : ITempDataProvider
        {
            public IDictionary<string, object> LoadTempData(HttpContext context) => new Dictionary<string, object>();
            public void SaveTempData(HttpContext context, IDictionary<string, object> values) { }
        }

        private sealed class Env : IAsyncDisposable
        {
            public string Cs = "";
            public ServiceProvider Provider = null!;
            public readonly string Tag = "ZZTEST DUP " + Guid.NewGuid().ToString("N")[..8];

            public async Task<T> ScalarAsync<T>(string sql, params (string Name, object? Value)[] args)
            {
                using var conn = new SqlConnection(Cs);
                await conn.OpenAsync();
                using var cmd = new SqlCommand(sql, conn);
                foreach (var (n, v) in args) cmd.Parameters.AddWithValue(n, v ?? DBNull.Value);
                var r = await cmd.ExecuteScalarAsync();
                return r == null || r == DBNull.Value ? default! : (T)Convert.ChangeType(r, typeof(T));
            }

            public Task<int> CountAsync(string likeTag)
                => ScalarAsync<int>("SELECT COUNT(*) FROM dbo.PlantSpecies WHERE Name LIKE @T", ("@T", likeTag + "%"));

            public PlantModel Page(int typeId, string name, int? days = null)
            {
                var http = new DefaultHttpContext();
                var page = ActivatorUtilities.CreateInstance<PlantModel>(Provider);
                page.PageContext = new PageContext(new ActionContext(http, new Microsoft.AspNetCore.Routing.RouteData(), new CompiledPageActionDescriptor()))
                {
                    ViewData = new ViewDataDictionary(new Microsoft.AspNetCore.Mvc.ModelBinding.EmptyModelMetadataProvider(), new Microsoft.AspNetCore.Mvc.ModelBinding.ModelStateDictionary())
                };
                page.TempData = new TempDataDictionary(http, new NullTempData());
                page.NewSpeciesTypeId = typeId; page.NewSpeciesName = name; page.NewSpeciesScientificName = ""; page.NewSpeciesReadyStockDays = days; page.NewSpeciesColor = null;
                return page;
            }

            public PlantModel EditPage(int id, string name, int? days = null)
            {
                var page = Page(0, "");
                page.EditSpeciesId = id; page.EditSpeciesName = name; page.EditSpeciesScientificName = ""; page.EditSpeciesReadyStockDays = days; page.EditSpeciesColor = null;
                return page;
            }

            // inserted directly (bypassing the page) so a test can also build a pre-existing duplicate pair
            public Task<int> InsertAsync(int typeId, string name, int? days = 32)
                => ScalarAsync<int>("INSERT INTO dbo.PlantSpecies (PlantTypeId, Name, ReadyStockDays) VALUES (@T, @N, @D); SELECT CAST(SCOPE_IDENTITY() AS INT)",
                    ("@T", typeId), ("@N", name), ("@D", days));

            public Task<string> NameAsync(int id) => ScalarAsync<string>("SELECT Name FROM dbo.PlantSpecies WHERE Id = @I", ("@I", id));

            public async ValueTask DisposeAsync()
            {
                using (var conn = new SqlConnection(Cs))
                {
                    await conn.OpenAsync();
                    using var cmd = new SqlCommand("DELETE FROM dbo.PlantSpecies WHERE Name LIKE N'ZZTEST DUP %'", conn);
                    await cmd.ExecuteNonQueryAsync();
                }
                await Provider.DisposeAsync();
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
            services.AddSingleton<DatabaseHelper>();
            services.AddScoped<PlantTypeRepository>();
            services.AddScoped<PlantSpeciesRepository>();
            return new Env { Cs = cs!, Provider = services.BuildServiceProvider() };
        }

        private static async Task<int> TypeIdAsync(Env env, string name)
            => await env.ScalarAsync<int>("SELECT Id FROM dbo.PlantTypes WHERE Name = @N", ("@N", name));

        private static string? Error(PlantModel page) => page.TempData["Error"] as string;

        [SkippableFact]
        public async Task NewName_AddedOnce_CaseSpacingAndOtherPlantType_Refused()
        {
            await using var env = await OpenAsync();
            var seasonal = await TypeIdAsync(env, "SEASONAL VARITIES");
            var chrys = await TypeIdAsync(env, "CHRYSANTHEMUM");
            var total = await env.ScalarAsync<int>("SELECT COUNT(*) FROM dbo.PlantSpecies");

            var first = env.Page(seasonal, env.Tag + " Bello Mix", 32);
            await first.OnPostAddSpeciesAsync();
            Assert.Null(Error(first));
            Assert.Equal(1, await env.CountAsync(env.Tag));

            foreach (var variant in new[] { env.Tag.ToLowerInvariant() + " bello mix", "  " + env.Tag + "   Bello\tMix ", env.Tag + "\r\nBello Mix" })
            {
                var again = env.Page(seasonal, variant, 32);
                await again.OnPostAddSpeciesAsync();
                Assert.Contains("already exists", Error(again));
                Assert.Contains(env.Tag + " Bello Mix", Error(again));                               // names the existing variety
            }
            var otherType = env.Page(chrys, env.Tag + " BELLO MIX", 20);
            await otherType.OnPostAddSpeciesAsync();
            Assert.Contains("already exists", Error(otherType));

            Assert.Equal(1, await env.CountAsync(env.Tag));
            Assert.Equal(total + 1, await env.ScalarAsync<int>("SELECT COUNT(*) FROM dbo.PlantSpecies"));
            Assert.Equal(32, await env.ScalarAsync<int>("SELECT ReadyStockDays FROM dbo.PlantSpecies WHERE Name = @N", ("@N", env.Tag + " Bello Mix")));
        }

        [SkippableFact]
        public async Task ExistingMasterName_InOtherCapitals_Refused_NothingInserted()
        {
            await using var env = await OpenAsync();
            var id = await env.ScalarAsync<int>("SELECT TOP 1 Id FROM dbo.PlantSpecies WHERE Name NOT LIKE N'ZZTEST%' AND PlantTypeId IS NOT NULL ORDER BY Id");
            var name = await env.ScalarAsync<string>("SELECT Name FROM dbo.PlantSpecies WHERE Id = @I", ("@I", id));
            var typeId = await env.ScalarAsync<int>("SELECT PlantTypeId FROM dbo.PlantSpecies WHERE Id = @I", ("@I", id));
            var total = await env.ScalarAsync<int>("SELECT COUNT(*) FROM dbo.PlantSpecies");

            var page = env.Page(typeId, "  " + name.Trim().ToUpperInvariant() + "  ");
            await page.OnPostAddSpeciesAsync();
            Assert.Contains($"(Id {id}", Error(page));
            Assert.Equal(total, await env.ScalarAsync<int>("SELECT COUNT(*) FROM dbo.PlantSpecies"));
        }

        [SkippableFact]
        public async Task PunctuationOrSpellingDifferences_AreSeparateVarieties()
        {
            await using var env = await OpenAsync();
            var seasonal = await TypeIdAsync(env, "SEASONAL VARITIES");
            foreach (var n in new[] { " S.C. White", " SC White", " Success 360 Purple", " Success 360 Violet", " Success 360 Blue" })
            {
                var page = env.Page(seasonal, env.Tag + n);
                await page.OnPostAddSpeciesAsync();
                Assert.Null(Error(page));
            }
            Assert.Equal(5, await env.CountAsync(env.Tag));
        }

        [SkippableFact]
        public async Task SimultaneousAddsOfOneName_InsertItOnce()
        {
            await using var env = await OpenAsync();
            var seasonal = await TypeIdAsync(env, "SEASONAL VARITIES");
            var pages = Enumerable.Range(0, 8).Select(i => env.Page(seasonal, i % 2 == 0 ? env.Tag + " Race" : env.Tag.ToLowerInvariant() + "  race")).ToList();
            await Task.WhenAll(pages.Select(p => p.OnPostAddSpeciesAsync()));
            Assert.Equal(1, await env.CountAsync(env.Tag));
            Assert.Equal(7, pages.Count(p => Error(p)?.Contains("already exists") == true));
        }

        // ---- Edit (rename) ---------------------------------------------------------------------------------------------

        [SkippableFact]
        public async Task Edit_KeepingOwnName_InAnyCapitalsOrSpacing_IsSaved()
        {
            await using var env = await OpenAsync();
            var seasonal = await TypeIdAsync(env, "SEASONAL VARITIES");
            var id = await env.InsertAsync(seasonal, env.Tag + " Keep");

            var same = env.EditPage(id, env.Tag + " Keep", 40);                                  // unchanged name, new days
            await same.OnPostEditSpeciesAsync();
            Assert.Null(Error(same));
            Assert.Equal(40, await env.ScalarAsync<int>("SELECT ReadyStockDays FROM dbo.PlantSpecies WHERE Id = @I", ("@I", id)));

            var variant = env.EditPage(id, env.Tag.ToUpperInvariant() + "   KEEP", 40);          // own name, other capitals/spacing
            await variant.OnPostEditSpeciesAsync();
            Assert.Null(Error(variant));
            Assert.Equal(env.Tag.ToUpperInvariant() + "   KEEP", await env.NameAsync(id));
        }

        [SkippableFact]
        public async Task Edit_RenameToAnotherVarietysName_Refused_RowUnchanged_FreshNameSaved()
        {
            await using var env = await OpenAsync();
            var seasonal = await TypeIdAsync(env, "SEASONAL VARITIES");
            var chrys = await TypeIdAsync(env, "CHRYSANTHEMUM");
            var other = await env.InsertAsync(chrys, env.Tag + " Taken", 20);                    // other Plant Type: still a clash
            var id = await env.InsertAsync(seasonal, env.Tag + " Mine");

            foreach (var clash in new[] { env.Tag + " Taken", env.Tag.ToLowerInvariant() + "  taken ", env.Tag + "\r\nTaken" })
            {
                var page = env.EditPage(id, clash, 50);
                await page.OnPostEditSpeciesAsync();
                Assert.Contains("already named", Error(page));
                Assert.Contains($"(Id {other}", Error(page));
                Assert.Equal(env.Tag + " Mine", await env.NameAsync(id));
                Assert.Equal(32, await env.ScalarAsync<int>("SELECT ReadyStockDays FROM dbo.PlantSpecies WHERE Id = @I", ("@I", id)));   // nothing saved
            }

            var punctuation = env.EditPage(id, env.Tag + " Taken.");                              // punctuation differs: allowed
            await punctuation.OnPostEditSpeciesAsync();
            Assert.Null(Error(punctuation));
            Assert.Equal(env.Tag + " Taken.", await env.NameAsync(id));
        }

        [SkippableFact]
        public async Task Edit_OneCopyOfAnExistingDuplicatePair_KeepingItsName_IsSaved()
        {
            await using var env = await OpenAsync();
            var chrys = await TypeIdAsync(env, "CHRYSANTHEMUM");
            var older = await env.InsertAsync(chrys, env.Tag + " Pair\r\n", 20);                // as the old chrysanthemum copies
            var newer = await env.InsertAsync(chrys, env.Tag + " Pair", 20);

            var page = env.EditPage(older, env.Tag + " Pair\r\n", 22);
            await page.OnPostEditSpeciesAsync();
            Assert.Null(Error(page));
            Assert.Equal(22, await env.ScalarAsync<int>("SELECT ReadyStockDays FROM dbo.PlantSpecies WHERE Id = @I", ("@I", older)));

            var intoTwin = env.EditPage(newer, env.Tag + " Other", 20);                           // renaming away, then back: refused (older has it)
            await intoTwin.OnPostEditSpeciesAsync();
            Assert.Null(Error(intoTwin));
            var back = env.EditPage(newer, env.Tag + " Pair", 20);
            await back.OnPostEditSpeciesAsync();
            Assert.Contains($"(Id {older}", Error(back));
        }

        [SkippableFact]
        public async Task Edit_SimultaneousRenamesOfTwoVarietiesToOneName_OnlyOneSaved()
        {
            await using var env = await OpenAsync();
            var seasonal = await TypeIdAsync(env, "SEASONAL VARITIES");
            var ids = new List<int>();
            for (var i = 0; i < 6; i++) ids.Add(await env.InsertAsync(seasonal, env.Tag + " Start " + i));

            var pages = ids.Select(id => env.EditPage(id, env.Tag + " Target")).ToList();
            await Task.WhenAll(pages.Select(p => p.OnPostEditSpeciesAsync()));
            Assert.Equal(1, await env.ScalarAsync<int>("SELECT COUNT(*) FROM dbo.PlantSpecies WHERE Name = @N", ("@N", env.Tag + " Target")));
            Assert.Equal(5, pages.Count(p => Error(p)?.Contains("already named") == true));
        }

        [SkippableFact]
        public async Task ReadyStockDaysCheck_StillApplies()
        {
            await using var env = await OpenAsync();
            var seasonal = await TypeIdAsync(env, "SEASONAL VARITIES");
            var page = env.Page(seasonal, env.Tag + " Zero Days", 0);
            await page.OnPostAddSpeciesAsync();
            Assert.Equal("Ready Stock Days must be greater than zero when provided.", Error(page));
            Assert.Equal(0, await env.CountAsync(env.Tag));

            var blank = env.Page(seasonal, env.Tag + " Blank Days");                               // blank days are still allowed
            await blank.OnPostAddSpeciesAsync();
            Assert.Null(Error(blank));
            Assert.Equal(0, await env.ScalarAsync<int>("SELECT COUNT(*) FROM dbo.PlantSpecies WHERE Name = @N AND ReadyStockDays IS NOT NULL", ("@N", env.Tag + " Blank Days")));
        }
    }
}
