using System.Net;
using System.Security.Claims;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.SqlClient;
using PlantStockManager.Authorization;
using PlantStockManager.Models;

namespace PlantStockManager.Tests
{
    // Ready Stock transfer to Mother Plant Areas + transplanting: who may do what.
    //   * the tray-area rule (TrayAreaScope) -- pure;
    //   * every new page over real HTTP through the real pipeline (FeatureAuthorizationConventions +
    //     permission policy), signed in with the permission sets of the actual roles. Refusals need no
    //     database; pages that do open run against the MIGRATED scratch database and are skipped
    //     without it (never against PlantsIMS2_Test).
    [Collection("ScratchDb")]
    public class TransplantAuthorizationTests : IClassFixture<DeleteAuthorizationTests.AppFactory>, IClassFixture<DailyOperationsReportAuthorizationTests.ScratchAppFactory>
    {
        // Permission sets of the roles after 2026-10-09_ReadyStockTransplanting.sql.
        private const string MotherPlantSupervisor = "Dashboard.View,InternalTransfer.Enter,InternalTransfer.View,MotherPlant.Enter,MotherPlant.View,ReadyStock.Confirm,ReadyStock.View,PotProduction.View,ReadyStock.ReceiveTransfer,Transplant.Enter,Transplant.View";
        private const string SowingSupervisor = "Sowing.View,Sowing.Enter,ReadyStock.View,ReadyStock.Confirm,TrayStock.Enter,ReadyStock.TransferToMotherPlant";
        private const string OfficeCoordinator = "Dashboard.View,ReadyStock.View,ReadyStock.TransferToMotherPlant,TrayStock.EnterMotherPlant";
        private const string BookingExecutive = "Booking.View,Booking.Direct,ReadyStock.View,TrayStock.EnterMotherPlant";
        private const string DispatchExecutive = "Dispatch.View,ReadyStock.View";

        private readonly DeleteAuthorizationTests.AppFactory _noDb;
        private readonly DailyOperationsReportAuthorizationTests.ScratchAppFactory _scratch;

        public TransplantAuthorizationTests(DeleteAuthorizationTests.AppFactory noDb, DailyOperationsReportAuthorizationTests.ScratchAppFactory scratch)
        {
            _noDb = noDb;
            _scratch = scratch;
        }

        // ---- TrayAreaScope (pure) ------------------------------------------------------------------

        private static ClaimsPrincipal User(params string[] permissions)
        {
            var claims = permissions.Select(p => p == "FULL"
                ? new Claim(ClaimsPrincipalSecurityExtensions.FullAccessClaimType, "true")
                : new Claim(MinimumAuthorizationLevelHandler.PermissionClaimType, p)).ToList();
            return new ClaimsPrincipal(new ClaimsIdentity(claims, "test"));
        }

        private static readonly Area Main = new() { Id = 2, Name = "Main-Office-Areas", AreaType = "MainOffice", IsActive = true };
        private static readonly Area Mp = new() { Id = 127, Name = "Green Bless Nursery", AreaType = "MotherPlant", IsActive = true };
        private static readonly Area InactiveMp = new() { Id = 999, Name = "Closed", AreaType = "MotherPlant", IsActive = false };
        private static readonly Area Outlet = new() { Id = 3, Name = "Outlet", AreaType = "Outlet", IsActive = true };

        [Fact]
        public void TrayArea_EnterMotherPlant_ReachesEveryActiveMotherPlantArea_AndNothingElse()
        {
            var u = User("TrayStock.EnterMotherPlant");
            bool OwnAreasOnlyMain(int id) => id == 2;     // an Office Coordinator / Booking Executive is assigned to the Main Office
            Assert.True(TrayAreaScope.IsAddableArea(u, Mp, OwnAreasOnlyMain));
            Assert.False(TrayAreaScope.IsAddableArea(u, Main, OwnAreasOnlyMain));      // no Main Office trays through this permission
            Assert.False(TrayAreaScope.IsAddableArea(u, InactiveMp, OwnAreasOnlyMain));
            Assert.False(TrayAreaScope.IsAddableArea(u, Outlet, _ => true));
        }

        [Fact]
        public void TrayArea_TrayStockEnter_KeepsTheOwnAreasRule_Unchanged()
        {
            var u = User("TrayStock.Enter");
            Assert.True(TrayAreaScope.IsAddableArea(u, Main, id => id == 2));
            Assert.False(TrayAreaScope.IsAddableArea(u, Mp, id => id == 2));           // a Sowing Supervisor gains no Mother Plant trays
            Assert.True(TrayAreaScope.IsAddableArea(u, Mp, id => id == 127));          // ... unless assigned there, as before
            Assert.False(TrayAreaScope.IsAddableArea(User("ReadyStock.View"), Mp, _ => true));
        }

        [Fact]
        public void TrayArea_TheNewPermission_NeverGrantsGrowingMaterial()
        {
            var u = User("TrayStock.EnterMotherPlant");
            Assert.True(u.CanAddTrays());
            Assert.False(u.CanAddMaterials());
            Assert.True(u.CanViewTrays());
        }

        // ---- HTTP: refused before any page code runs -------------------------------------------------

        private static async Task<HttpResponseMessage> SendAsync(WebApplicationFactory<Program> f, HttpMethod method, string url, string? permissions)
        {
            var client = f.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
            var request = new HttpRequestMessage(method, url);
            if (method == HttpMethod.Post) request.Content = new FormUrlEncodedContent(Array.Empty<KeyValuePair<string, string>>());
            if (permissions != null) request.Headers.Add(DeleteAuthorizationTests.HeaderAuthHandler.Header, permissions);
            return await client.SendAsync(request);
        }

        private static void AssertDenied(HttpResponseMessage r)
        {
            Assert.Equal(HttpStatusCode.Redirect, r.StatusCode);
            Assert.Contains("/Account/AccessDenied", r.Headers.Location!.ToString());
        }

        [Theory]
        [InlineData("/Production/MotherPlantTransfers/Index")]
        [InlineData("/Production/Transplanting/Index")]
        [InlineData("/Production/Transplanting/Create/1")]
        [InlineData("/Production/Transplanting/History")]
        [InlineData("/Production/Transplanting/Details/1")]
        [InlineData("/Production/ReadyStock/Lineage/1")]
        [InlineData("/Production/ReadyStock/TransferToMotherPlant/1")]
        public async Task Anonymous_IsSentToLogin(string url)
        {
            var r = await SendAsync(_noDb, HttpMethod.Get, url, null);
            Assert.Equal(HttpStatusCode.Redirect, r.StatusCode);
            Assert.Contains("/Account/Login", r.Headers.Location!.ToString());
        }

        [Theory]
        // sending: only ReadyStock.TransferToMotherPlant (Sowing Supervisor, Office Coordinator)
        [InlineData("GET", "/Production/ReadyStock/TransferToMotherPlant/1", MotherPlantSupervisor)]
        [InlineData("GET", "/Production/ReadyStock/TransferToMotherPlant/1", BookingExecutive)]
        [InlineData("POST", "/Production/ReadyStock/TransferToMotherPlant/1", DispatchExecutive)]
        // transfers screen: senders and receivers only
        [InlineData("GET", "/Production/MotherPlantTransfers/Index", DispatchExecutive)]
        [InlineData("GET", "/Production/MotherPlantTransfers/Index", BookingExecutive)]
        [InlineData("POST", "/Production/MotherPlantTransfers/Index?handler=Receive&id=1", "Transplant.Enter,Transplant.View")]
        // transplanting: Transplant.Enter (Mother Plant Supervisor)
        [InlineData("GET", "/Production/Transplanting/Index", SowingSupervisor)]
        [InlineData("GET", "/Production/Transplanting/Index", "Transplant.View,ReadyStock.View")]
        [InlineData("GET", "/Production/Transplanting/Create/1", OfficeCoordinator)]
        [InlineData("POST", "/Production/Transplanting/Create/1", "Transplant.View,MotherPlant.Enter")]
        // history / details: Transplant.View
        [InlineData("GET", "/Production/Transplanting/History", SowingSupervisor)]
        [InlineData("GET", "/Production/Transplanting/Details/1", DispatchExecutive)]
        [InlineData("POST", "/Production/Transplanting/Details/1?handler=Reverse", BookingExecutive)]
        // lineage: ReadyStock.View | Transplant.View
        [InlineData("GET", "/Production/ReadyStock/Lineage/1", "Sowing.Enter,Booking.Direct")]
        // tray adding: an unrelated role still cannot add trays
        [InlineData("GET", "/Production/MaterialStock/Add", DispatchExecutive)]
        public async Task OtherRoles_AreRefused(string method, string url, string permissions)
        {
            AssertDenied(await SendAsync(_noDb, new HttpMethod(method), url, permissions));
        }

        // ---- HTTP: the roles that should get in (scratch database, migrated) --------------------------

        private Task<HttpResponseMessage> AsAsync(string url, string permissions)
        {
            var cs = DailyOperationsReportE2ETests.ScratchConnectionOrSkip();
            using (var conn = new SqlConnection(cs))
            {
                conn.Open();
                using var cmd = new SqlCommand("SELECT COL_LENGTH('dbo.ReadyStock', 'InTransitQuantity')", conn);
                Skip.If(cmd.ExecuteScalar() is DBNull or null, "2026-10-09_ReadyStockTransplanting.sql is not applied to this scratch database.");
            }
            return SendAsync(_scratch, HttpMethod.Get, url, permissions);
        }

        [SkippableTheory]
        [InlineData("/Production/MotherPlantTransfers/Index", MotherPlantSupervisor, "Incoming Ready Stock")]
        [InlineData("/Production/MotherPlantTransfers/Index", SowingSupervisor, "Sent &mdash; still in transit")]
        [InlineData("/Production/MotherPlantTransfers/Index", OfficeCoordinator, "Mother Plant Transfers")]
        [InlineData("/Production/Transplanting/Index", MotherPlantSupervisor, "Transplanting")]
        [InlineData("/Production/Transplanting/History", MotherPlantSupervisor, "Transplanting History")]
        [InlineData("/Production/Transplanting/Index", "FULL", "Transplanting")]
        [InlineData("/Production/Transplanting/History", "FULL", "Transplanting History")]
        [InlineData("/Production/MotherPlantTransfers/Index", "FULL", "Incoming &mdash; waiting to be received")]
        [InlineData("/ManagementDashboard/Index", "FULL", "Mother Plant seedling flow")]
        public async Task RightRoles_OpenTheirPages(string url, string permissions, string expect)
        {
            var r = await AsAsync(url, permissions);
            Assert.Equal(HttpStatusCode.OK, r.StatusCode);
            Assert.Contains(expect, await r.Content.ReadAsStringAsync());
        }

        [SkippableFact]
        public async Task MotherPlantSupervisor_SeesNoSendOrReverseOptions_SenderSeesNoReceiveOptions()
        {
            var mp = await (await AsAsync("/Production/MotherPlantTransfers/Index", MotherPlantSupervisor)).Content.ReadAsStringAsync();
            Assert.DoesNotContain("Send from Ready Stock", mp);
            Assert.DoesNotContain("Sent &mdash; still in transit", mp);
            var sender = await (await AsAsync("/Production/MotherPlantTransfers/Index", SowingSupervisor)).Content.ReadAsStringAsync();
            Assert.DoesNotContain("Incoming &mdash; waiting to be received", sender);
            Assert.DoesNotContain("handler=Receive", sender);
        }

        [SkippableTheory]
        [InlineData(OfficeCoordinator)]
        [InlineData(BookingExecutive)]
        public async Task TrayAdd_EnterMotherPlant_OffersOnlyActiveMotherPlantAreas(string permissions)
        {
            var r = await AsAsync("/Production/MaterialStock/Add?type=TRAYS", permissions);
            Assert.Equal(HttpStatusCode.OK, r.StatusCode);
            var html = await r.Content.ReadAsStringAsync();
            var select = Regex.Match(html, "<select[^>]*id=\"trayAreaSelect\"[^>]*>(.*?)</select>", RegexOptions.Singleline);
            Assert.True(select.Success, "tray Area dropdown not rendered");
            Assert.Contains("Green Bless Nursery", select.Groups[1].Value);
            Assert.DoesNotContain("Main-Office-Areas", select.Groups[1].Value);
            Assert.DoesNotContain("Outlet", select.Groups[1].Value);
        }
    }
}
