using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;
using PlantStockManager.Services;

namespace PlantStockManager.Tests
{
    // 2026-11-09: Daily Labour Attendance (today only for non-administrators) through the REAL HTTP pipeline, with the
    // header test scheme of DeleteAuthorizationTests ("X-Test-Permissions: A,B", "FULL" = System Administrator, no
    // header = anonymous). Without a database: who may reach the page at all, and that a forged POST (no anti-forgery
    // token) never reaches the handler. With PSM_SCRATCH_CONNECTION: the rendered date picker per role.
    // The date rule inside the handler is covered by DailyLabourAttendanceE2ETests.Dates_* (a page-model call bypasses
    // these filters, so the handler re-checks everything itself).
    public class LabourAttendanceDateAuthorizationTests : IClassFixture<DeleteAuthorizationTests.AppFactory>, IClassFixture<DailyOperationsReportAuthorizationTests.ScratchAppFactory>
    {
        private const string Page = "/Production/Labour/Attendance";
        private readonly DeleteAuthorizationTests.AppFactory _noDb;
        private readonly DailyOperationsReportAuthorizationTests.ScratchAppFactory _scratch;

        public LabourAttendanceDateAuthorizationTests(DeleteAuthorizationTests.AppFactory noDb, DailyOperationsReportAuthorizationTests.ScratchAppFactory scratch)
        {
            _noDb = noDb;
            _scratch = scratch;
        }

        private static async Task<HttpResponseMessage> SendAsync(WebApplicationFactory<Program> factory, HttpMethod method, string url, string? permissions,
            IEnumerable<KeyValuePair<string, string>>? form = null)
        {
            var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
            var request = new HttpRequestMessage(method, url);
            if (method == HttpMethod.Post)
                request.Content = new FormUrlEncodedContent(form ?? Array.Empty<KeyValuePair<string, string>>());
            if (permissions != null)
                request.Headers.Add(DeleteAuthorizationTests.HeaderAuthHandler.Header, permissions);
            return await client.SendAsync(request);
        }

        private static KeyValuePair<string, string>[] YesterdayForm() => new[]
        {
            new KeyValuePair<string, string>("LabourDate", AppClock.Today.AddDays(-1).ToString("yyyy-MM-dd")),
            new KeyValuePair<string, string>("AreaId", "1"),
            new KeyValuePair<string, string>("Male9", "1"), new KeyValuePair<string, string>("Female9", "1"),
        };

        // 8. unauthorized users stay blocked -- GET and POST
        [Theory]
        [InlineData("GET")]
        [InlineData("POST")]
        public async Task Anonymous_IsSentToLogin(string method)
        {
            var r = await SendAsync(_noDb, new HttpMethod(method), Page, null, YesterdayForm());
            Assert.Equal(HttpStatusCode.Redirect, r.StatusCode);
            Assert.Contains("/Account/Login", r.Headers.Location!.ToString());
        }

        [Theory]
        [InlineData("GET", "Sowing.View")]
        [InlineData("POST", "Sowing.View")]
        [InlineData("GET", "Labour.View")]
        [InlineData("POST", "Labour.View,Reports.View")]
        public async Task WithoutLabourEnterOrAttendance_GetsAccessDenied(string method, string permissions)
        {
            var r = await SendAsync(_noDb, new HttpMethod(method), Page, permissions, YesterdayForm());
            Assert.Equal(HttpStatusCode.Redirect, r.StatusCode);
            Assert.Contains("/Account/AccessDenied", r.Headers.Location!.ToString());
        }

        // 4. a hand-made POST (another date, no anti-forgery token) is refused before the handler runs -- for a recorder
        // and even for an administrator; nothing is written (no database is configured here).
        [Theory]
        [InlineData("Labour.Attendance")]
        [InlineData("Labour.Enter")]
        [InlineData("FULL")]
        public async Task ForgedPost_WithoutAntiforgeryToken_IsRefused(string permissions)
            => Assert.Equal(HttpStatusCode.BadRequest, (await SendAsync(_noDb, HttpMethod.Post, Page, permissions, YesterdayForm())).StatusCode);

        // A browser-supplied "admin" flag is not bound: only the login claims decide (see the bound-property guard).
        [Fact]
        public async Task BrowserSuppliedAdminFields_AreIgnored()
        {
            var form = YesterdayForm().Concat(new[]
            {
                new KeyValuePair<string, string>("CanUseAnyDate", "true"), new KeyValuePair<string, string>("IsFullAccess", "true"),
                new KeyValuePair<string, string>("Role", "System Administrator"),
            });
            Assert.Equal(HttpStatusCode.BadRequest, (await SendAsync(_noDb, HttpMethod.Post, Page, "Labour.Attendance", form)).StatusCode);
            var bound = typeof(PlantStockManager.Pages.Production.Labour.AttendanceModel).GetProperties()
                .Where(p => p.GetCustomAttributes(typeof(Microsoft.AspNetCore.Mvc.BindPropertyAttribute), true).Any())
                .Select(p => p.Name).ToList();
            Assert.DoesNotContain("CanUseAnyDate", bound);
            Assert.DoesNotContain("DateNotice", bound);
        }

        // The rendered page (scratch database): a recorder's date field is locked to India's today; an administrator's is not.
        [SkippableFact]
        public async Task RenderedDatePicker_RecorderTodayOnly_AdministratorAnyDate()
        {
            Skip.If(string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("PSM_SCRATCH_CONNECTION")), "Set PSM_SCRATCH_CONNECTION to a PlantsIMS2_Scratch_* database.");
            var today = AppClock.Today.ToString("yyyy-MM-dd");
            var yesterday = AppClock.Today.AddDays(-1).ToString("yyyy-MM-dd");

            var r = await SendAsync(_scratch, HttpMethod.Get, $"{Page}?labourDate={yesterday}", "Labour.Attendance");
            Assert.Equal(HttpStatusCode.OK, r.StatusCode);
            var html = await r.Content.ReadAsStringAsync();
            Assert.Contains($"value=\"{today}\" min=\"{today}\" max=\"{today}\"", html);
            Assert.Contains("readonly", html);
            Assert.Contains("id=\"attendanceDateNotice\"", html);
            Assert.DoesNotContain($"value=\"{yesterday}\"", html);

            r = await SendAsync(_scratch, HttpMethod.Get, $"{Page}?labourDate={yesterday}", "FULL");
            Assert.Equal(HttpStatusCode.OK, r.StatusCode);
            html = await r.Content.ReadAsStringAsync();
            Assert.Contains($"id=\"labourDate\" value=\"{yesterday}\" class=\"form-control\" required", html);
            Assert.DoesNotContain("id=\"todayOnlyHint\"", html);
            Assert.DoesNotContain("id=\"attendanceDateNotice\"", html);
        }
    }
}
