using System.Net;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PlantStockManager.Authorization;
using PlantStockManager.Data;
using PlantStockManager.Services.FieldWork;

namespace PlantStockManager.Tests
{
    // Field Work Phase 2 (2026-11-03) over real HTTP through the real pipeline (page map + FieldDayGateFilter + page code).
    //  * Page permissions (anonymous -> Login; missing permission -> AccessDenied): no database needed (FieldWorkAuthorizationTests'
    //    factory: header sign-in, unreachable database).
    //  * Another employee's customer / photo / visit by changing the id -> 404: against PSM_SCRATCH_CONNECTION only (skipped
    //    otherwise), signed in as REAL users through the X-Test-User header below.
    [Collection("ScratchDb")]
    public class FieldCustomerAuthorizationTests : IClassFixture<FieldWorkAuthorizationTests.FieldWorkAppFactory>
    {
        private readonly FieldWorkAuthorizationTests.FieldWorkAppFactory _factory;
        public FieldCustomerAuthorizationTests(FieldWorkAuthorizationTests.FieldWorkAppFactory factory) => _factory = factory;

        private static readonly string[] Pages =
        {
            "/FieldWork/Customers/Index", "/FieldWork/Customers/Create", "/FieldWork/Customers/Details?id=1",
            "/FieldWork/Customers/Details?id=1&handler=Photo&photoId=1", "/FieldWork/Customers/Edit?id=1", "/FieldWork/Customers/Note?customerId=1",
            "/FieldWork/Customers/Reassign", "/FieldWork/Visits/Create?customerId=1", "/FieldWork/Visits/Edit?id=1", "/FieldWork/FollowUps/Index",
        };

        private async Task<HttpResponseMessage> GetAsync(WebApplicationFactory<Program> factory, string url, string? permissions, string? userHeader = null)
        {
            var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
            var request = new HttpRequestMessage(HttpMethod.Get, url);
            if (permissions != null)
                request.Headers.Add(DeleteAuthorizationTests.HeaderAuthHandler.Header, permissions);
            if (userHeader != null)
                request.Headers.Add(UserHeaderAuthHandler.Header, userHeader);
            return await client.SendAsync(request);
        }

        private static void AssertRedirect(HttpResponseMessage r, string path)
        {
            Assert.Equal(HttpStatusCode.Redirect, r.StatusCode);
            Assert.Contains(path, r.Headers.Location!.ToString(), StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void EveryPhase2Page_IsMapped()
        {
            foreach (var p in Pages)
                Assert.True(FeatureAuthorizationConventions.IsMapped(p), p);
            Assert.Equal("FieldWork.Manage", FeatureAuthorizationConventions.GetRule("/FieldWork/Customers/Reassign").Read);
            Assert.Equal("FieldWork.Manage", FeatureAuthorizationConventions.GetRule("/FieldWork/Visits/Edit").Read);
            Assert.Equal("FieldWork.Day", FeatureAuthorizationConventions.GetRule("/FieldWork/Visits/Create").Read);
        }

        [Fact]
        public async Task Anonymous_IsSentToLogin()
        {
            foreach (var p in Pages)
                AssertRedirect(await GetAsync(_factory, p, null), "/Account/Login");
        }

        [Fact]
        public async Task WithoutFieldWorkPermission_IsDenied()
        {
            foreach (var p in Pages)
                AssertRedirect(await GetAsync(_factory, p, "Dashboard.View,Booking.View"), "/Account/AccessDenied");
        }

        [Fact]
        public async Task FieldEmployee_CannotOpenManagerPages_ManagerCannotLogVisits()
        {
            AssertRedirect(await GetAsync(_factory, "/FieldWork/Customers/Reassign", "FieldWork.Day"), "/Account/AccessDenied");
            AssertRedirect(await GetAsync(_factory, "/FieldWork/Visits/Edit?id=1", "FieldWork.Day"), "/Account/AccessDenied");
            AssertRedirect(await GetAsync(_factory, "/FieldWork/Visits/Create?customerId=1", "FieldWork.Manage"), "/Account/AccessDenied");
        }

        // ---- scratch: real users, real records ----------------------------------------------------------------------

        // "X-Test-User: <id>|<perm>,<perm>" signs in as that IMSUsers id with those permission claims.
        public sealed class UserHeaderAuthHandler : AuthenticationHandler<AuthenticationSchemeOptions>
        {
            public const string SchemeName = "TestUser";
            public const string Header = "X-Test-User";

            public UserHeaderAuthHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
                : base(options, logger, encoder) { }

            protected override Task<AuthenticateResult> HandleAuthenticateAsync()
            {
                if (!Request.Headers.TryGetValue(Header, out var value))
                    return Task.FromResult(AuthenticateResult.NoResult());
                var parts = value.ToString().Split('|');
                var claims = new List<Claim> { new(ClaimTypes.Name, "zztest-" + parts[0]), new("UserId", parts[0]) };
                foreach (var p in parts.ElementAtOrDefault(1)?.Split(',', StringSplitOptions.RemoveEmptyEntries) ?? Array.Empty<string>())
                    claims.Add(new Claim(MinimumAuthorizationLevelHandler.PermissionClaimType, p));
                var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, SchemeName));
                return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(principal, SchemeName)));
            }
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
                    services.AddAuthentication().AddScheme<AuthenticationSchemeOptions, UserHeaderAuthHandler>(UserHeaderAuthHandler.SchemeName, _ => { });
                    services.PostConfigure<AuthenticationOptions>(o => o.DefaultAuthenticateScheme = UserHeaderAuthHandler.SchemeName);
                });
            }
        }

        [SkippableFact]
        public async Task AnotherEmployeesCustomerVisitAndPhoto_Return404_OwnerAndManagerGet200()
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

            // B owns a customer with a photo and a visit; A is a field employee with consent and an open day (passes the gate)
            var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:DefaultConnection"] = cs }).Build();
            var db = new DatabaseHelper(config);
            var options = new FieldWorkOptions { PhotoRoot = Path.Combine(Path.GetTempPath(), "psm-fw-e2e") };
            var photos = new FieldPhotoStore(options);
            var customers = new FieldCustomerService(new FieldCustomerRepository(db), photos, options);
            var visits = new FieldVisitService(new FieldVisitRepository(db), customers, photos, options);
            var days = new FieldDayService(new FieldDayRepository(db), photos, options);
            byte[] Jpeg() { var x = new byte[512]; x[0] = 0xFF; x[1] = 0xD8; x[2] = 0xFF; x[^2] = 0xFF; x[^1] = 0xD9; return x; }
            ClaimsPrincipal P(int id, string perm) => new(new ClaimsIdentity(new[] { new Claim("UserId", id.ToString()), new Claim(MinimumAuthorizationLevelHandler.PermissionClaimType, perm) }, "T"));
            foreach (var u in new[] { a, b })
            {
                await days.RecordConsentAsync(u);
                Assert.True((await days.StartDayAsync(u, "OwnBike", new FieldDayService.ReadingRequest(10m, 12.9m, 77.5m, 10m, null, Jpeg(), Guid.NewGuid().ToString()))).Ok);
            }
            var created = await customers.CreateAsync(P(b, FieldDayRules.DayPermission), new FieldCustomerService.CreateRequest(
                new FieldCustomerRules.CustomerInput("ZZTEST Http Farm", null, "9" + Random.Shared.NextInt64(100_000_000, 999_999_999), null, "Farmer", "Hoskote", null, null),
                null, 12.9m, 77.5m, 10m, null, Jpeg(), 1, true, Guid.NewGuid().ToString()));
            Assert.True(created.Ok, string.Join(" ", created.Errors));
            var cid = created.Id!.Value;
            var visit = await visits.CreateAsync(P(b, FieldDayRules.DayPermission), new FieldVisitService.VisitRequest(cid, 12.9m, 77.5m, 10m, null, "Talked", null,
                Array.Empty<string?>(), Array.Empty<string?>(), new List<byte[]> { Jpeg() }, 1, null, null, null, Guid.NewGuid().ToString()));
            Assert.True(visit.Ok, string.Join(" ", visit.Errors));
            int customerPhoto, visitPhoto;
            using (var conn = new SqlConnection(cs))
            {
                await conn.OpenAsync();
                using var cmd = new SqlCommand("SELECT c.PhotoId, (SELECT TOP 1 PhotoId FROM dbo.FieldVisitPhotos WHERE VisitId = @V) FROM dbo.FieldCustomers c WHERE c.Id = @C", conn);
                cmd.Parameters.AddWithValue("@C", cid);
                cmd.Parameters.AddWithValue("@V", visit.Id!.Value);
                using var r = await cmd.ExecuteReaderAsync();
                await r.ReadAsync();
                (customerPhoto, visitPhoto) = (r.GetInt32(0), r.GetInt32(1));
            }

            using var factory = new ScratchFactory(cs!);
            var asA = $"{a}|FieldWork.Day";
            var asB = $"{b}|FieldWork.Day";
            var asM = $"{m}|FieldWork.Manage";
            foreach (var url in new[]
            {
                $"/FieldWork/Customers/Details?id={cid}", $"/FieldWork/Customers/Details?id={cid}&handler=Photo&photoId={customerPhoto}",
                $"/FieldWork/Customers/Details?id={cid}&handler=Photo&photoId={visitPhoto}", $"/FieldWork/Customers/Edit?id={cid}",
                $"/FieldWork/Customers/Note?customerId={cid}", $"/FieldWork/Customers/Note?customerId={cid}&visitId={visit.Id}",
                $"/FieldWork/Visits/Create?customerId={cid}",
            })
                Assert.Equal(HttpStatusCode.NotFound, (await GetAsync(factory, url, null, asA)).StatusCode);
            // the owner and a manager get the page and the photos
            Assert.Equal(HttpStatusCode.OK, (await GetAsync(factory, $"/FieldWork/Customers/Details?id={cid}", null, asB)).StatusCode);
            var photo = await GetAsync(factory, $"/FieldWork/Customers/Details?id={cid}&handler=Photo&photoId={visitPhoto}", null, asB);
            Assert.Equal(HttpStatusCode.OK, photo.StatusCode);
            Assert.Equal("image/jpeg", photo.Content.Headers.ContentType!.MediaType);
            Assert.Equal(HttpStatusCode.OK, (await GetAsync(factory, $"/FieldWork/Customers/Details?id={cid}", null, asM)).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await GetAsync(factory, $"/FieldWork/Visits/Edit?id={visit.Id}", null, asM)).StatusCode);
            // a photo id that belongs to another customer is 404 even for the manager
            Assert.Equal(HttpStatusCode.NotFound, (await GetAsync(factory, $"/FieldWork/Customers/Details?id={cid}&handler=Photo&photoId={customerPhoto + 100000}", null, asM)).StatusCode);
            // the employee list does not contain B's customer
            var list = await (await GetAsync(factory, "/FieldWork/Customers/Index?Q=ZZTEST+Http", null, asA)).Content.ReadAsStringAsync();
            Assert.DoesNotContain("ZZTEST Http Farm", list);
            Assert.Contains("ZZTEST Http Farm", await (await GetAsync(factory, "/FieldWork/Customers/Index?Q=ZZTEST+Http", null, asB)).Content.ReadAsStringAsync());
        }
    }
}
