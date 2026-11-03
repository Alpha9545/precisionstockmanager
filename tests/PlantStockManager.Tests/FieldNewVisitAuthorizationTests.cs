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
    // Field Work follow-up (2026-11-03) over real HTTP: the New Visit page, its search / duplicate handlers, Add Customer now
    // manager-only, and the old Log Visit address redirecting. Permission checks need no database; the per-record checks
    // (another employee's customer -> 404, search returns only my customers) run on PSM_SCRATCH_CONNECTION with real users.
    [Collection("ScratchDb")]
    public class FieldNewVisitAuthorizationTests : IClassFixture<FieldWorkAuthorizationTests.FieldWorkAppFactory>
    {
        private readonly FieldWorkAuthorizationTests.FieldWorkAppFactory _factory;
        public FieldNewVisitAuthorizationTests(FieldWorkAuthorizationTests.FieldWorkAppFactory factory) => _factory = factory;

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
        public async Task NewVisit_Mapped_AnonymousToLogin_DeniedWithoutFieldWorkDay_AddCustomerIsManagerOnly()
        {
            Assert.Equal("FieldWork.Day", FeatureAuthorizationConventions.GetRule("/FieldWork/Visits/New").Read);
            Assert.Equal("FieldWork.Manage", FeatureAuthorizationConventions.GetRule("/FieldWork/Customers/Create").Read);
            foreach (var url in new[] { "/FieldWork/Visits/New", "/FieldWork/Visits/New?handler=Search&q=a", "/FieldWork/Visits/New?handler=CheckPhone&phone=9876543210" })
            {
                AssertRedirect(await GetAsync(_factory, url, null), "/Account/Login");
                AssertRedirect(await GetAsync(_factory, url, "Dashboard.View"), "/Account/AccessDenied");
                AssertRedirect(await GetAsync(_factory, url, "FieldWork.Manage"), "/Account/AccessDenied");   // managers do not log visits
            }
            AssertRedirect(await GetAsync(_factory, "/FieldWork/Customers/Create", "FieldWork.Day"), "/Account/AccessDenied");
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
        public async Task OthersCustomer_404_SearchOnlyMine_DuplicateUsableOnlyForMine_OldLogVisitRedirects()
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
            var options = new FieldWorkOptions { PhotoRoot = Path.Combine(Path.GetTempPath(), "psm-fw-e2e") };
            var photos = new FieldPhotoStore(options);
            var days = new FieldDayService(new FieldDayRepository(db), photos, options);
            var customers = new FieldCustomerService(new FieldCustomerRepository(db), photos, options);
            var visits = new FieldVisitService(new FieldVisitRepository(db), customers, photos, options);
            byte[] Jpeg() { var x = new byte[512]; x[0] = 0xFF; x[1] = 0xD8; x[2] = 0xFF; x[^2] = 0xFF; x[^1] = 0xD9; return x; }
            ClaimsPrincipal P(int id) => new(new ClaimsIdentity(new[] { new Claim("UserId", id.ToString()), new Claim(MinimumAuthorizationLevelHandler.PermissionClaimType, FieldDayRules.DayPermission) }, "T"));
            foreach (var u in new[] { a, b })
            {
                await days.RecordConsentAsync(u);
                Assert.True((await days.StartDayAsync(u, "OwnBike", new FieldDayService.ReadingRequest(10m, 12.97m, 77.59m, 10m, null, Jpeg(), Guid.NewGuid().ToString()))).Ok);
            }
            async Task<(int Id, string Phone)> NewCustomer(int user, string name)
            {
                var phone = "9" + Random.Shared.NextInt64(100_000_000, 999_999_999);
                var r = await visits.SaveVisitAsync(P(user), new FieldVisitService.NewVisitRequest(FieldVisitService.ModeNew, null,
                    new FieldCustomerRules.CustomerInput(name, null, phone, null, "Farmer", "Hoskote", null, null), true,
                    new FieldVisitService.VisitRequest(0, 12.98m, 77.6m, 10m, null, "Talked", null, Array.Empty<string?>(), Array.Empty<string?>(), new List<byte[]>(),
                        null, null, null, null, Guid.NewGuid().ToString())));
                Assert.True(r.Ok, string.Join(" ", r.Errors));
                return (r.CustomerId!.Value, phone);
            }
            var mine = await NewCustomer(a, "ZZTEST NVH Mine");
            var theirs = await NewCustomer(b, "ZZTEST NVH Theirs");

            using var factory = new ScratchFactory(cs!);
            var asA = $"{a}|FieldWork.Day";
            Assert.Equal(HttpStatusCode.OK, (await GetAsync(factory, "/FieldWork/Visits/New", null, asA)).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await GetAsync(factory, $"/FieldWork/Visits/New?customerId={mine.Id}", null, asA)).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await GetAsync(factory, $"/FieldWork/Visits/New?customerId={theirs.Id}", null, asA)).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await GetAsync(factory, $"/FieldWork/Visits/Create?customerId={theirs.Id}", null, asA)).StatusCode);
            AssertRedirect(await GetAsync(factory, $"/FieldWork/Visits/Create?customerId={mine.Id}", null, asA), $"/FieldWork/Visits/New?customerId={mine.Id}");

            var search = await (await GetAsync(factory, "/FieldWork/Visits/New?handler=Search&q=ZZTEST+NVH", null, asA)).Content.ReadAsStringAsync();
            Assert.Contains("ZZTEST NVH Mine", search);
            Assert.DoesNotContain("ZZTEST NVH Theirs", search);

            var dupMine = await (await GetAsync(factory, $"/FieldWork/Visits/New?handler=CheckPhone&phone={mine.Phone}", null, asA)).Content.ReadAsStringAsync();
            Assert.Contains($"\"id\":{mine.Id}", dupMine);
            Assert.Contains("\"usable\":true", dupMine);
            var dupTheirs = await (await GetAsync(factory, $"/FieldWork/Visits/New?handler=CheckPhone&phone={theirs.Phone}", null, asA)).Content.ReadAsStringAsync();
            Assert.Contains("\"usable\":false", dupTheirs);
            Assert.DoesNotContain("\"usable\":true", dupTheirs);
        }
    }
}
