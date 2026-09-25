using System.Security.Claims;
using Microsoft.Extensions.Options;

namespace PlantStockManager.Authorization
{
    // Seedling workflow switch, bound from the "SeedlingWorkflow" section:
    //   "SeedlingWorkflow": { "EnforceAreaScope": false }
    // Default TRUE (Area scope enforced). It is switched off in
    // appsettings.Development.json only while Polyhouses/Areas and user Area
    // assignments are not configured yet; switch it back on once they are.
    public sealed class SeedlingWorkflowOptions
    {
        public const string SectionName = "SeedlingWorkflow";

        public bool EnforceAreaScope { get; set; } = true;
    }

    // Area scope for the seedling workflow pages ONLY
    // (Seed Stock -> Direct Sowing -> Ready Alerts -> Supervisor Approval ->
    // Ready Stock). Same method names as AreaAccessService so those pages use
    // it the same way. When EnforceAreaScope is off, the page permissions
    // (FeatureAuthorizationConventions) alone decide access; when on, the
    // regular AreaAccessService rule applies unchanged.
    // AreaAccessService itself is NOT changed: every other module (Mother
    // Plant, Cutting, Pot Production, Transfers, Growing Partner, Outlet, ...)
    // keeps its Area scope exactly as before.
    public class SeedlingAreaScope
    {
        private readonly AreaAccessService _areaAccess;
        private readonly IOptionsMonitor<SeedlingWorkflowOptions> _options;

        public SeedlingAreaScope(AreaAccessService areaAccess, IOptionsMonitor<SeedlingWorkflowOptions> options)
        {
            _areaAccess = areaAccess;
            _options = options;
        }

        public bool IsEnforced => _options.CurrentValue.EnforceAreaScope;

        public bool HasFullAreaAccess(ClaimsPrincipal user)
            => !IsEnforced || _areaAccess.HasFullAreaAccess(user);

        public bool CanAccessArea(ClaimsPrincipal user, int? areaId)
            => !IsEnforced || _areaAccess.CanAccessArea(user, areaId);
    }
}
