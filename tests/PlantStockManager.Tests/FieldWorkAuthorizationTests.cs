using System.Net;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using PlantStockManager.Authorization;

namespace PlantStockManager.Tests
{
    // Field Work Phase 1 (2026-11-03): page permissions over real HTTP through the real pipeline
    // (FeatureAuthorizationConventions + WritePermissionPageFilter). Only sign-in is replaced (DeleteAuthorizationTests'
    // header scheme: "X-Test-Permissions: A,B", FULL = full access), and the database is UNREACHABLE: every request here
    // is refused before any page code or database call runs.
    public class FieldWorkAuthorizationTests : IClassFixture<FieldWorkAuthorizationTests.FieldWorkAppFactory>
    {
        private readonly FieldWorkAppFactory _factory;
        public FieldWorkAuthorizationTests(FieldWorkAppFactory factory) => _factory = factory;

        public sealed class FieldWorkAppFactory : WebApplicationFactory<Program>
        {
            protected override void ConfigureWebHost(Microsoft.AspNetCore.Hosting.IWebHostBuilder builder)
            {
                builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["ConnectionStrings:DefaultConnection"] = "Server=tcp:127.0.0.1,1;Database=PlantsIMS2_Scratch_Unreachable;Connect Timeout=2;Encrypt=False",
                    ["DailyReportWhatsApp:Enabled"] = "false",
                    ["FieldWork:AutoCloseEnabled"] = "false",
                }));
                builder.ConfigureTestServices(services =>
                {
                    services.AddAuthentication().AddScheme<AuthenticationSchemeOptions, DeleteAuthorizationTests.HeaderAuthHandler>(
                        DeleteAuthorizationTests.HeaderAuthHandler.SchemeName, _ => { });
                    services.PostConfigure<AuthenticationOptions>(o => o.DefaultAuthenticateScheme = DeleteAuthorizationTests.HeaderAuthHandler.SchemeName);
                });
            }
        }

        private static readonly string[] EmployeePages =
            { "/FieldWork/Consent", "/FieldWork/StartDay", "/FieldWork/EndDay", "/FieldWork/Index" };

        private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string url, string? permissions)
        {
            var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
            var request = new HttpRequestMessage(method, url);
            if (method == HttpMethod.Post)
                request.Content = new FormUrlEncodedContent(Array.Empty<KeyValuePair<string, string>>());
            if (permissions != null)
                request.Headers.Add(DeleteAuthorizationTests.HeaderAuthHandler.Header, permissions);
            return await client.SendAsync(request);
        }

        private static void AssertRedirect(HttpResponseMessage r, string path)
        {
            Assert.Equal(HttpStatusCode.Redirect, r.StatusCode);
            Assert.Contains(path, r.Headers.Location!.ToString(), StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void EveryFieldWorkPage_IsMapped()
        {
            foreach (var page in EmployeePages.Concat(new[] { "/FieldWork/DayDetails", "/FieldWork/Manage/Days" }))
                Assert.True(FeatureAuthorizationConventions.IsMapped(page), page);
            Assert.Equal("FieldWork.Manage", FeatureAuthorizationConventions.GetRule("/FieldWork/Manage/Days").Read);
            Assert.Equal("FieldWork.Manage", FeatureAuthorizationConventions.GetRule("/FieldWork/DayDetails").Write);
        }

        [Fact]
        public async Task Anonymous_IsSentToLogin()
        {
            foreach (var page in EmployeePages.Concat(new[] { "/FieldWork/DayDetails?id=1", "/FieldWork/DayDetails?id=1&handler=Photo&photoId=1", "/FieldWork/Manage/Days" }))
                AssertRedirect(await SendAsync(HttpMethod.Get, page, null), "/Account/Login");
        }

        [Fact]
        public async Task UserWithoutFieldWorkPermission_IsDenied()
        {
            foreach (var page in EmployeePages.Concat(new[] { "/FieldWork/DayDetails?id=1", "/FieldWork/DayDetails?id=1&handler=Photo&photoId=1", "/FieldWork/Manage/Days" }))
                AssertRedirect(await SendAsync(HttpMethod.Get, page, "Dashboard.View,Booking.View"), "/Account/AccessDenied");
        }

        [Fact]
        public async Task FieldManagerOnly_CannotOpenTheEmployeePages()
        {
            foreach (var page in EmployeePages)
                AssertRedirect(await SendAsync(HttpMethod.Get, page, "FieldWork.Manage"), "/Account/AccessDenied");
        }

        [Fact]
        public async Task FieldEmployee_CannotOpenTheManagerList()
            => AssertRedirect(await SendAsync(HttpMethod.Get, "/FieldWork/Manage/Days", "FieldWork.Day"), "/Account/AccessDenied");

        [Fact]
        public async Task FieldEmployee_CannotPostReviewActions()
        {
            foreach (var handler in new[] { "Approve", "Flag", "Reopen" })
            {
                var r = await SendAsync(HttpMethod.Post, $"/FieldWork/DayDetails?id=1&handler={handler}", "FieldWork.Day");
                // refused either by the anti-forgery check (400) or by the page's Write rule (access denied) -- never run
                Assert.True(r.StatusCode == HttpStatusCode.BadRequest
                            || (r.StatusCode == HttpStatusCode.Redirect && r.Headers.Location!.ToString().Contains("/Account/AccessDenied")),
                    $"{handler}: {(int)r.StatusCode} {r.Headers.Location}");
            }
        }
    }
}
