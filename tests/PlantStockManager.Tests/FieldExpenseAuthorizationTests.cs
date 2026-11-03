using System.Net;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using PlantStockManager.Authorization;
using PlantStockManager.Data;
using PlantStockManager.Services.FieldWork;

namespace PlantStockManager.Tests
{
    // Field Work Phase 4 (2026-11-03) over real HTTP: the expense pages in the permission map, anonymous -> login, an employee
    // cannot reach the manager page or its export, and (on PSM_SCRATCH_CONNECTION) another employee's expense and bill photo
    // are 404 while the owner and a manager get them; the Start Day gate lets the expense pages through after consent.
    [Collection("ScratchDb")]
    public class FieldExpenseAuthorizationTests : IClassFixture<FieldWorkAuthorizationTests.FieldWorkAppFactory>
    {
        private readonly FieldWorkAuthorizationTests.FieldWorkAppFactory _factory;
        public FieldExpenseAuthorizationTests(FieldWorkAuthorizationTests.FieldWorkAppFactory factory) => _factory = factory;

        private static async Task<HttpResponseMessage> GetAsync(WebApplicationFactory<Program> factory, string url, string? permissions, string? user = null)
        {
            var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
            var request = new HttpRequestMessage(HttpMethod.Get, url);
            if (permissions != null) request.Headers.Add(DeleteAuthorizationTests.HeaderAuthHandler.Header, permissions);
            if (user != null) request.Headers.Add(FieldCustomerAuthorizationTests.UserHeaderAuthHandler.Header, user);
            return await client.SendAsync(request);
        }

        private static void AssertRedirect(HttpResponseMessage r, string path)
        {
            Assert.Equal(HttpStatusCode.Redirect, r.StatusCode);
            Assert.Contains(path, r.Headers.Location!.ToString(), StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public async Task Pages_Mapped_AnonymousToLogin_OthersDenied_EmployeeCannotReachManagerPageOrExport()
        {
            Assert.Equal("FieldWork.Day", FeatureAuthorizationConventions.GetRule("/FieldWork/Expenses/Index").Read);
            Assert.Equal("FieldWork.Day", FeatureAuthorizationConventions.GetRule("/FieldWork/Expenses/Create").Read);
            Assert.Equal("FieldWork.Day|FieldWork.Manage", FeatureAuthorizationConventions.GetRule("/FieldWork/Expenses/Details").Read);
            Assert.Equal("FieldWork.Manage", FeatureAuthorizationConventions.GetRule("/FieldWork/Expenses/Manage/Index").Read);

            foreach (var url in new[] { "/FieldWork/Expenses/Index", "/FieldWork/Expenses/Create", "/FieldWork/Expenses/Details?id=1",
                                        "/FieldWork/Expenses/Details?id=1&handler=Photo", "/FieldWork/Expenses/Manage/Index",
                                        "/FieldWork/Expenses/Manage/Index?handler=ExportExcel" })
            {
                AssertRedirect(await GetAsync(_factory, url, null), "/Account/Login");
                AssertRedirect(await GetAsync(_factory, url, "Dashboard.View"), "/Account/AccessDenied");
            }
            AssertRedirect(await GetAsync(_factory, "/FieldWork/Expenses/Manage/Index", "FieldWork.Day"), "/Account/AccessDenied");
            AssertRedirect(await GetAsync(_factory, "/FieldWork/Expenses/Manage/Index?handler=ExportExcel", "FieldWork.Day"), "/Account/AccessDenied");
            AssertRedirect(await GetAsync(_factory, "/FieldWork/Expenses/Create", "FieldWork.Manage"), "/Account/AccessDenied");
        }

        private sealed class ScratchFactory : WebApplicationFactory<Program>
        {
            private readonly string _cs;
            public ScratchFactory(string cs) => _cs = cs;
            protected override void ConfigureWebHost(Microsoft.AspNetCore.Hosting.IWebHostBuilder builder)
            {
                builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["ConnectionStrings:DefaultConnection"] = _cs,
                    ["DailyReportWhatsApp:Enabled"] = "false",
                    ["FieldWork:AutoCloseEnabled"] = "false",
                    ["FieldWork:PhotoRoot"] = Path.Combine(Path.GetTempPath(), "psm-fw-exp-http"),
                }));
                builder.ConfigureTestServices(services =>
                {
                    services.AddAuthentication().AddScheme<AuthenticationSchemeOptions, FieldCustomerAuthorizationTests.UserHeaderAuthHandler>(
                        FieldCustomerAuthorizationTests.UserHeaderAuthHandler.SchemeName, _ => { });
                    services.PostConfigure<AuthenticationOptions>(o => o.DefaultAuthenticateScheme = FieldCustomerAuthorizationTests.UserHeaderAuthHandler.SchemeName);
                });
            }
        }

        [SkippableFact]
        public async Task OthersExpenseAndPhoto_404_OwnerAndManager200_GateLetsExpensePagesThroughWithoutAnOpenDay()
        {
            var cs = Environment.GetEnvironmentVariable("PSM_SCRATCH_CONNECTION");
            Skip.If(string.IsNullOrWhiteSpace(cs) || !cs.Contains("PlantsIMS2_Scratch_", StringComparison.Ordinal), "Set PSM_SCRATCH_CONNECTION to a PlantsIMS2_Scratch_* copy.");
            int a, b, m;
            using (var conn = new SqlConnection(cs))
            {
                await conn.OpenAsync();
                using (var probe = new SqlCommand(FieldExpenseSchema.ProbeSql, conn))
                    Skip.If((int)(await probe.ExecuteScalarAsync())! != 1, "2026-11-03_FieldWorkExpenses.sql is not applied to this scratch copy.");
                var ids = new List<int>();
                using (var cmd = new SqlCommand("SELECT TOP 3 Id FROM dbo.IMSUsers WHERE IsActive = 1 ORDER BY Id", conn))
                using (var r = await cmd.ExecuteReaderAsync())
                    while (await r.ReadAsync()) ids.Add(r.GetInt32(0));
                (a, b, m) = (ids[0], ids[1], ids[2]);
                using var clean = new SqlCommand(FieldCustomerE2ETests.Phase2CleanupSql + @"
DELETE e FROM dbo.FieldDayEvents e JOIN dbo.FieldDays d ON d.Id = e.FieldDayId WHERE d.UserId IN (@A, @B, @C);
DELETE FROM dbo.FieldDays WHERE UserId IN (@A, @B, @C);
DELETE FROM dbo.FieldPhotos WHERE OwnerUserId IN (@A, @B, @C);", conn);
                clean.Parameters.AddWithValue("@A", a);
                clean.Parameters.AddWithValue("@B", b);
                clean.Parameters.AddWithValue("@C", m);
                await clean.ExecuteNonQueryAsync();
            }
            var db = new DatabaseHelper(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:DefaultConnection"] = cs }).Build());
            var options = new FieldWorkOptions { PhotoRoot = Path.Combine(Path.GetTempPath(), "psm-fw-exp-http") };
            var photos = new FieldPhotoStore(options);
            var days = new FieldDayService(new FieldDayRepository(db), photos, options);
            var expenses = new FieldExpenseService(new FieldExpenseRepository(db), photos, options);
            foreach (var u in new[] { a, b })
                await days.RecordConsentAsync(u);   // consent given, but NO day started today
            var jpeg = new byte[512]; jpeg[0] = 0xFF; jpeg[1] = 0xD8; jpeg[2] = 0xFF; jpeg[^2] = 0xFF; jpeg[^1] = 0xD9;
            var owner = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim("UserId", a.ToString()),
                new Claim(MinimumAuthorizationLevelHandler.PermissionClaimType, FieldDayRules.DayPermission) }, "T"));
            var created = await expenses.CreateAsync(owner, new FieldExpenseService.CreateRequest("Toll", "45", null, null, jpeg, 1, Guid.NewGuid().ToString()));
            Assert.True(created.Ok, string.Join(" ", created.Errors));
            var id = created.Id!.Value;

            using var factory = new ScratchFactory(cs!);
            string Emp(int u) => $"{u}|FieldWork.Day";
            var mgr = $"{m}|FieldWork.Manage";

            // the gate: no Open day, yet the employee's expense pages open; a visit page still sends them to Start Day
            Assert.Equal(HttpStatusCode.OK, (await GetAsync(factory, "/FieldWork/Expenses/Index", null, Emp(a))).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await GetAsync(factory, "/FieldWork/Expenses/Create", null, Emp(a))).StatusCode);
            AssertRedirect(await GetAsync(factory, "/FieldWork/Visits/New", null, Emp(a)), "/FieldWork/StartDay");

            Assert.Equal(HttpStatusCode.OK, (await GetAsync(factory, $"/FieldWork/Expenses/Details?id={id}", null, Emp(a))).StatusCode);
            var ownPhoto = await GetAsync(factory, $"/FieldWork/Expenses/Details?id={id}&handler=Photo", null, Emp(a));
            Assert.Equal(HttpStatusCode.OK, ownPhoto.StatusCode);
            Assert.Equal("image/jpeg", ownPhoto.Content.Headers.ContentType!.MediaType);

            Assert.Equal(HttpStatusCode.NotFound, (await GetAsync(factory, $"/FieldWork/Expenses/Details?id={id}", null, Emp(b))).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await GetAsync(factory, $"/FieldWork/Expenses/Details?id={id}&handler=Photo", null, Emp(b))).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await GetAsync(factory, "/FieldWork/Expenses/Details?id=2147483000", null, Emp(a))).StatusCode);
            var listB = await (await GetAsync(factory, "/FieldWork/Expenses/Index", null, Emp(b))).Content.ReadAsStringAsync();
            Assert.DoesNotContain($"Details?id={id}\"", listB);

            Assert.Equal(HttpStatusCode.OK, (await GetAsync(factory, $"/FieldWork/Expenses/Details?id={id}", null, mgr)).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await GetAsync(factory, $"/FieldWork/Expenses/Details?id={id}&handler=Photo", null, mgr)).StatusCode);
            var list = await (await GetAsync(factory, "/FieldWork/Expenses/Manage/Index", null, mgr)).Content.ReadAsStringAsync();
            Assert.Contains($"Details?id={id}", list);
            var export = await GetAsync(factory, "/FieldWork/Expenses/Manage/Index?handler=ExportExcel", null, mgr);
            Assert.Equal(HttpStatusCode.OK, export.StatusCode);
            Assert.Equal("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", export.Content.Headers.ContentType!.MediaType);
        }
    }
}
