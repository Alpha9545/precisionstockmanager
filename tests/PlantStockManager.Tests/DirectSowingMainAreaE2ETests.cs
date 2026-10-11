using System.Security.Claims;
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
using CreatePage = PlantStockManager.Pages.Production.SeedSowing.CreateModel;

namespace PlantStockManager.Tests
{
    // Direct Seed Sowing -- Main Areas only, Area derived from the Polyhouse (2026-10-08).
    // Against a SCRATCH copy (PSM_SCRATCH_CONNECTION, PlantsIMS2_Scratch_*): the REAL
    // Create page (real login claims, Area scope enforced) and SeedSowingRepository.
    // The browser never chooses the Area: the server reads it from dbo.Polyhouses.AreaId,
    // requires an active MainOffice Area, refuses a posted AreaId that disagrees, checks
    // the user's Area scope, and deducts trays + the ONE selected material atomically.
    //
    // Scratch users: 11 Mahadev (Main Office Store Keeper, Sowing.Enter, Area 2),
    // 9 Maya (Sowing Supervisor -- the approver), 21 Prajwal (System Administrator).
    [Collection("ScratchDb")]
    public class DirectSowingMainAreaE2ETests
    {
        private const string ConnectionVariable = "PSM_SCRATCH_CONNECTION";
        private const string RequiredPrefix = "PlantsIMS2_Scratch_";

        private const int MainOffice = 2, Facility5 = 2, Facility3 = 3;   // Main-Office-Areas (AreaType MainOffice)
        private const int GreenBless = 127, GreenBlessP1 = 14;             // Mother Plant Area
        private const int Samarth = 123, SamarthP1 = 8;                    // Mother Plant Area
        private const int Outlet = 124, OutletP1 = 18;                     // Outlet
        private const int StoreKeeper = 11, Supervisor = 9, Admin = 21;
        private const int MpA = 114;                                       // its variety is sown
        private const string Cavity150 = "150 Cavity";

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
            public string Cs { get; init; } = "";
            public ServiceProvider Provider { get; init; } = null!;
            public int Species { get; init; }
            public int PlantType { get; init; }
            public int SeedStockId { get; init; }
            public int Cocopeat { get; init; }
            public int Pitmos { get; init; }
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

            // Material balances set through the real ledger (scratch-only clean-up first).
            public async Task SetMaterialAsync(int areaId, int polyhouseId, decimal cocopeat, decimal pitmos)
            {
                await ExecAsync(@"
EXEC sp_set_session_context N'MaterialTestCleanup', 1;
DELETE t FROM dbo.MaterialTransactions t JOIN dbo.MaterialStock s ON s.Id = t.MaterialStockId WHERE s.AreaId = @A AND s.PolyhouseId = @P;
UPDATE dbo.MaterialStock SET CurrentQuantity = 0 WHERE AreaId = @A AND PolyhouseId = @P;
EXEC sp_set_session_context N'MaterialTestCleanup', 0;", ("@A", areaId), ("@P", polyhouseId));
                foreach (var (material, kg) in new[] { (Cocopeat, cocopeat), (Pitmos, pitmos) })
                    if (kg > 0)
                    {
                        var (ok, msg, _) = await Get<MaterialStockRepository>().AdjustAsync(areaId, polyhouseId, material, 1, kg, "test setup", Guid.NewGuid(), Admin, _ => true);
                        Assert.True(ok, msg);
                    }
            }

            public Task<decimal> MaterialAsync(int areaId, int polyhouseId, int material)
                => ScalarAsync<decimal>("SELECT ISNULL((SELECT CurrentQuantity FROM dbo.MaterialStock WHERE AreaId = @A AND PolyhouseId = @P AND MaterialTypeId = @M), 0)",
                    ("@A", areaId), ("@P", polyhouseId), ("@M", material));

            public async Task SetTraysAsync(int areaId, int polyhouseId, string cavity, decimal trays)
            {
                await ExecAsync(@"
UPDATE dbo.TrayStock SET PhysicalQuantity = 0 WHERE AreaId = @A AND PolyhouseId = @P AND TraySize = @T;
DELETE tr FROM dbo.TrayStockTransactions tr JOIN dbo.TrayStock t ON t.Id = tr.TrayStockId WHERE t.AreaId = @A AND t.PolyhouseId = @P AND t.TraySize = @T",
                    ("@A", areaId), ("@P", polyhouseId), ("@T", cavity));
                if (trays > 0)
                {
                    var (ok, msg, _, _) = await Get<TrayStockRepository>().AddStockAsync(areaId, polyhouseId, cavity, trays, DateTime.UtcNow,
                        Random.Shared.Next(1, int.MaxValue), Supervisor, "ds-test", "test setup", _ => true);
                    Assert.True(ok, msg);
                }
            }

            public Task<decimal> TraysAsync(int areaId, int polyhouseId, string cavity)
                => ScalarAsync<decimal>("SELECT ISNULL((SELECT PhysicalQuantity FROM dbo.TrayStock WHERE AreaId = @A AND PolyhouseId = @P AND TraySize = @T), 0)",
                    ("@A", areaId), ("@P", polyhouseId), ("@T", cavity));

            public Task SetSeedAsync(decimal physical)
                => ExecAsync("UPDATE dbo.SeedStock SET InTransitQuantity = 0, PhysicalQuantity = @P WHERE Id = @Id", ("@P", physical), ("@Id", SeedStockId));

            public Task<decimal> SeedAsync() => ScalarAsync<decimal>("SELECT PhysicalQuantity FROM dbo.SeedStock WHERE Id = @Id", ("@Id", SeedStockId));

            public Task<int> SowingCountAsync() => ScalarAsync<int>("SELECT COUNT(*) FROM dbo.SeedSowings");

            public async Task<ClaimsPrincipal> LoginAsync(int userId)
            {
                var factory = Get<UserClaimsFactory>();
                var user = await factory.GetActiveUserAsync(userId);
                Assert.NotNull(user);
                return await factory.CreatePrincipalAsync(user!);
            }

            // The same user, but assigned to other Area(s) only -- no access to the Main Office.
            public async Task<ClaimsPrincipal> LoginWithAreasAsync(int userId, params int[] areaIds)
            {
                var real = await LoginAsync(userId);
                var claims = real.Claims.Where(c => c.Type != AreaAccessService.AreaAccessClaimType).ToList();
                claims.AddRange(areaIds.Select(a => new Claim(AreaAccessService.AreaAccessClaimType, a.ToString())));
                return new ClaimsPrincipal(new ClaimsIdentity(claims, real.Identity!.AuthenticationType));
            }

            public CreatePage Page(ClaimsPrincipal principal, string method = "POST")
            {
                var http = new DefaultHttpContext { User = principal };
                http.Request.Method = method;
                var page = ActivatorUtilities.CreateInstance<CreatePage>(Provider);
                page.PageContext = new PageContext(new ActionContext(http, new Microsoft.AspNetCore.Routing.RouteData(), new CompiledPageActionDescriptor()))
                {
                    ViewData = new ViewDataDictionary(new Microsoft.AspNetCore.Mvc.ModelBinding.EmptyModelMetadataProvider(), new Microsoft.AspNetCore.Mvc.ModelBinding.ModelStateDictionary())
                };
                page.TempData = new TempDataDictionary(http, new NullTempData());
                return page;
            }

            // What the form posts: NO AreaId (the page has no Area field).
            public SeedSowing Form(int polyhouseId, int trays, int? material, string cavity = Cavity150) => new()
            {
                SourceSeedStockId = SeedStockId, SpeciesId = Species, SeedQuantity = trays * DirectSowingRules.CavityCount(cavity)!.Value, CavityType = cavity,
                SowingDate = DateTime.Today, PolyhouseId = polyhouseId, SupervisorId = Supervisor, GrowingMaterialTypeId = material,
                SubmissionToken = Guid.NewGuid(), Remarks = "ds-test",
            };
        }

        private static async Task<Env> OpenAsync()
        {
            var cs = Environment.GetEnvironmentVariable(ConnectionVariable);
            Skip.If(string.IsNullOrWhiteSpace(cs), $"Set {ConnectionVariable} to a {RequiredPrefix}* database connection string to run the Direct Sowing tests.");
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
            services.AddSingleton<AreaAccessService>();
            services.AddSingleton<IOptions<SecurityOptions>>(Options.Create(new SecurityOptions { FullAccessRoleNames = new[] { "System Administrator" } }));
            // Area scope ENFORCED (the production default).
            services.AddSingleton<IOptionsMonitor<SeedlingWorkflowOptions>>(new StaticMonitor<SeedlingWorkflowOptions>(new SeedlingWorkflowOptions { EnforceAreaScope = true }));
            services.AddSingleton<SeedlingAreaScope>();
            services.AddScoped<UserClaimsFactory>();
            foreach (var t in typeof(DatabaseHelper).Assembly.GetTypes().Where(t => t.Namespace == "PlantStockManager.Data" && t.IsClass && t.Name.EndsWith("Repository")))
                services.AddScoped(t);
            var provider = services.BuildServiceProvider();

            var probe = new Env { Cs = cs!, Provider = provider };
            Skip.If(await probe.ScalarAsync<int>("SELECT CASE WHEN OBJECT_ID('dbo.MaterialStock', 'U') IS NULL THEN 0 ELSE 1 END") == 0,
                "Apply Database/Migrations/2026-10-08_MaterialStock.sql to the scratch copy first.");
            var species = await probe.ScalarAsync<int>("SELECT SpeciesId FROM dbo.MotherPlants WHERE Id = @M", ("@M", MpA));
            await probe.ExecAsync("UPDATE dbo.PlantSpecies SET ReadyStockDays = 60 WHERE Id = @S AND ReadyStockDays IS NULL", ("@S", species));
            var seedStockId = await probe.ScalarAsync<int>("SELECT TOP 1 Id FROM dbo.SeedStock WHERE SpeciesId = @S AND AreaId = @A ORDER BY Id", ("@S", species), ("@A", MainOffice));
            if (seedStockId == 0)
            {
                await probe.ExecAsync("INSERT INTO dbo.SeedStock (SpeciesId, AreaId, PhysicalQuantity, CreatedDate, CreatedBy) VALUES (@S, @A, 0, SYSUTCDATETIME(), N'ds-test')",
                    ("@S", species), ("@A", MainOffice));
                seedStockId = await probe.ScalarAsync<int>("SELECT TOP 1 Id FROM dbo.SeedStock WHERE SpeciesId = @S AND AreaId = @A ORDER BY Id", ("@S", species), ("@A", MainOffice));
            }
            await probe.ExecAsync("UPDATE dbo.MaterialConsumptionRules SET QuantityPerTray = 1.5, IsActive = 1;");
            var env = new Env
            {
                Cs = cs!, Provider = provider, Species = species, SeedStockId = seedStockId,
                PlantType = await probe.ScalarAsync<int>("SELECT PlantTypeId FROM dbo.PlantSpecies WHERE Id = @S", ("@S", species)),
                Cocopeat = await probe.ScalarAsync<int>("SELECT Id FROM dbo.MaterialTypes WHERE Code = 'COCOPEAT'"),
                Pitmos = await probe.ScalarAsync<int>("SELECT Id FROM dbo.MaterialTypes WHERE Code = 'PITMOS'"),
            };
            await env.SetSeedAsync(1_000_000);
            return env;
        }

        private static string[] Errors(PageModel page) => page.ModelState.Values.SelectMany(v => v.Errors).Select(e => e.ErrorMessage).ToArray();

        private static async Task<(IActionResult Result, CreatePage Page)> PostAsync(Env env, ClaimsPrincipal user, SeedSowing form, int? forgedAreaId = null)
        {
            var page = env.Page(user);
            page.SeedSowing = form;
            page.PlantTypeId = env.PlantType;
            if (forgedAreaId.HasValue)
                page.SeedSowing.AreaId = forgedAreaId.Value;   // what a tampered request would carry
            return (await page.OnPostAsync(), page);
        }

        // ---- 1. the form: no Area selector; only Main Area Polyhouses ----------------------------------

        [SkippableFact]
        public async Task Form_OffersOnlyMainAreaPolyhouses_ForTheUsersAreas_AndShowsTheMainAreaReadOnly()
        {
            var env = await OpenAsync();
            var mainOfficePolyhouses = (await env.Get<PolyhouseRepository>().GetByAreaIdAsync(MainOffice)).Select(p => p.Id).OrderBy(i => i).ToList();
            Assert.Contains(Facility5, mainOfficePolyhouses);

            foreach (var userId in new[] { StoreKeeper, Admin })
            {
                var page = env.Page(await env.LoginAsync(userId), "GET");
                await page.OnGetAsync();
                Assert.Equal(mainOfficePolyhouses, page.Polyhouses.Select(p => p.Id).OrderBy(i => i).ToList());
                Assert.All(page.Polyhouses, p => Assert.Equal(MainOffice, p.AreaId));
                Assert.DoesNotContain(page.Polyhouses, p => p.Id is GreenBlessP1 or SamarthP1 or OutletP1);
                Assert.Equal("Main-Office-Areas", page.MainAreaName);   // only one Main Area: shown read-only
            }

            // A user with no Main Area is offered nothing.
            var elsewhere = env.Page(await env.LoginWithAreasAsync(StoreKeeper, GreenBless), "GET");
            await elsewhere.OnGetAsync();
            Assert.Empty(elsewhere.Polyhouses);

            // The page model has no Area list any more.
            Assert.Null(typeof(CreatePage).GetProperty("Areas"));
            Assert.Null(typeof(CreatePage).GetMethod("OnGetPolyhousesAsync"));
        }

        // ---- 2 / 8 / 9 / 10 / 11. a valid Main Area sowing (the PH-03 example) --------------------------

        [SkippableFact]
        public async Task ValidMainAreaPolyhouse_Succeeds_AreaComesFromThePolyhouse_OnlyTheSelectedMaterialAndItsTraysAreDeducted()
        {
            var env = await OpenAsync();
            await env.SetMaterialAsync(MainOffice, Facility3, cocopeat: 500, pitmos: 300);
            await env.SetTraysAsync(MainOffice, Facility3, Cavity150, 200);
            var seedBefore = await env.SeedAsync();

            var (result, page) = await PostAsync(env, await env.LoginAsync(StoreKeeper), env.Form(Facility3, 100, env.Cocopeat));

            Assert.IsType<RedirectToPageResult>(result);
            Assert.Empty(Errors(page));
            var id = page.SeedSowing.Id;
            Assert.True(id > 0);
            Assert.Equal(MainOffice, await env.ScalarAsync<int>("SELECT AreaId FROM dbo.SeedSowings WHERE Id = @Id", ("@Id", id)));       // from the Polyhouse
            Assert.Equal(Facility3, await env.ScalarAsync<int>("SELECT PolyhouseId FROM dbo.SeedSowings WHERE Id = @Id", ("@Id", id)));
            Assert.Equal(env.Cocopeat, await env.ScalarAsync<int>("SELECT GrowingMaterialTypeId FROM dbo.SeedSowings WHERE Id = @Id", ("@Id", id)));
            Assert.Equal(350m, await env.MaterialAsync(MainOffice, Facility3, env.Cocopeat));   // 500 - 100 x 1.5
            Assert.Equal(300m, await env.MaterialAsync(MainOffice, Facility3, env.Pitmos));     // untouched
            Assert.Equal(100m, await env.TraysAsync(MainOffice, Facility3, Cavity150));         // 200 - 100
            Assert.Equal(seedBefore - 15000m, await env.SeedAsync());
            Assert.Contains("Cocopeat used: 150.00 kg", (string)page.TempData["Success"]!);

            // Details shows the Area, the Polyhouse and the material consumed.
            var details = await env.Get<SeedSowingRepository>().GetByIdAsync(id);
            Assert.Equal("Main-Office-Areas", details!.AreaName);
            Assert.Equal("Facility-3", details.PolyhouseName);
            Assert.Equal("Cocopeat", details.GrowingMaterialName);
            var consumed = Assert.Single(await env.Get<MaterialStockRepository>().GetForSowingAsync(id), t => t.TransactionType == MaterialStockRules.Consume);
            Assert.Equal(150m, consumed.StockQuantity);
            Assert.Equal("Facility-3", consumed.PolyhouseName);
        }

        [SkippableFact]
        public async Task PitmosSelected_DeductsOnlyPitmos()
        {
            var env = await OpenAsync();
            await env.SetMaterialAsync(MainOffice, Facility3, cocopeat: 500, pitmos: 300);
            await env.SetTraysAsync(MainOffice, Facility3, Cavity150, 200);

            var (result, page) = await PostAsync(env, await env.LoginAsync(StoreKeeper), env.Form(Facility3, 100, env.Pitmos));

            Assert.IsType<RedirectToPageResult>(result);
            Assert.Equal(500m, await env.MaterialAsync(MainOffice, Facility3, env.Cocopeat));
            Assert.Equal(150m, await env.MaterialAsync(MainOffice, Facility3, env.Pitmos));
            Assert.Equal(100m, await env.TraysAsync(MainOffice, Facility3, Cavity150));
        }

        [SkippableFact]
        public async Task MainAreaSowing_WithoutAMaterial_IsRefused_NothingChanges()
        {
            var env = await OpenAsync();
            await env.SetMaterialAsync(MainOffice, Facility3, cocopeat: 500, pitmos: 300);
            await env.SetTraysAsync(MainOffice, Facility3, Cavity150, 200);
            var before = await env.SowingCountAsync();

            var (result, page) = await PostAsync(env, await env.LoginAsync(StoreKeeper), env.Form(Facility3, 10, null));

            Assert.IsType<PageResult>(result);
            Assert.NotEmpty(Errors(page));
            Assert.Equal(before, await env.SowingCountAsync());
            Assert.Equal(200m, await env.TraysAsync(MainOffice, Facility3, Cavity150));
            Assert.Equal(500m, await env.MaterialAsync(MainOffice, Facility3, env.Cocopeat));
        }

        // ---- 3 / 5 / 6 / 7. refused locations -- page AND repository, nothing changes -------------------

        [SkippableTheory]
        [InlineData(GreenBlessP1, null, DirectSowingRules.DirectSowingNotMainAreaMessage)]          // Mother Plant Area Polyhouse
        [InlineData(SamarthP1, null, DirectSowingRules.DirectSowingNotMainAreaMessage)]             // another Mother Plant Area
        [InlineData(OutletP1, null, DirectSowingRules.DirectSowingNotMainAreaMessage)]              // Outlet
        [InlineData(999999, null, DirectSowingRules.DirectSowingPolyhouseNotFoundMessage)]          // does not exist
        [InlineData(Facility3, GreenBless, DirectSowingRules.DirectSowingAreaMismatchMessage)]      // forged AreaId (another Area)
        [InlineData(Facility3, Outlet, DirectSowingRules.DirectSowingAreaMismatchMessage)]          // forged AreaId (Outlet)
        [InlineData(GreenBlessP1, MainOffice, DirectSowingRules.DirectSowingNotMainAreaMessage)]    // forged Main AreaId on a Mother Plant Polyhouse
        public async Task InvalidLocation_IsRefused_ByThePageAndTheRepository_NothingChanges(int polyhouseId, int? forgedAreaId, string expected)
        {
            var env = await OpenAsync();
            await env.SetMaterialAsync(MainOffice, Facility3, cocopeat: 500, pitmos: 300);
            await env.SetTraysAsync(MainOffice, Facility3, Cavity150, 200);
            await env.SetTraysAsync(GreenBless, GreenBlessP1, Cavity150, 50);
            var sowings = await env.SowingCountAsync();
            var seed = await env.SeedAsync();

            // The page (Administrator: every Area is authorized, so only the location rule can refuse).
            var (result, page) = await PostAsync(env, await env.LoginAsync(Admin), env.Form(polyhouseId, 10, env.Cocopeat), forgedAreaId);
            Assert.IsType<PageResult>(result);
            Assert.Contains(expected, Errors(page));

            // The repository on its own (as if the page check were bypassed).
            var direct = env.Form(polyhouseId, 10, env.Cocopeat);
            direct.AreaId = forgedAreaId ?? 0;
            direct.CreatedBy = "ds-test";
            direct.CreatedById = Admin;
            var (ok, msg, id) = await env.Get<SeedSowingRepository>().InsertAsync(direct, Admin, _ => true);
            Assert.False(ok);
            Assert.Equal(expected, msg);
            Assert.Equal(0, id);

            Assert.Equal(sowings, await env.SowingCountAsync());
            Assert.Equal(seed, await env.SeedAsync());
            Assert.Equal(200m, await env.TraysAsync(MainOffice, Facility3, Cavity150));
            Assert.Equal(50m, await env.TraysAsync(GreenBless, GreenBlessP1, Cavity150));
            Assert.Equal(500m, await env.MaterialAsync(MainOffice, Facility3, env.Cocopeat));
            Assert.Equal(300m, await env.MaterialAsync(MainOffice, Facility3, env.Pitmos));
        }

        [SkippableFact]
        public async Task PostedAreaId_EqualToThePolyhousesOwnArea_IsAccepted_AndStillComesFromThePolyhouse()
        {
            var env = await OpenAsync();
            await env.SetMaterialAsync(MainOffice, Facility5, cocopeat: 100, pitmos: 0);
            await env.SetTraysAsync(MainOffice, Facility5, Cavity150, 20);

            var (result, page) = await PostAsync(env, await env.LoginAsync(StoreKeeper), env.Form(Facility5, 2, env.Cocopeat), forgedAreaId: MainOffice);

            Assert.IsType<RedirectToPageResult>(result);
            Assert.Equal(MainOffice, await env.ScalarAsync<int>("SELECT AreaId FROM dbo.SeedSowings WHERE Id = @Id", ("@Id", page.SeedSowing.Id)));
        }

        // ---- 4. unauthorized Main Area -------------------------------------------------------------------

        [SkippableFact]
        public async Task UnauthorizedMainAreaPolyhouse_IsRefused_ByThePageAndTheRepository_NothingChanges()
        {
            var env = await OpenAsync();
            await env.SetMaterialAsync(MainOffice, Facility3, cocopeat: 500, pitmos: 0);
            await env.SetTraysAsync(MainOffice, Facility3, Cavity150, 200);
            var sowings = await env.SowingCountAsync();

            // The Store Keeper, but assigned to Green Bless only (no Main Office access).
            var outsider = await env.LoginWithAreasAsync(StoreKeeper, GreenBless);
            var (result, page) = await PostAsync(env, outsider, env.Form(Facility3, 10, env.Cocopeat));
            Assert.IsType<PageResult>(result);
            Assert.Contains("You are not authorized to sow in the selected Polyhouse's Area.", Errors(page));

            // The material preview for that Polyhouse reveals nothing to them either.
            var preview = await env.Page(outsider, "GET").OnGetMaterialPreviewAsync(Facility3, Cavity150, 1500);
            Assert.False((bool)preview.Value!.GetType().GetProperty("applicable")!.GetValue(preview.Value)!);

            var direct = env.Form(Facility3, 10, env.Cocopeat);
            direct.CreatedBy = "ds-test";
            direct.CreatedById = StoreKeeper;
            var (ok, msg, _) = await env.Get<SeedSowingRepository>().InsertAsync(direct, StoreKeeper, area => area != MainOffice);
            Assert.False(ok);
            Assert.Equal("You are not authorized to sow in this Area.", msg);

            Assert.Equal(sowings, await env.SowingCountAsync());
            Assert.Equal(200m, await env.TraysAsync(MainOffice, Facility3, Cavity150));
            Assert.Equal(500m, await env.MaterialAsync(MainOffice, Facility3, env.Cocopeat));
        }

        // ---- 12 / 13. insufficient stock: the whole sowing is refused -----------------------------------

        [SkippableFact]
        public async Task InsufficientMaterial_RefusesTheSowing_TraysSeedAndMaterialsUnchanged()
        {
            var env = await OpenAsync();
            await env.SetMaterialAsync(MainOffice, Facility3, cocopeat: 100, pitmos: 300);   // 100 trays need 150 kg
            await env.SetTraysAsync(MainOffice, Facility3, Cavity150, 200);
            var sowings = await env.SowingCountAsync();
            var seed = await env.SeedAsync();

            var (result, page) = await PostAsync(env, await env.LoginAsync(StoreKeeper), env.Form(Facility3, 100, env.Cocopeat));

            Assert.IsType<PageResult>(result);
            Assert.Contains(Errors(page), e => e.Contains("Insufficient Cocopeat stock", StringComparison.Ordinal) && e.Contains("Required: 150.00 kg", StringComparison.Ordinal));
            Assert.Equal(sowings, await env.SowingCountAsync());
            Assert.Equal(seed, await env.SeedAsync());
            Assert.Equal(200m, await env.TraysAsync(MainOffice, Facility3, Cavity150));
            Assert.Equal(100m, await env.MaterialAsync(MainOffice, Facility3, env.Cocopeat));
            Assert.Equal(300m, await env.MaterialAsync(MainOffice, Facility3, env.Pitmos));
        }

        [SkippableFact]
        public async Task InsufficientTrays_RefusesTheSowing_MaterialsAndSeedUnchanged()
        {
            var env = await OpenAsync();
            await env.SetMaterialAsync(MainOffice, Facility3, cocopeat: 500, pitmos: 300);
            await env.SetTraysAsync(MainOffice, Facility3, Cavity150, 50);                 // 100 needed
            var sowings = await env.SowingCountAsync();
            var seed = await env.SeedAsync();

            var (result, page) = await PostAsync(env, await env.LoginAsync(StoreKeeper), env.Form(Facility3, 100, env.Cocopeat));

            Assert.IsType<PageResult>(result);
            Assert.Contains(Errors(page), e => e.Contains("Insufficient tray stock", StringComparison.Ordinal));
            Assert.Equal(sowings, await env.SowingCountAsync());
            Assert.Equal(seed, await env.SeedAsync());
            Assert.Equal(50m, await env.TraysAsync(MainOffice, Facility3, Cavity150));
            Assert.Equal(500m, await env.MaterialAsync(MainOffice, Facility3, env.Cocopeat));
            Assert.Equal(300m, await env.MaterialAsync(MainOffice, Facility3, env.Pitmos));
        }

        // ---- cancellation still returns exactly what was taken ------------------------------------------

        [SkippableFact]
        public async Task Cancel_ReturnsTheTraysAndTheMaterial_ToTheSamePolyhouse()
        {
            var env = await OpenAsync();
            await env.SetMaterialAsync(MainOffice, Facility3, cocopeat: 500, pitmos: 300);
            await env.SetTraysAsync(MainOffice, Facility3, Cavity150, 200);
            var (result, page) = await PostAsync(env, await env.LoginAsync(StoreKeeper), env.Form(Facility3, 100, env.Cocopeat));
            Assert.IsType<RedirectToPageResult>(result);

            var (ok, msg) = await env.Get<SeedSowingRepository>().CancelAsync(page.SeedSowing.Id, "ds-test", Supervisor);

            Assert.True(ok, msg);
            Assert.Equal(500m, await env.MaterialAsync(MainOffice, Facility3, env.Cocopeat));
            Assert.Equal(300m, await env.MaterialAsync(MainOffice, Facility3, env.Pitmos));
            Assert.Equal(200m, await env.TraysAsync(MainOffice, Facility3, Cavity150));
        }
    }
}
