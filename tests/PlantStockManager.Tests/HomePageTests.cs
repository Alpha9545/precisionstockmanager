using System.Security.Claims;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using PlantStockManager.Authorization;
using PlantStockManager.Data;
using PlantStockManager.Services;

namespace PlantStockManager.Tests
{
    // Company Home page ("/") and its separation from the Administrator-only Management
    // Dashboard: who may open what, what each user sees, navigation and links. Uses the
    // application's real permission policy provider and handler.
    public class HomePageTests
    {
        private static string RepoRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "PlantStockManager.sln")))
                dir = dir.Parent;
            return dir?.FullName ?? throw new InvalidOperationException("Repository root not found.");
        }

        private static string ReadSource(params string[] parts) => File.ReadAllText(Path.Combine(new[] { RepoRoot() }.Concat(parts).ToArray()));

        internal static FeatureAccessService Access()
        {
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddSingleton<IAuthorizationPolicyProvider, PermissionAuthorizationPolicyProvider>();
            services.AddScoped<IAuthorizationHandler, MinimumAuthorizationLevelHandler>();
            services.AddAuthorization();
            services.AddScoped<FeatureAccessService>();
            return services.BuildServiceProvider().CreateScope().ServiceProvider.GetRequiredService<FeatureAccessService>();
        }

        internal static ClaimsPrincipal User(string[] permissions, bool fullAccess = false, bool authenticated = true, int userId = 1)
        {
            var claims = permissions.Select(p => new Claim(MinimumAuthorizationLevelHandler.PermissionClaimType, p)).ToList();
            claims.Add(new Claim("UserId", userId.ToString()));
            claims.Add(new Claim(ClaimTypes.Name, "tester"));
            if (fullAccess) claims.Add(new Claim(ClaimsPrincipalSecurityExtensions.FullAccessClaimType, "true"));
            return new ClaimsPrincipal(authenticated ? new ClaimsIdentity(claims, "test") : new ClaimsIdentity());
        }

        private static readonly string[] ManagerLike =
            { "Dashboard.View", "Reports.View", "Booking.View", "Dispatch.View", "ReadyStock.View", "Maintenance.Manage", "Store.Manage", "Labour.View" };

        // ---- 1 / 5 / 6 / 8: access rules -------------------------------------------------

        [Fact]
        public void AccessRules_AreUnchanged()
        {
            Assert.Equal("Dashboard.View", FeatureAuthorizationConventions.GetRule("/Index").Read);   // home: every role holds it
            Assert.Equal("Admin.ManageAreas", FeatureAuthorizationConventions.GetRule("/ManagementDashboard/Index").Read);
        }

        [Fact]
        public async Task AnyUserWithTheHomePermission_CanOpenHome_OnlyTheAdministratorTheManagementDashboard()
        {
            var access = Access();
            var admin = User(Array.Empty<string>(), fullAccess: true);
            var manager = User(ManagerLike);
            var storeUser = User(new[] { "Dashboard.View", "Store.Request" });

            Assert.True(await access.CanAccessPageAsync(admin, "/Index"));
            Assert.True(await access.CanAccessPageAsync(manager, "/Index"));
            Assert.True(await access.CanAccessPageAsync(storeUser, "/Index"));
            Assert.False(await access.CanAccessPageAsync(User(Array.Empty<string>(), authenticated: false), "/Index"));

            Assert.True(await access.CanAccessPageAsync(admin, HomeQuickAccess.ManagementDashboardPage));
            Assert.False(await access.CanAccessPageAsync(manager, HomeQuickAccess.ManagementDashboardPage));
            Assert.False(await access.CanAccessPageAsync(storeUser, HomeQuickAccess.ManagementDashboardPage));
        }

        // ---- 3 / 4: what each user sees ------------------------------------------------------

        [Fact]
        public async Task QuickAccess_ShowsOnlyPermittedLinks_AndHidesEmptyModules()
        {
            var access = Access();
            var store = await HomeQuickAccess.VisibleAsync(p => access.CanAccessPageAsync(User(new[] { "Dashboard.View", "Store.Request" }), p));

            var keys = store.Select(m => m.Key).ToList();
            Assert.Contains("store", keys);
            Assert.Contains("suggestions", keys);   // open to every signed-in user
            Assert.DoesNotContain("bookings", keys);
            Assert.DoesNotContain("seed", keys);
            Assert.Contains(store.Single(m => m.Key == "store").Links, l => l.Page == "/Store/NewRequest");

            var all = await HomeQuickAccess.VisibleAsync(p => access.CanAccessPageAsync(User(Array.Empty<string>(), fullAccess: true), p));
            Assert.Equal(HomeQuickAccess.Modules.Count, all.Count);
            Assert.Equal(HomeQuickAccess.Modules.Sum(m => m.Links.Count), all.Sum(m => m.Links.Count));

            Assert.Empty(await HomeQuickAccess.VisibleAsync(p => access.CanAccessPageAsync(User(Array.Empty<string>(), authenticated: false), p)));
        }

        [Fact]
        public void QuickAccess_NeverLinksTheManagementDashboard_ItIsASeparateAdminShortcut()
            => Assert.DoesNotContain(HomeQuickAccess.Modules.SelectMany(m => m.Links), l => l.Page.StartsWith("/ManagementDashboard", StringComparison.OrdinalIgnoreCase));

        [Fact]
        public void HomeView_ShowsTheManagementShortcut_OnlyBehindTheAdminCheck()
        {
            var view = ReadSource("Pages", "Index.cshtml");
            var shortcut = Regex.Match(view, @"@if \(Model\.CanOpenManagementDashboard\)\s*\{\s*<a[^>]*asp-page=""/ManagementDashboard/Index""");
            Assert.True(shortcut.Success);
            Assert.Equal(1, Regex.Matches(view, "/ManagementDashboard/Index").Count);
        }

        // ---- 2: no management KPIs on the home page ------------------------------------------

        [Fact]
        public void HomePage_DoesNotUseAnyManagementDashboardData()
        {
            var ctorParams = typeof(PlantStockManager.Pages.IndexModel).GetConstructors().Single().GetParameters().Select(p => p.ParameterType).ToList();
            Assert.DoesNotContain(typeof(ManagementDashboardRepository), ctorParams);
            Assert.DoesNotContain(typeof(BookingRepository), ctorParams);
            Assert.DoesNotContain(typeof(SeedlingFulfilmentRepository), ctorParams);

            var props = typeof(PlantStockManager.Pages.IndexModel).GetProperties().Select(p => p.Name).ToList();
            Assert.DoesNotContain("Overview", props);
            Assert.DoesNotContain("Dashboard", props);
            Assert.DoesNotContain("Crops", props);   // the former company-wide stock / booking figures

            var view = ReadSource("Pages", "Index.cshtml");
            foreach (var kpi in new[] { "Overview", "Ready seedling stock", "Open seedling orders", "Top 3", "Qty(", "Reserved", "still to dispatch" })
                Assert.DoesNotContain(kpi, view);
        }

        // ---- 10 / 11: navigation and links ---------------------------------------------------

        [Fact]
        public void Navigation_HasOneHomeItem_AndOneManagementDashboardItem()
        {
            var layout = ReadSource("Pages", "Shared", "_Layout.cshtml");
            Assert.Equal(1, Regex.Matches(layout, @"<li nav-page=""/ManagementDashboard/Index"">").Count);
            Assert.Equal(1, Regex.Matches(layout, @"<li nav-page=""/index"">", RegexOptions.IgnoreCase).Count);
            var home = layout.Substring(layout.IndexOf(@"<li nav-page=""/index"">", StringComparison.OrdinalIgnoreCase), 600);
            Assert.Contains("Home", home);
            Assert.DoesNotContain("Dashboard", home.Replace("Dashboard Icon", ""));
        }

        [Fact]
        public void EveryQuickAccessLink_IsARealMappedPage_AndNothingIsDuplicated()
        {
            var root = RepoRoot();
            var links = HomeQuickAccess.Modules.SelectMany(m => m.Links).ToList();
            foreach (var l in links)
            {
                Assert.True(FeatureAuthorizationConventions.IsMapped(l.Page), $"{l.Page} is not in the page permission map");
                Assert.True(File.Exists(Path.Combine(root, "Pages" + l.Page.Replace('/', Path.DirectorySeparatorChar) + ".cshtml")), $"{l.Page} has no page file");
            }
            Assert.Equal(HomeQuickAccess.Modules.Count, HomeQuickAccess.Modules.Select(m => m.Key).Distinct().Count());
            Assert.All(HomeQuickAccess.Modules, m => Assert.Equal(m.Links.Count, m.Links.Select(l => l.Page).Distinct().Count()));
            Assert.All(HomeQuickAccess.Modules, m => Assert.True(File.Exists(Path.Combine(root, "wwwroot", "images", m.Icon)), $"icon {m.Icon} missing"));
            Assert.True(File.Exists(Path.Combine(root, "wwwroot", "images", "company-logo.png")));
        }
    }

    // The Home page model against a real scratch database (PSM_SCRATCH_CONNECTION, a
    // PlantsIMS2_Scratch_* copy) -- including an empty one -- for an Administrator and a
    // non-admin.
    [Collection("ScratchDb")]
    public class HomePageE2ETests
    {
        private static async Task<IServiceProvider> OpenAsync()
        {
            var cs = Environment.GetEnvironmentVariable("PSM_SCRATCH_CONNECTION");
            Skip.If(string.IsNullOrWhiteSpace(cs), "Set PSM_SCRATCH_CONNECTION to a PlantsIMS2_Scratch_* database connection string to run the end-to-end tests.");
            using (var conn = new SqlConnection(cs))
            {
                await conn.OpenAsync();
                using var cmd = new SqlCommand("SELECT DB_NAME()", conn);
                var name = (string)(await cmd.ExecuteScalarAsync())!;
                if (!name.StartsWith("PlantsIMS2_Scratch_", StringComparison.Ordinal))
                    throw new InvalidOperationException($"Refusing to run end-to-end tests against '{name}'.");
            }
            var services = new ServiceCollection();
            var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:DefaultConnection"] = cs }).Build();
            services.AddSingleton<IConfiguration>(config);
            services.AddSingleton<DatabaseHelper>();
            services.AddSingleton<Microsoft.Extensions.Options.IOptions<SecurityOptions>>(Microsoft.Extensions.Options.Options.Create(new SecurityOptions()));
            services.Configure<SeedlingWorkflowOptions>(_ => { });
            foreach (var t in typeof(DatabaseHelper).Assembly.GetTypes().Where(t => t.Namespace == "PlantStockManager.Data" && t.IsClass && t.Name.EndsWith("Repository")))
                services.AddScoped(t);
            services.AddLogging();
            services.AddSingleton<IAuthorizationPolicyProvider, PermissionAuthorizationPolicyProvider>();
            services.AddScoped<IAuthorizationHandler, MinimumAuthorizationLevelHandler>();
            services.AddAuthorization();
            services.AddScoped<FeatureAccessService>();
            services.AddScoped<AreaAccessService>();
            services.AddScoped<SeedlingAreaScope>();
            return services.BuildServiceProvider().CreateScope().ServiceProvider;
        }

        private static async Task<PlantStockManager.Pages.IndexModel> LoadAsync(IServiceProvider sp, ClaimsPrincipal user)
        {
            var page = ActivatorUtilities.CreateInstance<PlantStockManager.Pages.IndexModel>(sp);
            page.PageContext = new Microsoft.AspNetCore.Mvc.RazorPages.PageContext { HttpContext = new Microsoft.AspNetCore.Http.DefaultHttpContext { User = user, RequestServices = sp } };
            await page.OnGetAsync();
            return page;
        }

        [SkippableFact]
        public async Task Administrator_GetsTheHomePage_WithTheManagementShortcut_AndEveryModule()
        {
            var sp = await OpenAsync();
            var page = await LoadAsync(sp, HomePageTests.User(Array.Empty<string>(), fullAccess: true, userId: -1));
            Assert.True(page.CanOpenManagementDashboard);
            Assert.Equal(HomeQuickAccess.Modules.Count, page.Modules.Count);
            Assert.False(string.IsNullOrWhiteSpace(page.DisplayName));   // falls back to the username
            Assert.Equal(PlantStockManager.Services.AppClock.Today, page.NowIndia.Date);
        }

        [SkippableFact]
        public async Task NonAdmin_GetsTheHomePage_WithoutTheManagementShortcut()
        {
            var sp = await OpenAsync();
            var page = await LoadAsync(sp, HomePageTests.User(new[] { "Dashboard.View", "Store.Request", "Booking.View" }, userId: -1));
            Assert.False(page.CanOpenManagementDashboard);
            Assert.Contains(page.Modules, m => m.Key == "store");
            Assert.DoesNotContain(page.Modules, m => m.Key == "seed");
            Assert.All(page.Items, i => Assert.NotEqual(HomeQuickAccess.ManagementDashboardPage, i.Page));
        }

        [SkippableFact]
        public async Task CompanyInformation_ComesFromMasterData_AndNeedsNothingElse()
        {
            var sp = await OpenAsync();
            var page = await LoadAsync(sp, HomePageTests.User(new[] { "Dashboard.View" }, userId: -1));
            // Plant Types / Areas are master data; with no stock, sowings or bookings the page still loads.
            Assert.All(page.PlantTypes, t => Assert.False(string.IsNullOrWhiteSpace(t)));
            Assert.All(page.AreaGroups, g => Assert.NotEmpty(g.Names));
            Assert.Empty(page.Items);   // this user holds no permission that produces work items
        }
    }
}
