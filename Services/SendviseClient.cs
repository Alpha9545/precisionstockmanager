using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace PlantStockManager.Services
{
    // Step 8A: the ONLY class in this codebase that talks to Sendvise over
    // HTTP. Deliberately separate from DailyReportService/
    // DailyReportWhatsAppFormatter (database/report aggregation and message
    // formatting stay completely independent of WhatsApp transport) and
    // from any Razor Page. Registered via AddHttpClient<SendviseClient>()
    // (Program.cs) -- standard DI-managed HttpClient, no manual disposal.
    //
    // Security: the API key is read ONLY from SendviseOptions.ApiKey (bound
    // from the "Sendvise" config section -- see SendviseOptions.cs for
    // where that value must live). Never logged, never included in any
    // exception message this class constructs, never echoed back in
    // SendviseSendResult. Request headers (which carry X-API-Key) are never
    // logged at all.
    //
    // Diagnostic change (manual-test 422 investigation): on a non-success
    // response, BuildSafeDiagnostic below DOES now surface the provider's
    // status, Content-Type and response body -- but only after Redact()
    // strips the exact configured ApiKey plus any sv_live_/sv_test_-shaped
    // token or api_key=-style fragment as defense-in-depth, only when the
    // Content-Type looks textual (never an arbitrary/binary body), and only
    // up to MaxDiagnosticBodyLength characters. A successful (2xx) response
    // still never has its body logged or returned at all -- only
    // message_id/status, exactly as before.
    public class SendviseClient
    {
        private readonly HttpClient _httpClient;
        private readonly SendviseOptions _options;
        private readonly ILogger<SendviseClient> _logger;

        public SendviseClient(HttpClient httpClient, IOptions<SendviseOptions> options, ILogger<SendviseClient> logger)
        {
            _httpClient = httpClient;
            _options = options.Value;
            _logger = logger;
        }

        public async Task<SendviseSendResult> SendTemplateAsync(string to, IReadOnlyList<string> variables, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(_options.ApiKey))
                return new SendviseSendResult(false, null, null, "Sendvise API key is not configured (Sendvise:ApiKey).");
            if (string.IsNullOrWhiteSpace(to))
                return new SendviseSendResult(false, null, null, "No recipient number was provided.");
            if (variables.Count != DailyReportWhatsAppFormatter.ExpectedVariableCount)
                return new SendviseSendResult(false, null, null,
                    $"Expected exactly {DailyReportWhatsAppFormatter.ExpectedVariableCount} template variables, got {variables.Count}.");

            var url = $"{_options.BaseUrl.TrimEnd('/')}/messages/send-template";
            var payload = new
            {
                to,
                template_name = _options.TemplateName,
                language = _options.Language,
                variables
            };

            using var request = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = JsonContent.Create(payload)
            };
            request.Headers.Add("X-API-Key", _options.ApiKey);

            try
            {
                using var response = await _httpClient.SendAsync(request, cancellationToken);
                var body = await response.Content.ReadAsStringAsync(cancellationToken);

                string? messageId = null;
                string? status = null;
                try
                {
                    using var doc = JsonDocument.Parse(body);
                    if (doc.RootElement.TryGetProperty("message_id", out var idEl))
                        messageId = idEl.GetString();
                    if (doc.RootElement.TryGetProperty("status", out var statusEl))
                        status = statusEl.GetString();
                }
                catch (JsonException)
                {
                    // Tolerate a non-JSON or differently-shaped response --
                    // the HTTP status code and Success flag are still valid.
                }

                if (!response.IsSuccessStatusCode)
                {
                    var diagnostic = BuildSafeDiagnostic(response, body);
                    _logger.LogWarning("Sendvise send-template failed: {Diagnostic}", diagnostic);
                    return new SendviseSendResult(false, messageId, (int)response.StatusCode, diagnostic);
                }

                _logger.LogInformation("Sendvise send-template succeeded: status={Status} messageId={MessageId}", status, messageId);
                return new SendviseSendResult(true, messageId, (int)response.StatusCode, status);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Sendvise send-template threw an exception.");
                return new SendviseSendResult(false, null, null, ex.Message);
            }
        }

        private const int MaxDiagnosticBodyLength = 1000;
        private static readonly Regex ApiKeyLikeToken = new(@"sv_(?:live|test)_[A-Za-z0-9_\-]+|(?<=api[_-]?key[""'\s:=]{0,5})[A-Za-z0-9_\-]{12,}",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        // Strips the exact configured ApiKey (if present) plus anything that
        // merely LOOKS like a Sendvise/generic API key token, so a provider
        // response can never leak a credential even if it unexpectedly
        // echoed one back.
        private string Redact(string text)
        {
            if (string.IsNullOrEmpty(text))
                return text;
            if (!string.IsNullOrEmpty(_options.ApiKey))
                text = text.Replace(_options.ApiKey, "[REDACTED]");
            return ApiKeyLikeToken.Replace(text, "[REDACTED]");
        }

        // HTTP status + Content-Type always included; the body only when its
        // Content-Type looks textual (json/text -- never an arbitrary/binary
        // body), always redacted, always capped in length.
        private string BuildSafeDiagnostic(HttpResponseMessage response, string body)
        {
            var status = (int)response.StatusCode;
            var contentType = response.Content.Headers.ContentType?.ToString() ?? "(none)";
            var mediaType = response.Content.Headers.ContentType?.MediaType;
            var looksTextual = mediaType == null || mediaType.Contains("json", StringComparison.OrdinalIgnoreCase)
                || mediaType.StartsWith("text/", StringComparison.OrdinalIgnoreCase);

            string bodyPart;
            if (!looksTextual)
                bodyPart = "(non-text response body omitted)";
            else if (string.IsNullOrWhiteSpace(body))
                bodyPart = "(empty response body)";
            else
            {
                var redacted = Redact(body);
                bodyPart = redacted.Length > MaxDiagnosticBodyLength
                    ? redacted.Substring(0, MaxDiagnosticBodyLength) + "... [truncated]"
                    : redacted;
            }

            return $"Sendvise returned HTTP {status}. Content-Type: {contentType}. Body: {bodyPart}";
        }
    }

    public sealed record SendviseSendResult(bool Success, string? MessageId, int? HttpStatusCode, string? StatusOrError);
}
