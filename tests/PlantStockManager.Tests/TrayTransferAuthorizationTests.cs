using System.Net;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.SqlClient;

namespace PlantStockManager.Tests
{
    // Tray Adjustment / Tray Transfers (2026-10-10): who may open the pages and use each action,
    // over real HTTP through the real pipeline. Refusals need no database; pages that open run
    // against the scratch database (never PlantsIMS2_Test) and are skipped without it.
    [Collection("ScratchDb")]
    public class TrayTransferAuthorizationTests : IClassFixture<DeleteAuthorizationTests.AppFactory>, IClassFixture<DailyOperationsReportAuthorizationTests.ScratchAppFactory>
    {
        // Permission sets of the roles after 2026-10-10_TrayTransfers.sql (only the parts that matter here).
        private const string SowingSupervisor = "Sowing.View,Sowing.Enter,ReadyStock.View,TrayStock.Enter,TrayStock.Adjust,TrayStock.Transfer,TrayStock.ReceiveTransferMainArea";
        private const string OfficeCoordinator = "Dashboard.View,ReadyStock.View,TrayStock.EnterMotherPlant,TrayStock.Transfer";
        private const string MotherPlantSupervisor = "MotherPlant.View,ReadyStock.View,Transplant.Enter,TrayStock.ReceiveTransfer,TrayStock.ViewMotherPlant";
        // intended set (2026-10-11_RolePermissionAlignment.sql): no Dispatch.Enter, no Dispatch.View
        private const string BookingExecutive = "Dashboard.View,Booking.View,Booking.Enter,ReadyStock.View,TrayStock.EnterMotherPlant";
        private const string DispatchExecutive = "Dispatch.View,ReadyStock.View";

        private readonly DeleteAuthorizationTests.AppFactory _noDb;
        private readonly DailyOperationsReportAuthorizationTests.ScratchAppFactory _scratch;

        public TrayTransferAuthorizationTests(DeleteAuthorizationTests.AppFactory noDb, DailyOperationsReportAuthorizationTests.ScratchAppFactory scratch)
        {
            _noDb = noDb;
            _scratch = scratch;
        }

        private static async Task<HttpResponseMessage> SendAsync(HttpClient client, HttpMethod method, string url, string? permissions,
            IEnumerable<KeyValuePair<string, string>>? form = null)
        {
            var request = new HttpRequestMessage(method, url);
            if (method == HttpMethod.Post) request.Content = new FormUrlEncodedContent(form ?? Array.Empty<KeyValuePair<string, string>>());
            if (permissions != null) request.Headers.Add(DeleteAuthorizationTests.HeaderAuthHandler.Header, permissions);
            return await client.SendAsync(request);
        }

        private static HttpClient Client(WebApplicationFactory<Program> f) => f.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        private static void AssertDenied(HttpResponseMessage r)
        {
            if (r.StatusCode == HttpStatusCode.Forbidden) return;
            Assert.Equal(HttpStatusCode.Redirect, r.StatusCode);
            Assert.Contains("/Account/AccessDenied", r.Headers.Location!.ToString());
        }

        [Theory]
        [InlineData("/Production/MaterialStock/TrayAdjust")]
        [InlineData("/Production/MaterialStock/TrayTransfers")]
        public async Task Anonymous_IsSentToLogin(string url)
        {
            var r = await SendAsync(Client(_noDb), HttpMethod.Get, url, null);
            Assert.Equal(HttpStatusCode.Redirect, r.StatusCode);
            Assert.Contains("/Account/Login", r.Headers.Location!.ToString());
        }

        [Theory]
        // adjustment: TrayStock.Adjust only (Sowing Supervisor; Admin through full access)
        [InlineData("GET", "/Production/MaterialStock/TrayAdjust", OfficeCoordinator)]
        [InlineData("GET", "/Production/MaterialStock/TrayAdjust", MotherPlantSupervisor)]
        [InlineData("GET", "/Production/MaterialStock/TrayAdjust", BookingExecutive)]
        [InlineData("POST", "/Production/MaterialStock/TrayAdjust", "TrayStock.Enter,Sowing.View")]   // adding trays is not adjusting
        // transfers: TrayStock.Transfer | TrayStock.ReceiveTransfer
        [InlineData("GET", "/Production/MaterialStock/TrayTransfers", BookingExecutive)]           // EnterMotherPlant does not send
        [InlineData("GET", "/Production/MaterialStock/TrayTransfers", DispatchExecutive)]
        [InlineData("POST", "/Production/MaterialStock/TrayTransfers?handler=Send", "TrayStock.Enter,TrayStock.Adjust")]
        // 2026-10-11: the Mother Plant Supervisor's tray VIEW never adds, adjusts, or manages material
        [InlineData("GET", "/Production/MaterialStock/Add", MotherPlantSupervisor)]
        [InlineData("POST", "/Production/MaterialStock/Add?handler=Tray", MotherPlantSupervisor)]
        [InlineData("POST", "/Production/MaterialStock/Add", MotherPlantSupervisor)]
        [InlineData("GET", "/Production/MaterialStock/TrayAdjust", MotherPlantSupervisor)]
        [InlineData("GET", "/Production/MaterialStock/Settings", MotherPlantSupervisor)]
        [InlineData("GET", "/Production/MaterialStock/Index", "TrayStock.ReceiveTransfer,MotherPlant.View")]   // receiving alone is not viewing
        // Booking Executive (intended set): no dispatch entry and no dispatch-only screens
        [InlineData("GET", "/Production/Dispatch/Create", BookingExecutive)]
        [InlineData("GET", "/Production/Dispatch/Edit/1", BookingExecutive)]
        [InlineData("GET", "/Production/Dispatch/Index", BookingExecutive)]
        [InlineData("GET", "/Bookings/SeedlingDispatch?id=1", BookingExecutive)]
        public async Task OtherRoles_AreRefused(string method, string url, string permissions)
        {
            AssertDenied(await SendAsync(Client(_noDb), new HttpMethod(method), url, permissions));
        }

        private HttpClient ScratchClient()
        {
            var cs = DailyOperationsReportE2ETests.ScratchConnectionOrSkip();
            using (var conn = new SqlConnection(cs))
            {
                conn.Open();
                using var cmd = new SqlCommand("SELECT OBJECT_ID(N'dbo.TrayTransfers', N'U')", conn);
                Skip.If(cmd.ExecuteScalar() is DBNull or null, "2026-10-10_TrayTransfers.sql is not applied to this scratch database.");
            }
            return Client(_scratch);
        }

        [SkippableTheory]
        [InlineData("/Production/MaterialStock/TrayAdjust", SowingSupervisor, "Tray Adjustment")]   // no Area assigned to the test user: the form says so
        [InlineData("/Production/MaterialStock/TrayAdjust", "FULL", "Save Adjustment")]
        [InlineData("/Production/MaterialStock/TrayTransfers", SowingSupervisor, "Send trays")]
        [InlineData("/Production/MaterialStock/TrayTransfers", OfficeCoordinator, "Send trays")]
        [InlineData("/Production/MaterialStock/TrayTransfers", MotherPlantSupervisor, "Tray Transfers")]
        [InlineData("/Production/MaterialStock/TrayTransfers", "FULL", "Send Trays")]
        // 2026-10-11: read-only tray balances and history for the Mother Plant Supervisor
        [InlineData("/Production/MaterialStock/Index?Material=TRAYS", MotherPlantSupervisor, "Material Stock")]
        [InlineData("/Production/MaterialStock/History?Material=TRAYS", MotherPlantSupervisor, "Material History")]
        // Booking Executive still sees dispatch STATUS through the booking pages (Booking.View)
        [InlineData("/Bookings/DispatchRegister", BookingExecutive, "Dispatch")]
        [InlineData("/Bookings/Fulfilment", BookingExecutive, "Fulfilment")]
        public async Task RightRoles_OpenTheirPages(string url, string permissions, string expect)
        {
            var r = await SendAsync(ScratchClient(), HttpMethod.Get, url, permissions);
            Assert.Equal(HttpStatusCode.OK, r.StatusCode);
            Assert.Contains(expect, await r.Content.ReadAsStringAsync());
        }

        [SkippableFact]
        public async Task Receiver_SeesNoSendForm_AndItsSendPostIsForbidden()
        {
            var client = ScratchClient();
            var page = await SendAsync(client, HttpMethod.Get, "/Production/MaterialStock/TrayTransfers", MotherPlantSupervisor);
            var html = await page.Content.ReadAsStringAsync();
            Assert.DoesNotContain("id=\"sendForm\"", html);
            var token = Regex.Match(html, "name=\"__RequestVerificationToken\" type=\"hidden\" value=\"([^\"]+)\"");
            Skip.IfNot(token.Success, "No antiforgery token on the page (no form rendered for this role).");
            var r = await SendAsync(client, HttpMethod.Post, "/Production/MaterialStock/TrayTransfers?handler=Send", MotherPlantSupervisor,
                new[] { new KeyValuePair<string, string>("__RequestVerificationToken", token.Groups[1].Value) });
            AssertDenied(r);
        }

        [SkippableFact]
        public async Task MotherPlantSupervisor_TrayView_OffersNoAddAdjustTransferOrMaterial()
        {
            var html = await (await SendAsync(ScratchClient(), HttpMethod.Get, "/Production/MaterialStock/Index?Material=TRAYS", MotherPlantSupervisor)).Content.ReadAsStringAsync();
            Assert.DoesNotContain("/Production/MaterialStock/Add\"", html);
            Assert.DoesNotContain("/Production/MaterialStock/TrayAdjust\"", html);
            Assert.DoesNotContain(">Cocopeat<", html);
            Assert.DoesNotContain(">Pitmos<", html);
        }

        [SkippableFact]
        public async Task OfficeCoordinator_SendsOnlyFromMotherPlantAreas()
        {
            var html = await (await SendAsync(ScratchClient(), HttpMethod.Get, "/Production/MaterialStock/TrayTransfers", OfficeCoordinator)).Content.ReadAsStringAsync();
            var select = Regex.Match(html, "<select[^>]*id=\"srcArea\"[^>]*>(.*?)</select>", RegexOptions.Singleline);
            Assert.True(select.Success, "source Area dropdown not rendered");
            Assert.Contains("Green Bless Nursery", select.Groups[1].Value);
            Assert.DoesNotContain("Main-Office-Areas", select.Groups[1].Value);
        }
    }
}
