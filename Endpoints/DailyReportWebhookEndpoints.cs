using PlantStockManager.Services;

namespace PlantStockManager.Endpoints
{
    // Daily Report / Sendvise webhook RECEIVER ONLY -- this step creates and
    // tests the endpoint, nothing more. No report is generated, no WhatsApp
    // message is sent, no business logic runs. Minimal API route handlers
    // (this app has no MVC Controllers pipeline anywhere -- AddControllers/
    // MapControllers are never called -- so a full Controller class would be
    // new architecture for a two-route stub; this is the smaller footprint).
    //
    // AllowAnonymous() is required and intentional: Program.cs's
    // AddAuthorization sets a global FallbackPolicy of
    // RequireAuthenticatedUser() for any endpoint with no explicit
    // authorization metadata. Sendvise calls this over plain HTTP with no
    // login cookie, so without AllowAnonymous() every request would be
    // redirected to /Account/Login (not a 200) -- this does not change or
    // weaken that fallback policy for any other endpoint, it only opts this
    // one new route out of it, the same way the existing Razor Pages
    // (/Account/Login, /Error, etc.) are already opted out via
    // FeatureAuthorizationConventions' "Anonymous" entries.
    public static class DailyReportWebhookEndpoints
    {
        public const string RoutePrefix = "/api/daily-report/webhook";

        // Caps how much of an unparseable (non-JSON) body is ever logged,
        // so a large/garbage POST can't blow up the log either.
        private const int MaxRawBodyLogLength = 500;

        public static IEndpointRouteBuilder MapDailyReportWebhookEndpoints(this IEndpointRouteBuilder app)
        {
            app.MapGet(RoutePrefix, () =>
                Results.Ok(new { success = true, message = "Daily report webhook endpoint is active" }))
                .AllowAnonymous();

            app.MapPost(RoutePrefix, HandlePostAsync)
                .AllowAnonymous();

            return app;
        }

        private static async Task<IResult> HandlePostAsync(HttpRequest request, ILoggerFactory loggerFactory)
        {
            var logger = loggerFactory.CreateLogger("DailyReportWebhook");

            // Never let anything in here -- an unexpected payload shape, an
            // encoding issue, whatever -- turn into a 500. A webhook
            // receiver that 500s just makes the sender (Sendvise) retry;
            // acknowledging receipt is always safe at this stub stage,
            // since nothing here has any side effect yet.
            try
            {
                string rawBody;
                using (var reader = new StreamReader(request.Body))
                    rawBody = await reader.ReadToEndAsync();

                if (string.IsNullOrWhiteSpace(rawBody))
                {
                    logger.LogInformation("Sendvise webhook received: empty body (Content-Type: {ContentType})", request.ContentType);
                }
                else
                {
                    var redacted = WebhookLogRedactor.RedactJson(rawBody);
                    if (redacted != null)
                    {
                        logger.LogInformation("Sendvise webhook received (redacted): {Payload}", redacted);
                    }
                    else
                    {
                        // Not valid JSON. Never dump raw, unvalidated body
                        // text that might itself carry a secret -- log only
                        // that something non-JSON arrived, capped in length.
                        var length = rawBody.Length;
                        var truncated = length > MaxRawBodyLogLength;
                        logger.LogWarning(
                            "Sendvise webhook received: body is not valid JSON (length: {Length}{Truncated})",
                            length, truncated ? ", truncated in this log line" : "");
                    }
                }
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Sendvise webhook: unexpected error while reading/logging the request body.");
            }

            return Results.Ok(new { success = true, message = "Webhook received" });
        }
    }
}
