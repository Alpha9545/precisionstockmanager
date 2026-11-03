using System.Net;
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
    // Field Work Phase 5 (2026-11-03) over real HTTP: the dashboard / reports are manager pages (anonymous -> login, an employee
    // -> access denied, also for the export handler); My Summary is an employee page that shows only the signed-in user's
    // numbers and passes the Start Day gate after consent (on PSM_SCRATCH_CONNECTION).
    [Collection("ScratchDb")]
    public class FieldReportAuthorizationTests : IClassFixture<FieldWorkAuthorizationTests.FieldWorkAppFactory>
    {
        private readonly FieldWorkAuthorizationTests.FieldWorkAppFactory _factory;
        public FieldReportAuthorizationTests(FieldWorkAuthorizationTests.FieldWorkAppFactory factory) => _factory = factory;

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
        public async Task ManagerPages_Mapped_AnonymousToLogin_EmployeeDenied_IncludingEveryExport()
        {
            Assert.Equal("FieldWork.Manage", FeatureAuthorizationConventions.GetRule("/FieldWork/Manage/Dashboard").Read);
            Assert.Equal("FieldWork.Manage", FeatureAuthorizationConventions.GetRule("/FieldWork/Manage/Reports").Read);
            Assert.Equal("FieldWork.Day", FeatureAuthorizationConventions.GetRule("/FieldWork/MySummary").Read);

            var managerUrls = new List<string> { "/FieldWork/Manage/Dashboard", "/FieldWork/Manage/Reports" };
            managerUrls.AddRange(FieldReportExport.Reports.Select(r => $"/FieldWork/Manage/Reports?handler=ExportExcel&Report={r}"));
            foreach (var url in managerUrls)
            {
                AssertRedirect(await GetAsync(_factory, url, null), "/Account/Login");
                AssertRedirect(await GetAsync(_factory, url, "Dashboard.View"), "/Account/AccessDenied");
                AssertRedirect(await GetAsync(_factory, url, "FieldWork.Day"), "/Account/AccessDenied");
            }
            AssertRedirect(await GetAsync(_factory, "/FieldWork/MySummary", null), "/Account/Login");
            AssertRedirect(await GetAsync(_factory, "/FieldWork/MySummary", "FieldWork.Manage"), "/Account/AccessDenied");
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
                    ["FieldWork:PhotoRoot"] = Path.Combine(Path.GetTempPath(), "psm-fw-rep-http"),
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
        public async Task MySummary_ShowsOnlyMyDays_WithoutAnOpenDay_ManagerPagesAndExportsWork()
        {
            var cs = Environment.GetEnvironmentVariable("PSM_SCRATCH_CONNECTION");
            Skip.If(string.IsNullOrWhiteSpace(cs) || !cs.Contains("PlantsIMS2_Scratch_", StringComparison.Ordinal), "Set PSM_SCRATCH_CONNECTION to a PlantsIMS2_Scratch_* copy.");
            int a, b, m;
            using (var conn = new SqlConnection(cs))
            {
                await conn.OpenAsync();
                using (var probe = new SqlCommand(FieldCustomerSchema.ProbeSql, conn))
                    Skip.If((int)(await probe.ExecuteScalarAsync())! != 1, "2026-11-03_FieldWorkCustomers.sql is not applied to this scratch copy.");
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
            var options = new FieldWorkOptions { PhotoRoot = Path.Combine(Path.GetTempPath(), "psm-fw-rep-http") };
            var days = new FieldDayService(new FieldDayRepository(db), new FieldPhotoStore(options), options);
            var jpeg = new byte[512]; jpeg[0] = 0xFF; jpeg[1] = 0xD8; jpeg[2] = 0xFF; jpeg[^2] = 0xFF; jpeg[^1] = 0xD9;
            await days.RecordConsentAsync(a);
            await days.RecordConsentAsync(b);
            // only B works today (start 77 km); A has consent but no day
            Assert.True((await days.StartDayAsync(b, "OwnBike", new FieldDayService.ReadingRequest(77m, 12.97m, 77.59m, 10m, null, jpeg, Guid.NewGuid().ToString()))).Ok);
            Assert.True((await days.EndDayAsync(b, new FieldDayService.ReadingRequest(98.5m, 12.97m, 77.59m, 10m, null, jpeg, Guid.NewGuid().ToString()))).Ok);

            using var factory = new ScratchFactory(cs!);
            var pageA = await GetAsync(factory, "/FieldWork/MySummary", null, $"{a}|FieldWork.Day");
            Assert.Equal(HttpStatusCode.OK, pageA.StatusCode);                 // no day today, yet the gate lets it through
            var htmlA = await pageA.Content.ReadAsStringAsync();
            Assert.Contains("No field days in this month.", htmlA);
            Assert.DoesNotContain("21.5", htmlA);                              // B's odometer km is not on A's page
            var htmlB = await (await GetAsync(factory, "/FieldWork/MySummary", null, $"{b}|FieldWork.Day")).Content.ReadAsStringAsync();
            Assert.Contains("21.5 km", htmlB);
            // a query parameter cannot point My Summary at someone else
            var forged = await (await GetAsync(factory, $"/FieldWork/MySummary?UserId={b}&EmployeeId={b}", null, $"{a}|FieldWork.Day")).Content.ReadAsStringAsync();
            Assert.DoesNotContain("21.5", forged);

            var mgr = $"{m}|FieldWork.Manage";
            Assert.Equal(HttpStatusCode.OK, (await GetAsync(factory, "/FieldWork/Manage/Dashboard", null, mgr)).StatusCode);
            foreach (var r in FieldReportExport.Reports)
            {
                Assert.Equal(HttpStatusCode.OK, (await GetAsync(factory, $"/FieldWork/Manage/Reports?Report={r}", null, mgr)).StatusCode);
                var x = await GetAsync(factory, $"/FieldWork/Manage/Reports?handler=ExportExcel&Report={r}", null, mgr);
                Assert.Equal(HttpStatusCode.OK, x.StatusCode);
                Assert.Equal("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", x.Content.Headers.ContentType!.MediaType);
            }
            Assert.Contains("21.5", await (await GetAsync(factory, "/FieldWork/Manage/Reports?Report=daily", null, mgr)).Content.ReadAsStringAsync());
        }
    }
}
