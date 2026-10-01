using System.Net;
using System.Net.Http.Json;
using System.Text;
using Microsoft.AspNetCore.Mvc.Testing;
using PlantStockManager.Endpoints;

namespace PlantStockManager.Tests
{
    // Daily Report / Sendvise webhook RECEIVER, over real HTTP against the
    // real application pipeline (same WebApplicationFactory<Program> pattern
    // as DeleteAuthorizationTests). No database is used: this endpoint never
    // touches one. Both routes must be reachable WITHOUT authentication
    // (AllowAnonymous -- see Endpoints/DailyReportWebhookEndpoints.cs)
    // despite Program.cs's global FallbackPolicy requiring an authenticated
    // user everywhere else.
    public class DailyReportWebhookEndpointTests : IClassFixture<WebApplicationFactory<Program>>
    {
        private readonly WebApplicationFactory<Program> _factory;

        public DailyReportWebhookEndpointTests(WebApplicationFactory<Program> factory) => _factory = factory;

        private HttpClient CreateClient()
            => _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        [Fact]
        public async Task Get_ReturnsOk_WithActiveMessage_NoAuthRequired()
        {
            var client = CreateClient();
            var response = await client.GetAsync(DailyReportWebhookEndpoints.RoutePrefix);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var body = await response.Content.ReadFromJsonAsync<WebhookResponse>();
            Assert.NotNull(body);
            Assert.True(body!.Success);
            Assert.Equal("Daily report webhook endpoint is active", body.Message);
        }

        [Fact]
        public async Task Post_WithValidJson_ReturnsOk_WithReceivedMessage_NoAuthRequired()
        {
            var client = CreateClient();
            var payload = JsonContent.Create(new { status = "sent", recipient = "+911234567890", apiKey = "should-never-leak" });

            var response = await client.PostAsync(DailyReportWebhookEndpoints.RoutePrefix, payload);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var body = await response.Content.ReadFromJsonAsync<WebhookResponse>();
            Assert.NotNull(body);
            Assert.True(body!.Success);
            Assert.Equal("Webhook received", body.Message);
        }

        [Fact]
        public async Task Post_WithEmptyBody_ReturnsOk_NeverA500()
        {
            var client = CreateClient();
            var response = await client.PostAsync(DailyReportWebhookEndpoints.RoutePrefix, content: null);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var body = await response.Content.ReadFromJsonAsync<WebhookResponse>();
            Assert.True(body!.Success);
        }

        [Fact]
        public async Task Post_WithInvalidJson_ReturnsOk_NeverA500()
        {
            var client = CreateClient();
            var content = new StringContent("{ this is not valid json ]]]", Encoding.UTF8, "application/json");

            var response = await client.PostAsync(DailyReportWebhookEndpoints.RoutePrefix, content);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var body = await response.Content.ReadFromJsonAsync<WebhookResponse>();
            Assert.True(body!.Success);
        }

        [Fact]
        public async Task Post_WithPlainTextGarbage_ReturnsOk_NeverA500()
        {
            var client = CreateClient();
            var content = new StringContent("not json at all, just some random text from a misbehaving sender", Encoding.UTF8, "text/plain");

            var response = await client.PostAsync(DailyReportWebhookEndpoints.RoutePrefix, content);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        // Diagnostic check for the REPORTED failure -- Sendvise's own log
        // shows it calling ".../precisionagritech.in//api/daily-report/webhook"
        // (a double slash right before "api"). Empirically reproduced here:
        // a URL starting "//api/..." is a "network-path reference" per RFC
        // 3986 -- when resolved by an HttpClient against a base address, the
        // segment right after "//" ("api") is parsed as a HOST, not a path
        // segment, silently dropping "/api" from the request entirely. The
        // request that actually reaches the server is for "/daily-report/webhook"
        // (no /api prefix) at a bogus host -- which matches no route, so the
        // global FallbackPolicy (RequireAuthenticatedUser) redirects it to
        // /Account/Login. This is almost certainly the real root cause of
        // Sendvise's reported 404s: whatever HTTP client/library Sendvise
        // uses is very likely doing the same base-URL + path merge, which
        // means the actual fix is on Sendvise's side -- remove the trailing
        // slash from the configured webhook base URL there, so the final
        // URL is exactly ".../precisionagritech.in/api/daily-report/webhook"
        // (single slash). This app-side fix (creating the route) is
        // necessary but may not be SUFFICIENT if Sendvise keeps sending the
        // double-slash URL.
        [Fact]
        public async Task Get_WithReportedDoubleSlashUrl_NeverReachesTheApiRoute_ConfirmingTheRealRootCause()
        {
            var client = CreateClient();
            var response = await client.GetAsync("/" + DailyReportWebhookEndpoints.RoutePrefix.TrimStart('/'));
            // sanity: the correct, single-slash URL works
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            var doubleSlashResponse = await client.GetAsync("//api/daily-report/webhook");
            // Never a 200: the "/api" prefix never reaches routing, so this
            // can only ever hit the authentication fallback (redirect to
            // Login) or, depending on the intervening infrastructure, some
            // other non-success outcome -- but never this endpoint's 200.
            Assert.NotEqual(HttpStatusCode.OK, doubleSlashResponse.StatusCode);
        }

        private sealed class WebhookResponse
        {
            public bool Success { get; set; }
            public string Message { get; set; } = string.Empty;
        }
    }
}
