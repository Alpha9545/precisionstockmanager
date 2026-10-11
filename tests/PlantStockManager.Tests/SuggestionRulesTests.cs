using System.IO;
using System.Reflection;
using System.Security.Claims;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using PlantStockManager.Authorization;
using PlantStockManager.Models;
using PlantStockManager.Services;

namespace PlantStockManager.Tests
{
    // Suggestions & Problems -- rules, photo handling, authorization matrix and wiring
    // (no database; the end-to-end tests are SuggestionE2ETests).
    public class SuggestionRulesTests
    {
        private static string ReadSource(params string[] parts)
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "PlantStockManager.sln")))
                dir = dir.Parent;
            Assert.NotNull(dir);
            return File.ReadAllText(Path.Combine(new[] { dir!.FullName }.Concat(parts).ToArray()));
        }

        private static readonly DateTime Created = new(2026, 10, 7, 5, 0, 0, DateTimeKind.Utc);   // 07-10-2026 10:30 AM IST

        // ---- lifetime ------------------------------------------------------------------------------

        [Fact]
        public void Lifetime_IsExactly48Hours_FromCreation()
        {
            Assert.Equal(Created.AddHours(48), SuggestionRules.ExpiresAt(Created));
            Assert.Equal(new DateTime(2026, 10, 9, 10, 30, 0), AppClock.ToIndiaTime(SuggestionRules.ExpiresAt(Created)));   // 9 Oct 10:30 AM IST
        }

        [Fact]
        public void NormalPost_ActiveBefore48Hours_ExpiredAtAndAfter()
        {
            var expires = SuggestionRules.ExpiresAt(Created);
            Assert.False(SuggestionRules.IsExpired(false, expires, Created));
            Assert.False(SuggestionRules.IsExpired(false, expires, Created.AddHours(47).AddMinutes(59)));
            Assert.True(SuggestionRules.IsExpired(false, expires, Created.AddHours(48)));
            Assert.True(SuggestionRules.IsExpired(false, expires, Created.AddDays(30)));
        }

        [Fact]
        public void PermanentPost_NeverExpires()
        {
            var expires = SuggestionRules.ExpiresAt(Created);
            Assert.False(SuggestionRules.IsExpired(true, expires, Created.AddHours(49)));
            Assert.False(SuggestionRules.IsExpired(true, expires, Created.AddYears(5)));
        }

        [Theory]
        [InlineData(28 * 60, "1 day 4 hours")]
        [InlineData(48 * 60, "2 days")]
        [InlineData(3 * 60 + 20, "3 hours 20 minutes")]
        [InlineData(60, "1 hour")]
        [InlineData(12, "12 minutes")]
        [InlineData(1, "1 minute")]
        [InlineData(0, "expired")]
        [InlineData(-5, "expired")]
        public void Remaining_IsReadable(int minutesLeft, string expected)
        {
            var now = Created;
            Assert.Equal(expected, SuggestionRules.Remaining(now.AddMinutes(minutesLeft), now));
        }

        [Fact]
        public void Lifetime_Permanent_AndStatus_AreSeparateConcepts()
        {
            Assert.Equal(new[] { "Open", "InDiscussion", "InProgress", "Resolved", "Closed" }, SuggestionRules.Statuses);
            Assert.Equal(new[] { "All", "Active", "Expired", "Permanent", "Deleted" }, SuggestionRules.Lifetimes);
            Assert.DoesNotContain("Expired", SuggestionRules.Statuses);
            Assert.DoesNotContain("Permanent", SuggestionRules.Statuses);
            Assert.Equal("In Discussion", SuggestionRules.StatusLabel("InDiscussion"));
            Assert.Equal("In Progress", SuggestionRules.StatusLabel("InProgress"));
        }

        [Theory]
        [InlineData("MainOffice", true)]
        [InlineData("MotherPlant", false)]
        [InlineData("Outlet", false)]
        [InlineData(null, false)]
        public void Polyhouse_IsRequiredOnlyForTheMainOfficeArea(string? areaType, bool required)
            => Assert.Equal(required, SuggestionRules.RequiresPolyhouse(areaType));

        [Fact]
        public void Text_IsRequired_Trimmed_AndLimited()
        {
            Assert.Equal("Title is required.", SuggestionRules.Required("   ", "Title", SuggestionRules.MaxTitleLength));
            Assert.Equal("Title is required.", SuggestionRules.Required(null, "Title", SuggestionRules.MaxTitleLength));
            Assert.Null(SuggestionRules.Required("  " + new string('x', 150) + "  ", "Title", SuggestionRules.MaxTitleLength));   // trimmed first
            Assert.Equal("Title cannot be longer than 150 characters.", SuggestionRules.Required(new string('x', 151), "Title", SuggestionRules.MaxTitleLength));
            Assert.Equal("Details cannot be longer than 2000 characters.", SuggestionRules.Required(new string('x', 2001), "Details", SuggestionRules.MaxDescriptionLength));
            Assert.Equal("Comment cannot be longer than 1000 characters.", SuggestionRules.Required(new string('x', 1001), "Comment", SuggestionRules.MaxCommentLength));
        }

        [Fact]
        public void OwnComment_IsEditable_For30Minutes()
        {
            Assert.True(SuggestionRules.CanEditComment(Created, Created.AddMinutes(30)));
            Assert.False(SuggestionRules.CanEditComment(Created, Created.AddMinutes(31)));
        }

        // ---- photos (the Maintenance photo rules, own folder) ----------------------------------------

        private static readonly byte[] Png = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 0x0D, 0x49, 0x48, 0x44, 0x52 };
        private static readonly byte[] Jpeg = { 0xFF, 0xD8, 0xFF, 0xE0, 0, 0x10, 0x4A, 0x46, 0x49, 0x46, 0, 1 };
        private static readonly byte[] Webp = Encoding.ASCII.GetBytes("RIFF\x24\0\0\0WEBPVP8 ");

        private static IFormFile Upload(byte[] content, string name, long? length = null)
            => new FormFile(new MemoryStream(content), 0, length ?? content.Length, "Photos", name) { Headers = new HeaderDictionary(), ContentType = "image/jpeg" };

        [Fact]
        public async Task Photo_JpegPngWebp_Accepted_ByTheirBytes()
        {
            Assert.Equal("image/jpeg", (await MaintenancePhotoStore.ValidateAsync(Upload(Jpeg, "leak.jpg"))).Type!.ContentType);
            Assert.Equal("image/png", (await MaintenancePhotoStore.ValidateAsync(Upload(Png, "leak.png"))).Type!.ContentType);
            Assert.Equal("image/webp", (await MaintenancePhotoStore.ValidateAsync(Upload(Webp, "leak.webp"))).Type!.ContentType);
            Assert.Equal("image/png", (await MaintenancePhotoStore.ValidateAsync(Upload(Png, "renamed.jpg"))).Type!.ContentType);   // bytes win over the name
        }

        [Fact]
        public async Task Photo_InvalidContent_AndOversize_Rejected()
        {
            var (type, error) = await MaintenancePhotoStore.ValidateAsync(Upload(Encoding.ASCII.GetBytes("<svg onload=alert(1)>"), "leak.jpg"));
            Assert.Null(type);
            Assert.Equal("leak.jpg: only JPEG, PNG or WebP photos are allowed.", error);
            Assert.EndsWith("only JPEG, PNG or WebP photos are allowed.", (await MaintenancePhotoStore.ValidateAsync(Upload(Encoding.ASCII.GetBytes("MZ\x90\0exe"), "a.png"))).Error);
            var big = new byte[Png.Length];
            Png.CopyTo(big, 0);
            Assert.EndsWith("a photo can be at most 5 MB.", (await MaintenancePhotoStore.ValidateAsync(Upload(big, "big.png", MaintenancePhotoStore.MaxBytes + 1))).Error);
        }

        [Fact]
        public void PhotoStore_HasItsOwnFolder_OutsideTheWebRoot()
        {
            var store = new SuggestionPhotoStore(new SuggestionStorageOptions());
            Assert.EndsWith(Path.Combine("PlantStockManager", "SuggestionPhotos"), store.Root);
            Assert.NotEqual(new MaintenancePhotoStore(new MaintenanceStorageOptions()).Root, store.Root);
            Assert.DoesNotContain("wwwroot", store.Root, StringComparison.OrdinalIgnoreCase);
            var configured = Path.Combine(Path.GetTempPath(), "psm-sp-configured");
            Assert.Equal(Path.GetFullPath(configured), new SuggestionPhotoStore(new SuggestionStorageOptions { PhotoRoot = configured }).Root);
        }

        [Fact]
        public async Task PhotoStore_GuidNames_AndPathTraversalBlocked()
        {
            var root = Path.Combine(Path.GetTempPath(), "psm-sp-" + Guid.NewGuid().ToString("N"));
            try
            {
                var store = new SuggestionPhotoStore(new SuggestionStorageOptions { PhotoRoot = root });
                var saved = await store.SaveAsync(Upload(Jpeg, "..\\..\\PH-04 leak.jpg"), MaintenancePhotoStore.Detect(Jpeg)!);
                Assert.Matches("^[0-9a-f]{32}\\.jpg$", saved.StoredFileName);
                Assert.Equal("PH-04 leak.jpg", saved.OriginalFileName);
                Assert.StartsWith(Path.GetFullPath(root), store.PathFor(saved.StoredFileName)!);
                foreach (var bad in new[] { "../" + saved.StoredFileName, "..\\..\\appsettings.json", "../../web.config", Path.Combine(root, saved.StoredFileName),
                                            "%2e%2e%2fappsettings.json", "0123456789abcdef0123456789abcdef.svg", saved.StoredFileName.ToUpperInvariant(), "", null })
                    Assert.Null(store.PathFor(bad));
            }
            finally
            {
                if (Directory.Exists(root)) Directory.Delete(root, true);
            }
        }

        [Fact]
        public async Task PhotoStore_FailedDatabaseWrite_LeavesNoOrphanedFile()
        {
            var root = Path.Combine(Path.GetTempPath(), "psm-sp-" + Guid.NewGuid().ToString("N"));
            try
            {
                var store = new SuggestionPhotoStore(new SuggestionStorageOptions { PhotoRoot = root });
                string? written = null;
                await Assert.ThrowsAsync<InvalidOperationException>(() => store.SaveThenAsync<int>(Upload(Png, "a.png"), MaintenancePhotoStore.Detect(Png), saved =>
                {
                    written = store.PathFor(saved!.StoredFileName);
                    Assert.True(File.Exists(written));                       // the file was on disk during the write ...
                    throw new InvalidOperationException("database down");
                }));
                Assert.NotNull(written);
                Assert.False(File.Exists(written));                          // ... and is gone after the failure
                Assert.Empty(Directory.GetFiles(root));

                var id = await store.SaveThenAsync(Upload(Png, "b.png"), MaintenancePhotoStore.Detect(Png), saved => Task.FromResult(saved!.StoredFileName));
                Assert.True(File.Exists(store.PathFor(id)));                 // success keeps the file
                Assert.Null(await store.SaveThenAsync<string?>(null, null, saved => Task.FromResult(saved?.StoredFileName)));   // no photo = no file
            }
            finally
            {
                if (Directory.Exists(root)) Directory.Delete(root, true);
            }
        }

        // ---- authorization: a GLOBAL module ---------------------------------------------------------

        private static ClaimsPrincipal User(int id, string[] roles, string[] permissions, int[] areas, bool fullAccess = false)
        {
            var claims = new List<Claim> { new("UserId", id.ToString()) };
            claims.AddRange(roles.Select(r => new Claim(AreaAccessService.RoleNameClaimType, r)));
            claims.AddRange(permissions.Select(p => new Claim(MinimumAuthorizationLevelHandler.PermissionClaimType, p)));
            claims.AddRange(areas.Select(a => new Claim(AreaAccessService.AreaAccessClaimType, a.ToString())));
            if (fullAccess) claims.Add(new Claim(ClaimsPrincipalSecurityExtensions.FullAccessClaimType, "true"));
            return new ClaimsPrincipal(new ClaimsIdentity(claims, "test"));
        }

        // Every role of the application, with its real Area assignment shape and typical permissions --
        // and NO Suggestion.* permission (the module's grants are global, not per role).
        public static readonly TheoryData<string, int[], string[]> AllRoles = new()
        {
            { "Sowing Supervisor", new[] { 2 }, new[] { "Labour.Enter", "Maintenance.Raise", "Store.Request" } },
            { "Mother Plant Supervisor", new[] { 127 }, new[] { "MotherPlant.Enter", "Maintenance.Raise" } },
            { "Fertilizer Supervisor", new[] { 2 }, new[] { "Fertilizer.Manage" } },
            { "Office Coordinator", new[] { 2 }, new[] { "Booking.View" } },
            { "Booking Executive", new[] { 2 }, new[] { "Booking.Enter" } },
            { "Dispatch Executive", new[] { 2 }, new[] { "Dispatch.Enter" } },
            { "Electrician", Array.Empty<int>(), new[] { "Maintenance.Work" } },
            { "Welder", Array.Empty<int>(), new[] { "Maintenance.Work" } },
            { "Plumber", Array.Empty<int>(), new[] { "Maintenance.Work" } },
            { "Sowing Operator", new[] { 2 }, new[] { "SeedSowing.Enter" } },
            { "Pot Production Operator", new[] { 2 }, new[] { "PotProduction.Enter" } },
            { "Main Office Store Keeper", new[] { 2 }, new[] { "Store.Manage" } },
            { "Outlet Sales", new[] { 124 }, new[] { "Outlet.Sell" } },
            { "Purchase Officer", new[] { 2 }, new[] { "Purchase.Enter" } },
            { "(no role at all)", Array.Empty<int>(), Array.Empty<string>() },
        };

        private static readonly ClaimsPrincipal SowingSup = User(6, new[] { "Sowing Supervisor" }, Array.Empty<string>(), new[] { 2 });
        private static readonly ClaimsPrincipal MpSup = User(110, new[] { "Mother Plant Supervisor" }, Array.Empty<string>(), new[] { 127 });
        private static readonly ClaimsPrincipal Electrician = User(118, new[] { "Electrician" }, new[] { "Maintenance.Work" }, Array.Empty<int>());
        private static readonly ClaimsPrincipal Admin = User(21, new[] { "System Administrator" }, Array.Empty<string>(), Array.Empty<int>(), fullAccess: true);
        private static readonly ClaimsPrincipal Anonymous = new(new ClaimsIdentity());
        private static readonly ClaimsPrincipal NoUserId = new(new ClaimsIdentity(new[] { new Claim(AreaAccessService.RoleNameClaimType, "Sowing Supervisor") }, "test"));

        private static SuggestionAuthorization Auth() => new();

        private static SuggestionPost Post(int area = 2, bool expired = false, bool permanent = false, bool deleted = false, int postedBy = 6)
            => new() { Id = 1, AreaId = area, IsExpired = expired && !permanent, IsPermanent = permanent, IsDeleted = deleted, PostedById = postedBy, Status = "Open" };

        private static SuggestionComment Comment(int author, int minutesAgo = 1, bool deleted = false)
            => new() { Id = 5, PostId = 1, AuthorId = author, CreatedAt = DateTime.UtcNow.AddMinutes(-minutesAgo), IsDeleted = deleted };

        [Theory]
        [MemberData(nameof(AllRoles))]
        public void EveryRole_ViewsCreatesCommentsAndReplies_InEveryArea(string role, int[] areas, string[] permissions)
        {
            var auth = Auth();
            var user = User(500, role == "(no role at all)" ? Array.Empty<string>() : new[] { role }, permissions, areas);
            foreach (var code in SuggestionAuthorization.GlobalPermissions)
                Assert.True(auth.Has(user, code), $"{role}: {code}");
            Assert.True(auth.CanUseModule(user));
            foreach (var area in new[] { 1, 2, 122, 123, 124, 127, 128 })                 // assigned or not
            {
                Assert.True(auth.CanCreateFor(user, area), $"{role}: create for Area {area}");
                Assert.True(auth.CanView(user, Post(area: area)), $"{role}: view Area {area}");
                Assert.True(auth.CanComment(user, Post(area: area)), $"{role}: comment in Area {area}");   // replies use the same rule
                Assert.True(auth.CanView(user, Post(area: area, expired: true, permanent: true)), $"{role}: permanent post of Area {area}");
            }
            Assert.True(auth.CanEditComment(user, Post(area: 127), Comment(author: 500), DateTime.UtcNow));
            Assert.True(auth.CanDeleteComment(user, Post(area: 127), Comment(author: 500)));
            // ... but never the administrator's rights, nor expired / deleted posts.
            Assert.False(auth.IsManager(user));
            Assert.False(auth.Has(user, SuggestionAuthorization.ManagePermission));
            Assert.False(auth.CanManagePost(user, Post(postedBy: 500)));
            Assert.False(auth.CanView(user, Post(expired: true)));
            Assert.False(auth.CanView(user, Post(deleted: true)));
            Assert.False(auth.CanDeleteComment(user, Post(), Comment(author: 4)));
        }

        [Fact]
        public void SystemAdministrator_HasTheGlobalGrants_AndManage()
        {
            var auth = Auth();
            foreach (var code in SuggestionAuthorization.GlobalPermissions.Append(SuggestionAuthorization.ManagePermission))
                Assert.True(auth.Has(Admin, code));
            foreach (var post in new[] { Post(area: 2), Post(area: 127), Post(expired: true), Post(deleted: true), Post(area: 124, permanent: true) })
                Assert.True(auth.CanView(Admin, post));
            Assert.True(auth.CanComment(Admin, Post(expired: true)));
            Assert.False(auth.CanComment(Admin, Post(deleted: true)));
            Assert.True(auth.CanManagePost(Admin, Post()));
            Assert.False(auth.CanManagePost(Admin, Post(deleted: true)));
            var granted = User(40, new[] { "Office Coordinator" }, new[] { "Suggestion.Manage" }, new[] { 2 });
            Assert.True(auth.IsManager(granted));                                    // Manage can be granted to a role later
        }

        [Fact]
        public void NotSignedIn_GetsNothing()
        {
            var auth = Auth();
            foreach (var user in new[] { Anonymous, NoUserId })
            {
                Assert.False(auth.CanUseModule(user));
                Assert.False(auth.CanCreateFor(user, 2));
                Assert.False(auth.CanView(user, Post()));
                Assert.False(auth.CanComment(user, Post()));
                Assert.False(auth.IsManager(user));
            }
            Assert.False(auth.CanCreateFor(SowingSup, 0));                            // no Area chosen
        }

        [Fact]
        public void CommentEditAndDelete_OwnOnly_AdministratorModerates()
        {
            var auth = Auth();
            var now = DateTime.UtcNow;
            Assert.True(auth.CanEditComment(SowingSup, Post(), Comment(author: 6), now));
            Assert.False(auth.CanEditComment(SowingSup, Post(), Comment(author: 6, minutesAgo: 31), now));   // edit window over
            Assert.False(auth.CanEditComment(SowingSup, Post(), Comment(author: 4), now));                   // someone else's (IDOR by CommentId)
            Assert.False(auth.CanEditComment(Admin, Post(), Comment(author: 6), now));                       // moderators delete, they do not rewrite
            Assert.False(auth.CanEditComment(SowingSup, Post(), Comment(author: 6, deleted: true), now));
            Assert.True(auth.CanDeleteComment(SowingSup, Post(), Comment(author: 6, minutesAgo: 600)));
            Assert.False(auth.CanDeleteComment(MpSup, Post(), Comment(author: 6)));                          // another user's comment
            Assert.True(auth.CanDeleteComment(Admin, Post(), Comment(author: 4)));
            Assert.False(auth.CanDeleteComment(Admin, Post(), Comment(author: 4, deleted: true)));
            Assert.False(auth.CanDeleteComment(MpSup, Post(expired: true), Comment(author: 110)));           // cannot see the post any more
        }

        // ---- wiring ----------------------------------------------------------------------------------

        private sealed class MustNotBeAsked : Microsoft.AspNetCore.Authorization.IAuthorizationService
        {
            public Task<Microsoft.AspNetCore.Authorization.AuthorizationResult> AuthorizeAsync(ClaimsPrincipal user, object? resource, IEnumerable<Microsoft.AspNetCore.Authorization.IAuthorizationRequirement> requirements)
                => throw new InvalidOperationException("A Suggestions page must not need a permission policy.");
            public Task<Microsoft.AspNetCore.Authorization.AuthorizationResult> AuthorizeAsync(ClaimsPrincipal user, object? resource, string policyName)
                => throw new InvalidOperationException("A Suggestions page must not need a permission policy: " + policyName);
        }

        [Theory]
        [InlineData("/Suggestions/Index")]
        [InlineData("/Suggestions/Create")]
        [InlineData("/Suggestions/Details")]
        public async Task Pages_RequireSignIn_AndNothingElse(string page)
        {
            var rule = FeatureAuthorizationConventions.Rules[page];
            Assert.True(rule.IsAuthenticatedOnly);
            Assert.False(rule.IsAnonymous);
            Assert.Null(rule.Write);
            var access = new FeatureAccessService(new MustNotBeAsked());
            Assert.True(await access.CanAccessPageAsync(Electrician, page));            // menu + page for a role with no Area
            Assert.True(await access.CanAccessPageAsync(SowingSup, page));
            Assert.True(await access.CanAccessPageAsync(Admin, page));
            Assert.False(await access.CanAccessPageAsync(Anonymous, page));             // still behind sign-in
        }

        [Fact]
        public void Wiring_MenuServicesAndDeletionRule()
        {
            var layout = ReadSource("Pages", "Shared", "_Layout.cshtml");
            Assert.Contains("<li nav-page=\"/Suggestions/Index\"><a asp-page=\"/Suggestions/Index\">All Posts</a></li>", layout);
            Assert.Contains("<li nav-page=\"/Suggestions/Create\"><a asp-page=\"/Suggestions/Create\">Create Post</a></li>", layout);
            Assert.Single(Regex.Matches(layout, "Suggestions &amp; Problems</span>"));   // one menu section
            var program = ReadSource("Program.cs");
            Assert.Contains("builder.Services.AddScoped<SuggestionRepository>();", program);
            Assert.Contains("builder.Services.AddSingleton<SuggestionPhotoStore>();", program);
            Assert.Contains("GetSection(\"SuggestionStorage\")", program);
            Assert.Contains(DeletionRules.AreaDependencies, d => d.Table == "SuggestionPosts" && d.Column == "AreaId");
            // The module no longer consults Area authorization at all.
            var code = ReadSource("Authorization", "SuggestionAuthorization.cs") + ReadSource("Pages", "Suggestions", "Index.cshtml.cs")
                       + ReadSource("Pages", "Suggestions", "Create.cshtml.cs") + ReadSource("Pages", "Suggestions", "Details.cshtml.cs");
            Assert.DoesNotMatch(new Regex("AreaAccessService\\s+_|_areaAccess|GetAccessibleAreaIds|CanAccessRequiredArea"), Regex.Replace(code, "//.*", ""));
        }

        // ---- regression: Area authorization everywhere else is unchanged -----------------------------

        [Fact]
        public void OnlySuggestionsAndPrivacy_AreSignInOnly_EveryOtherPageKeepsItsPermissionRule()
        {
            var signInOnly = FeatureAuthorizationConventions.Rules.Where(r => r.Value.IsAuthenticatedOnly).Select(r => r.Key).OrderBy(k => k).ToArray();
            Assert.Equal(new[] { "/Privacy", "/Suggestions/Create", "/Suggestions/Details", "/Suggestions/Index" }, signInOnly);
            Assert.DoesNotContain(FeatureAuthorizationConventions.Rules, r => !r.Key.StartsWith("/Suggestions/") && (r.Value.Read.Contains("Suggestion.") || (r.Value.Write ?? "").Contains("Suggestion.")));
            foreach (var page in new[] { "/Production/Labour/Attendance", "/Production/Labour/Overtime", "/Maintenance/Raise", "/Store/NewRequest", "/Store/Issue" })
            {
                Assert.True(FeatureAuthorizationConventions.IsMapped(page), page);
                Assert.False(FeatureAuthorizationConventions.GetRule(page).IsAuthenticatedOnly, page);
            }
        }

        [Fact]
        public void AreaAuthorization_OfOtherModules_StillRestrictsTheSameUsers()
        {
            var areas = new AreaAccessService();
            var mp = User(110, new[] { "Mother Plant Supervisor" }, new[] { "Maintenance.Raise", "Store.Request" }, new[] { 127 });
            var sowing = User(6, new[] { "Sowing Supervisor" }, new[] { "Maintenance.Raise", "Store.Request" }, new[] { 2 });
            // AreaAccessService itself
            Assert.True(areas.CanAccessArea(mp, 127));
            Assert.False(areas.CanAccessArea(mp, 2));
            Assert.False(areas.CanAccessArea(Electrician, 2));
            Assert.Equal(new[] { 127 }, areas.GetAccessibleAreaIds(mp));
            Assert.Empty(areas.GetAccessibleAreaIds(Electrician));
            Assert.False(areas.HasFullAreaAccess(mp));
            Assert.True(areas.HasFullAreaAccess(Admin));
            // Maintenance and Store still scope by Area
            var maintenance = new MaintenanceAuthorization(areas);
            Assert.True(maintenance.CanRaiseFor(mp, 127));
            Assert.False(maintenance.CanRaiseFor(mp, 2));
            Assert.False(maintenance.CanRaiseFor(sowing, 127));
            var store = new StoreAuthorization(areas);
            Assert.True(store.CanRequestFor(sowing, 2));
            Assert.False(store.CanRequestFor(sowing, 127));
            Assert.False(store.CanRequestFor(mp, 2));
            // Suggestions -- and only Suggestions -- is global for the same users.
            Assert.True(Auth().CanCreateFor(mp, 2));
            Assert.True(Auth().CanView(Electrician, Post(area: 127)));
        }

        [Fact]
        public void Pages_KeepAntiforgery_AndEveryDetailsFormPostsThePostId()
        {
            foreach (var type in new[] { typeof(Pages.Suggestions.IndexModel), typeof(Pages.Suggestions.CreateModel), typeof(Pages.Suggestions.DetailsModel) })
                Assert.Null(type.GetCustomAttribute<IgnoreAntiforgeryTokenAttribute>());
            var details = ReadSource("Pages", "Suggestions", "Details.cshtml") + ReadSource("Pages", "Suggestions", "_Comment.cshtml");
            var forms = Regex.Matches(details, "<form method=\"post\"[^>]*>").Select(m => m.Value).ToList();
            Assert.Equal(7, forms.Count);   // Details: MakePermanent, Status, Delete, Comment; _Comment: Reply, Edit, DeleteComment
            Assert.All(forms, f => Assert.Contains("asp-route-id=\"@", f));
            foreach (var handler in new[] { "MakePermanent", "Status", "Delete", "Comment", "EditComment", "DeleteComment" })
                Assert.Contains(forms, f => f.Contains($"asp-page-handler=\"{handler}\""));
        }

        [Fact]
        public void Views_EncodeUserText_OfferCameraAndGallery_AndHaveNoReactions()
        {
            var views = string.Join("\n", new[] { "Index.cshtml", "Create.cshtml", "Details.cshtml", "_Comment.cshtml", "_Lifetime.cshtml" }
                .Select(f => ReadSource("Pages", "Suggestions", f)));
            Assert.DoesNotContain("Html.Raw", views);                                  // all user text is encoded by Razor
            var create = ReadSource("Pages", "Suggestions", "Create.cshtml");
            Assert.Contains("capture=\"environment\"", create);                        // phone camera
            Assert.Contains("📷 Take Photo", create);
            Assert.Contains("🖼 Choose from Gallery", create);
            Assert.Contains("enctype=\"multipart/form-data\"", create);
            // No likes / dislikes / reactions anywhere in the module's UI, model or data access (comments aside).
            var code = Regex.Replace(views, "@\\*.*?\\*@", "", RegexOptions.Singleline)
                       + ReadSource("Models", "Suggestion.cs") + ReadSource("Data", "SuggestionRepository.cs")
                       + ReadSource("Pages", "Suggestions", "Details.cshtml.cs");
            code = Regex.Replace(code, "//.*", "");
            Assert.DoesNotMatch(new Regex("\\b(like|likes|liked|dislike|unlike|reaction|reactions|emoji)\\b", RegexOptions.IgnoreCase), code);
        }

        [Fact]
        public void Migration_IsGuarded_Computes48HourLifetime_AndKeepsRepliesOnTheSamePost()
        {
            var sql = ReadSource("Database", "Migrations", "2026-10-07_SuggestionsProblems.sql");
            Assert.Contains("IF @@SERVERNAME <> N'$(TARGET_SERVER)'", sql);
            Assert.Contains("IF N'$(MODE)' NOT IN (N'DRYRUN', N'COMMIT')", sql);
            Assert.Contains("ExpiresAt           AS (DATEADD(HOUR, 48, CreatedAt)) PERSISTED", sql);
            Assert.Contains("FOREIGN KEY (PostId, ParentCommentId) REFERENCES dbo.SuggestionComments (PostId, Id)", sql);
            Assert.Contains("THROW 51713, ''This Polyhouse does not belong to the selected Area.''", sql);
            Assert.Contains("THROW 51839, ''A permanent post stays permanent and a deleted post stays deleted.''", sql);
            Assert.DoesNotContain("SuggestionLikes", sql);
            Assert.DoesNotContain("Reaction", sql);
            var rollback = ReadSource("Database", "Migrations", "2026-10-07_SuggestionsProblems_Rollback.sql");
            Assert.Contains("refusing to drop business records", rollback);
            // Global access: the per-role grants of View / Create / Comment are removed, Manage stays.
            var global = ReadSource("Database", "Migrations", "2026-10-07_SuggestionsGlobalAccess.sql");
            Assert.Contains("IF @@SERVERNAME <> N'$(TARGET_SERVER)'", global);
            Assert.Contains("WHERE p.Code IN (N'Suggestion.View', N'Suggestion.Create', N'Suggestion.Comment');", global);
            Assert.Contains("DELETE FROM dbo.Permissions WHERE Code IN (N'Suggestion.View', N'Suggestion.Create', N'Suggestion.Comment');", global);
            Assert.DoesNotContain("DELETE FROM dbo.Permissions WHERE Code = N'Suggestion.Manage'", global);
            Assert.DoesNotContain("AreaAccess", Regex.Replace(global, "--.*", ""));   // no Area assignment / other module touched
        }
    }
}
