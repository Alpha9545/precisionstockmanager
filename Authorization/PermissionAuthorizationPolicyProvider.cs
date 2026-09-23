using Microsoft.AspNetCore.Authorization;

namespace PlantStockManager.Authorization
{
    // Lets [Authorize(Policy = "MainOffice.Confirm")] (or any string that
    // matches a dbo.Permissions.Code) work WITHOUT pre-registering every
    // permission code by name in Program.cs. Permission codes are
    // admin-configurable data (Pages/Admin/Roles can add new ones over
    // time), so hard-coding a fixed list of AddPolicy(...) calls would
    // silently fall out of sync. Instead: any policy name not already
    // known to the default provider is treated as a permission code and
    // wrapped in a MinimumAuthorizationLevelRequirement on the fly.
    public class PermissionAuthorizationPolicyProvider : IAuthorizationPolicyProvider
    {
        private readonly DefaultAuthorizationPolicyProvider _fallbackProvider;

        public PermissionAuthorizationPolicyProvider(Microsoft.Extensions.Options.IOptions<AuthorizationOptions> options)
        {
            _fallbackProvider = new DefaultAuthorizationPolicyProvider(options);
        }

        public Task<AuthorizationPolicy> GetDefaultPolicyAsync() => _fallbackProvider.GetDefaultPolicyAsync();

        public Task<AuthorizationPolicy?> GetFallbackPolicyAsync() => _fallbackProvider.GetFallbackPolicyAsync();

        public async Task<AuthorizationPolicy?> GetPolicyAsync(string policyName)
        {
            // Explicitly registered policies (there are none yet, but this
            // keeps the door open) always win.
            var existing = await _fallbackProvider.GetPolicyAsync(policyName);
            if (existing != null)
            {
                return existing;
            }

            // Otherwise treat the policy name as a permission code.
            return new AuthorizationPolicyBuilder()
                .AddRequirements(new MinimumAuthorizationLevelRequirement(policyName))
                .Build();
        }
    }
}
