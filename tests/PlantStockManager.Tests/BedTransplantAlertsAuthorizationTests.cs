using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using PlantStockManager.Authorization;
using PlantStockManager.Data;
using PlantStockManager.Models;
using PlantStockManager.Services;

namespace PlantStockManager.Tests
{
    // 2026-11-07 Bed Sowing Alerts over real HTTP through the real pipeline (FeatureAuthorizationConventions + the
    // permission policy), header sign-in as DeleteAuthorizationTests. Refusals need no database; requests that reach the
    // page run against the scratch copy (PSM_SCRATCH_CONNECTION) and are skipped without it -- never PlantsIMS2_Test.
    // Also: an alert links to its bed's Details page, and the dashboard links to the alerts page.
    [Collection("ScratchDb")]
    public class BedTransplantAlertsAuthorizationTests : IClassFixture<DeleteAuthorizationTests.AppFactory>, IClassFixture<DailyOperationsReportAuthorizationTests.ScratchAppFactory>
    {
        private const string Page = "/Production/BedSowing/Alerts";
        private readonly DeleteAuthorizationTests.AppFactory _noDb;
        private readonly DailyOperationsReportAuthorizationTests.ScratchAppFactory _scratch;

        public BedTransplantAlertsAuthorizationTests(DeleteAuthorizationTests.AppFactory noDb, DailyOperationsReportAuthorizationTests.ScratchAppFactory scratch)
        {
            _noDb = noDb;
            _scratch = scratch;
        }

        private static async Task<HttpResponseMessage> GetAsync(WebApplicationFactory<Program> factory, string url, string? permissions)
        {
            var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
            var request = new HttpRequestMessage(HttpMethod.Get, url);
            if (permissions != null)
                request.Headers.Add(DeleteAuthorizationTests.HeaderAuthHandler.Header, permissions);
            return await client.SendAsync(request);
        }

        [Fact]
        public void Page_HasTheSameViewersAsBedSowing()
        {
            Assert.Equal(FeatureAuthorizationConventions.GetRule("/Production/BedSowing/Index").Read,
                FeatureAuthorizationConventions.GetRule(Page).Read);
            Assert.Equal("Sowing.View|" + BedSowingRules.TransplantPermission, FeatureAuthorizationConventions.GetRule(Page).Read);
        }

        [Fact]
        public async Task Anonymous_IsSentToLogin()
        {
            var response = await GetAsync(_noDb, Page, null);
            Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
            Assert.Contains("/Account/Login", response.Headers.Location!.ToString());
        }

        [Theory]
        [InlineData("Booking.View")]
        [InlineData("Sowing.Enter")]
        [InlineData("ReadyStock.View")]      // may see Ready Alerts, not bed alerts
        public async Task WithoutSowingViewOrTransplant_IsDenied(string permissions)
        {
            var response = await GetAsync(_noDb, Page, permissions);
            Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
            Assert.Contains("/Account/AccessDenied", response.Headers.Location!.ToString());
        }

        [SkippableTheory]
        [InlineData("Sowing.View")]
        [InlineData(BedSowingRules.TransplantPermission)]
        public async Task SowingViewOrTransplanter_MayOpen(string permissions)
        {
            DailyOperationsReportE2ETests.ScratchConnectionOrSkip();
            var response = await GetAsync(_scratch, Page, permissions);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Contains("Bed Sowing Alerts", await response.Content.ReadAsStringAsync());
        }

        [SkippableFact]
        public async Task OverdueAlert_LinksToItsBedDetails_AndTheNavigationAndDashboardLinkToThePage()
        {
            var cs = DailyOperationsReportE2ETests.ScratchConnectionOrSkip()!;
            int bedId;
            using (var scope = _scratch.Services.CreateScope())
            {
                using var conn = new SqlConnection(cs);
                await conn.OpenAsync();
                using (var probe = new SqlCommand(BedTransplantScheduleSchema.ProbeSql, conn))
                    Skip.If((int)(await probe.ExecuteScalarAsync())! != 1, "2026-11-07_BedTransplantDays.sql is not applied to this scratch database.");
                async Task<int> Scalar(string sql, params (string, object)[] args)
                {
                    using var cmd = new SqlCommand(sql, conn);
                    foreach (var (n, v) in args) cmd.Parameters.AddWithValue(n, v);
                    return Convert.ToInt32(await cmd.ExecuteScalarAsync());
                }
                var polyhouse = await Scalar("SELECT TOP 1 PolyhouseId FROM dbo.BedSowingLocations WHERE IsActive = 1 ORDER BY PolyhouseId");
                var area = await Scalar("SELECT AreaId FROM dbo.BedSowingLocations WHERE PolyhouseId = @P", ("@P", polyhouse));
                var name = "ZZTEST BedAlertHttp " + Guid.NewGuid().ToString("N")[..10];
                var species = await Scalar(@"INSERT INTO dbo.PlantSpecies (PlantTypeId, Name, ReadyStockDays, BedTransplantDays)
VALUES ((SELECT MIN(Id) FROM dbo.PlantTypes), @N, 30, 10); SELECT CAST(SCOPE_IDENTITY() AS INT)", ("@N", name));
                var lot = await Scalar(@"INSERT INTO dbo.SeedStock (SpeciesId, AreaId, BatchNo, Unit, PhysicalQuantity, CreatedBy)
VALUES (@S, @A, @B, N'Seeds', 10000, N'bed-alert-http'); SELECT CAST(SCOPE_IDENTITY() AS INT)", ("@S", species), ("@A", area), ("@B", "LOT-" + name[^10..]));
                var user = await Scalar("SELECT TOP 1 Id FROM dbo.IMSUsers WHERE IsActive = 1 ORDER BY Id");
                var bed = new BedSowing
                {
                    SpeciesId = species, SeedStockId = lot, PolyhouseId = polyhouse, Quantity = 500,
                    SowingDate = AppClock.Today.AddDays(-15), Remarks = "bed alert http test", SubmissionToken = Guid.NewGuid()   // due 5 days ago
                };
                var (ok, msg, _) = await scope.ServiceProvider.GetRequiredService<BedSowingRepository>()
                    .CreateAsync(bed, new BedSowingRepository.Actor(user, "bed-alert-http", false), _ => true);
                Assert.True(ok, msg);
                bedId = bed.Id;
            }

            var html = await (await GetAsync(_scratch, Page, "FULL")).Content.ReadAsStringAsync();
            var overdue = html[html.IndexOf("id=\"overdue\"", StringComparison.Ordinal)..html.IndexOf("id=\"today\"", StringComparison.Ordinal)];
            Assert.Contains($"href=\"/Production/BedSowing/Details/{bedId}\"", overdue);   // Details is @page "{id:int}"
            Assert.Contains("href=\"/Production/BedSowing/Alerts\"", html);                       // navigation entry

            var dashboard = await (await GetAsync(_scratch, "/ManagementDashboard/Index", "FULL")).Content.ReadAsStringAsync();
            Assert.Contains("id=\"bed-sowing-alerts\"", dashboard);
            Assert.Contains("href=\"/Production/BedSowing/Alerts\"", dashboard);
            Assert.Contains("href=\"/Production/ReadyAlerts\"", dashboard);                // Ready Alerts still linked
        }
    }
}
