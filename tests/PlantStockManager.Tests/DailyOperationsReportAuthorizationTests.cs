using System.Net;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using PlantStockManager.Data;
using PlantStockManager.Services;

namespace PlantStockManager.Tests
{
    // Daily Operations Report over real HTTP through the real pipeline
    // (FeatureAuthorizationConventions + the permission policy handler). Sign-in is the
    // header scheme of DeleteAuthorizationTests ("X-Test-Permissions: FULL" = System
    // Administrator); a denial still goes through the app's cookie scheme, i.e. the
    // same redirect to /Account/AccessDenied as in production.
    //
    // Refusals need no database (the request never reaches the page). Requests that DO
    // reach the page run against the scratch database (PSM_SCRATCH_CONNECTION) and are
    // skipped without it -- never against the developer's PlantsIMS2_Test.
    [Collection("ScratchDb")]
    public class DailyOperationsReportAuthorizationTests : IClassFixture<DeleteAuthorizationTests.AppFactory>, IClassFixture<DailyOperationsReportAuthorizationTests.ScratchAppFactory>
    {
        private const string Page = "/ManagementDashboard/DailyOperationsReport";
        private readonly DeleteAuthorizationTests.AppFactory _noDb;
        private readonly ScratchAppFactory _scratch;

        public DailyOperationsReportAuthorizationTests(DeleteAuthorizationTests.AppFactory noDb, ScratchAppFactory scratch)
        {
            _noDb = noDb;
            _scratch = scratch;
        }

        // The app pointed at the scratch database.
        public sealed class ScratchAppFactory : WebApplicationFactory<Program>
        {
            protected override void ConfigureWebHost(Microsoft.AspNetCore.Hosting.IWebHostBuilder builder)
            {
                var cs = Environment.GetEnvironmentVariable("PSM_SCRATCH_CONNECTION");
                builder.ConfigureTestServices(services =>
                {
                    services.AddAuthentication().AddScheme<AuthenticationSchemeOptions, DeleteAuthorizationTests.HeaderAuthHandler>(DeleteAuthorizationTests.HeaderAuthHandler.SchemeName, _ => { });
                    services.PostConfigure<AuthenticationOptions>(o => o.DefaultAuthenticateScheme = DeleteAuthorizationTests.HeaderAuthHandler.SchemeName);
                    if (!string.IsNullOrWhiteSpace(cs))
                    {
                        var config = new ConfigurationBuilder()
                            .AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:DefaultConnection"] = cs }).Build();
                        services.RemoveAll<DatabaseHelper>();
                        services.AddSingleton(new DatabaseHelper(config));
                    }
                });
            }
        }

        private static async Task<HttpResponseMessage> GetAsync(WebApplicationFactory<Program> factory, string url, string? permissions)
        {
            var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
            var request = new HttpRequestMessage(HttpMethod.Get, url);
            if (permissions != null)
                request.Headers.Add(DeleteAuthorizationTests.HeaderAuthHandler.Header, permissions);
            return await client.SendAsync(request);
        }

        private Task<HttpResponseMessage> AsAdminAsync(string url)
        {
            DailyOperationsReportE2ETests.ScratchConnectionOrSkip();
            return GetAsync(_scratch, url, "FULL");
        }

        private static void AssertAccessDenied(HttpResponseMessage response)
        {
            Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
            Assert.Contains("/Account/AccessDenied", response.Headers.Location!.ToString());
        }

        // ---- Refused before any page code runs -----------------------------------

        [Theory]
        [InlineData(Page)]
        [InlineData(Page + "?Date=2026-10-01")]
        [InlineData(Page + "?handler=Text&Date=2026-10-01")]
        [InlineData(Page + "?From=2026-09-01&To=2026-10-01&ShowAll=true")]
        public async Task Anonymous_IsSentToLogin(string url)
        {
            var response = await GetAsync(_noDb, url, null);
            Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
            Assert.Contains("/Account/Login", response.Headers.Location!.ToString());
        }

        // Every non-administrator permission set -- including one holding every Admin.*
        // code and the Management Dashboard's own Admin.ManageAreas -- is refused, for
        // the page, a specific date, the history range and the .txt export alike.
        [Theory]
        [InlineData(Page, "Dashboard.View")]
        [InlineData(Page, "Admin.ManageAreas")]
        [InlineData(Page, "Reports.View,Booking.View,Dispatch.View,ReadyStock.View")]
        [InlineData(Page, "Admin.ManageUsers,Admin.ManageRoles,Admin.ManageMasters,Admin.ManageAreas")]
        [InlineData(Page + "?Date=2026-10-01", "Admin.ManageAreas")]
        [InlineData(Page + "?From=2026-09-01&To=2026-10-01&ShowAll=true", "Reports.View")]
        [InlineData(Page + "?handler=Text&Date=2026-10-01", "Admin.ManageAreas")]
        [InlineData(Page + "?handler=Text", "Reports.View,Dashboard.View")]
        public async Task NonAdministrator_GetsAccessDenied(string url, string permissions)
            => AssertAccessDenied(await GetAsync(_noDb, url, permissions));

        // Read-only page: no state-changing request is accepted from anyone else either.
        [Fact]
        public async Task NonAdministrator_Post_GetsAccessDenied()
        {
            var client = _noDb.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
            var request = new HttpRequestMessage(HttpMethod.Post, Page) { Content = new FormUrlEncodedContent(Array.Empty<KeyValuePair<string, string>>()) };
            request.Headers.Add(DeleteAuthorizationTests.HeaderAuthHandler.Header, "Admin.ManageAreas");
            AssertAccessDenied(await client.SendAsync(request));
        }

        // ---- The administrator (scratch database) ---------------------------------

        [SkippableFact]
        public async Task Administrator_SeesTheReport_ForTheRequestedDate()
        {
            var response = await AsAdminAsync(Page + "?Date=2026-10-01");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var html = await response.Content.ReadAsStringAsync();
            Assert.Contains("<h2 class=\"mb-0\">Daily Operations Report</h2>", html);
            Assert.Contains("Date: 2026-10-01", html);
            Assert.Contains("value=\"2026-10-01\"", html);
            Assert.Contains("Daily Operations", html);
            Assert.Contains("Software Activity", html);
            Assert.Contains("Report history", html);
        }

        [SkippableFact]
        public async Task PreviousAndNextDay_LinkToTheAdjacentDates()
        {
            var html = await (await AsAdminAsync(Page + "?Date=2026-10-01")).Content.ReadAsStringAsync();
            Assert.Matches(new Regex("<a[^>]*id=\"prev-day\"[^>]*Date=2026-09-30"), html);
            Assert.Matches(new Regex("<a[^>]*id=\"next-day\"[^>]*Date=2026-10-02"), html);

            var today = await (await AsAdminAsync(Page)).Content.ReadAsStringAsync();
            Assert.Contains($"value=\"{AppClock.Today:yyyy-MM-dd}\"", today);
            Assert.DoesNotContain("id=\"next-day\"", today);
            Assert.Contains("title=\"There is no report for a future date\"", today);
        }

        [SkippableFact]
        public async Task FutureDate_ShowsToday_WithANotice()
        {
            var html = await (await AsAdminAsync(Page + "?Date=2999-01-01")).Content.ReadAsStringAsync();
            Assert.Contains($"Date: {AppClock.Today:yyyy-MM-dd}", html);
            Assert.Contains("is in the future", html);
        }

        [SkippableFact]
        public async Task HistoricalRange_ListsEveryDay_AndCanShowFullReports()
        {
            var html = await (await AsAdminAsync(Page + "?Date=2026-10-01&From=2026-09-25&To=2026-10-01&ShowAll=true")).Content.ReadAsStringAsync();
            for (var d = new DateTime(2026, 9, 25); d <= new DateTime(2026, 10, 1); d = d.AddDays(1))
                Assert.Contains($">{d:yyyy-MM-dd}</a>", html);
            Assert.Contains("Full reports, 2026-09-25 to 2026-10-01", html);
            Assert.Contains("id=\"all-reports-text\"", html);
        }

        [SkippableFact]
        public async Task CopyText_IsThePlainTextReport()
        {
            var html = await (await AsAdminAsync(Page + "?Date=2026-10-01")).Content.ReadAsStringAsync();
            var m = Regex.Match(html, "<textarea id=\"report-text\"[^>]*>(.*?)</textarea>", RegexOptions.Singleline);
            Assert.True(m.Success);
            var text = WebUtility.HtmlDecode(m.Groups[1].Value);
            Assert.StartsWith("Date: 2026-10-01\nDaily Operations\nSowing: ", text);
            Assert.Contains("\nBookings: Seedling: ", text);
            Assert.Contains("\nSoftware Activity\nTotal Employees: ", text);
            Assert.EndsWith("Thank You!", text);
            Assert.Contains("data-copy-target=\"report-text\"", html);
        }

        [SkippableFact]
        public async Task TextExport_IsPlainText_WithTheSameReport()
        {
            var response = await AsAdminAsync(Page + "?handler=Text&Date=2026-10-01");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("text/plain", response.Content.Headers.ContentType!.MediaType);
            Assert.Contains("daily-operations-2026-10-01.txt", response.Content.Headers.ContentDisposition!.ToString());
            var text = await response.Content.ReadAsStringAsync();
            Assert.StartsWith("Date: 2026-10-01\nDaily Operations\n", text);
            Assert.EndsWith("Thank You!", text);
        }

        [SkippableFact]
        public async Task PrintLayout_HidesNavigationButtonsAndHistory()
        {
            var html = await (await AsAdminAsync(Page + "?Date=2026-10-01")).Content.ReadAsStringAsync();
            Assert.Contains("@media print", html);
            Assert.Contains(".sidebar, .top-header, footer, .d-print-none { display: none !important; }", html);
            Assert.Matches(new Regex("<div class=\"[^\"]*d-print-none[^\"]*\" id=\"date-nav\">"), html);      // date nav + Copy/Print/Download
            Assert.Matches(new Regex("id=\"date-nav\">(?:(?!id=\"report\").)*id=\"copy-report\"", RegexOptions.Singleline), html);
            Assert.Matches(new Regex("id=\"date-nav\">(?:(?!id=\"report\").)*id=\"print-report\"", RegexOptions.Singleline), html);
            Assert.Matches(new Regex("<div class=\"d-print-none\">\\s*<h4 class=\"mt-2\">Report history</h4>"), html);
            Assert.Contains("onclick=\"window.print()\"", html);
        }

        // ---- The Management Dashboard keeps working; the link follows the same rule ----

        [SkippableFact]
        public async Task Dashboard_ShowsTheLink_ToTheAdministratorOnly()
        {
            var admin = await AsAdminAsync("/ManagementDashboard/Index");
            Assert.Equal(HttpStatusCode.OK, admin.StatusCode);
            var adminHtml = await admin.Content.ReadAsStringAsync();
            var link = Regex.Match(adminHtml, "<a [^>]*id=\"daily-operations-report-link\"[^>]*>Daily Operations Report</a>");
            Assert.True(link.Success);
            Assert.Contains("href=\"/ManagementDashboard/DailyOperationsReport\"", link.Value);
            Assert.Contains("Management Dashboard", adminHtml);

            // Admin.ManageAreas alone may open the dashboard (unchanged) but must not see the report link.
            var areas = await GetAsync(_scratch, "/ManagementDashboard/Index", "Admin.ManageAreas");
            Assert.Equal(HttpStatusCode.OK, areas.StatusCode);
            Assert.DoesNotContain("daily-operations-report-link", await areas.Content.ReadAsStringAsync());
        }

        [SkippableTheory]
        [InlineData("/Data/WastedStock")]
        [InlineData("/ManagementDashboard/Index?Range=7d")]
        public async Task ExistingReports_StillLoad(string url)
            => Assert.Equal(HttpStatusCode.OK, (await AsAdminAsync(url)).StatusCode);
    }
}
