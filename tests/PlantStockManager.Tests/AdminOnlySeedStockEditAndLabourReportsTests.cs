using System.Net;
using System.Reflection;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Testing;
using PlantStockManager.Authorization;
using PlantStockManager.Services;

namespace PlantStockManager.Tests
{
    // 2026-10-20 (no database):
    //   1. Seed Stock > Edit (correct a receipt's date / quantity / notes) is System Administrator only.
    //   2. Attendance History and Overtime History moved under Reports and are System Administrator only;
    //      recording (Daily Attendance / Overtime / an entry's details) keeps its permissions.
    // Server-side refusals go through the real pipeline (FeatureAuthorizationConventions + the permission handler,
    // header sign-in of DeleteAuthorizationTests): a denial is the app's redirect to /Account/AccessDenied.
    public class AdminOnlySeedStockEditAndLabourReportsTests : IClassFixture<DeleteAuthorizationTests.AppFactory>
    {
        private const string SeedEdit = "/Production/SeedStock/Edit";
        private const string AttendanceHistory = "/Production/Labour/AttendanceHistory";
        private const string OvertimeHistory = "/Production/Labour/OvertimeHistory";

        private readonly DeleteAuthorizationTests.AppFactory _factory;

        public AdminOnlySeedStockEditAndLabourReportsTests(DeleteAuthorizationTests.AppFactory factory) => _factory = factory;

        private static string RepoRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "PlantStockManager.sln")))
                dir = dir.Parent;
            return dir?.FullName ?? throw new InvalidOperationException("Repository root not found.");
        }

        private static string ReadSource(params string[] parts) => File.ReadAllText(Path.Combine(new[] { RepoRoot() }.Concat(parts).ToArray()));

        // Every non-administrator permission set that could plausibly reach these pages.
        public static readonly string[][] NonAdministrators =
        {
            new[] { "SeedStock.View", "SeedStock.Enter" },
            new[] { "Labour.View", "Labour.Enter" },
            new[] { "Reports.View", "Dashboard.View" },
            new[] { "Admin.ManageUsers", "Admin.ManageRoles", "Admin.ManageMasters", "Admin.ManageAreas" },
            new[] { "SeedStock.View", "SeedStock.Enter", "Labour.View", "Labour.Enter", "Reports.View", "Admin.ManageAreas", "Admin.ManageMasters" },
        };

        // ---- the page map -----------------------------------------------------------------------------

        [Theory]
        [InlineData(SeedEdit)]
        [InlineData(AttendanceHistory)]
        [InlineData(OvertimeHistory)]
        public void Page_IsMapped_ToFullAccessOnly(string page)
        {
            Assert.True(FeatureAuthorizationConventions.IsMapped(page));
            Assert.Same(FeatureAuthorizationConventions.FullAccessOnlyRule, FeatureAuthorizationConventions.GetRule(page));
            Assert.Equal(PermissionPolicy.FullAccessOnly, FeatureAuthorizationConventions.GetRule(page).Read);
        }

        [Fact]
        public void ExistingSeedStockAndRecordingPages_KeepTheirPermissions()
        {
            Assert.Equal("SeedStock.View", FeatureAuthorizationConventions.GetRule("/Production/SeedStock/Index").Read);
            Assert.Equal("SeedStock.View", FeatureAuthorizationConventions.GetRule("/Production/SeedStock/Details").Read);
            Assert.Equal("SeedStock.Enter", FeatureAuthorizationConventions.GetRule("/Production/SeedStock/Create").Read);
            Assert.Equal("SeedStock.Enter", FeatureAuthorizationConventions.GetRule("/Production/SeedStock/AddStock").Read);
            Assert.Equal("SeedStock.Enter", FeatureAuthorizationConventions.GetRule("/Production/SeedStock/AddSeedStock").Read);
            Assert.Equal("Labour.Enter", FeatureAuthorizationConventions.GetRule("/Production/Labour/Attendance").Read);
            Assert.Equal("Labour.Enter", FeatureAuthorizationConventions.GetRule("/Production/Labour/Overtime").Read);
            Assert.Equal("Labour.View", FeatureAuthorizationConventions.GetRule("/Production/Labour/OvertimeDetails").Read);   // saving overtime lands here
            Assert.Equal("Labour.View", FeatureAuthorizationConventions.GetRule("/Production/Labour/Index").Read);
            Assert.Equal("Labour.Enter", FeatureAuthorizationConventions.GetRule("/Production/Labour/Entry").Read);
        }

        [Fact]
        public async Task FeatureAccess_OnlyTheAdministrator_MayOpenTheThreePages()
        {
            var access = HomePageTests.Access();
            var admin = HomePageTests.User(Array.Empty<string>(), fullAccess: true);
            foreach (var page in new[] { SeedEdit, AttendanceHistory, OvertimeHistory })
            {
                Assert.True(await access.CanAccessPageAsync(admin, page), page);
                foreach (var set in NonAdministrators)
                    Assert.False(await access.CanAccessPageAsync(HomePageTests.User(set), page), $"{page} / {string.Join(",", set)}");
                Assert.False(await access.CanAccessPageAsync(HomePageTests.User(Array.Empty<string>(), authenticated: false), page));
            }

            // the recording pages stay open to their recorders
            var recorder = HomePageTests.User(new[] { "Labour.View", "Labour.Enter" });
            Assert.True(await access.CanAccessPageAsync(recorder, "/Production/Labour/Attendance"));
            Assert.True(await access.CanAccessPageAsync(recorder, "/Production/Labour/Overtime"));
            Assert.True(await access.CanAccessPageAsync(recorder, "/Production/Labour/OvertimeDetails"));
        }

        // ---- server-side refusals over HTTP (no database: refused before any page code runs) ---------

        public static IEnumerable<object[]> RefusedRequests()
        {
            var urls = new[]
            {
                SeedEdit + "/1", SeedEdit + "/1?receiptId=1",
                AttendanceHistory, AttendanceHistory + "?fromDate=2026-10-01&toDate=2026-10-20&areaId=2",
                OvertimeHistory, OvertimeHistory + "?fromDate=2026-10-01&toDate=2026-10-20&groupBy=Area",
            };
            foreach (var url in urls)
                foreach (var set in NonAdministrators)
                    yield return new object[] { url, string.Join(",", set) };
        }

        [Theory]
        [MemberData(nameof(RefusedRequests))]
        public async Task NonAdministrator_Get_IsAccessDenied(string url, string permissions)
            => AssertAccessDenied(await SendAsync(HttpMethod.Get, url, permissions));

        [Theory]
        [InlineData(SeedEdit + "/1", "SeedStock.View,SeedStock.Enter")]
        [InlineData(SeedEdit + "/1", "Admin.ManageMasters,Admin.ManageAreas,Reports.View")]
        [InlineData(AttendanceHistory, "Labour.View,Labour.Enter")]
        [InlineData(OvertimeHistory, "Labour.View,Labour.Enter")]
        public async Task NonAdministrator_CraftedPost_IsAccessDenied(string url, string permissions)
        {
            var form = new Dictionary<string, string> { ["ReceiptId"] = "1", ["Quantity"] = "5000", ["ReceivedOn"] = "2026-10-01T10:00", ["Reason"] = "crafted" };
            AssertAccessDenied(await SendAsync(HttpMethod.Post, url, permissions, form));
        }

        [Theory]
        [InlineData(SeedEdit + "/1")]
        [InlineData(AttendanceHistory)]
        [InlineData(OvertimeHistory)]
        public async Task Anonymous_IsSentToLogin(string url)
        {
            var response = await SendAsync(HttpMethod.Get, url, null);
            Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
            Assert.Contains("/Account/Login", response.Headers.Location!.ToString());
        }

        private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string url, string? permissions, Dictionary<string, string>? form = null)
        {
            var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
            var request = new HttpRequestMessage(method, url);
            if (method == HttpMethod.Post)
                request.Content = new FormUrlEncodedContent(form ?? new Dictionary<string, string>());
            if (permissions != null)
                request.Headers.Add(DeleteAuthorizationTests.HeaderAuthHandler.Header, permissions);
            return await client.SendAsync(request);
        }

        private static void AssertAccessDenied(HttpResponseMessage response)
        {
            Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
            Assert.Contains("/Account/AccessDenied", response.Headers.Location!.ToString());
        }

        // ---- navigation ---------------------------------------------------------------------------------

        private static string MenuSection(string layout, string name)
        {
            var start = layout.IndexOf($"<span class=\"link_name\">{name}</span>", StringComparison.Ordinal);
            Assert.True(start > 0, $"menu section {name} not found");
            var end = layout.IndexOf("<!-- ====", start, StringComparison.Ordinal);
            return end > start ? layout[start..end] : layout[start..];
        }

        [Fact]
        public void Menu_BothReportsAreUnderReports_AndNoLongerUnderLabour()
        {
            var layout = ReadSource("Pages", "Shared", "_Layout.cshtml");
            var reports = MenuSection(layout, "Reports");
            var labour = MenuSection(layout, "Labour");

            Assert.Contains("<li nav-page=\"/Production/Labour/AttendanceHistory\"><a asp-page=\"/Production/Labour/AttendanceHistory\">Attendance History</a></li>", reports);
            Assert.Contains("<li nav-page=\"/Production/Labour/OvertimeHistory\"><a asp-page=\"/Production/Labour/OvertimeHistory\">Overtime History</a></li>", reports);
            Assert.DoesNotContain("AttendanceHistory", labour);
            Assert.DoesNotContain("OvertimeHistory", labour);
            // exactly once in the whole menu (no duplicate entries)
            Assert.Single(Regex.Matches(layout, Regex.Escape("nav-page=\"/Production/Labour/AttendanceHistory\"")));
            Assert.Single(Regex.Matches(layout, Regex.Escape("nav-page=\"/Production/Labour/OvertimeHistory\"")));
            // recording entries stay in Labour
            Assert.Contains("nav-page=\"/Production/Labour/Attendance\"", labour);
            Assert.Contains("nav-page=\"/Production/Labour/Overtime\"", labour);
        }

        [Fact]
        public async Task HomeQuickAccess_ListsTheReportsUnderReports_ShownOnlyToTheAdministrator()
        {
            var reports = Assert.Single(HomeQuickAccess.Modules, m => m.Key == "reports");
            Assert.Contains(reports.Links, l => l.Page == AttendanceHistory);
            Assert.Contains(reports.Links, l => l.Page == OvertimeHistory);
            var labour = Assert.Single(HomeQuickAccess.Modules, m => m.Key == "labour");
            Assert.DoesNotContain(labour.Links, l => l.Page == AttendanceHistory || l.Page == OvertimeHistory);

            var access = HomePageTests.Access();
            var admin = await HomeQuickAccess.VisibleAsync(p => access.CanAccessPageAsync(HomePageTests.User(Array.Empty<string>(), fullAccess: true), p));
            Assert.Contains(admin.Single(m => m.Key == "reports").Links, l => l.Page == AttendanceHistory);
            Assert.Contains(admin.Single(m => m.Key == "reports").Links, l => l.Page == OvertimeHistory);

            foreach (var set in NonAdministrators)
            {
                var visible = await HomeQuickAccess.VisibleAsync(p => access.CanAccessPageAsync(HomePageTests.User(set), p));
                Assert.DoesNotContain(visible.SelectMany(m => m.Links), l => l.Page == AttendanceHistory || l.Page == OvertimeHistory);
            }
            // a Labour recorder still gets the recording links
            var recorder = await HomeQuickAccess.VisibleAsync(p => access.CanAccessPageAsync(HomePageTests.User(new[] { "Labour.View", "Labour.Enter" }), p));
            Assert.Equal(new[] { "/Production/Labour/Attendance", "/Production/Labour/Overtime", LabourOvertimeRules.MyEntriesPage },
                recorder.Single(m => m.Key == "labour").Links.Select(l => l.Page));
        }

        // Every in-page link to the two reports is rendered only for a user who may open them.
        [Theory]
        [InlineData("Pages/Production/Labour/Attendance.cshtml", AttendanceHistory)]
        [InlineData("Pages/Production/Labour/Index.cshtml", AttendanceHistory)]
        [InlineData("Pages/Production/Labour/Overtime.cshtml", OvertimeHistory)]
        [InlineData("Pages/Production/Labour/OvertimeDetails.cshtml", OvertimeHistory)]
        [InlineData("Pages/ManagementDashboard/Index.cshtml", AttendanceHistory)]
        [InlineData("Pages/ManagementDashboard/Index.cshtml", OvertimeHistory)]
        public void InPageLinks_ToTheReports_AreGuarded(string file, string page)
        {
            var source = ReadSource(file.Split('/'));
            var links = Regex.Matches(source, $"<a [^>]*asp-page=\"{Regex.Escape(page)}\"");
            Assert.NotEmpty(links);
            foreach (Match link in links)
            {
                var before = source[..link.Index];
                var guard = before.LastIndexOf("@if (canSee", StringComparison.Ordinal);
                Assert.True(guard >= 0 && before.LastIndexOf('}') < guard, $"{file}: the link at {link.Index} to {page} is not inside an @if (canSee...) block");
            }
            Assert.Contains($"CanAccessPageAsync(User, \"{page}\")", source);
        }

        [Fact]
        public void DuplicateOvertimeSubmission_ReturnsToTheRecordersOwnList_NotTheAdministratorReport()
        {
            var source = ReadSource("Pages", "Production", "Labour", "Overtime.cshtml.cs");
            Assert.DoesNotContain("RedirectToPage(\"/Production/Labour/OvertimeHistory\"", source);
            Assert.Contains("RedirectToPage(LabourOvertimeRules.MyEntriesPage)", source);
            Assert.Contains("TempData[\"Error\"]", ReadSource("Pages", "Production", "Labour", "MyOvertime.cshtml"));
            Assert.DoesNotContain("Overtime History", LabourOvertimeRules.AlreadySubmittedMessage);
        }

        // ---- My Overtime Entries (a recorder's own entries) -----------------------------------------

        [Fact]
        public async Task MyOvertime_IsForRecorders_AndInTheLabourMenu_NotUnderReports()
        {
            Assert.Equal("Labour.Enter", FeatureAuthorizationConventions.GetRule(LabourOvertimeRules.MyEntriesPage).Read);
            Assert.Null(FeatureAuthorizationConventions.GetRule(LabourOvertimeRules.MyEntriesPage).Write);
            var access = HomePageTests.Access();
            Assert.True(await access.CanAccessPageAsync(HomePageTests.User(new[] { "Labour.Enter" }), LabourOvertimeRules.MyEntriesPage));
            Assert.True(await access.CanAccessPageAsync(HomePageTests.User(Array.Empty<string>(), fullAccess: true), LabourOvertimeRules.MyEntriesPage));
            Assert.False(await access.CanAccessPageAsync(HomePageTests.User(new[] { "Labour.View", "Reports.View" }), LabourOvertimeRules.MyEntriesPage));

            var layout = ReadSource("Pages", "Shared", "_Layout.cshtml");
            Assert.Contains("<li nav-page=\"/Production/Labour/MyOvertime\"><a asp-page=\"/Production/Labour/MyOvertime\">My Overtime Entries</a></li>", MenuSection(layout, "Labour"));
            Assert.DoesNotContain("MyOvertime", MenuSection(layout, "Reports"));
        }

        // The only values a request can set are the two dates -- never a user, Area or Polyhouse.
        [Fact]
        public void MyOvertime_BindsOnlyTheDateRange()
        {
            var bound = typeof(PlantStockManager.Pages.Production.Labour.MyOvertimeModel)
                .GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(p => p.GetCustomAttribute<BindPropertyAttribute>() != null)
                .Select(p => p.Name).OrderBy(x => x, StringComparer.Ordinal).ToList();
            Assert.Equal(new[] { "FromDate", "ToDate" }, bound);
            var view = ReadSource("Pages", "Production", "Labour", "MyOvertime.cshtml");
            Assert.DoesNotContain("Worker-Hours", view);   // no totals / report data
            Assert.DoesNotContain("areaId", view);
        }

        [Theory]
        [InlineData(false, 7, 7, true)]       // own entry
        [InlineData(false, 7, 8, false)]      // someone else's
        [InlineData(false, 7, null, false)]   // no recorder on the entry
        [InlineData(false, null, 7, false)]   // no user id on the login
        [InlineData(true, 1, 8, true)]        // administrator: every entry, as before
        [InlineData(true, null, null, true)]
        public void MayAccessEntry_IsOwnershipOrFullAccess(bool fullAccess, int? userId, int? createdById, bool expected)
            => Assert.Equal(expected, LabourOvertimeRules.MayAccessEntry(fullAccess, userId, createdById));

        [Theory]
        [InlineData("Pages/Production/Labour/OvertimeDetails.cshtml.cs")]
        [InlineData("Pages/Production/Labour/Overtime.cshtml.cs")]
        public void DetailsAndEdit_CheckOwnership_OnTheServer(string file)
            => Assert.Contains("LabourOvertimeRules.MayAccessEntry(User.IsFullAccess(), User.GetUserId(),", ReadSource(file.Split('/')));

        [Theory]
        [InlineData("Labour.View")]
        [InlineData("Reports.View,Dashboard.View")]
        [InlineData("Admin.ManageAreas,Admin.ManageUsers")]
        public async Task MyOvertime_WithoutLabourEnter_IsAccessDenied(string permissions)
            => AssertAccessDenied(await SendAsync(HttpMethod.Get, LabourOvertimeRules.MyEntriesPage + "?createdById=1&userId=1&areaId=2", permissions));

        [Fact]
        public async Task MyOvertime_Anonymous_IsSentToLogin()
        {
            var response = await SendAsync(HttpMethod.Get, LabourOvertimeRules.MyEntriesPage, null);
            Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
            Assert.Contains("/Account/Login", response.Headers.Location!.ToString());
        }

        // ---- Seed Stock edit: UI and rules -----------------------------------------------------------

        [Theory]
        [InlineData("Index.cshtml")]
        [InlineData("Details.cshtml")]
        public void SeedStockEditButton_IsShownOnlyWhenTheUserMayOpenTheEditPage(string file)
        {
            var source = ReadSource("Pages", "Production", "SeedStock", file);
            Assert.Contains("var canEdit = await FeatureAccess.CanAccessPageAsync(User, PlantStockManager.Services.SeedStockEditRules.EditPage)", source);
            var link = source.IndexOf("asp-page=\"/Production/SeedStock/Edit\"", StringComparison.Ordinal);
            Assert.True(link > 0);
            var before = source[..link];
            Assert.True(before.LastIndexOf("@if (canEdit)", StringComparison.Ordinal) > before.LastIndexOf('}'));
        }

        [Fact]
        public void EditPage_BindsOnlyTheReceiptFields()
        {
            var bound = typeof(PlantStockManager.Pages.Production.SeedStock.EditModel)
                .GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(p => p.GetCustomAttribute<BindPropertyAttribute>() != null)
                .Select(p => p.Name).OrderBy(x => x, StringComparer.Ordinal).ToList();
            Assert.Equal(new[] { "Notes", "Quantity", "Reason", "ReceiptId", "ReceivedOn" }, bound);   // never the lot, Area, variety or balance
            Assert.Equal("/Production/SeedStock/Edit", SeedStockEditRules.EditPage);
        }

        private static readonly DateTime Today = new(2026, 10, 20);

        [Fact]
        public void Validate_AcceptsAValidCorrection()
            => Assert.Empty(SeedStockEditRules.Validate(1200m, Today.AddDays(-3).AddHours(10), "recount", "wrong count", Today));

        [Fact]
        public void Validate_AcceptsToday()
            => Assert.Empty(SeedStockEditRules.Validate(1m, Today.AddHours(23).AddMinutes(59), null, "date", Today));

        [Theory]
        [InlineData(null, "Quantity is required.")]
        [InlineData("0", "Quantity must be greater than zero.")]
        [InlineData("-5", "Quantity must be greater than zero.")]
        [InlineData("10.5", "Quantity must be a whole number.")]
        public void Validate_RejectsInvalidQuantities(string? quantity, string message)
        {
            var errors = SeedStockEditRules.Validate(quantity == null ? null : decimal.Parse(quantity, System.Globalization.CultureInfo.InvariantCulture),
                Today.AddDays(-1), null, "reason", Today);
            var e = Assert.Single(errors);
            Assert.Equal(("Quantity", message), (e.Field, e.Message));
        }

        [Fact]
        public void Validate_RejectsInvalidDates()
        {
            Assert.Equal("Received On is required.", Assert.Single(SeedStockEditRules.Validate(10m, null, null, "r", Today)).Message);
            Assert.Equal("Received On cannot be in the future.", Assert.Single(SeedStockEditRules.Validate(10m, Today.AddDays(1), null, "r", Today)).Message);
            Assert.Equal("Received On cannot be in the future.", Assert.Single(SeedStockEditRules.Validate(10m, new DateTime(2099, 1, 1), null, "r", Today)).Message);
            var tooOld = Assert.Single(SeedStockEditRules.Validate(10m, new DateTime(1999, 12, 31), null, "r", Today));
            Assert.Equal("ReceivedOn", tooOld.Field);
            Assert.Equal("ReceivedOn", Assert.Single(SeedStockEditRules.Validate(10m, DateTime.MinValue, null, "r", Today)).Field);
        }

        [Fact]
        public void Validate_RequiresAReason_AndBoundsTheText()
        {
            Assert.Equal("Reason", Assert.Single(SeedStockEditRules.Validate(10m, Today, null, null, Today)).Field);
            Assert.Equal("Reason", Assert.Single(SeedStockEditRules.Validate(10m, Today, null, "   ", Today)).Field);
            Assert.Equal("Reason", Assert.Single(SeedStockEditRules.Validate(10m, Today, null, new string('r', 201), Today)).Field);
            Assert.Equal("Notes", Assert.Single(SeedStockEditRules.Validate(10m, Today, new string('n', 501), "r", Today)).Field);
        }

        [Fact]
        public void IsUnchanged_ComparesToTheMinute_AndIgnoresSurroundingSpaces()
        {
            var at = new DateTime(2026, 10, 1, 10, 30, 42);
            Assert.True(SeedStockEditRules.IsUnchanged(100m, at, "a", 100m, new DateTime(2026, 10, 1, 10, 30, 0), " a "));
            Assert.True(SeedStockEditRules.IsUnchanged(100m, at, null, 100m, at, ""));
            Assert.False(SeedStockEditRules.IsUnchanged(100m, at, "a", 101m, at, "a"));
            Assert.False(SeedStockEditRules.IsUnchanged(100m, at, "a", 100m, at.AddMinutes(1), "a"));
            Assert.False(SeedStockEditRules.IsUnchanged(100m, at, "a", 100m, at, "b"));
        }

        [Fact]
        public void AuditRemarks_NameTheOldAndNewValuesAndTheReason_WithinTheColumn()
        {
            DateTime oldAt = new(2026, 10, 1, 9, 0, 0), newAt = new(2026, 9, 30, 8, 15, 0);
            var text = SeedStockEditRules.AuditRemarks(42, 1000m, oldAt, 1200m, newAt, " recount ");
            Assert.Equal($"Admin edit of receipt #42: {QuantityFormat.Qty(1000m)} on {oldAt:dd/MM/yyyy HH:mm} -> {QuantityFormat.Qty(1200m)} on {newAt:dd/MM/yyyy HH:mm}. Reason: recount", text);
            Assert.Equal(SeedStockEditRules.NotesMaxLength,
                SeedStockEditRules.AuditRemarks(1, 1m, Today, 2m, Today, new string('x', 2000)).Length);
        }
    }
}
