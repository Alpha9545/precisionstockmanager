using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PlantStockManager.Services;

namespace PlantStockManager.Tests
{
    // Step 8A: verifies SendviseClient builds the exact approved request
    // shape (method, URL, header, JSON body) WITHOUT any real network call
    // -- a stub HttpMessageHandler intercepts the request in-process. No
    // automated test here ever calls the real Sendvise API (per the "avoid
    // sending a real WhatsApp message during normal automated unit tests"
    // requirement) -- the one real-send path is the separately-gated manual
    // test (DailyReportWhatsAppSenderManualTests.cs), never run by a plain
    // `dotnet test`.
    public class SendviseClientTests
    {
        private sealed class StubHandler : HttpMessageHandler
        {
            public HttpRequestMessage? CapturedRequest { get; private set; }
            public string? CapturedBody { get; private set; }
            public HttpStatusCode ResponseStatus { get; set; } = HttpStatusCode.OK;
            public string ResponseBody { get; set; } = """{"message_id":"msg_test_123","status":"queued"}""";
            public string ResponseContentType { get; set; } = "application/json";

            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                CapturedRequest = request;
                CapturedBody = request.Content == null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
                return new HttpResponseMessage(ResponseStatus)
                {
                    Content = new StringContent(ResponseBody, Encoding.UTF8, ResponseContentType)
                };
            }
        }

        private static (SendviseClient Client, StubHandler Handler) Build(string apiKey = "sv_test_fake_key_never_real")
        {
            var handler = new StubHandler();
            var httpClient = new HttpClient(handler) { BaseAddress = null };
            var options = Options.Create(new SendviseOptions
            {
                BaseUrl = "https://app.sendvise.com/api/v1/external",
                TemplateName = "daily_report_plantmanager",
                Language = "en",
                ApiKey = apiKey
            });
            var client = new SendviseClient(httpClient, options, NullLogger<SendviseClient>.Instance);
            return (client, handler);
        }

        private static List<string> SampleVariables() =>
            Enumerable.Range(1, DailyReportWhatsAppFormatter.ExpectedVariableCount).Select(i => $"value{i}").ToList();

        [Fact]
        public async Task SendTemplateAsync_PostsToTheExactApprovedEndpoint()
        {
            var (client, handler) = Build();
            await client.SendTemplateAsync("+911234567890", SampleVariables());

            Assert.NotNull(handler.CapturedRequest);
            Assert.Equal(HttpMethod.Post, handler.CapturedRequest!.Method);
            Assert.Equal("https://app.sendvise.com/api/v1/external/messages/send-template", handler.CapturedRequest.RequestUri!.ToString());
        }

        [Fact]
        public async Task SendTemplateAsync_SendsXApiKeyHeader_AndContentTypeJson()
        {
            var (client, handler) = Build(apiKey: "sv_test_abc123");
            await client.SendTemplateAsync("+911234567890", SampleVariables());

            Assert.True(handler.CapturedRequest!.Headers.TryGetValues("X-API-Key", out var values));
            Assert.Equal("sv_test_abc123", values!.Single());
            Assert.Equal("application/json", handler.CapturedRequest.Content!.Headers.ContentType!.MediaType);
        }

        [Fact]
        public async Task SendTemplateAsync_BodyMatchesApprovedShape_PositionalVariablesArray()
        {
            var (client, handler) = Build();
            var variables = SampleVariables();
            await client.SendTemplateAsync("+911234567890", variables);

            using var doc = JsonDocument.Parse(handler.CapturedBody!);
            var root = doc.RootElement;
            Assert.Equal("+911234567890", root.GetProperty("to").GetString());
            Assert.Equal("daily_report_plantmanager", root.GetProperty("template_name").GetString());
            Assert.Equal("en", root.GetProperty("language").GetString());

            var arr = root.GetProperty("variables");
            Assert.Equal(JsonValueKind.Array, arr.ValueKind);
            Assert.Equal(DailyReportWhatsAppFormatter.ExpectedVariableCount, arr.GetArrayLength());
            for (var i = 0; i < DailyReportWhatsAppFormatter.ExpectedVariableCount; i++)
                Assert.Equal($"value{i + 1}", arr[i].GetString()); // positional order preserved end to end
        }

        [Fact]
        public async Task SendTemplateAsync_MissingApiKey_NeverSendsAnHttpRequest_ReturnsFailure()
        {
            var (client, handler) = Build(apiKey: "");
            var result = await client.SendTemplateAsync("+911234567890", SampleVariables());

            Assert.False(result.Success);
            Assert.Null(handler.CapturedRequest); // no network call was made at all
            Assert.DoesNotContain("sv_", result.StatusOrError ?? "", StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public async Task SendTemplateAsync_WrongVariableCount_NeverSendsAnHttpRequest_ReturnsFailure()
        {
            var (client, handler) = Build();
            var result = await client.SendTemplateAsync("+911234567890", new List<string> { "only", "three", "values" });

            Assert.False(result.Success);
            Assert.Null(handler.CapturedRequest);
        }

        [Fact]
        public async Task SendTemplateAsync_SuccessResponse_ParsesMessageIdAndStatus()
        {
            var (client, handler) = Build();
            handler.ResponseBody = """{"message_id":"msg_abc","status":"sent"}""";
            var result = await client.SendTemplateAsync("+911234567890", SampleVariables());

            Assert.True(result.Success);
            Assert.Equal("msg_abc", result.MessageId);
            Assert.Equal("sent", result.StatusOrError);
            Assert.Equal(200, result.HttpStatusCode);
        }

        [Fact]
        public async Task SendTemplateAsync_ApiErrorResponse_ReturnsFailure_NeverThrows()
        {
            var (client, handler) = Build();
            handler.ResponseStatus = HttpStatusCode.BadRequest;
            handler.ResponseBody = """{"error":"invalid template"}""";
            var result = await client.SendTemplateAsync("+911234567890", SampleVariables());

            Assert.False(result.Success);
            Assert.Equal(400, result.HttpStatusCode);
        }

        [Fact]
        public async Task SendTemplateAsync_ResultNeverContainsTheApiKey_EvenOnFailure()
        {
            var (client, _) = Build(apiKey: "sv_live_should_never_leak_ABC123");
            var result = await client.SendTemplateAsync("", SampleVariables()); // empty recipient -> failure path

            var flattened = $"{result.StatusOrError} {result.MessageId}";
            Assert.DoesNotContain("sv_live_should_never_leak_ABC123", flattened);
        }

        // ---- Diagnostic change (422 investigation): safe response-body surfacing ----

        [Fact]
        public async Task SendTemplateAsync_422Response_StatusOrError_IncludesStatusContentTypeAndBody()
        {
            var (client, handler) = Build();
            handler.ResponseStatus = (HttpStatusCode)422;
            handler.ResponseBody = """{"error":"template variable count mismatch","expected":14,"received":17}""";
            handler.ResponseContentType = "application/json";

            var result = await client.SendTemplateAsync("+911234567890", SampleVariables());

            Assert.False(result.Success);
            Assert.Equal(422, result.HttpStatusCode);
            Assert.Contains("422", result.StatusOrError);
            Assert.Contains("application/json", result.StatusOrError);
            Assert.Contains("template variable count mismatch", result.StatusOrError);
            Assert.Contains("expected", result.StatusOrError);
        }

        [Fact]
        public async Task SendTemplateAsync_ErrorResponse_ApiKeyEchoedInBody_IsRedacted()
        {
            // Defense-in-depth: even if Sendvise's own response body somehow
            // echoed the configured key back (it shouldn't), the diagnostic
            // must never contain it.
            var (client, handler) = Build(apiKey: "sv_live_should_never_leak_XYZ789");
            handler.ResponseStatus = HttpStatusCode.Unauthorized;
            handler.ResponseBody = """{"error":"invalid api key sv_live_should_never_leak_XYZ789"}""";

            var result = await client.SendTemplateAsync("+911234567890", SampleVariables());

            Assert.DoesNotContain("sv_live_should_never_leak_XYZ789", result.StatusOrError);
            Assert.Contains("[REDACTED]", result.StatusOrError);
        }

        [Fact]
        public async Task SendTemplateAsync_ErrorResponse_GenericApiKeyLikeToken_IsRedacted_EvenIfNotTheConfiguredKey()
        {
            // A DIFFERENT sv_-shaped token than the one configured -- the
            // pattern-based redaction must still catch it.
            var (client, handler) = Build(apiKey: "sv_test_configured_key_abc");
            handler.ResponseStatus = HttpStatusCode.BadRequest;
            handler.ResponseBody = """{"error":"key sv_live_some_other_leaked_token_999 is not recognised"}""";

            var result = await client.SendTemplateAsync("+911234567890", SampleVariables());

            Assert.DoesNotContain("sv_live_some_other_leaked_token_999", result.StatusOrError);
        }

        [Fact]
        public async Task SendTemplateAsync_ErrorResponse_NonTextContentType_BodyIsOmitted()
        {
            var (client, handler) = Build();
            handler.ResponseStatus = HttpStatusCode.InternalServerError;
            handler.ResponseContentType = "application/octet-stream";
            handler.ResponseBody = "binary-looking-payload";

            var result = await client.SendTemplateAsync("+911234567890", SampleVariables());

            Assert.Contains("500", result.StatusOrError);
            Assert.DoesNotContain("binary-looking-payload", result.StatusOrError);
            Assert.Contains("omitted", result.StatusOrError);
        }

        [Fact]
        public async Task SendTemplateAsync_ErrorResponse_VeryLongBody_IsTruncated()
        {
            var (client, handler) = Build();
            handler.ResponseStatus = HttpStatusCode.BadRequest;
            handler.ResponseBody = new string('x', 5000);

            var result = await client.SendTemplateAsync("+911234567890", SampleVariables());

            Assert.Contains("truncated", result.StatusOrError);
            Assert.True(result.StatusOrError!.Length < 5000);
        }

        [Fact]
        public async Task SendTemplateAsync_SuccessResponse_NeverIncludesRawBodyInResult()
        {
            // A 2xx response's StatusOrError is the provider's own "status"
            // field only (e.g. "sent"/"queued") -- never the raw body, and
            // never routed through the diagnostic-building path at all.
            var (client, handler) = Build();
            handler.ResponseBody = """{"message_id":"msg_ok","status":"queued","extra_field":"should not leak either"}""";

            var result = await client.SendTemplateAsync("+911234567890", SampleVariables());

            Assert.True(result.Success);
            Assert.Equal("queued", result.StatusOrError);
            Assert.DoesNotContain("should not leak either", result.StatusOrError);
        }
    }
}
