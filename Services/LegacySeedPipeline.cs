using System.Net;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Options;

namespace PlantStockManager.Services
{
    // Phase B: the new pipeline (Main Office Seed Stock -> Direct Sowing ->
    // Supervisor Approval -> Ready Stock) is the operational source of truth.
    // The OLD seed pipeline pages stay readable (history is preserved), but no
    // NEW transaction may be posted through them unless an administrator
    // explicitly re-enables it for a transition period:
    //
    //   appsettings.json:  "LegacySeedPipeline": { "AllowLegacyWrites": true }
    //
    // This is a workflow switch, not an authorization rule: page access is
    // still decided only by FeatureAuthorizationConventions (Phase A), which
    // runs first. The switch applies to everyone, System Administrator
    // included, so the two pipelines cannot silently diverge.
    public sealed class LegacySeedPipelineOptions
    {
        public const string SectionName = "LegacySeedPipeline";

        // Default OFF: legacy writes are blocked.
        public bool AllowLegacyWrites { get; set; }

        // Phase C: fulfilling bookings from the OLD dbo.Inventory
        // (Pages/Bookings/FulfillBooking) stays ON during the transition, so the
        // seedlings already in legacy Inventory can still be delivered. Set it
        // to false after the historical cutover:
        //   "LegacySeedPipeline": { "AllowLegacyInventoryFulfilment": false }
        // No cutover date is assumed by the application.
        public bool AllowLegacyInventoryFulfilment { get; set; } = true;

        // Old-pipeline pages whose state-changing requests are blocked.
        // Pages that only advance EXISTING legacy batches (PlantPhase, temp,
        // GrowthTracker, SowingToInventory) stay open so work already in
        // progress can be finished.
        public static readonly IReadOnlySet<string> WriteBlockedPages =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "/Office/SeedBank",       // legacy Seed Bank entry  -> use Production/SeedStock
                "/SeedEntry/Sowing",      // legacy sowing entry     -> use Production/SeedSowing/Create
                "/SeedEntry/EditSowing",  // legacy sowing edit/delete
            };

        public static bool IsWriteBlockedPage(string? viewEnginePath)
            => viewEnginePath != null && WriteBlockedPages.Contains(viewEnginePath);
    }

    public sealed class LegacySeedPipelineWriteFilter : IAsyncPageFilter
    {
        public const string BlockedMessage =
            "The legacy seed pipeline is read-only. Record new seed in Production > Seed Stock and new sowings in Production > Direct Sowing.";

        private readonly IOptionsMonitor<LegacySeedPipelineOptions> _options;

        public LegacySeedPipelineWriteFilter(IOptionsMonitor<LegacySeedPipelineOptions> options) => _options = options;

        public Task OnPageHandlerSelectionAsync(PageHandlerSelectedContext context) => Task.CompletedTask;

        public async Task OnPageHandlerExecutionAsync(PageHandlerExecutingContext context, PageHandlerExecutionDelegate next)
        {
            var method = context.HttpContext.Request.Method;
            var isWrite = !(HttpMethods.IsGet(method) || HttpMethods.IsHead(method) || HttpMethods.IsOptions(method));

            if (isWrite
                && !_options.CurrentValue.AllowLegacyWrites
                && LegacySeedPipelineOptions.IsWriteBlockedPage(context.ActionDescriptor.ViewEnginePath))
            {
                context.Result = new ContentResult
                {
                    StatusCode = StatusCodes.Status409Conflict,
                    ContentType = "text/html; charset=utf-8",
                    Content = "<!DOCTYPE html><html><head><title>Legacy pipeline is read-only</title></head><body style=\"font-family:sans-serif;margin:2rem\">"
                              + "<h3>This page is read-only</h3><p>" + WebUtility.HtmlEncode(BlockedMessage) + "</p>"
                              + "<p><a href=\"/Production/SeedSowing/Create\">Go to Direct Sowing</a> &middot; "
                              + "<a href=\"/Production/SeedStock/Index\">Go to Seed Stock</a></p></body></html>"
                };
                return;
            }

            await next();
        }
    }
}
