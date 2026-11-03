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
    // Field Work Phase 3 (2026-11-03) over real HTTP through the real pipeline. Page permissions need no database; another
    // employee's route -> 404 runs on PSM_SCRATCH_CONNECTION only, signed in as real users (X-Test-User header).
    [Collection("ScratchDb")]
    public class FieldRouteAuthorizationTests : IClassFixture<FieldWorkAuthorizationTests.FieldWorkAppFactory>
    {
        private readonly FieldWorkAuthorizationTests.FieldWorkAppFactory _factory;
        public FieldRouteAuthorizationTests(FieldWorkAuthorizationTests.FieldWorkAppFactory factory) => _factory = factory;

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
        public async Task RoutePages_AreMapped_AnonymousToLogin_NoPermissionDenied_EmployeeCannotOpenTeam()
        {
            Assert.Equal("FieldWork.Day|FieldWork.Manage", FeatureAuthorizationConventions.GetRule("/FieldWork/Route").Read);
            Assert.Equal("FieldWork.Manage", FeatureAuthorizationConventions.GetRule("/FieldWork/Manage/Team").Read);
            foreach (var url in new[] { "/FieldWork/Route", "/FieldWork/Route?dayId=1", "/FieldWork/Manage/Team" })
            {
                AssertRedirect(await GetAsync(_factory, url, null), "/Account/Login");
                AssertRedirect(await GetAsync(_factory, url, "Dashboard.View"), "/Account/AccessDenied");
            }
            AssertRedirect(await GetAsync(_factory, "/FieldWork/Manage/Team", "FieldWork.Day"), "/Account/AccessDenied");
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
                    ["FieldWork:PhotoRoot"] = Path.Combine(Path.GetTempPath(), "psm-fw-e2e"),
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
        public async Task AnotherEmployeesRoute_Returns404_OwnerAndManagerSeeTheMap()
        {
            var cs = Environment.GetEnvironmentVariable("PSM_SCRATCH_CONNECTION");
            Skip.If(string.IsNullOrWhiteSpace(cs) || !cs.Contains("PlantsIMS2_Scratch_", StringComparison.Ordinal), "Set PSM_SCRATCH_CONNECTION to a PlantsIMS2_Scratch_* copy.");
            int a, b, m;
            using (var conn = new SqlConnection(cs))
            {
                await conn.OpenAsync();
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
            var options = new FieldWorkOptions { PhotoRoot = Path.Combine(Path.GetTempPath(), "psm-fw-e2e") };
            var days = new FieldDayService(new FieldDayRepository(db), new FieldPhotoStore(options), options);
            byte[] Jpeg() { var x = new byte[512]; x[0] = 0xFF; x[1] = 0xD8; x[2] = 0xFF; x[^2] = 0xFF; x[^1] = 0xD9; return x; }
            var dayIds = new Dictionary<int, int>();
            foreach (var u in new[] { a, b })
            {
                await days.RecordConsentAsync(u);
                var s = await days.StartDayAsync(u, "OwnBike", new FieldDayService.ReadingRequest(10m, 12.9716m, 77.5946m, 10m, null, Jpeg(), Guid.NewGuid().ToString()));
                Assert.True(s.Ok, string.Join(" ", s.Errors));
                dayIds[u] = s.DayId!.Value;
            }

            using var factory = new ScratchFactory(cs!);
            var asA = $"{a}|FieldWork.Day";
            var asM = $"{m}|FieldWork.Manage";
            Assert.Equal(HttpStatusCode.NotFound, (await GetAsync(factory, $"/FieldWork/Route?dayId={dayIds[b]}", null, asA)).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await GetAsync(factory, $"/FieldWork/Route?EmployeeId={b}", null, asA)).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await GetAsync(factory, $"/FieldWork/Route?dayId=99999999", null, asA)).StatusCode);
            var own = await GetAsync(factory, $"/FieldWork/Route?dayId={dayIds[a]}", null, asA);
            Assert.Equal(HttpStatusCode.OK, own.StatusCode);
            var html = await own.Content.ReadAsStringAsync();
            Assert.Contains("fw-route-map", html);
            Assert.Contains("\"kind\":\"Start\"", html);
            Assert.Contains("/lib/leaflet/dist/leaflet.js", html);
            Assert.Equal(HttpStatusCode.OK, (await GetAsync(factory, $"/FieldWork/Route?dayId={dayIds[b]}", null, asM)).StatusCode);
            var team = await (await GetAsync(factory, "/FieldWork/Manage/Team", null, asM)).Content.ReadAsStringAsync();
            Assert.Contains($"dayId={dayIds[a]}", team);
            // Day Details shows the route section to the owner
            Assert.Contains("fwDayRouteMap", await (await GetAsync(factory, $"/FieldWork/DayDetails?id={dayIds[a]}", null, asA)).Content.ReadAsStringAsync());
        }
    }
}
