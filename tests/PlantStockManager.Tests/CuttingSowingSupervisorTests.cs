using System.Reflection;
using Microsoft.AspNetCore.Mvc;
using PlantStockManager.Authorization;
using PlantStockManager.Services;

namespace PlantStockManager.Tests
{
    // Cutting Tray Sowing -- who can be chosen as the Sowing Supervisor.
    //
    // Root cause of the old behaviour: the dropdown was
    // UserRoleRepository.GetSowingApproversAsync() = every active user whose role
    // is named exactly "Sowing Supervisor", with NO Area filter (the Growing Area
    // never influenced it), and the page, the repository and the trigger
    // TR_SeedSowings_SupervisorRole all enforced that same role name.
    //
    // New rule (SowingSupervisorRules): active + assigned to the sowing's Area +
    // holds the approval permission (ReadyStock.Confirm); the recorder is NOT excluded;
    // no role name involved.
    public class CuttingSowingSupervisorTests
    {
        private const int Area2 = 2, Area122 = 122, Area123 = 123;
        private const string Approve = "ReadyStock.Confirm";

        private static AreaUserGrant G(int id, string name, int? area, string[]? perms = null, bool active = true)
            => new(id, name, area, active, perms ?? new[] { Approve });

        private static int[] Ids(IEnumerable<(int UserId, string UserName)> l) => l.Select(u => u.UserId).ToArray();

        // ---- the eligible list, per Area ------------------------------------------------------

        [Fact]
        public void AreaWithSeveralAssignedUsers_ListsThemAll_SortedByName_EachOnce()
        {
            var grants = new[]
            {
                G(9, "Maya", Area2), G(6, "Akshay", Area2), G(13, "Somnath", Area2),
                G(6, "Akshay", Area2, new[] { "Sowing.View" }),      // second role row of the same user
            };
            var list = SowingSupervisorRules.EligibleForArea(grants, Area2);
            Assert.Equal(new[] { "Akshay", "Maya", "Somnath" }, list.Select(u => u.UserName).ToArray());
            Assert.Equal(new[] { 6, 9, 13 }, Ids(list));
        }

        [Fact]
        public void UserAssignedToTheArea_WhoIsNotAMotherPlantOrSowingSupervisor_Appears()
        {
            // the role NAME does not matter: the grant carries no role at all,
            // only the Area assignment and the approval permission
            var grants = new[] { G(50, "Operator Omkar", Area122), G(51, "Supervisor Sita", Area122) };
            Assert.Equal(new[] { 50, 51 }, Ids(SowingSupervisorRules.EligibleForArea(grants, Area122)));
        }

        [Fact]
        public void UserAssignedOnlyToAnotherArea_IsExcluded()
        {
            var grants = new[] { G(6, "Akshay", Area2), G(101, "Achyut", Area122) };
            Assert.Equal(new[] { 101 }, Ids(SowingSupervisorRules.EligibleForArea(grants, Area122)));
            Assert.Empty(SowingSupervisorRules.EligibleForArea(grants, Area123));
        }

        [Fact]
        public void UserWithNoAreaAssignment_IsExcluded()
            => Assert.Empty(SowingSupervisorRules.EligibleForArea(new[] { G(7, "Nowhere", null) }, Area2));

        [Fact]
        public void InactiveUser_IsExcluded_EvenWhenAssignedAndAuthorized()
        {
            var grants = new[] { G(9, "Maya", Area2, active: false), G(13, "Somnath", Area2) };
            Assert.Equal(new[] { 13 }, Ids(SowingSupervisorRules.EligibleForArea(grants, Area2)));
        }

        [Fact]
        public void UserWhoCannotApprove_IsExcluded_BecauseTheSupervisorIsTheOnlyApprover()
        {
            var grants = new[] { G(2, "Dispatch Reshma", Area2, new[] { "Dispatch.Enter", "ReadyStock.View" }), G(6, "Akshay", Area2) };
            Assert.Equal(new[] { 6 }, Ids(SowingSupervisorRules.EligibleForArea(grants, Area2)));
        }

        [Fact]
        public void TheRecorder_IsOffered_WhenTheyMeetTheSameRules()
        {
            var grants = new[] { G(6, "Akshay", Area2), G(9, "Maya", Area2) };
            Assert.Equal(new[] { 6, 9 }, Ids(SowingSupervisorRules.EligibleForArea(grants, Area2)));
        }

        [Fact]
        public void PermissionAndAreaAreCombinedAcrossARowsLikeTheLoginClaimsAre()
        {
            // Area from one role row, the approval permission from another
            var grants = new[] { G(70, "Combo", Area122, new[] { "Dashboard.View" }), G(70, "Combo", null, new[] { Approve }) };
            Assert.Equal(new[] { 70 }, Ids(SowingSupervisorRules.EligibleForArea(grants, Area122)));
            Assert.Empty(SowingSupervisorRules.EligibleForArea(grants, Area2));
        }

        [Fact]
        public void ApprovalPermission_IsReadFromThePageMap_NotCopied()
        {
            Assert.Equal(FeatureAuthorizationConventions.GetRule("/Production/ReadyConfirmation/Confirm").Read, SowingSupervisorRules.ApprovalPermissions);
            Assert.Contains("ReadyStock.Confirm", SowingSupervisorRules.ApprovalPermissions);
        }

        // ---- assignment validation (what is saved) -----------------------------------------------

        [Fact]
        public void ValidateAssignment_AcceptsAnEligibleUser_ByUserId()
        {
            var (ok, error) = SowingSupervisorRules.ValidateAssignment(9, new[] { 6, 9, 13 });
            Assert.True(ok, error);
        }

        [Theory]
        [InlineData(null)]
        [InlineData(0)]
        [InlineData(-3)]
        public void ValidateAssignment_RequiresASupervisor(int? id)
            => Assert.False(SowingSupervisorRules.ValidateAssignment(id, new[] { 6 }).Ok);

        [Fact]
        public void ValidateAssignment_RefusesAnyoneNotEligibleForTheArea()
        {
            var (ok, error) = SowingSupervisorRules.ValidateAssignment(101, new[] { 6, 9 });   // 101 works in another Area
            Assert.False(ok);
            Assert.Contains("sowing's Area", error);
        }

        // ---- wiring: page, repository, permissions, unchanged workflows ------------------------------

        private static string Repo(params string[] parts)
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "PlantStockManager.sln")))
                dir = dir.Parent;
            return File.ReadAllText(Path.Combine(new[] { dir!.FullName }.Concat(parts).ToArray()));
        }

        [Fact]
        public void Page_LoadsSupervisorsForTheResolvedGrowingArea()
        {
            var type = typeof(PlantStockManager.Pages.Production.SeedSowing.CreateFromCuttingModel);
            var handler = type.GetMethod("OnGetSupervisorsAsync");
            Assert.NotNull(handler);
            Assert.Equal(new[] { "areaId", "cuttingStockId", "polyhouseId" }, handler!.GetParameters().Select(p => p.Name).ToArray());
            var cs = Repo("Pages", "Production", "SeedSowing", "CreateFromCutting.cshtml.cs");
            Assert.Contains("GetCuttingSowingSupervisorsAsync", cs);
            Assert.Contains("DirectSowingRules.ResolveGrowingLocation", cs);      // same Area resolution as the save
            Assert.DoesNotContain("GetSowingApproversAsync", cs);                 // the role-name list is gone from this page
            Assert.DoesNotContain("ValidateSupervisorAssignment", cs);
        }

        [Fact]
        public void Page_ReloadsTheListWhenTheAreaCuttingStockOrPolyhouseChanges()
        {
            var html = Repo("Pages", "Production", "SeedSowing", "CreateFromCutting.cshtml");
            Assert.Contains("handler: 'Supervisors'", html);
            Assert.Contains("$('#areaSelect').on('change'", html);
            Assert.Contains("$('#stock').on('change', loadSupervisors)", html);
            Assert.Contains("$('#polyhouseSelect').on('change', loadSupervisors)", html);
            Assert.Contains("id=\"supervisorSelect\"", html);
        }

        [Fact]
        public void SelectedSupervisorIsBoundAsAUserId_AndStoredAsSupervisorId()
        {
            var type = typeof(PlantStockManager.Pages.Production.SeedSowing.CreateFromCuttingModel);
            var prop = type.GetProperty("SupervisorId")!;
            Assert.Equal(typeof(int?), prop.PropertyType);
            Assert.NotNull(prop.GetCustomAttribute<BindPropertyAttribute>());
            Assert.Null(type.GetProperty("SupervisorName"));                       // no name is ever posted
            var cs = Repo("Pages", "Production", "SeedSowing", "CreateFromCutting.cshtml.cs");
            Assert.Contains("SupervisorId = SupervisorId,", cs);
            var repo = Repo("Data", "SeedSowingRepository.cs");
            Assert.Contains("cmd.Parameters.AddWithValue(\"@SupervisorId\", (object?)entry.SupervisorId ?? DBNull.Value)", repo);
        }

        [Fact]
        public void RepositoryChecksTheSupervisorAgainstTheGrowingArea_UnderItsTransaction()
        {
            var repo = Repo("Data", "SeedSowingRepository.cs");
            var start = repo.IndexOf("InsertFromCuttingAsync(", StringComparison.Ordinal);
            Assert.True(start > 0);
            var body = repo.Substring(start);
            var location = body.IndexOf("DirectSowingRules.ResolveGrowingLocation", StringComparison.Ordinal);
            var supervisor = body.IndexOf("GetCuttingSowingSupervisorsAsync(growingAreaId", StringComparison.Ordinal);
            Assert.True(location > 0 && supervisor > location, "the supervisor is checked AFTER the growing Area is resolved");
            Assert.Contains("SowingSupervisorRules.ValidateAssignment", body);
            Assert.Contains("conn, tx)", body.Substring(supervisor, 200));           // same connection/transaction as the insert
        }

        [Fact]
        public void SeedSowing_KeepsItsOwnSowingSupervisorRule()
        {
            // direct SEED sowing is not part of this change
            var seedPage = Repo("Pages", "Production", "SeedSowing", "Create.cshtml.cs");
            Assert.Contains("GetSowingApproversAsync", seedPage);
            Assert.Contains("ValidateSupervisorAssignment", seedPage);
            var repo = Repo("Data", "SeedSowingRepository.cs");
            Assert.Contains("DirectSowingRules.ValidateSupervisorAssignment(", repo);
            Assert.Equal("Sowing Supervisor", SupervisorRules.SowingSupervisor);
        }

        [Fact]
        public void PermissionsOfTheSowingAndApprovalPages_AreUnchanged()
        {
            Assert.Equal("Sowing.Enter", FeatureAuthorizationConventions.GetRule("/Production/SeedSowing/CreateFromCutting").Read);
            Assert.Equal("ReadyStock.Confirm", FeatureAuthorizationConventions.GetRule("/Production/ReadyConfirmation/Confirm").Read);
        }

        // ---- Mother Plant Supervisor of the Area ----------------------------------------------------------------
        //
        // The Area's Mother Plant Supervisor qualifies through the SAME permission rule as everyone else:
        // the migration grants the role "Mother Plant Supervisor" ReadyStock.Confirm + ReadyStock.View
        // (on top of what it already had), and their UserRoles row supplies the Area.

        private static readonly string[] MpsPermissions =
        {
            "Dashboard.View", "MotherPlant.View", "MotherPlant.Enter", "InternalTransfer.View", "PotProduction.View",
            "ReadyStock.View", "ReadyStock.Confirm",
        };

        private static AreaUserGrant Mps(int id, string name, int area, bool active = true) => G(id, name, area, MpsPermissions, active);

        [Fact]
        public void MotherPlantSupervisor_OfTheSameArea_Appears()
        {
            var list = SowingSupervisorRules.EligibleForArea(new[] { Mps(101, "Achyut", Area122), Mps(111, "Abhijeet", Area123) }, Area122);
            Assert.Equal(new[] { 101 }, Ids(list));
        }

        [Fact]
        public void MotherPlantSupervisor_OfAnotherArea_DoesNotAppear()
        {
            var grants = new[] { Mps(101, "Achyut", Area122), Mps(111, "Abhijeet", Area123) };
            Assert.DoesNotContain(101, Ids(SowingSupervisorRules.EligibleForArea(grants, Area123)));
            Assert.DoesNotContain(111, Ids(SowingSupervisorRules.EligibleForArea(grants, Area122)));
            Assert.Empty(SowingSupervisorRules.EligibleForArea(grants, Area2));
        }

        [Fact]
        public void InactiveMotherPlantSupervisor_DoesNotAppear()
        {
            var grants = new[] { Mps(101, "Achyut", Area122, active: false) };
            Assert.Empty(SowingSupervisorRules.EligibleForArea(grants, Area122));
        }

        [Fact]
        public void MotherPlantSupervisor_WhoIsTheRecorder_CanBeSelected()
        {
            var grants = new[] { Mps(113, "Rahul", 1), Mps(115, "Kiran", 1) };
            Assert.Equal(new[] { 113, 115 }, Ids(SowingSupervisorRules.EligibleForArea(grants, 1)).OrderBy(x => x).ToArray());
        }

        [Fact]
        public void SeveralValidSupervisorsOfOneArea_AreAllListed_MotherPlantAndSowingSupervisorsTogether()
        {
            var grants = new[]
            {
                Mps(115, "Kiran", 1), Mps(113, "Rahul", 1),
                G(6, "Akshay (Sowing Supervisor also assigned to Area 1)", 1),
            };
            Assert.Equal(new[] { 115, 113, 6 }.OrderBy(x => x), Ids(SowingSupervisorRules.EligibleForArea(grants, 1)).OrderBy(x => x));
            Assert.Equal(3, SowingSupervisorRules.EligibleForArea(grants, 1).Count);
        }

        [Fact]
        public void MotherPlantSupervisorWithoutTheGrant_WouldNotAppear_WhichIsWhyTheMigrationGrantsIt()
        {
            var beforeGrant = G(101, "Achyut", Area122, new[] { "Dashboard.View", "MotherPlant.View", "MotherPlant.Enter", "PotProduction.View" });
            Assert.Empty(SowingSupervisorRules.EligibleForArea(new[] { beforeGrant }, Area122));
        }

        private static System.Security.Claims.ClaimsPrincipal ClaimsOf(int userId, IEnumerable<string> permissions, params int[] areas)
        {
            var claims = new List<System.Security.Claims.Claim> { new("UserId", userId.ToString()) };
            claims.AddRange(permissions.Select(p => new System.Security.Claims.Claim(MinimumAuthorizationLevelHandler.PermissionClaimType, p)));
            claims.AddRange(areas.Select(a => new System.Security.Claims.Claim(AreaAccessService.AreaAccessClaimType, a.ToString())));
            return new System.Security.Claims.ClaimsPrincipal(new System.Security.Claims.ClaimsIdentity(claims, "test"));
        }

        [Fact]
        public void MotherPlantSupervisor_WithTheGrant_CanReachAndUseTheApprovalPagesForTheirOwnArea()
        {
            var mps = ClaimsOf(101, MpsPermissions, Area122);
            var access = new AreaAccessService();
            // pending-approvals list, then the approval page
            Assert.True(mps.HasPermission(FeatureAuthorizationConventions.GetRule("/Production/ReadyConfirmation/Index").Read));
            Assert.True(mps.HasPermission(FeatureAuthorizationConventions.GetRule("/Production/ReadyConfirmation/Confirm").Read));
            // the approval page's Area check: their Area yes, another Area no
            Assert.True(access.CanAccessArea(mps, Area122));
            Assert.False(access.CanAccessArea(mps, Area123));
            // and only the assigned supervisor (never the recorder) may approve
            Assert.True(DirectSowingRules.CanApprove(101, 21, "recorder", 101, "Achyut").Ok);
            Assert.False(DirectSowingRules.CanApprove(101, 21, "recorder", 111, "Abhijeet").Ok);
        }

        [Fact]
        public void MotherPlantSupervisor_WithoutTheGrant_CannotOpenTheApprovalPage()
        {
            var before = ClaimsOf(101, new[] { "Dashboard.View", "MotherPlant.View", "MotherPlant.Enter" }, Area122);
            Assert.False(before.HasPermission(FeatureAuthorizationConventions.GetRule("/Production/ReadyConfirmation/Confirm").Read));
        }

        // ---- a forged / tampered SupervisorId is refused on the server -------------------------------------

        [Theory]
        [InlineData(101)]       // a real user, but assigned to another Area
        [InlineData(2)]         // a real user in the Area who cannot approve
        [InlineData(99999)]     // an id that does not exist
        [InlineData(-6)]
        public void ForgedSupervisorId_IsRefused_BecauseTheServerRecomputesTheEligibleList(int forgedId)
        {
            var serverSide = SowingSupervisorRules.EligibleForArea(new[]
            {
                G(6, "Akshay", Area2), G(9, "Maya", Area2),
                G(2, "Dispatch Reshma", Area2, new[] { "Dispatch.Enter" }),
                G(101, "Achyut", Area122),
            }, Area2);
            Assert.False(SowingSupervisorRules.ValidateAssignment(forgedId, serverSide.Select(u => u.UserId).ToList()).Ok);
        }

        [Fact]
        public void PageAndRepository_NeverTrustTheClientList_TheyRebuildItFromTheDatabase()
        {
            // page: eligible list loaded from the repository, then the posted id validated against it
            var cs = Repo("Pages", "Production", "SeedSowing", "CreateFromCutting.cshtml.cs");
            var post = cs.Substring(cs.IndexOf("public async Task<IActionResult> OnPostAsync()", StringComparison.Ordinal));
            var load = post.IndexOf("GetCuttingSowingSupervisorsAsync", StringComparison.Ordinal);
            var validate = post.IndexOf("SowingSupervisorRules.ValidateAssignment(SupervisorId", StringComparison.Ordinal);
            Assert.True(load > 0 && validate > load);
            // repository: the same check again under the insert transaction, so a caller that skips the page is refused too
            var repo = Repo("Data", "SeedSowingRepository.cs");
            Assert.Contains("SowingSupervisorRules.ValidateAssignment(entry.SupervisorId", repo);
        }

        // ---- cross-Area confirmation ---------------------------------------------------------------------------

        [Fact]
        public void SupervisorOfAnotherArea_CannotBeAssigned_AndSoCannotApprove()
        {
            // eligible for Area 2 only; the sowing is in Area 122
            var grants = new[] { G(6, "Akshay", Area2) };
            var eligibleForSowingArea = SowingSupervisorRules.EligibleForArea(grants, Area122).Select(u => u.UserId).ToList();
            Assert.False(SowingSupervisorRules.ValidateAssignment(6, eligibleForSowingArea).Ok);
        }

        [Fact]
        public void ApprovalPage_RefusesAnyoneButTheAssignedSupervisor_AndChecksTheSowingsAreaFirst()
        {
            // only the assigned supervisor may approve (a SEED sowing's recorder never; see the cutting tests) ...
            Assert.True(DirectSowingRules.CanApprove(assignedSupervisorId: 9, createdById: 21, createdBy: "rec", approverId: 9, approverName: "Maya").Ok);
            Assert.False(DirectSowingRules.CanApprove(9, 21, "rec", approverId: 6, approverName: "Akshay").Ok);      // another user
            Assert.False(DirectSowingRules.CanApprove(9, 9, "Maya", approverId: 9, approverName: "Maya").Ok);        // recorder
            // ... and the page checks the user's access to the sowing's Area before that, on both GET and POST
            var cs = Repo("Pages", "Production", "ReadyConfirmation", "Confirm.cshtml.cs");
            Assert.True(System.Text.RegularExpressions.Regex.Matches(cs, @"CanAccessArea\(User, sowing\.AreaId\)").Count >= 2);
            Assert.Equal("ReadyStock.Confirm", FeatureAuthorizationConventions.GetRule("/Production/ReadyConfirmation/Confirm").Read);
        }

        // ---- editing / viewing an existing record ------------------------------------------------------

        [Fact]
        public void ExistingRecord_ShowsTheSavedSupervisor_EvenIfInactiveOrNoLongerEligible()
        {
            // the saved supervisor is read straight from SupervisorId (no IsActive / role / Area
            // filter), so the historical record always shows who was assigned
            var repo = Repo("Data", "SeedSowingRepository.cs");
            Assert.Contains("LEFT JOIN dbo.IMSUsers sup ON sw.SupervisorId = sup.Id", repo);
            Assert.DoesNotContain("sup.IsActive", repo);
            Assert.Contains("SupervisorName = reader.IsDBNull(reader.GetOrdinal(\"SupervisorName\"))", repo);
            var edit = Repo("Pages", "Production", "SeedSowing", "Edit.cshtml");
            Assert.Contains("s.SupervisorName", edit);
        }

        [Fact]
        public void EditPage_DoesNotLetTheAssignedSupervisorBeChanged()
        {
            // TR_SeedSowings_ImmutableTrayData fixes SupervisorId after saving (the sole approver
            // cannot be re-assigned), so the edit page has no supervisor input to rebuild
            var type = typeof(PlantStockManager.Pages.Production.SeedSowing.EditModel);
            Assert.DoesNotContain(type.GetProperties().Where(p => p.GetCustomAttribute<BindPropertyAttribute>() != null), p => p.Name.Contains("Supervisor"));
        }

        // ---- the database migration (written, dry-run first; not applied) -------------------------------

        [Fact]
        public void Migration_ChangesOnlyTheSupervisorTrigger_KeepsSeedRule_EnforcesAreaForCutting()
        {
            var sql = Repo("Database", "Migrations", "2026-09-28_CuttingSowingSupervisorByArea.sql");
            Assert.Contains("IF DB_NAME() <> N'PlantsIMS2_Test'", sql);
            Assert.Single(System.Text.RegularExpressions.Regex.Matches(sql, "CREATE OR ALTER TRIGGER"));
            Assert.Contains("CREATE OR ALTER TRIGGER dbo.TR_SeedSowings_SupervisorRole", sql);
            foreach (var forbidden in new[] { "ALTER TABLE", "CREATE TABLE", "DROP TABLE", "DROP CONSTRAINT", "DELETE FROM", "UPDATE dbo.", "TRUNCATE", "sp_rename" })
                Assert.DoesNotContain(forbidden, sql);
            // the ONLY data write: the permission grant (below)
            Assert.Single(System.Text.RegularExpressions.Regex.Matches(sql, @"INSERT INTO dbo\."));
            Assert.Single(System.Text.RegularExpressions.Regex.Matches(sql, @"INSERT INTO dbo\.RolePermissions \(RoleId, PermissionId\)"));
            Assert.Contains("i.SourceType <> N''Cutting''", sql);                       // seed branch
            Assert.Contains("= N''Sowing Supervisor''", sql);                            // ... keeps the role rule
            Assert.Contains("ur.AreaId = i.AreaId", sql);                                // cutting branch: Area boundary
            Assert.Contains("ISNULL(u.IsActive, 0) = 1", sql);
            Assert.Contains("p.Code = N''ReadyStock.Confirm''", sql);
            // approved and applied 2026-09-28: it ends in COMMIT; the only ROLLBACK left is the error path in CATCH
            Assert.Matches(@"(?m)^COMMIT TRANSACTION CuttingSowingSupervisor;", sql);
            Assert.Single(System.Text.RegularExpressions.Regex.Matches(sql, @"(?m)^[^-\r\n].*\bROLLBACK TRANSACTION\b"));
            Assert.Contains("IF XACT_STATE() <> 0 ROLLBACK TRANSACTION CuttingSowingSupervisor;", sql);
        }

        [Fact]
        public void Migration_GrantsExactlyTwoPermissions_ToExactlyTheMotherPlantSupervisorRole_Idempotently()
        {
            var sql = Repo("Database", "Migrations", "2026-09-28_CuttingSowingSupervisorByArea.sql");
            var start = sql.IndexOf("INSERT INTO dbo.RolePermissions", StringComparison.Ordinal);
            var block = sql.Substring(start, sql.IndexOf("@GrantsAdded", start, StringComparison.Ordinal) - start);
            Assert.Contains("= N'Mother Plant Supervisor'", block);                                    // one role, by name
            Assert.Contains("p.Code IN (N'ReadyStock.View', N'ReadyStock.Confirm')", block);          // two permissions, by code
            Assert.Contains("NOT EXISTS (SELECT 1 FROM dbo.RolePermissions rp WHERE rp.RoleId = r.Id AND rp.PermissionId = p.Id)", block);
            Assert.DoesNotContain("Sowing Supervisor", block);                                         // no other role touched
            // aborts rather than guessing if the role / permissions are not exactly as expected
            Assert.Contains("THROW 50321", sql);
            Assert.Contains("THROW 50322", sql);
            // the grant happens in the same transaction as the trigger change
            Assert.True(start < sql.IndexOf("CREATE OR ALTER TRIGGER", StringComparison.Ordinal));
        }
    }
}
