using System.Security.Claims;
using Microsoft.Extensions.Options;
using PlantStockManager.Authorization;

namespace PlantStockManager.Tests
{
    // Authorization/SeedlingAreaScope.cs: the seedling pages' Area scope can be
    // switched off while Areas are not configured, WITHOUT changing the Area
    // scope of any other module (AreaAccessService).
    public class SeedlingAreaScopeTests
    {
        private sealed class Options : IOptionsMonitor<SeedlingWorkflowOptions>
        {
            public Options(bool enforce) => CurrentValue = new SeedlingWorkflowOptions { EnforceAreaScope = enforce };
            public SeedlingWorkflowOptions CurrentValue { get; }
            public SeedlingWorkflowOptions Get(string? name) => CurrentValue;
            public IDisposable? OnChange(Action<SeedlingWorkflowOptions, string?> listener) => null;
        }

        private static ClaimsPrincipal User(params Claim[] claims)
            => new(new ClaimsIdentity(claims, "Test"));

        private static readonly ClaimsPrincipal NoAreaUser = User(new Claim("UserId", "6"), new Claim(AreaAccessService.RoleNameClaimType, "Sowing Supervisor"));
        private static readonly ClaimsPrincipal Area2User = User(new Claim("UserId", "2"), new Claim(AreaAccessService.AreaAccessClaimType, "2"));
        private static readonly ClaimsPrincipal Administrator = User(new Claim("UserId", "1"), new Claim(ClaimsPrincipalSecurityExtensions.FullAccessClaimType, "true"));

        [Fact]
        public void DefaultOption_EnforcesAreaScope()
            => Assert.True(new SeedlingWorkflowOptions().EnforceAreaScope);

        [Fact]
        public void ScopeOff_UserWithoutArea_CanUseEveryArea()
        {
            var scope = new SeedlingAreaScope(new AreaAccessService(), new Options(enforce: false));
            Assert.True(scope.CanAccessArea(NoAreaUser, 2));
            Assert.True(scope.CanAccessArea(NoAreaUser, 1));
            Assert.True(scope.HasFullAreaAccess(NoAreaUser));
        }

        [Fact]
        public void ScopeOn_BehavesExactlyLikeAreaAccessService()
        {
            var area = new AreaAccessService();
            var scope = new SeedlingAreaScope(area, new Options(enforce: true));
            foreach (var user in new[] { NoAreaUser, Area2User, Administrator })
            {
                foreach (var areaId in new int?[] { 1, 2, null })
                    Assert.Equal(area.CanAccessArea(user, areaId), scope.CanAccessArea(user, areaId));
                Assert.Equal(area.HasFullAreaAccess(user), scope.HasFullAreaAccess(user));
            }
            Assert.False(scope.CanAccessArea(NoAreaUser, 2));
            Assert.True(scope.CanAccessArea(Area2User, 2));
            Assert.False(scope.CanAccessArea(Area2User, 1));
            Assert.True(scope.CanAccessArea(Administrator, 1));
        }

        [Fact]
        public void OtherModules_AreaAccessService_IsNotRelaxed()
        {
            // The switch only exists on SeedlingAreaScope; AreaAccessService
            // (Mother Plant, Cutting, Pot Production, Transfers, Outlet, ...)
            // still refuses a user without that Area.
            _ = new SeedlingAreaScope(new AreaAccessService(), new Options(enforce: false));
            Assert.False(new AreaAccessService().CanAccessArea(NoAreaUser, 2));
        }
    }
}
