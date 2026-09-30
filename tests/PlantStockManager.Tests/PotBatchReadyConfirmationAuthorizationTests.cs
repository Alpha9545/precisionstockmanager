using System.Reflection;
using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using PlantStockManager.Authorization;
using PlantStockManager.Services;

namespace PlantStockManager.Tests
{
    // Pot Production READY confirmation: ANY authorized user assigned to the
    // batch's Area may confirm it. The rule under test:
    //
    //     Pot Production permission (PotBatchRules.ReadyConfirmPermissions)
    //   + strict assignment to the batch's Area (an "AreaAccess" claim)
    //
    // No Mother Plant Supervisor role, no "not the creator" restriction.
    public class PotBatchReadyConfirmationAuthorizationTests
    {
        private const int AreaA = 10, AreaB = 20, AreaC = 30;
        private const string PotEnter = "PotProduction.Enter";

        // A signed-in user as the auth cookie would describe them.
        private static ClaimsPrincipal User(int? userId, string[]? permissions = null, int[]? areas = null, string[]? roleNames = null, bool fullAccess = false)
        {
            var claims = new List<Claim>();
            if (userId.HasValue) claims.Add(new Claim("UserId", userId.Value.ToString()));
            foreach (var p in permissions ?? Array.Empty<string>())
                claims.Add(new Claim(MinimumAuthorizationLevelHandler.PermissionClaimType, p));
            foreach (var a in areas ?? Array.Empty<int>())
                claims.Add(new Claim(AreaAccessService.AreaAccessClaimType, a.ToString()));
            foreach (var r in roleNames ?? Array.Empty<string>())
                claims.Add(new Claim(AreaAccessService.RoleNameClaimType, r));
            if (fullAccess)
                claims.Add(new Claim(ClaimsPrincipalSecurityExtensions.FullAccessClaimType, "true"));
            return new ClaimsPrincipal(new ClaimsIdentity(claims, "test"));
        }

        // ---- 1 / 2. the Area is the boundary --------------------------------

        [Fact]
        public void AreaAUserWithPermission_CanConfirmAreaA()
            => Assert.True(PotBatchRules.CanConfirmReady(User(1, new[] { PotEnter }, new[] { AreaA }), AreaA));

        [Fact]
        public void AreaAUserWithPermission_CannotConfirmAreaB()
            => Assert.False(PotBatchRules.CanConfirmReady(User(1, new[] { PotEnter }, new[] { AreaA }), AreaB));

        [Fact]
        public void UserAssignedToBothAreas_CanConfirmEither_ButNotAThirdArea()
        {
            var user = User(1, new[] { PotEnter }, new[] { AreaA, AreaB });
            Assert.True(PotBatchRules.CanConfirmReady(user, AreaA));
            Assert.True(PotBatchRules.CanConfirmReady(user, AreaB));
            Assert.False(PotBatchRules.CanConfirmReady(user, AreaC));
        }

        // ---- 3 / 4. no supervisor role needed --------------------------------

        [Theory]
        [InlineData("Pot Production Operator")]
        [InlineData("Kiran Supervisor")]
        [InlineData("Some Other Role")]
        public void NormalAuthorizedAreaUser_CanConfirm_WhateverTheRoleIsCalled(string role)
            => Assert.True(PotBatchRules.CanConfirmReady(User(1, new[] { PotEnter }, new[] { AreaA }, new[] { role }), AreaA));

        [Fact]
        public void MotherPlantSupervisorRole_IsNotRequired()
        {
            var user = User(1, new[] { PotEnter }, new[] { AreaA }, roleNames: Array.Empty<string>());
            Assert.DoesNotContain(user.Claims, c => c.Type == AreaAccessService.RoleNameClaimType);
            Assert.True(PotBatchRules.CanConfirmReady(user, AreaA));
        }

        [Fact]
        public void MotherPlantSupervisorRole_AloneDoesNotGrantAnything()
        {
            // the role NAME confers nothing: permission + Area assignment decide
            var noArea = User(1, new[] { PotEnter }, Array.Empty<int>(), new[] { SupervisorRules.MotherPlantSupervisor });
            var noPermission = User(2, Array.Empty<string>(), new[] { AreaA }, new[] { SupervisorRules.MotherPlantSupervisor });
            Assert.False(PotBatchRules.CanConfirmReady(noArea, AreaA));
            Assert.False(PotBatchRules.CanConfirmReady(noPermission, AreaA));
        }

        // ---- 5. permission is still required ---------------------------------

        [Fact]
        public void UserWithoutThePermission_CannotConfirm_EvenInTheirOwnArea()
            => Assert.False(PotBatchRules.CanConfirmReady(User(1, Array.Empty<string>(), new[] { AreaA }), AreaA));

        [Theory]
        [InlineData("PotProduction.View")]
        [InlineData("MotherPlant.View")]
        [InlineData("ReadyStock.Confirm")]      // seedling tray approval: a different workflow
        [InlineData("Dashboard.View")]
        public void UserWithOnlyUnrelatedOrViewPermissions_CannotConfirm(string permission)
            => Assert.False(PotBatchRules.CanConfirmReady(User(1, new[] { permission }, new[] { AreaA }), AreaA));

        [Theory]
        [InlineData("PotProduction.Enter")]
        [InlineData("MotherPlant.Enter")]
        [InlineData("Kiran.Enter")]
        public void AnyOfThePotBatchWritePermissions_Suffices(string permission)
            => Assert.True(PotBatchRules.CanConfirmReady(User(1, new[] { permission }, new[] { AreaA }), AreaA));

        [Fact]
        public void RequiredPermission_IsExactlyTheBatchPageWritePolicy()
        {
            // one source of truth: the same rule the page map applies to every POST
            Assert.Equal(FeatureAuthorizationConventions.GetRule("/Production/PotBatch/Details").Write, PotBatchRules.ReadyConfirmPermissions);
            Assert.Contains(PotEnter, PotBatchRules.ReadyConfirmPermissions);
            Assert.DoesNotContain("View", PotBatchRules.ReadyConfirmPermissions);
        }

        // ---- 6. no Area at all -----------------------------------------------

        [Fact]
        public void UserAssignedToNoArea_CannotConfirm()
            => Assert.False(PotBatchRules.CanConfirmReady(User(1, new[] { PotEnter }), AreaA));

        [Fact]
        public void UserWithNeitherPermissionNorArea_CannotConfirm()
            => Assert.False(PotBatchRules.CanConfirmReady(User(1), AreaA));

        // ---- 7. the creator may confirm their own batch ------------------------

        [Fact]
        public void BatchCreator_CanConfirm_WhenAuthorizedForTheArea()
        {
            // The rule takes no creator input at all: nothing can exclude them.
            const int creator = 22;
            Assert.True(PotBatchRules.CanConfirmReady(User(creator, new[] { PotEnter }, new[] { AreaA }), AreaA));
        }

        [Fact]
        public void BatchCreator_CanBeChosenAsReadyConfirmationBy()
        {
            const int creator = 22;
            var (ok, error) = PotBatchRules.ValidateCreate(8000, 10000, new DateTime(2026, 9, 1), new DateTime(2026, 10, 1), creator, new[] { creator, 40 });
            Assert.True(ok, error);
        }

        [Fact]
        public void BatchCreator_StillNeedsThePermissionAndTheArea()
        {
            const int creator = 22;
            Assert.False(PotBatchRules.CanConfirmReady(User(creator, Array.Empty<string>(), new[] { AreaA }), AreaA));
            Assert.False(PotBatchRules.CanConfirmReady(User(creator, new[] { PotEnter }, new[] { AreaB }), AreaA));
        }

        // ---- 8. the existing Mother Plant Supervisor workflow keeps working ----

        [Fact]
        public void MotherPlantSupervisor_AssignedToTheArea_StillCanConfirm()
        {
            var supervisor = User(40, new[] { "MotherPlant.Enter", "MotherPlant.View" }, new[] { AreaA }, new[] { SupervisorRules.MotherPlantSupervisor });
            Assert.True(PotBatchRules.CanConfirmReady(supervisor, AreaA));
            Assert.False(PotBatchRules.CanConfirmReady(supervisor, AreaB));
        }

        [Fact]
        public void MotherPlantSupervisor_IsInTheReadyConfirmerList_AlongsideOtherAreaUsers()
        {
            var grants = new[]
            {
                Grant(40, "Meera (Mother Plant Supervisor)", AreaA, "MotherPlant.Enter", "MotherPlant.View"),
                Grant(41, "Omkar (Operator)", AreaA, "PotProduction.Enter"),
            };
            var names = PotBatchRules.EligibleReadyConfirmers(grants, AreaA, PotBatchRules.ReadyConfirmPermissions).Select(u => u.UserId);
            Assert.Equal(new[] { 40, 41 }, names.OrderBy(x => x));
        }

        // ---- 9. no cross-Area confirmation is possible -------------------------

        [Theory]
        [InlineData(AreaB)]
        [InlineData(AreaC)]
        [InlineData(0)]
        [InlineData(-1)]
        public void AUserOfAreaA_CanNeverConfirmAnyOtherAreasBatch(int otherArea)
            => Assert.False(PotBatchRules.CanConfirmReady(User(1, new[] { PotEnter, "MotherPlant.Enter", "Kiran.Enter" }, new[] { AreaA }), otherArea));

        [Fact]
        public void FullAccessUser_WithoutAreaAssignment_CannotConfirm()
        {
            // Area ASSIGNMENT is strict for the confirmation itself: the
            // cross-Area full-access convention (which lets such users VIEW
            // any Area) does not extend to confirming READY.
            var admin = User(1, Array.Empty<string>(), Array.Empty<int>(), new[] { "Admin" }, fullAccess: true);
            Assert.True(new AreaAccessService().CanAccessArea(admin, AreaA));
            Assert.False(PotBatchRules.CanConfirmReady(admin, AreaA));
        }

        [Fact]
        public void FullAccessUser_AssignedToTheArea_CanConfirmThere_ButNotElsewhere()
        {
            var admin = User(1, Array.Empty<string>(), new[] { AreaA }, fullAccess: true);
            Assert.True(PotBatchRules.CanConfirmReady(admin, AreaA));
            Assert.False(PotBatchRules.CanConfirmReady(admin, AreaB));
        }

        [Fact]
        public void PermissionsHeldForAnotherArea_DoNotWidenTheAreaScope()
        {
            // permission alone never widens Area scope (same principle as AreaAccessService)
            var user = User(1, new[] { PotEnter }, new[] { AreaB });
            Assert.False(PotBatchRules.CanConfirmReady(user, AreaA));
        }

        [Fact]
        public void UnidentifiedOrUnauthenticatedUser_CannotConfirm()
        {
            Assert.False(PotBatchRules.CanConfirmReady(User(null, new[] { PotEnter }, new[] { AreaA }), AreaA));
            var anonymous = new ClaimsPrincipal(new ClaimsIdentity());
            Assert.False(PotBatchRules.CanConfirmReady(anonymous, AreaA));
        }

        // ---- database-side check (inside the READY transaction) ------------------

        private static PotBatchRules.ReadyConfirmerGrant Grant(int userId, string name, int? area, params string[] permissions)
            => new(userId, name, area, true, permissions);

        [Fact]
        public void DbCheck_ActiveUserAssignedToTheBatchArea_IsAccepted_WhateverTheRole()
        {
            var grants = new[] { Grant(41, "Omkar", AreaA, "PotProduction.Enter") };
            Assert.True(PotBatchRules.IsActiveAssignedToArea(grants, 41, AreaA));
        }

        [Fact]
        public void DbCheck_TheBatchCreator_IsAccepted()
        {
            const int creator = 22;
            Assert.True(PotBatchRules.IsActiveAssignedToArea(new[] { Grant(creator, "Creator", AreaA) }, creator, AreaA));
        }

        [Fact]
        public void DbCheck_OtherArea_NoArea_UnknownUser_AndInactiveUser_AreRefused()
        {
            var grants = new[]
            {
                Grant(41, "Omkar", AreaA),
                Grant(42, "Nobody", null),
                new PotBatchRules.ReadyConfirmerGrant(43, "Left the company", AreaA, false, Array.Empty<string>()),
            };
            Assert.False(PotBatchRules.IsActiveAssignedToArea(grants, 41, AreaB));   // assigned to A only
            Assert.False(PotBatchRules.IsActiveAssignedToArea(grants, 42, AreaA));   // no Area
            Assert.False(PotBatchRules.IsActiveAssignedToArea(grants, 43, AreaA));   // inactive
            Assert.False(PotBatchRules.IsActiveAssignedToArea(grants, 99, AreaA));   // not a user
        }

        // ---- the "Ready Confirmation By" list ----------------------------------------

        [Fact]
        public void ReadyConfirmerList_HasOnlyActiveAuthorizedUsersOfThatArea()
        {
            var grants = new[]
            {
                Grant(1, "Asha (operator)", AreaA, "PotProduction.Enter"),
                Grant(2, "Bhau (supervisor)", AreaA, "MotherPlant.Enter"),
                Grant(3, "Chetan (view only)", AreaA, "PotProduction.View"),
                Grant(4, "Dipa (other Area)", AreaB, "PotProduction.Enter"),
                Grant(5, "Esha (no Area)", null, "PotProduction.Enter"),
                new PotBatchRules.ReadyConfirmerGrant(6, "Farid (inactive)", AreaA, false, new[] { "PotProduction.Enter" }),
            };
            var list = PotBatchRules.EligibleReadyConfirmers(grants, AreaA, PotBatchRules.ReadyConfirmPermissions);
            Assert.Equal(new[] { 1, 2 }, list.Select(u => u.UserId).ToArray());
        }

        [Fact]
        public void ReadyConfirmerList_PoolsPermissionsAcrossRoles_LikeTheLoginClaimsDo()
        {
            // Gita: role 1 assigns her to Area A (no permission), role 2 (any Area)
            // carries the permission -> claims give her both -> eligible.
            var grants = new[]
            {
                Grant(7, "Gita", AreaA, "Dashboard.View"),
                Grant(7, "Gita", null, "PotProduction.Enter"),
            };
            Assert.Single(PotBatchRules.EligibleReadyConfirmers(grants, AreaA, PotBatchRules.ReadyConfirmPermissions));
            Assert.Empty(PotBatchRules.EligibleReadyConfirmers(grants, AreaB, PotBatchRules.ReadyConfirmPermissions));
        }

        [Fact]
        public void ReadyConfirmerList_ListsEachUserOnce_SortedByName()
        {
            var grants = new[]
            {
                Grant(2, "Zed", AreaA, "PotProduction.Enter"),
                Grant(1, "amit", AreaA, "PotProduction.Enter"),
                Grant(1, "amit", AreaA, "MotherPlant.Enter"),
            };
            var names = PotBatchRules.EligibleReadyConfirmers(grants, AreaA, PotBatchRules.ReadyConfirmPermissions).Select(u => u.UserName).ToArray();
            Assert.Equal(new[] { "amit", "Zed" }, names);
        }

        [Fact]
        public void Create_RefusesAConfirmerWhoIsNotEligibleForTheArea()
        {
            var (ok, error) = PotBatchRules.ValidateCreate(8000, 10000, new DateTime(2026, 9, 1), new DateTime(2026, 10, 1), 99, new[] { 22, 40 });
            Assert.False(ok);
            Assert.Contains("not authorized", error);
        }

        // ---- wiring: the pages and repository use the new rule ---------------------------

        private static string Repo(params string[] parts)
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "PlantStockManager.sln")))
                dir = dir.Parent;
            return File.ReadAllText(Path.Combine(new[] { dir!.FullName }.Concat(parts).ToArray()));
        }

        [Fact]
        public void CreatePage_UsesTheNewLabel_AndDropsTheSupervisorRestrictionText()
        {
            var html = Repo("Pages", "Production", "PotBatch", "Create.cshtml");
            Assert.Contains("Ready Confirmation By", html);
            Assert.DoesNotContain("Supervisor (confirms READY)", html);
            Assert.DoesNotContain("A Mother Plant Supervisor of the Area, other than you.", html);
            Assert.DoesNotContain("other than you", html);
            Assert.DoesNotContain("Mother Plant Supervisor", html);
        }

        [Fact]
        public void CreatePage_ListsAreaUsers_NotOnlyMotherPlantSupervisors()
        {
            var type = typeof(PlantStockManager.Pages.Production.PotBatch.CreateModel);
            Assert.NotNull(type.GetMethod("OnGetReadyConfirmersAsync"));
            Assert.Null(type.GetMethod("OnGetSupervisorsAsync"));
            var cs = Repo("Pages", "Production", "PotBatch", "Create.cshtml.cs");
            Assert.DoesNotContain("MotherPlantSupervisor", cs);
            Assert.DoesNotContain("EmployeeID != me", cs);      // the creator is not excluded
        }

        [Fact]
        public void ConfirmReadyRepository_IsNotBoundToTheSupervisorRole_OrTheDesignatedPerson()
        {
            var repo = Repo("Data", "PotBatchRepository.cs");
            Assert.DoesNotContain("MotherPlantSupervisor", repo);
            // only the READY method: the (unchanged) cancel rule legitimately names the creator / designated user
            var start = repo.IndexOf("public async Task<(bool Success, string? Message)> ConfirmReadyAsync", StringComparison.Ordinal);
            var end = repo.IndexOf("public async Task<(bool Success, string? Message)> CancelAsync", StringComparison.Ordinal);
            Assert.True(start > 0 && end > start);
            var src = repo.Substring(start, end - start);
            Assert.DoesNotContain("createdById", src);
            Assert.DoesNotContain("supervisorId", src);
            Assert.Contains("IsActiveAssignedToArea", src);
            Assert.Contains("ReadyConfirmedById = @UserId", src);       // the ACTUAL confirmer is what is recorded
            Assert.Contains("ReadyDate = SYSUTCDATETIME()", src);
            Assert.Contains("ModifiedBy = @ModifiedBy", src);
        }

        [Fact]
        public void DetailsPage_ChecksTheRuleOnEveryReadyPost_AndNoLongerNamesASingleSupervisor()
        {
            var cs = Repo("Pages", "Production", "PotBatch", "Details.cshtml.cs");
            Assert.Contains("PotBatchRules.CanConfirmReady(User, batch.AreaId)", cs);
            Assert.Contains("!CanConfirmReady", cs);
            var html = Repo("Pages", "Production", "PotBatch", "Details.cshtml");
            Assert.DoesNotContain("Only @b.SupervisorName", html);
            Assert.DoesNotContain("Assigned Supervisor (confirms READY)", html);
        }

        [Fact]
        public void ReadyConfirmation_KeepsTheAreaAccessGateOnTheBatchPage()
        {
            // viewing / acting on a batch of another Area is still refused before any handler runs
            var cs = Repo("Pages", "Production", "PotBatch", "Details.cshtml.cs");
            Assert.Contains("!_areaAccess.CanAccessArea(User, batch.AreaId)", cs);
        }

        [Fact]
        public void ReadyPost_StillBindsNoAreaOrSupervisorInputs()
        {
            var bound = typeof(PlantStockManager.Pages.Production.PotBatch.DetailsModel)
                .GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(p => p.GetCustomAttribute<BindPropertyAttribute>() != null)
                .Select(p => p.Name).ToList();
            Assert.DoesNotContain(bound, n => n.Contains("Area") || n.Contains("Supervisor") || n.Contains("Confirm"));
        }

        // ---- the (not yet applied) database migration ---------------------------------------

        [Fact]
        public void Migration_ReplacesTheRoleBoundDbRules_KeepsTheAreaBoundary_AndIsGuarded()
        {
            var sql = Repo("Database", "Migrations", "2026-09-28_PotBatchReadyConfirmByAreaUser.sql");
            Assert.Contains("IF DB_NAME() <> N'PlantsIMS2_Test'", sql);
            Assert.Contains("DROP CONSTRAINT CK_PotBatches_ConfirmedBySupervisor", sql);
            Assert.Contains("DROP CONSTRAINT CK_PotBatches_SupervisorNotCreator", sql);
            Assert.Contains("CREATE OR ALTER TRIGGER dbo.TR_PotBatches_Insert", sql);
            Assert.Contains("CREATE OR ALTER TRIGGER dbo.TR_PotBatches_Update", sql);
            Assert.Contains("ur.AreaId = i.AreaId", sql);                       // Area boundary kept
            Assert.Contains("ISNULL(u.IsActive, 0) = 1", sql);
            Assert.DoesNotContain("= N''Mother Plant Supervisor''", sql);
            // approved and applied 2026-09-28: it ends in COMMIT; the only ROLLBACK left is the error path in CATCH
            Assert.Matches(@"(?m)^COMMIT TRANSACTION PotBatchReadyConfirm;", sql);
            Assert.Equal(1, System.Text.RegularExpressions.Regex.Matches(sql, @"(?m)^\s*(?!--).*\bROLLBACK TRANSACTION\b").Count);
            Assert.Contains("IF XACT_STATE() <> 0 ROLLBACK TRANSACTION PotBatchReadyConfirm;", sql);
            Assert.DoesNotContain("ALTER TABLE dbo.PotProductionBatches ADD", sql);   // no new column
        }
    }
}
