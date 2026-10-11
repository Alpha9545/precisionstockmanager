using System.IO;
using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Http;
using PlantStockManager.Authorization;
using PlantStockManager.Models;
using PlantStockManager.Services;
using Action = PlantStockManager.Services.MaintenanceRules.Action;

namespace PlantStockManager.Tests
{
    // Maintenance Tickets (2026-10-06) -- pure rules, authorization and file security: the status
    // machine, who may take each step, durations, overdue detection, photo type / size / path
    // checks, wiring. The database behaviour is MaintenanceE2ETests, against a scratch copy.
    public class MaintenanceRulesTests
    {
        private static string ReadSource(params string[] parts)
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "PlantStockManager.sln")))
                dir = dir.Parent;
            Assert.NotNull(dir);
            return File.ReadAllText(Path.Combine(new[] { dir!.FullName }.Concat(parts).ToArray()));
        }

        // ---- status machine ------------------------------------------------------------------

        [Theory]
        [InlineData(Action.Accept, "Open", "Accepted")]
        [InlineData(Action.Assign, "Open", "Accepted")]
        [InlineData(Action.Reassign, "Accepted", "Accepted")]
        [InlineData(Action.Arrive, "Accepted", "Arrived")]
        [InlineData(Action.ConfirmArrival, "Arrived", "ArrivalConfirmed")]
        [InlineData(Action.StartWork, "ArrivalConfirmed", "InProgress")]
        [InlineData(Action.Resolve, "InProgress", "Resolved")]
        [InlineData(Action.ConfirmCompletion, "Resolved", "Closed")]
        [InlineData(Action.RejectCompletion, "Resolved", "InProgress")]
        [InlineData(Action.Cancel, "Open", "Cancelled")]
        [InlineData(Action.Cancel, "Accepted", "Cancelled")]
        public void ApprovedTransitions(Action action, string from, string to)
        {
            Assert.True(MaintenanceRules.IsAllowed(action, from));
            Assert.Equal(to, MaintenanceRules.Transitions[action].To);
        }

        [Fact]
        public void NoArbitraryStatusChanges()
        {
            // Every action is allowed from exactly its listed statuses and no other.
            foreach (var action in Enum.GetValues<Action>())
                foreach (var status in MaintenanceRules.Statuses)
                    Assert.Equal(MaintenanceRules.Transitions[action].From.Contains(status), MaintenanceRules.IsAllowed(action, status));
            Assert.False(MaintenanceRules.IsAllowed(Action.Resolve, "Open"));             // cannot skip the visit
            Assert.False(MaintenanceRules.IsAllowed(Action.ConfirmCompletion, "InProgress")); // cannot close before resolved
            Assert.False(MaintenanceRules.IsAllowed(Action.Cancel, "Arrived"));            // no cancelling once the worker is there
            Assert.False(MaintenanceRules.IsAllowed(Action.Accept, "Closed"));
            Assert.False(MaintenanceRules.IsAllowed(Action.Accept, null));
            foreach (var final in new[] { "Closed", "Cancelled" })
                Assert.DoesNotContain(Enum.GetValues<Action>(), a => MaintenanceRules.IsAllowed(a, final));
        }

        // ---- durations -----------------------------------------------------------------------

        [Fact]
        public void Durations_ApprovedExample()
        {
            var day = new DateTime(2026, 10, 6, 0, 0, 0, DateTimeKind.Utc);
            var t = new MaintenanceTicket
            {
                RequestedAt = day.AddHours(9).AddMinutes(30),
                ArrivedAt = day.AddHours(10).AddMinutes(20),      // 10:20
                WorkStartedAt = day.AddHours(10).AddMinutes(30),  // 10:30
                WorkEndedAt = day.AddHours(12).AddMinutes(15),    // 12:15
                ClosedAt = day.AddHours(13),
            };
            Assert.Equal("1h 45m", MaintenanceRules.Format(t.WorkTime));      // approved: work 1h 45m
            Assert.Equal("50m", MaintenanceRules.Format(t.ResponseTime));     // request -> arrival
            Assert.Equal("1h 55m", MaintenanceRules.Format(t.OnSiteTime));    // arrival -> work end
            Assert.Equal("3h 30m", MaintenanceRules.Format(t.ResolutionTime));// request -> closed
            Assert.Equal("1.75", MaintenanceRules.Hours(t.WorkTime!.Value));
        }

        [Fact]
        public void Durations_MissingOrBackwardsTimestamps_AreBlank()
        {
            var now = DateTime.UtcNow;
            Assert.Null(MaintenanceRules.Work(null, now));
            Assert.Null(MaintenanceRules.Work(now, null));
            Assert.Null(MaintenanceRules.Work(now, now.AddMinutes(-1)));
            Assert.Equal("—", MaintenanceRules.Format(null));
            Assert.Equal("0m", MaintenanceRules.Format(TimeSpan.FromSeconds(40)));
            Assert.Equal("2d 3h", MaintenanceRules.Format(new TimeSpan(2, 3, 10, 0)));
        }

        // ---- overdue ---------------------------------------------------------------------------

        [Theory]
        [InlineData("Critical", 3.9, false)]
        [InlineData("Critical", 4.1, true)]
        [InlineData("Urgent", 23, false)]
        [InlineData("Urgent", 25, true)]
        [InlineData("Normal", 71, false)]
        [InlineData("Normal", 73, true)]
        public void Open_IsOverdue_WhenNotAcceptedInTime(string priority, double hoursAgo, bool overdue)
        {
            var now = new DateTime(2026, 10, 6, 12, 0, 0, DateTimeKind.Utc);
            Assert.Equal(overdue, MaintenanceRules.OverdueReason("Open", priority, now.AddHours(-hoursAgo), null, now, new DateTime(2026, 10, 6)) != null);
        }

        [Fact]
        public void Accepted_IsOverdue_WhenTheExpectedVisitDatePassed_InIndiaDates()
        {
            var now = new DateTime(2026, 10, 6, 12, 0, 0, DateTimeKind.Utc);
            var today = new DateTime(2026, 10, 6);
            Assert.Equal("Expected visit date passed", MaintenanceRules.OverdueReason("Accepted", "Normal", now.AddDays(-5), today.AddDays(-1), now, today));
            Assert.Null(MaintenanceRules.OverdueReason("Accepted", "Normal", now.AddDays(-5), today, now, today));
            // Once the worker has arrived, or the ticket is finished, it is never overdue.
            foreach (var status in new[] { "Arrived", "ArrivalConfirmed", "InProgress", "Resolved", "Closed", "Cancelled" })
                Assert.Null(MaintenanceRules.OverdueReason(status, "Critical", now.AddDays(-30), today.AddDays(-20), now, today));
        }

        // ---- validation --------------------------------------------------------------------------

        [Fact]
        public void ExpectedVisitDate_IsRequired_AndNotInThePast()
        {
            var today = new DateTime(2026, 10, 6);
            Assert.Equal(MaintenanceRules.ExpectedDateRequiredMessage, MaintenanceRules.ValidateExpectedVisitDate(null, today));
            Assert.Equal(MaintenanceRules.ExpectedDatePastMessage, MaintenanceRules.ValidateExpectedVisitDate(today.AddDays(-1), today));
            Assert.Null(MaintenanceRules.ValidateExpectedVisitDate(today, today));
            Assert.Null(MaintenanceRules.ValidateExpectedVisitDate(today.AddDays(3), today));
        }

        [Fact]
        public void TextLimits()
        {
            Assert.Equal("Problem is required.", MaintenanceRules.Required("  ", "Problem", 150));
            Assert.Equal("Problem cannot be longer than 150 characters.", MaintenanceRules.Required(new string('x', 151), "Problem", 150));
            Assert.Null(MaintenanceRules.Required(new string('x', 150), "Problem", 150));
            Assert.Null(MaintenanceRules.Optional(null, "Remarks", 500));
            Assert.NotNull(MaintenanceRules.Optional(new string('x', 501), "Remarks", 500));
        }

        // ---- photo security -----------------------------------------------------------------------

        private static readonly byte[] Png = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 0x0D, 0x49, 0x48, 0x44, 0x52 };
        private static readonly byte[] Jpeg = { 0xFF, 0xD8, 0xFF, 0xE0, 0, 0x10, 0x4A, 0x46, 0x49, 0x46, 0, 1 };
        private static readonly byte[] Webp = Encoding.ASCII.GetBytes("RIFF\x24\0\0\0WEBPVP8 ");

        [Fact]
        public void Detect_UsesTheFileContent_NotTheName()
        {
            Assert.Equal("image/png", MaintenancePhotoStore.Detect(Png)!.ContentType);
            Assert.Equal("image/jpeg", MaintenancePhotoStore.Detect(Jpeg)!.ContentType);
            Assert.Equal("image/webp", MaintenancePhotoStore.Detect(Webp)!.ContentType);
            Assert.Null(MaintenancePhotoStore.Detect(Encoding.ASCII.GetBytes("MZ\x90\0\x03\0\0\0\x04\0\0\0")));            // Windows executable
            Assert.Null(MaintenancePhotoStore.Detect(Encoding.ASCII.GetBytes("<svg xmlns='http://www.w3.org/2000/svg'>"))); // SVG (scriptable)
            Assert.Null(MaintenancePhotoStore.Detect(Encoding.ASCII.GetBytes("<html><script>")));
            Assert.Null(MaintenancePhotoStore.Detect(Encoding.ASCII.GetBytes("GIF89a......")));
            Assert.Null(MaintenancePhotoStore.Detect(Encoding.ASCII.GetBytes("%PDF-1.7 ....")));
            Assert.Null(MaintenancePhotoStore.Detect(Encoding.ASCII.GetBytes("RIFF\x24\0\0\0WAVEfmt ")));                 // RIFF but not WebP
            Assert.Null(MaintenancePhotoStore.Detect(new byte[] { 0xFF, 0xD8 }));
            Assert.Null(MaintenancePhotoStore.Detect(Array.Empty<byte>()));
        }

        private static IFormFile Upload(byte[] content, string name, long? length = null)
            => new FormFile(new MemoryStream(content), 0, length ?? content.Length, "Photos", name) { Headers = new HeaderDictionary(), ContentType = "image/jpeg" };

        [Fact]
        public async Task Validate_RejectsWrongContent_EmptyAndOverFiveMegabytes()
        {
            Assert.Equal("image/png", (await MaintenancePhotoStore.ValidateAsync(Upload(Png, "fan.png"))).Type!.ContentType);
            Assert.Equal("image/png", (await MaintenancePhotoStore.ValidateAsync(Upload(Png, "renamed.jpg"))).Type!.ContentType);   // real type wins
            var (type, error) = await MaintenancePhotoStore.ValidateAsync(Upload(Encoding.ASCII.GetBytes("MZ\x90\0virus"), "fan.jpg"));
            Assert.Null(type);
            Assert.Equal("fan.jpg: only JPEG, PNG or WebP photos are allowed.", error);
            Assert.EndsWith("the file is empty.", (await MaintenancePhotoStore.ValidateAsync(Upload(Array.Empty<byte>(), "e.png"))).Error);
            var big = new byte[MaintenancePhotoStore.MaxBytes + 1];
            Png.CopyTo(big, 0);
            Assert.EndsWith("a photo can be at most 5 MB.", (await MaintenancePhotoStore.ValidateAsync(Upload(big, "big.png"))).Error);
            var exact = new byte[MaintenancePhotoStore.MaxBytes];
            Png.CopyTo(exact, 0);
            Assert.Null((await MaintenancePhotoStore.ValidateAsync(Upload(exact, "max.png"))).Error);
        }

        [Theory]
        [InlineData("../../web.config", "web.config")]
        [InlineData("..\\..\\appsettings.json", "appsettings.json")]
        [InlineData("C:\\Users\\x\\fan.jpg", "fan.jpg")]
        [InlineData("/etc/passwd", "passwd")]
        [InlineData("", "photo")]
        [InlineData("a\u0000b\nc.png", "abc.png")]
        public void OriginalName_IsDisplayTextOnly_NoFolders(string uploaded, string shown)
            => Assert.Equal(shown, MaintenancePhotoStore.SafeOriginalName(uploaded));

        [Fact]
        public async Task Store_SavesUnderAGuidName_AndOnlyResolvesItsOwnNames()
        {
            var root = Path.Combine(Path.GetTempPath(), "psm-mt-" + Guid.NewGuid().ToString("N"));
            try
            {
                var store = new MaintenancePhotoStore(new MaintenanceStorageOptions { PhotoRoot = root });
                var file = Upload(Png, "../../evil.png");
                var saved = await store.SaveAsync(file, MaintenancePhotoStore.Detect(Png)!);
                Assert.Matches("^[0-9a-f]{32}\\.png$", saved.StoredFileName);
                Assert.Equal("evil.png", saved.OriginalFileName);
                var path = store.PathFor(saved.StoredFileName)!;
                Assert.StartsWith(Path.GetFullPath(root), path);
                Assert.Equal(Png, await System.IO.File.ReadAllBytesAsync(path));

                foreach (var bad in new[] { "../" + saved.StoredFileName, "..\\x.png", saved.StoredFileName.ToUpperInvariant(), "web.config",
                                            Path.Combine(root, saved.StoredFileName), "0123456789abcdef0123456789abcdef.exe", "0123456789abcdef0123456789abcde.png", null })
                    Assert.Null(store.PathFor(bad));

                store.DeleteQuietly(new[] { saved.StoredFileName, "../../should-not-matter.png" });
                Assert.False(System.IO.File.Exists(path));
            }
            finally
            {
                if (Directory.Exists(root)) Directory.Delete(root, true);
            }
        }

        [Fact]
        public void PhotoRoot_DefaultsOutsideTheApplicationFolder_AndIsConfigurable()
        {
            var defaultRoot = new MaintenanceStorageOptions().ResolvePhotoRoot();
            Assert.EndsWith(Path.Combine("PlantStockManager", "MaintenancePhotos"), defaultRoot);
            Assert.DoesNotContain("wwwroot", defaultRoot, StringComparison.OrdinalIgnoreCase);
            Assert.False(defaultRoot.StartsWith(AppContext.BaseDirectory, StringComparison.OrdinalIgnoreCase));
            Assert.Equal(Path.GetFullPath("D:/data/photos"), new MaintenanceStorageOptions { PhotoRoot = "D:/data/photos" }.ResolvePhotoRoot());
        }

        // ---- who may take each step ------------------------------------------------------------

        private static ClaimsPrincipal User(int id, string[] roles, string[] permissions, int[] areas, bool fullAccess = false)
        {
            var claims = new List<Claim> { new("UserId", id.ToString()) };
            claims.AddRange(roles.Select(r => new Claim(AreaAccessService.RoleNameClaimType, r)));
            claims.AddRange(permissions.Select(p => new Claim(MinimumAuthorizationLevelHandler.PermissionClaimType, p)));
            claims.AddRange(areas.Select(a => new Claim(AreaAccessService.AreaAccessClaimType, a.ToString())));
            if (fullAccess) claims.Add(new Claim(ClaimsPrincipalSecurityExtensions.FullAccessClaimType, "true"));
            return new ClaimsPrincipal(new ClaimsIdentity(claims, "test"));
        }

        private static readonly ClaimsPrincipal Electrician = User(50, new[] { "Electrician" }, new[] { "Maintenance.Work" }, Array.Empty<int>());
        private static readonly ClaimsPrincipal Plumber = User(51, new[] { "Plumber" }, new[] { "Maintenance.Work" }, Array.Empty<int>());
        private static readonly ClaimsPrincipal SowingSup = User(6, new[] { "Sowing Supervisor" }, new[] { "Maintenance.Raise", "Maintenance.View" }, new[] { 2 });
        private static readonly ClaimsPrincipal MpSup = User(110, new[] { "Mother Plant Supervisor" }, new[] { "Maintenance.Raise", "Maintenance.View" }, new[] { 127 });
        private static readonly ClaimsPrincipal Admin = User(21, new[] { "System Administrator" }, Array.Empty<string>(), Array.Empty<int>(), fullAccess: true);
        private static readonly ClaimsPrincipal BookingExec = User(15, new[] { "Booking Executive" }, new[] { "Booking.Enter" }, new[] { 2 });

        private static MaintenanceTicket Ticket(string status, int? worker = null, int requestedBy = 6)
            => new() { AreaId = 2, WorkerRoleName = "Electrician", Status = status, AssignedWorkerId = worker, RequestedById = requestedBy };

        [Fact]
        public void Accept_OnlyAWorkerOfTheTicketsTrade_NeverAnAdministrator()
        {
            var auth = new MaintenanceAuthorization(new AreaAccessService());
            Assert.True(auth.CanPerform(Electrician, Ticket("Open"), Action.Accept));
            Assert.False(auth.CanPerform(Plumber, Ticket("Open"), Action.Accept));
            Assert.False(auth.CanPerform(Admin, Ticket("Open"), Action.Accept));       // full access is not a trade
            Assert.False(auth.CanPerform(SowingSup, Ticket("Open"), Action.Accept));
            // A role claim without the Work permission is not enough, nor the permission without the role.
            Assert.False(auth.CanPerform(User(52, new[] { "Electrician" }, Array.Empty<string>(), Array.Empty<int>()), Ticket("Open"), Action.Accept));
            Assert.False(auth.CanPerform(User(53, Array.Empty<string>(), new[] { "Maintenance.Work" }, Array.Empty<int>()), Ticket("Open"), Action.Accept));
        }

        [Fact]
        public void WorkerSteps_OnlyTheAssignedWorker()
        {
            var auth = new MaintenanceAuthorization(new AreaAccessService());
            Assert.True(auth.CanPerform(Electrician, Ticket("Accepted", 50), Action.Arrive));
            Assert.False(auth.CanPerform(User(54, new[] { "Electrician" }, new[] { "Maintenance.Work" }, Array.Empty<int>()), Ticket("Accepted", 50), Action.Arrive));
            Assert.False(auth.CanPerform(Admin, Ticket("Accepted", 50), Action.Arrive));
            Assert.True(auth.CanPerform(Electrician, Ticket("ArrivalConfirmed", 50), Action.StartWork));
            Assert.True(auth.CanPerform(Electrician, Ticket("InProgress", 50), Action.Resolve));
            Assert.False(auth.CanPerform(SowingSup, Ticket("InProgress", 50), Action.Resolve));
        }

        [Fact]
        public void Confirmations_ASupervisorOfTheArea_NeverTheWorker()
        {
            var auth = new MaintenanceAuthorization(new AreaAccessService());
            Assert.True(auth.CanPerform(SowingSup, Ticket("Arrived", 50), Action.ConfirmArrival));
            Assert.False(auth.CanPerform(MpSup, Ticket("Arrived", 50), Action.ConfirmArrival));          // another Area
            Assert.False(auth.CanPerform(Electrician, Ticket("Arrived", 50), Action.ConfirmArrival));    // the worker himself
            Assert.True(auth.CanPerform(Admin, Ticket("Arrived", 50), Action.ConfirmArrival));
            Assert.False(auth.CanPerform(BookingExec, Ticket("Arrived", 50), Action.ConfirmArrival));    // no Maintenance permission
            // A supervisor who is ALSO the assigned worker may not confirm his own work.
            var both = User(6, new[] { "Sowing Supervisor", "Electrician" }, new[] { "Maintenance.Raise", "Maintenance.Work" }, new[] { 2 });
            Assert.False(auth.CanPerform(both, Ticket("Arrived", 6), Action.ConfirmArrival));
            Assert.False(auth.CanPerform(both, Ticket("Resolved", 6), Action.ConfirmCompletion));
            Assert.True(auth.CanPerform(SowingSup, Ticket("Resolved", 50), Action.ConfirmCompletion));
            Assert.True(auth.CanPerform(SowingSup, Ticket("Resolved", 50), Action.RejectCompletion));
            Assert.False(auth.CanPerform(MpSup, Ticket("Resolved", 50), Action.RejectCompletion));
        }

        [Fact]
        public void Cancel_RequesterOrAdministrator_OnlyWhileOpenOrAccepted()
        {
            var auth = new MaintenanceAuthorization(new AreaAccessService());
            Assert.True(auth.CanPerform(SowingSup, Ticket("Open", requestedBy: 6), Action.Cancel));
            Assert.True(auth.CanPerform(SowingSup, Ticket("Accepted", 50, requestedBy: 6), Action.Cancel));
            Assert.False(auth.CanPerform(SowingSup, Ticket("Arrived", 50, requestedBy: 6), Action.Cancel));
            var otherSupervisorSameArea = User(4, new[] { "Office Coordinator" }, new[] { "Maintenance.Raise" }, new[] { 2 });
            Assert.False(auth.CanPerform(otherSupervisorSameArea, Ticket("Open", requestedBy: 6), Action.Cancel));
            Assert.True(auth.CanPerform(Admin, Ticket("Accepted", 50), Action.Cancel));
            Assert.False(auth.CanPerform(Electrician, Ticket("Accepted", 50), Action.Cancel));
        }

        [Fact]
        public void AssignAndReassign_AdministratorOnly_BeforeArrival()
        {
            var auth = new MaintenanceAuthorization(new AreaAccessService());
            Assert.True(auth.CanPerform(Admin, Ticket("Open"), Action.Assign));
            Assert.True(auth.CanPerform(Admin, Ticket("Accepted", 50), Action.Reassign));
            Assert.False(auth.CanPerform(Admin, Ticket("Arrived", 50), Action.Reassign));
            Assert.False(auth.CanPerform(SowingSup, Ticket("Open"), Action.Assign));
            Assert.False(auth.CanPerform(Electrician, Ticket("Accepted", 50), Action.Reassign));
        }

        [Fact]
        public void Viewing_OwnAreas_AssignedWorker_OrOpenTicketsOfOwnTrade()
        {
            var auth = new MaintenanceAuthorization(new AreaAccessService());
            Assert.True(auth.CanView(SowingSup, Ticket("InProgress", 50)));
            Assert.False(auth.CanView(MpSup, Ticket("InProgress", 50)));
            Assert.True(auth.CanView(Admin, Ticket("Closed", 50)));
            Assert.True(auth.CanView(Electrician, Ticket("Open")));                       // can see it to accept it
            Assert.False(auth.CanView(Plumber, Ticket("Open")));                          // other trade
            Assert.True(auth.CanView(Electrician, Ticket("Closed", 50)));                 // own past work
            Assert.False(auth.CanView(User(54, new[] { "Electrician" }, new[] { "Maintenance.Work" }, Array.Empty<int>()), Ticket("InProgress", 50)));   // another electrician's ticket
            Assert.False(auth.CanView(BookingExec, Ticket("Open")));
        }

        // ---- wiring ------------------------------------------------------------------------------

        [Fact]
        public void Pages_AreMapped_AndInTheMenu()
        {
            Assert.Equal("Maintenance.Raise", FeatureAuthorizationConventions.GetRule("/Maintenance/Raise").Read);
            Assert.Equal("Maintenance.Work", FeatureAuthorizationConventions.GetRule("/Maintenance/Assigned").Read);
            Assert.Contains("Maintenance.Work", FeatureAuthorizationConventions.GetRule("/Maintenance/Details").Read);
            Assert.Contains("Maintenance.View", FeatureAuthorizationConventions.GetRule("/Maintenance/MyTickets").Read);
            Assert.Contains("Maintenance.Manage", FeatureAuthorizationConventions.GetRule("/Maintenance/Reports").Read);
            Assert.DoesNotContain("Maintenance.Work", FeatureAuthorizationConventions.GetRule("/Maintenance/Reports").Read);
            var layout = ReadSource("Pages", "Shared", "_Layout.cshtml");
            foreach (var item in new[] { ">Raise Ticket<", ">My Tickets<", ">Assigned Tickets<", ">Maintenance Reports<" })
                Assert.Contains(item, layout);
        }

        // Found by the browser test: a handler URL does not keep the page's ?id=, so a form without
        // asp-route-id posted to ticket 0 (404). Every action form must carry the ticket id.
        [Fact]
        public void DetailsPage_EveryActionFormPostsTheTicketId()
        {
            var page = ReadSource("Pages", "Maintenance", "Details.cshtml");
            var handlers = System.Text.RegularExpressions.Regex.Matches(page, "asp-page-handler=\"[A-Za-z]+\"( asp-route-id=\"@t.Id\")?");
            Assert.Equal(9, handlers.Count);
            Assert.All(handlers, m => Assert.True(m.Groups[1].Success, m.Value));
        }

        [Fact]
        public void Migration_IsGuarded_AndEnforcesTheWorkflowInTheDatabase()
        {
            var sql = ReadSource("Database", "Migrations", "2026-10-06_MaintenanceTickets.sql");
            Assert.Contains("NOT LIKE N'PlantsIMS2[_]Scratch[_]%'", sql);
            Assert.Contains("@@SERVERNAME <> N'$(TARGET_SERVER)'", sql);
            Assert.Contains("N'$(MODE)' NOT IN (N'DRYRUN', N'COMMIT')", sql);
            Assert.Contains("COLUMNPROPERTY(OBJECT_ID('dbo.Roles'), 'Id', 'IsIdentity')", sql);      // live-safe inserts
            Assert.Contains("ArrivalConfirmedById <> AssignedWorkerId", sql);                         // the worker never confirms himself
            Assert.Contains("CompletionConfirmedById <> AssignedWorkerId", sql);
            Assert.Contains("ExpectedVisitDate IS NOT NULL", sql);
            Assert.Contains("CK_MaintenanceTicketPhotos_Size CHECK (SizeBytes BETWEEN 1 AND 5242880)", sql);
            Assert.Contains("ContentType IN ('image/jpeg', 'image/png', 'image/webp')", sql);
            Assert.Contains("TR_MaintenanceTicketEvents_Protect", sql);
            foreach (var role in new[] { "Sowing Supervisor", "Mother Plant Supervisor", "Fertilizer Supervisor", "Office Coordinator" })
                Assert.Contains($"N'{role}'", sql);
            Assert.DoesNotContain("DROP TABLE", sql);
            var rollback = ReadSource("Database", "Migrations", "2026-10-06_MaintenanceTickets_Rollback.sql");
            Assert.Contains("THROW 51774", rollback);   // refuses while tickets exist
            Assert.Contains("THROW 51775", rollback);   // refuses while users hold a worker role
        }

        [Fact]
        public void MaintenanceTickets_BlockAreaDeletion()
            => Assert.Contains(DeletionRules.AreaDependencies, d => d.Table == "MaintenanceTickets" && d.Column == "AreaId");
    }
}
