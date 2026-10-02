using System.Net;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PlantStockManager.Authorization;

namespace PlantStockManager.Tests
{
    // Delete / Deactivate authorization, over real HTTP against the real
    // application pipeline (FeatureAuthorizationConventions + the permission
    // policy handler). Only the SIGN-IN is replaced by a test scheme that
    // stamps the permission claims a login would; challenge / forbid still use
    // the application's cookie scheme, so a denied request is redirected to
    // /Account/AccessDenied exactly as in production. No database is used:
    // every request here is either refused before any page code runs, or
    // (authorized, no anti-forgery token) stopped by anti-forgery before the
    // handler touches the database.
    public class DeleteAuthorizationTests : IClassFixture<DeleteAuthorizationTests.AppFactory>
    {
        private readonly AppFactory _factory;

        public DeleteAuthorizationTests(AppFactory factory) => _factory = factory;

        public sealed class AppFactory : WebApplicationFactory<Program>
        {
            protected override void ConfigureWebHost(Microsoft.AspNetCore.Hosting.IWebHostBuilder builder)
            {
                builder.ConfigureTestServices(services =>
                {
                    services.AddAuthentication().AddScheme<AuthenticationSchemeOptions, HeaderAuthHandler>(HeaderAuthHandler.SchemeName, _ => { });
                    services.PostConfigure<AuthenticationOptions>(o => o.DefaultAuthenticateScheme = HeaderAuthHandler.SchemeName);
                });
            }
        }

        // "X-Test-Permissions: A,B" signs the request in with those Permission
        // claims ("FULL" = full access). No header = anonymous.
        public sealed class HeaderAuthHandler : AuthenticationHandler<AuthenticationSchemeOptions>
        {
            public const string SchemeName = "Test";
            public const string Header = "X-Test-Permissions";

            public HeaderAuthHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
                : base(options, logger, encoder) { }

            protected override Task<AuthenticateResult> HandleAuthenticateAsync()
            {
                if (!Request.Headers.TryGetValue(Header, out var value))
                    return Task.FromResult(AuthenticateResult.NoResult());

                var claims = new List<Claim> { new(ClaimTypes.Name, "zztest-user"), new("UserId", "999999") };
                foreach (var p in value.ToString().Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    claims.Add(p == "FULL"
                        ? new Claim(ClaimsPrincipalSecurityExtensions.FullAccessClaimType, "true")
                        : new Claim(MinimumAuthorizationLevelHandler.PermissionClaimType, p));
                }
                var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, SchemeName));
                return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(principal, SchemeName)));
            }
        }

        private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string url, string? permissions)
        {
            var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
            var request = new HttpRequestMessage(method, url);
            if (method == HttpMethod.Post)
                request.Content = new FormUrlEncodedContent(Array.Empty<KeyValuePair<string, string>>());
            if (permissions != null)
                request.Headers.Add(HeaderAuthHandler.Header, permissions);
            return await client.SendAsync(request);
        }

        private static void AssertAccessDenied(HttpResponseMessage response)
        {
            Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
            Assert.Contains("/Account/AccessDenied", response.Headers.Location!.ToString());
        }

        private static void AssertLoginRequired(HttpResponseMessage response)
        {
            Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
            Assert.Contains("/Account/Login", response.Headers.Location!.ToString());
        }

        // Authorization passed; the POST was then refused only for its missing
        // anti-forgery token (so no handler, and no database, ran).
        private static void AssertAuthorizedButNeedsAntiforgery(HttpResponseMessage response)
            => Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        // ---- Area -----------------------------------------------------------

        [Theory]
        [InlineData("GET", "/Admin/Area?handler=ConfirmDelete&id=1")]
        [InlineData("POST", "/Admin/Area?handler=Delete&id=1")]
        [InlineData("POST", "/Admin/Area?handler=SetActive&id=1&isActive=false")]
        public async Task Area_Anonymous_IsSentToLogin(string method, string url)
            => AssertLoginRequired(await SendAsync(new HttpMethod(method), url, null));

        [Theory]
        [InlineData("GET", "/Admin/Area?handler=ConfirmDelete&id=1", "MotherPlant.Enter")]
        [InlineData("POST", "/Admin/Area?handler=Delete&id=1", "MotherPlant.Enter")]
        [InlineData("POST", "/Admin/Area?handler=Delete&id=1", "Admin.ManageUsers,Admin.ManageMasters")]
        [InlineData("POST", "/Admin/Area?handler=SetActive&id=1&isActive=false", "MotherPlant.Enter,Dashboard.View")]
        public async Task Area_WithoutAdminManageAreas_GetsAccessDenied(string method, string url, string permissions)
            => AssertAccessDenied(await SendAsync(new HttpMethod(method), url, permissions));

        [Theory]
        [InlineData("/Admin/Area?handler=Delete&id=1", "Admin.ManageAreas")]
        [InlineData("/Admin/Area?handler=SetActive&id=1&isActive=false", "Admin.ManageAreas")]
        [InlineData("/Admin/Area?handler=Delete&id=1", "FULL")]
        public async Task Area_WithAdminManageAreas_IsAuthorized(string url, string permissions)
            => AssertAuthorizedButNeedsAntiforgery(await SendAsync(HttpMethod.Post, url, permissions));

        // ---- Mother Plant ---------------------------------------------------

        [Theory]
        [InlineData("GET", "/Production/MotherPlant/Delete?id=1")]
        [InlineData("POST", "/Production/MotherPlant/Delete?handler=Delete&id=1")]
        [InlineData("POST", "/Production/MotherPlant/Delete?handler=Deactivate&id=1")]
        public async Task MotherPlant_Anonymous_IsSentToLogin(string method, string url)
            => AssertLoginRequired(await SendAsync(new HttpMethod(method), url, null));

        [Theory]
        [InlineData("GET", "/Production/MotherPlant/Delete?id=1", "MotherPlant.View")]
        [InlineData("POST", "/Production/MotherPlant/Delete?handler=Delete&id=1", "MotherPlant.View")]
        [InlineData("POST", "/Production/MotherPlant/Delete?handler=Deactivate&id=1", "MotherPlant.View")]
        [InlineData("POST", "/Production/MotherPlant/Delete?handler=Delete&id=1", "Admin.ManageAreas")]
        [InlineData("POST", "/Production/MotherPlant/Delete?handler=Deactivate&id=1", "Sowing.Enter,PotProduction.Enter")]
        public async Task MotherPlant_WithoutMotherPlantEnter_GetsAccessDenied(string method, string url, string permissions)
            => AssertAccessDenied(await SendAsync(new HttpMethod(method), url, permissions));

        [Theory]
        [InlineData("/Production/MotherPlant/Delete?handler=Delete&id=1", "MotherPlant.Enter")]
        [InlineData("/Production/MotherPlant/Delete?handler=Deactivate&id=1", "MotherPlant.Enter")]
        [InlineData("/Production/MotherPlant/Delete?handler=Delete&id=1", "FULL")]
        public async Task MotherPlant_WithMotherPlantEnter_IsAuthorized(string url, string permissions)
            => AssertAuthorizedButNeedsAntiforgery(await SendAsync(HttpMethod.Post, url, permissions));

        // ---- Outlet Sale (Issue 2: Mother Plant Supervisor can now send pots to customers) ----
        // Real HTTP, through the real app pipeline and the real FeatureAuthorizationConventions
        // policy -- no database involved (GET is refused by authorization alone; POST is stopped
        // by anti-forgery before any page handler or repository runs). The header permission sets
        // below are the role's ACTUAL dbo.RolePermissions grants: "old" = before this fix (what
        // Mother Plant Supervisor had when the user reported being unable to send pots to
        // customers), "new" = after it (the same set plus Outlet.Sell).

        private const string MotherPlantSupervisorOldPermissions =
            "Dashboard.View,MotherPlant.View,MotherPlant.Enter,InternalTransfer.View,InternalTransfer.Enter,PotProduction.View,ReadyStock.Confirm,ReadyStock.View";
        private const string MotherPlantSupervisorNewPermissions = MotherPlantSupervisorOldPermissions + ",Outlet.Sell";

        [Theory]
        [InlineData("GET", "/Production/OutletSale/Create")]
        [InlineData("POST", "/Production/OutletSale/Create")]
        public async Task OutletSale_Anonymous_IsSentToLogin(string method, string url)
            => AssertLoginRequired(await SendAsync(new HttpMethod(method), url, null));

        [Theory]
        [InlineData("GET", MotherPlantSupervisorOldPermissions)]                 // the exact pre-fix permission set -- reproduces the reported restriction
        [InlineData("GET", "MotherPlant.Enter,MotherPlant.View")]                // Mother Plant access alone is not enough
        [InlineData("GET", "Dashboard.View")]
        [InlineData("GET", "Fertilizer.Enter,Fertilizer.View")]                  // a genuinely unrelated role stays unauthorized
        public async Task OutletSaleCreate_WithoutOutletSell_GetsAccessDenied(string method, string permissions)
            => AssertAccessDenied(await SendAsync(new HttpMethod(method), "/Production/OutletSale/Create", permissions));

        [Theory]
        [InlineData(MotherPlantSupervisorNewPermissions)]  // the exact post-fix permission set
        [InlineData("Outlet.Sell")]                        // the permission alone is sufficient, nothing extra required
        [InlineData("FULL")]                               // Admin behavior is unchanged
        public async Task OutletSaleCreate_WithOutletSell_IsAuthorized(string permissions)
            => AssertAuthorizedButNeedsAntiforgery(await SendAsync(HttpMethod.Post, "/Production/OutletSale/Create", permissions));

        [Fact]
        public async Task OutletSaleCreate_GET_WithOutletSell_Succeeds()
        {
            // GET has no anti-forgery gate, so a real 200 OK proves full access,
            // not just "authorization passed."
            var response = await SendAsync(HttpMethod.Get, "/Production/OutletSale/Create", MotherPlantSupervisorNewPermissions);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        [Fact]
        public async Task MotherPlantSupervisorsNewPermission_DoesNotGrantAccessToUnrelatedAdminPages()
        {
            // No unrelated permission is accidentally usable elsewhere: the exact
            // post-fix permission set still cannot delete/deactivate an Area
            // (Admin.ManageAreas) -- the fix is scoped to Outlet.Sell only.
            AssertAccessDenied(await SendAsync(HttpMethod.Post, "/Admin/Area?handler=Delete&id=1", MotherPlantSupervisorNewPermissions));
        }

        // ---- Permission map -------------------------------------------------

        [Fact]
        public void PermissionMap_DeleteAndDeactivateRequireTheRightPermission()
        {
            Assert.Equal("Admin.ManageAreas", FeatureAuthorizationConventions.GetRule("/Admin/Area").Read);
            Assert.True(FeatureAuthorizationConventions.IsMapped("/Production/MotherPlant/Delete"));
            Assert.Equal("MotherPlant.Enter", FeatureAuthorizationConventions.GetRule("/Production/MotherPlant/Delete").Read);
        }

        [Fact]
        public void PermissionMap_OutletSaleCreate_RequiresOutletSell()
        {
            Assert.True(FeatureAuthorizationConventions.IsMapped("/Production/OutletSale/Create"));
            Assert.Equal("Outlet.Sell", FeatureAuthorizationConventions.GetRule("/Production/OutletSale/Create").Read);
        }
    }
}
