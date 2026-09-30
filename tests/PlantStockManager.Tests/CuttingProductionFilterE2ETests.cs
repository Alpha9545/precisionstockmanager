using System.Security.Claims;
using Microsoft.AspNetCore.Http;
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
using RegisterPage = PlantStockManager.Pages.Production.Cutting.IndexModel;

namespace PlantStockManager.Tests
{
    // CORRECTION #5 -- end-to-end: the REAL Cutting Production register (page model + repository) against a SCRATCH COPY of
    // the test database (same safety rules as the other E2E classes: PSM_SCRATCH_CONNECTION, database name must start with
    // PlantsIMS2_Scratch_, otherwise refused / skipped).
    //
    // The test data is created through the real Cutting Entry code (CuttingProductionRepository.InsertAsync), and the
    // deliveries are confirmed / rejected / cancelled through the real transfer code:
    //   A1 Area 1   Mother Plant 114  supervisor 115  Main Office  100  today      delivery Awaiting Main Office
    //   A2 Area 1   Mother Plant 114  supervisor 115  Pot Prod.     200  today-1    (no delivery)
    //   A3 Area 1   Mother Plant 114  supervisor 115  Main Office   70  today-4    delivery Awaiting Main Office
    //   B1 Area 122 Mother Plant 106  supervisor 101  Main Office   50  today-2    delivery Completed
    //   B2 Area 122 Mother Plant 106  supervisor 101  Main Office   60  today-3    delivery Rejected
    //   B3 Area 122 Mother Plant 106  supervisor 101  Pot Prod.     30  today-5    (no delivery)
    // plus the 6 real historical entries (ids 167, 171-175: no destination, no delivery). Other E2E classes share the copy and
    // add entries of their own, so every check looks at the entries of THIS test data (or at Area membership), never at totals.
    [Collection("ScratchDb")]
    public class CuttingProductionFilterE2ETests
    {
        private const string ConnectionVariable = "PSM_SCRATCH_CONNECTION";
        private const string RequiredPrefix = "PlantsIMS2_Scratch_";
        private const int MpA = 114, SupA = 115, AreaA = 1, MpB = 106, SupB = 101, AreaB = 122, MainOfficeArea = 2;
        private static readonly int[] HistoricalIds = { 167, 171, 172, 173, 174, 175 };

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

        private sealed class Data
        {
            public int A1, A2, A3, B1, B2, B3;
            public int[] Mine => new[] { A1, A2, A3, B1, B2, B3 };
            public int SpeciesA, SpeciesB; public int? PolyhouseA, PolyhouseB;
        }

        private sealed class Env
        {
            public string ConnectionString { get; init; } = "";
            public ServiceProvider Provider { get; init; } = null!;
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

            public async Task<ClaimsPrincipal> LoginAsync(int userId)
            {
                var factory = Get<UserClaimsFactory>();
                var user = await factory.GetActiveUserAsync(userId);
                Assert.NotNull(user);
                return await factory.CreatePrincipalAsync(user!);
            }

            public RegisterPage Register(ClaimsPrincipal principal)
            {
                var http = new DefaultHttpContext { User = principal };
                var page = ActivatorUtilities.CreateInstance<RegisterPage>(Provider);
                page.PageContext = new PageContext(new Microsoft.AspNetCore.Mvc.ActionContext(http, new Microsoft.AspNetCore.Routing.RouteData(), new Microsoft.AspNetCore.Mvc.RazorPages.CompiledPageActionDescriptor()));
                page.TempData = new TempDataDictionary(http, new NullTempData());
                return page;
            }
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
            services.AddSingleton<IOptions<SecurityOptions>>(Options.Create(new SecurityOptions { FullAccessRoleNames = new[] { "System Administrator" } }));
            services.AddSingleton<IOptionsMonitor<SeedlingWorkflowOptions>>(new StaticMonitor<SeedlingWorkflowOptions>(new SeedlingWorkflowOptions()));
            services.AddSingleton<SeedlingAreaScope>();
            services.AddScoped<UserClaimsFactory>();
            foreach (var t in typeof(DatabaseHelper).Assembly.GetTypes().Where(t => t.Namespace == "PlantStockManager.Data" && t.IsClass && t.Name.EndsWith("Repository")))
                services.AddScoped(t);
            return new Env { ConnectionString = cs!, Provider = services.BuildServiceProvider() };
        }

        private static async Task<int> EnterAsync(Env env, int motherPlant, int supervisor, decimal quantity, string destination, int daysAgo)
        {
            var entry = new CuttingProduction
            {
                MotherPlantId = motherPlant, SupervisorId = supervisor, Quantity = quantity, CuttingDate = DateTime.Today.AddDays(-daysAgo),
                DestinationType = destination, SubmissionToken = Guid.NewGuid(), CreatedBy = "e2e-test"
            };
            var (ok, message, id) = await env.Get<CuttingProductionRepository>().InsertAsync(entry, supervisor);
            Assert.True(ok, message);
            if (destination == CuttingDestination.MainOffice)
                Assert.NotNull(entry.TransferId);
            return id;
        }

        private static async Task<Data> SeedAsync(Env env)
        {
            var transfers = env.Get<InternalTransferRepository>();
            var d = new Data();
            d.A1 = await EnterAsync(env, MpA, SupA, 100, CuttingDestination.MainOffice, 0);
            d.A2 = await EnterAsync(env, MpA, SupA, 200, CuttingDestination.PotProduction, 1);
            d.A3 = await EnterAsync(env, MpA, SupA, 70, CuttingDestination.MainOffice, 4);
            d.B1 = await EnterAsync(env, MpB, SupB, 50, CuttingDestination.MainOffice, 2);
            d.B2 = await EnterAsync(env, MpB, SupB, 60, CuttingDestination.MainOffice, 3);
            d.B3 = await EnterAsync(env, MpB, SupB, 30, CuttingDestination.PotProduction, 5);
            async Task<int> TransferOf(int entry) => await env.ScalarAsync<int>("SELECT Id FROM dbo.InternalTransfers WHERE SourceCuttingProductionId = @C", ("@C", entry));
            Assert.True((await transfers.ConfirmReceiptAsync(await TransferOf(d.B1), 50, null, 1, "e2e-test")).Success);
            Assert.True((await transfers.RejectAsync(await TransferOf(d.B2), "e2e reject", "e2e-test")).Success);
            d.SpeciesA = await env.ScalarAsync<int>("SELECT SpeciesId FROM dbo.MotherPlants WHERE Id = @M", ("@M", MpA));
            d.SpeciesB = await env.ScalarAsync<int>("SELECT SpeciesId FROM dbo.MotherPlants WHERE Id = @M", ("@M", MpB));
            d.PolyhouseA = await env.ScalarAsync<int?>("SELECT PolyhouseId FROM dbo.MotherPlants WHERE Id = @M", ("@M", MpA));
            d.PolyhouseB = await env.ScalarAsync<int?>("SELECT PolyhouseId FROM dbo.MotherPlants WHERE Id = @M", ("@M", MpB));
            return d;
        }

        private static CuttingProductionFilter Wide() => new() { From = DateTime.Today.AddDays(-30), To = DateTime.Today };
        private static int[] Ids(IEnumerable<CuttingProduction> list) => list.Select(c => c.Id).OrderBy(i => i).ToArray();
        private static int[] Only(Data d, IEnumerable<CuttingProduction> list) => Ids(list).Intersect(d.Mine).ToArray();
        private static int[] Set(params int[] ids) => ids.OrderBy(i => i).ToArray();

        // ---- the test data really is what the comments say ----

        [SkippableFact]
        public async Task TheTestData_HasTheDestinationsAndDeliveryStatusesItWasBuiltWith()
        {
            var env = await OpenAsync();
            var d = await SeedAsync(env);
            var all = await env.Get<CuttingProductionRepository>().SearchAsync(Wide(), null);
            string Status(int id) => all.Single(c => c.Id == id).TransferStatus ?? "none";
            Assert.Equal("PendingConfirmation", Status(d.A1));
            Assert.Equal("none", Status(d.A2));
            Assert.Equal("PendingConfirmation", Status(d.A3));
            Assert.Equal("Completed", Status(d.B1));
            Assert.Equal("Rejected", Status(d.B2));
            Assert.Equal("none", Status(d.B3));
        }

        // ---- each filter on its own ----

        [SkippableFact]
        public async Task EveryFilter_OnItsOwn_ReturnsExactlyTheMatchingRecords()
        {
            var env = await OpenAsync();
            var d = await SeedAsync(env);
            var repo = env.Get<CuttingProductionRepository>();
            async Task<int[]> With(Action<CuttingProductionFilter> set)
            {
                var f = Wide(); set(f); f.Normalize();
                return Only(d, await repo.SearchAsync(f, null));
            }

            // date range
            Assert.Equal(Set(d.A1, d.A2, d.B1), await With(f => { f.From = DateTime.Today.AddDays(-2); f.To = DateTime.Today; }));
            Assert.Equal(Set(d.B3), await With(f => { f.From = DateTime.Today.AddDays(-6); f.To = DateTime.Today.AddDays(-5); }));
            // Area
            Assert.Equal(Set(d.A1, d.A2, d.A3), await With(f => f.AreaId = AreaA));
            Assert.Equal(Set(d.B1, d.B2, d.B3), await With(f => f.AreaId = AreaB));
            // Mother Plant
            Assert.Equal(Set(d.A1, d.A2, d.A3), await With(f => f.MotherPlantId = MpA));
            Assert.Equal(Set(d.B1, d.B2, d.B3), await With(f => f.MotherPlantId = MpB));
            // Variety
            if (d.SpeciesA != d.SpeciesB)
            {
                Assert.Equal(Set(d.A1, d.A2, d.A3), await With(f => f.SpeciesId = d.SpeciesA));
                Assert.Equal(Set(d.B1, d.B2, d.B3), await With(f => f.SpeciesId = d.SpeciesB));
            }
            // Polyhouse (the Mother Plant's)
            if (d.PolyhouseA.HasValue && d.PolyhouseA != d.PolyhouseB)
                Assert.Equal(Set(d.A1, d.A2, d.A3), await With(f => f.PolyhouseId = d.PolyhouseA));
            if (d.PolyhouseB.HasValue && d.PolyhouseA != d.PolyhouseB)
                Assert.Equal(Set(d.B1, d.B2, d.B3), await With(f => f.PolyhouseId = d.PolyhouseB));
            // Supervisor
            Assert.Equal(Set(d.A1, d.A2, d.A3), await With(f => f.SupervisorId = SupA));
            Assert.Equal(Set(d.B1, d.B2, d.B3), await With(f => f.SupervisorId = SupB));
            // Destination: Main Office / Pot Production / not recorded
            Assert.Equal(Set(d.A1, d.A3, d.B1, d.B2), await With(f => f.Destination = CuttingDestination.MainOffice));
            Assert.Equal(Set(d.A2, d.B3), await With(f => f.Destination = CuttingDestination.PotProduction));
            Assert.Empty(await With(f => f.Destination = CuttingProductionFilter.DestinationNotRecorded));          // none of the new entries
            // Main Office Area of the delivery
            Assert.Equal(Set(d.A1, d.A3, d.B1, d.B2), await With(f => f.DestinationAreaId = MainOfficeArea));
            // delivery status
            Assert.Equal(Set(d.A1, d.A3), await With(f => f.DeliveryStatus = "PendingConfirmation"));
            Assert.Equal(Set(d.B1), await With(f => f.DeliveryStatus = "Completed"));
            Assert.Equal(Set(d.B2), await With(f => f.DeliveryStatus = "Rejected"));
            Assert.Equal(Set(d.A2, d.B3), await With(f => f.DeliveryStatus = CuttingProductionFilter.DeliveryNone));
        }

        // ---- filters together ----

        [SkippableFact]
        public async Task Filters_CombineWithAnd()
        {
            var env = await OpenAsync();
            var d = await SeedAsync(env);
            var repo = env.Get<CuttingProductionRepository>();
            async Task<int[]> With(Action<CuttingProductionFilter> set)
            {
                var f = Wide(); set(f); f.Normalize();
                return Only(d, await repo.SearchAsync(f, null));
            }

            // Date + Area + Supervisor
            Assert.Equal(Set(d.A1, d.A2), await With(f => { f.From = DateTime.Today.AddDays(-3); f.To = DateTime.Today; f.AreaId = AreaA; f.SupervisorId = SupA; }));
            // Destination + delivery status + Area
            Assert.Equal(Set(d.A1, d.A3), await With(f => { f.Destination = CuttingDestination.MainOffice; f.DeliveryStatus = "PendingConfirmation"; f.AreaId = AreaA; }));
            Assert.Equal(Set(d.B1), await With(f => { f.Destination = CuttingDestination.MainOffice; f.DeliveryStatus = "Completed"; f.AreaId = AreaB; f.MotherPlantId = MpB; f.SupervisorId = SupB; }));
            // date narrows a delivery-status result further
            Assert.Equal(Set(d.A1), await With(f => { f.DeliveryStatus = "PendingConfirmation"; f.From = DateTime.Today.AddDays(-1); f.To = DateTime.Today; }));
            // Pot Production + no delivery + Area
            Assert.Equal(Set(d.A2), await With(f => { f.Destination = CuttingDestination.PotProduction; f.DeliveryStatus = CuttingProductionFilter.DeliveryNone; f.AreaId = AreaA; }));
            // every filter set at once, all true of exactly one record
            Assert.Equal(Set(d.B1), await With(f =>
            {
                f.From = DateTime.Today.AddDays(-2); f.To = DateTime.Today.AddDays(-2); f.AreaId = AreaB; f.MotherPlantId = MpB; f.SpeciesId = d.SpeciesB;
                f.SupervisorId = SupB; f.Destination = CuttingDestination.MainOffice; f.DestinationAreaId = MainOfficeArea; f.DeliveryStatus = "Completed";
            }));
            // conditions that cannot all be true together
            Assert.Empty(await With(f => { f.AreaId = AreaA; f.SupervisorId = SupB; }));
            Assert.Empty(await With(f => { f.Destination = CuttingDestination.PotProduction; f.DeliveryStatus = "PendingConfirmation"; }));
            Assert.Empty(await With(f => { f.Destination = CuttingDestination.MainOffice; f.DeliveryStatus = CuttingProductionFilter.DeliveryNone; }));
        }

        // ---- no matching records ----

        [SkippableFact]
        public async Task NoMatch_ReturnsNothing_AndThePageSaysSo()
        {
            var env = await OpenAsync();
            await SeedAsync(env);
            var repo = env.Get<CuttingProductionRepository>();
            Assert.Empty(await repo.SearchAsync(new CuttingProductionFilter { From = new DateTime(2001, 1, 1), To = new DateTime(2001, 12, 31) }, null));
            Assert.Empty(await repo.SearchAsync(new CuttingProductionFilter { From = DateTime.Today.AddDays(-30), To = DateTime.Today, SupervisorId = 999999 }, null));

            var page = env.Register(await env.LoginAsync(21));
            page.SupervisorId = 999999;
            await page.OnGetAsync(null, null);
            Assert.Empty(page.Items);
            Assert.True(page.FilterActive);
            Assert.Contains("No cutting records match these filters.", PageText());
            static string PageText() => File.ReadAllText(System.IO.Path.Combine(RepoRoot(), "Pages", "Production", "Cutting", "Index.cshtml"));
        }

        private static string RepoRoot()
        {
            var dir = AppContext.BaseDirectory;
            while (dir != null && !Directory.GetFiles(dir, "*.sln").Any()) dir = System.IO.Path.GetDirectoryName(dir);
            return dir!;
        }

        // ---- Clear filters, and the chosen values staying on screen ----

        [SkippableFact]
        public async Task TheChosenValuesStayOnScreen_AndClearingGivesTheDefaultView()
        {
            var env = await OpenAsync();
            var d = await SeedAsync(env);
            var admin = await env.LoginAsync(21);

            var filtered = env.Register(admin);
            filtered.AreaId = AreaA; filtered.MotherPlantId = MpA; filtered.SupervisorId = SupA; filtered.Destination = CuttingDestination.MainOffice;
            filtered.DestinationAreaId = MainOfficeArea; filtered.DeliveryStatus = "PendingConfirmation"; filtered.SpeciesId = d.SpeciesA; filtered.PolyhouseId = d.PolyhouseA;
            await filtered.OnGetAsync(DateTime.Today.AddDays(-7), DateTime.Today);
            Assert.Equal(Set(d.A1, d.A3), Only(d, filtered.Items));
            Assert.Equal((AreaA, MpA, SupA, CuttingDestination.MainOffice, MainOfficeArea, "PendingConfirmation"),
                         (filtered.AreaId, filtered.MotherPlantId, filtered.SupervisorId, filtered.Destination, filtered.DestinationAreaId, filtered.DeliveryStatus));   // still selected
            Assert.Equal((DateTime.Today.AddDays(-7), DateTime.Today), (filtered.From, filtered.To));
            Assert.True(filtered.FilterActive);

            // "Clear filters" is a link to the page without a query string: the default view
            var cleared = env.Register(admin);
            await cleared.OnGetAsync(null, null);
            Assert.False(cleared.FilterActive);
            Assert.Null(cleared.AreaId); Assert.Null(cleared.Destination); Assert.Null(cleared.DeliveryStatus); Assert.Null(cleared.SupervisorId);
            Assert.Equal((DateTime.Today.AddDays(-30), DateTime.Today), (cleared.From, cleared.To));              // the default period, as before
            Assert.Equal(d.Mine.Length, Only(d, cleared.Items).Length);                                             // everything again
        }

        // ---- Area isolation / security ----

        [SkippableFact]
        public async Task AreaSecurity_UsersOnlyEverSeeTheirOwnAreasRecords_WhateverTheFilters()
        {
            var env = await OpenAsync();
            var d = await SeedAsync(env);

            var kiran = env.Register(await env.LoginAsync(SupA));                       // Area 1
            await kiran.OnGetAsync(null, null);
            Assert.Equal(Set(d.A1, d.A2, d.A3), Only(d, kiran.Items));
            Assert.All(kiran.Items, c => Assert.Equal(AreaA, c.AreaId));

            var achyut = env.Register(await env.LoginAsync(SupB));                      // Area 122
            await achyut.OnGetAsync(null, null);
            Assert.Equal(Set(d.B1, d.B2, d.B3), Only(d, achyut.Items));
            Assert.All(achyut.Items, c => Assert.Equal(AreaB, c.AreaId));

            // asking for another Area's records (by Area, Mother Plant, supervisor, delivery status...) returns nothing at all
            foreach (var attack in new (string Name, Action<RegisterPage> Set)[]
            {
                ("Area", p => p.AreaId = AreaB), ("Mother Plant", p => p.MotherPlantId = MpB), ("Supervisor", p => p.SupervisorId = SupB),
                ("Variety", p => p.SpeciesId = d.SpeciesB), ("Delivery", p => { p.AreaId = AreaB; p.DeliveryStatus = "Completed"; })
            })
            {
                if (attack.Name == "Variety" && d.SpeciesA == d.SpeciesB) continue;
                var p = env.Register(await env.LoginAsync(SupA));
                attack.Set(p);
                await p.OnGetAsync(null, null);
                Assert.True(p.Items.All(c => c.AreaId == AreaA), attack.Name);
                Assert.Empty(Only(d, p.Items).Intersect(Set(d.B1, d.B2, d.B3)));
            }
            var byArea = env.Register(await env.LoginAsync(SupA));
            byArea.AreaId = AreaB;
            await byArea.OnGetAsync(null, null);
            Assert.Empty(byArea.Items);

            // the dropdowns only offer values from the user's own Areas
            Assert.Equal(new[] { AreaA }, kiran.Options.Areas.Select(o => o.Id).ToArray());
            Assert.DoesNotContain(kiran.Options.MotherPlants, o => o.Id == MpB);
            Assert.DoesNotContain(kiran.Options.Supervisors, o => o.Id == SupB);
            Assert.Contains(kiran.Options.Supervisors, o => o.Id == SupA);
            var areaAMotherPlants = await env.ScalarAsync<int>("SELECT COUNT(*) FROM dbo.MotherPlants WHERE AreaId = @A AND Id IN (SELECT MotherPlantId FROM dbo.CuttingProductions)", ("@A", AreaA));
            Assert.Equal(areaAMotherPlants, kiran.Options.MotherPlants.Count);

            // an Area with no records, and a user with no Area at all: nothing
            var none = env.Register(await env.LoginAsync(111));                         // Samarth Ropvatika (Area 123)
            await none.OnGetAsync(null, null);
            Assert.Empty(none.Items);
            var noAreas = env.Register(new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim(AreaAccessService.RoleNameClaimType, "Mother Plant Supervisor") }, "test")));
            await noAreas.OnGetAsync(null, null);
            Assert.Empty(noAreas.Items);
            Assert.Empty(noAreas.Options.Areas);
            Assert.Empty(await env.Get<CuttingProductionRepository>().SearchAsync(Wide(), new int[0]));               // an empty Area list returns nothing

            // full access sees every Area, and can narrow by Area
            var admin = env.Register(await env.LoginAsync(21));
            await admin.OnGetAsync(null, null);
            Assert.Equal(d.Mine.Length, Only(d, admin.Items).Length);
            Assert.True(admin.Options.Areas.Any(o => o.Id == AreaA) && admin.Options.Areas.Any(o => o.Id == AreaB));
            var adminArea = env.Register(await env.LoginAsync(21));
            adminArea.AreaId = AreaB;
            await adminArea.OnGetAsync(null, null);
            Assert.Equal(Set(d.B1, d.B2, d.B3), Only(d, adminArea.Items));
        }

        [SkippableFact]
        public async Task TheFilteredRegister_IsIdenticalToTheOldRegister_ForEveryUser_WhenNoFilterIsSet()
        {
            var env = await OpenAsync();
            await SeedAsync(env);
            var repo = env.Get<CuttingProductionRepository>();
            var access = env.Get<AreaAccessService>();
            var from = DateTime.Today.AddDays(-400); var to = DateTime.Today;
            foreach (var userId in new[] { 21, SupA, SupB, 111, 110, 8 })
            {
                var user = await env.LoginAsync(userId);
                var old = access.FilterByArea(user, await repo.GetAllAsync(from, to), c => (int?)c.AreaId);           // the previous behaviour
                var allowed = access.HasFullAreaAccess(user) ? null : access.GetAccessibleAreaIds(user);
                var now = await repo.SearchAsync(new CuttingProductionFilter { From = from, To = to }, allowed);
                Assert.Equal(old.Select(c => c.Id).ToArray(), now.Select(c => c.Id).ToArray());                         // same records, same order
            }
        }

        [SkippableFact]
        public async Task SortingIsPreserved_NewestFirst()
        {
            var env = await OpenAsync();
            await SeedAsync(env);
            var list = await env.Get<CuttingProductionRepository>().SearchAsync(Wide(), null);
            Assert.Equal(list.OrderByDescending(c => c.CuttingDate).ThenByDescending(c => c.Id).Select(c => c.Id), list.Select(c => c.Id));
        }

        // ---- historical records ----

        [SkippableFact]
        public async Task HistoricalEntries_AreListed_AsNotRecorded_WithNoDelivery_AndAreNeverChangedByFiltering()
        {
            var env = await OpenAsync();
            var fingerprint = await env.ScalarAsync<string>($"SELECT CONCAT(COUNT(*), '/', CHECKSUM_AGG(CHECKSUM(*))) FROM dbo.CuttingProductions WHERE Id IN ({string.Join(",", HistoricalIds)})");
            var d = await SeedAsync(env);
            var repo = env.Get<CuttingProductionRepository>();
            var all = new CuttingProductionFilter { From = new DateTime(2026, 1, 1), To = DateTime.Today };

            var historical = (await repo.SearchAsync(all, null)).Where(c => HistoricalIds.Contains(c.Id)).ToList();
            Assert.Equal(HistoricalIds.Length, historical.Count);
            Assert.All(historical, c => { Assert.Null(c.DestinationType); Assert.Null(c.TransferCode); Assert.Null(c.TransferStatus); });

            all.Destination = CuttingProductionFilter.DestinationNotRecorded;
            Assert.Equal(Set(HistoricalIds), Ids((await repo.SearchAsync(all, null)).Where(c => HistoricalIds.Contains(c.Id))));
            Assert.Empty(Only(d, await repo.SearchAsync(all, null)));                                                     // and none of the new ones
            all.Destination = null; all.DeliveryStatus = CuttingProductionFilter.DeliveryNone;
            Assert.Equal(HistoricalIds.Length, (await repo.SearchAsync(all, null)).Count(c => HistoricalIds.Contains(c.Id)));   // "no delivery" includes them
            all.DeliveryStatus = null; all.Destination = CuttingDestination.MainOffice;
            Assert.DoesNotContain(await repo.SearchAsync(all, null), c => HistoricalIds.Contains(c.Id));                 // they are neither Main Office ...
            all.Destination = CuttingDestination.PotProduction;
            Assert.DoesNotContain(await repo.SearchAsync(all, null), c => HistoricalIds.Contains(c.Id));                 // ... nor Pot Production: unknown stays unknown

            // by Area / Polyhouse / supervisor as the historical rows were made (Area 1: entries 173, 174, Mother Plant Polyhouse 11 "Net House")
            var areaOne = await repo.SearchAsync(new CuttingProductionFilter { From = new DateTime(2026, 1, 1), To = DateTime.Today, AreaId = AreaA, SupervisorId = SupA }, null);
            Assert.Contains(areaOne, c => c.Id == 173 && c.PolyhouseId == 11 && c.PolyhouseName == "Net House");
            var byPolyhouse = await repo.SearchAsync(new CuttingProductionFilter { From = new DateTime(2026, 1, 1), To = DateTime.Today, PolyhouseId = 9 }, null);
            Assert.Equal(new[] { 167, 171 }, Ids(byPolyhouse.Where(c => HistoricalIds.Contains(c.Id))));                 // Mother Plant MP-2025-00001 sits in Polyhouse 9

            Assert.Equal(fingerprint, await env.ScalarAsync<string>($"SELECT CONCAT(COUNT(*), '/', CHECKSUM_AGG(CHECKSUM(*))) FROM dbo.CuttingProductions WHERE Id IN ({string.Join(",", HistoricalIds)})"));
        }

        [SkippableFact]
        public async Task FilteringNeverChangesData_NoStockOrDeliveryMoves()
        {
            var env = await OpenAsync();
            await SeedAsync(env);
            const string state = @"SELECT CONCAT((SELECT CONCAT(COUNT(*), '/', SUM(PhysicalQuantity), '/', SUM(InTransitQuantity)) FROM dbo.CuttingStock), '|',
                (SELECT COUNT(*) FROM dbo.CuttingStockTransactions), '|', (SELECT COUNT(*) FROM dbo.InternalTransfers), '|', (SELECT COUNT(*) FROM dbo.CuttingProductions))";
            var before = await env.ScalarAsync<string>(state);
            var page = env.Register(await env.LoginAsync(21));
            page.AreaId = AreaA; page.Destination = CuttingDestination.MainOffice; page.DeliveryStatus = "PendingConfirmation";
            await page.OnGetAsync(null, null);
            Assert.Equal(before, await env.ScalarAsync<string>(state));
        }
    }
}
