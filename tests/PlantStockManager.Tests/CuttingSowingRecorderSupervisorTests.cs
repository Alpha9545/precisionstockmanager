using PlantStockManager.Services;

namespace PlantStockManager.Tests
{
    // Cutting Tray Sowing: the recorder may be chosen as its Sowing Supervisor and,
    // as that assigned supervisor, approve it -- if they otherwise meet the normal
    // rules (active + assigned to the sowing's Area + ReadyStock.Confirm).
    // Direct SEED sowing keeps "the recorder cannot be its supervisor / approve it".
    public class CuttingSowingRecorderSupervisorTests
    {
        private const int Area1 = 1, Area2 = 2, Area122 = 122;
        private const string Approve = "ReadyStock.Confirm";

        private static AreaUserGrant G(int id, string name, int? area, string[]? perms = null, bool active = true)
            => new(id, name, area, active, perms ?? new[] { Approve });

        private static int[] Ids(IEnumerable<(int UserId, string UserName)> l) => l.Select(u => u.UserId).OrderBy(x => x).ToArray();

        // ---- selection --------------------------------------------------------------------------------

        [Fact]
        public void SameAreaAuthorizedUser_CanSelectThemselves()
        {
            // "I am a Sowing Supervisor, assigned to Area 1, I hold ReadyStock.Confirm; I record a sowing for Area 1"
            const int me = 6;
            var grants = new[] { G(me, "Me", Area1), G(9, "Colleague", Area1) };
            var list = SowingSupervisorRules.EligibleForArea(grants, Area1);
            Assert.Contains(me, Ids(list));                                                  // I appear in the dropdown
            Assert.True(SowingSupervisorRules.ValidateAssignment(me, list.Select(u => u.UserId).ToList()).Ok);   // and can save
        }

        [Fact]
        public void SameAreaMotherPlantSupervisor_CanSelectThemselves()
        {
            var mps = new[] { "Dashboard.View", "MotherPlant.View", "MotherPlant.Enter", "PotProduction.View", "ReadyStock.View", Approve };
            var grants = new[] { G(113, "Rahul", Area1, mps), G(115, "Kiran", Area1, mps) };
            var eligible = SowingSupervisorRules.EligibleForArea(grants, Area1).Select(u => u.UserId).ToList();
            Assert.True(SowingSupervisorRules.ValidateAssignment(113, eligible).Ok);
        }

        [Fact]
        public void SameAreaAuthorizedUser_CanStillSelectAnotherEligibleSupervisor()
        {
            var grants = new[] { G(6, "Me", Area1), G(9, "Colleague", Area1), G(13, "Other", Area1) };
            var eligible = SowingSupervisorRules.EligibleForArea(grants, Area1).Select(u => u.UserId).ToList();
            Assert.True(SowingSupervisorRules.ValidateAssignment(9, eligible).Ok);
            Assert.True(SowingSupervisorRules.ValidateAssignment(13, eligible).Ok);
            Assert.Equal(new[] { 6, 9, 13 }, eligible.OrderBy(x => x).ToArray());
        }

        [Fact]
        public void DifferentAreaUser_IsNotShown_AndIsRefused_EvenForTheRecorder()
        {
            // I am assigned to Area 122 only, but the sowing is in Area 1
            var grants = new[] { G(101, "Me", Area122), G(9, "Colleague", Area1) };
            var list = SowingSupervisorRules.EligibleForArea(grants, Area1);
            Assert.DoesNotContain(101, Ids(list));
            Assert.False(SowingSupervisorRules.ValidateAssignment(101, list.Select(u => u.UserId).ToList()).Ok);
        }

        [Fact]
        public void InactiveUser_IsNotShown_AndIsRefused()
        {
            var grants = new[] { G(6, "Me", Area1, active: false), G(9, "Colleague", Area1) };
            var list = SowingSupervisorRules.EligibleForArea(grants, Area1);
            Assert.DoesNotContain(6, Ids(list));
            Assert.False(SowingSupervisorRules.ValidateAssignment(6, list.Select(u => u.UserId).ToList()).Ok);
        }

        [Fact]
        public void UserWithoutReadyStockConfirm_IsNotShown_AndIsRefused_EvenForTheRecorder()
        {
            var grants = new[] { G(2, "Me (no approval permission)", Area1, new[] { "Dispatch.Enter", "ReadyStock.View" }), G(9, "Colleague", Area1) };
            var list = SowingSupervisorRules.EligibleForArea(grants, Area1);
            Assert.DoesNotContain(2, Ids(list));
            Assert.False(SowingSupervisorRules.ValidateAssignment(2, list.Select(u => u.UserId).ToList()).Ok);
        }

        [Fact]
        public void TheEligibilityRule_NoLongerKnowsWhoTheRecorderIs()
        {
            // the API takes no recorder at all: there is nothing left to exclude
            var eligible = typeof(SowingSupervisorRules).GetMethod("EligibleForArea")!;
            Assert.DoesNotContain(eligible.GetParameters(), p => p.Name!.Contains("recorder", StringComparison.OrdinalIgnoreCase));
            var validate = typeof(SowingSupervisorRules).GetMethod("ValidateAssignment")!;
            Assert.DoesNotContain(validate.GetParameters(), p => p.Name!.Contains("recorder", StringComparison.OrdinalIgnoreCase));
        }

        // ---- approval: the recorder-supervisor can approve their own CUTTING sowing --------------------

        [Fact]
        public void CuttingSowing_RecorderWhoIsTheAssignedSupervisor_CanApprove()
            => Assert.True(DirectSowingRules.CanApprove(assignedSupervisorId: 6, createdById: 6, createdBy: "Akshay", approverId: 6, approverName: "Akshay", sourceType: "Cutting").Ok);

        [Fact]
        public void CuttingSowing_AnotherSupervisorAssigned_RecorderStillCannotApprove()
        {
            // being the recorder gives no authority of its own: only the ASSIGNED supervisor approves
            var (ok, error) = DirectSowingRules.CanApprove(assignedSupervisorId: 9, createdById: 6, createdBy: "Akshay", approverId: 6, approverName: "Akshay", sourceType: "Cutting");
            Assert.False(ok);
            Assert.Equal(DirectSowingRules.NotAssignedMessage, error);
        }

        [Fact]
        public void CuttingSowing_AssignedSupervisorWhoDidNotRecordIt_CanApprove_AsBefore()
            => Assert.True(DirectSowingRules.CanApprove(9, 6, "Akshay", 9, "Maya", "Cutting").Ok);

        [Fact]
        public void CuttingSowing_NoSupervisor_OrAnonymous_IsStillRefused()
        {
            Assert.False(DirectSowingRules.CanApprove(null, 6, "Akshay", 6, "Akshay", "Cutting").Ok);
            Assert.False(DirectSowingRules.CanApprove(6, 6, "Akshay", null, "Akshay", "Cutting").Ok);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("Seed")]
        public void SeedSowing_RecorderStillCannotApproveTheirOwnSowing_Unchanged(string? sourceType)
        {
            var (ok, error) = DirectSowingRules.CanApprove(6, 6, "Akshay", 6, "Akshay", sourceType);
            Assert.False(ok);
            Assert.Equal(DirectSowingRules.OwnSowingMessage, error);
        }

        [Fact]
        public void SeedSowing_RecorderStillCannotBeItsSupervisor_Unchanged()
        {
            var (ok, error) = DirectSowingRules.ValidateSupervisorAssignment(6, 6, new[] { 6, 9 });
            Assert.False(ok);
            Assert.Contains("cannot assign yourself", error);
        }

        // ---- wiring: seed untouched, cutting paths carry the sowing's source --------------------------------

        private static string Repo(params string[] parts)
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "PlantStockManager.sln")))
                dir = dir.Parent;
            return File.ReadAllText(Path.Combine(new[] { dir!.FullName }.Concat(parts).ToArray()));
        }

        [Fact]
        public void SeedSowingPage_StillExcludesTheRecorder_FromItsSupervisorList()
        {
            var cs = Repo("Pages", "Production", "SeedSowing", "Create.cshtml.cs");
            Assert.Contains("GetSowingApproversAsync()).Where(a => a.EmployeeID != me)", cs);
            Assert.Contains("DirectSowingRules.ValidateSupervisorAssignment(", cs);
            var repo = Repo("Data", "SeedSowingRepository.cs");
            Assert.Contains("DirectSowingRules.ValidateSupervisorAssignment(", repo);      // seed insert unchanged
        }

        [Fact]
        public void CuttingSowingPage_AndRepository_NoLongerPassTheRecorderToTheSupervisorRules()
        {
            var page = Repo("Pages", "Production", "SeedSowing", "CreateFromCutting.cshtml.cs");
            Assert.DoesNotContain("User.GetUserId(), eligible", page);
            Assert.DoesNotContain("GetCuttingSowingSupervisorsAsync(growingAreaId.Value, User.GetUserId())", page);
            var repo = Repo("Data", "SeedSowingRepository.cs");
            Assert.DoesNotContain("GetCuttingSowingSupervisorsAsync(growingAreaId, entry.CreatedById", repo);
            Assert.Contains("ValidateAssignment(entry.SupervisorId, supervisorCandidates)", repo);
        }

        [Fact]
        public void EveryApprovalCheck_PassesTheSowingsSourceType()
        {
            var confirm = Repo("Data", "ReadyConfirmationRepository.cs");
            Assert.Contains("SupervisorId, SourceType", confirm);
            Assert.Contains("createdBy, sowingSourceType)", confirm);
            var page = Repo("Pages", "Production", "ReadyConfirmation", "Confirm.cshtml.cs");
            Assert.Equal(2, System.Text.RegularExpressions.Regex.Matches(page, @"sowing\.SourceType\)").Count);
            var index = Repo("Pages", "Production", "ReadyConfirmation", "Index.cshtml");
            Assert.Contains("User.Identity?.Name, sw.SourceType)", index);
        }

        // ---- the database migration (written; dry run first, not applied) -----------------------------------

        [Fact]
        public void Migration_ChangesExactlyTwoTriggers_AndNothingElse()
        {
            var sql = Repo("Database", "Migrations", "2026-09-28_CuttingSowingRecorderAsSupervisor.sql");
            Assert.Contains("IF DB_NAME() <> N'PlantsIMS2_Test'", sql);
            Assert.Equal(2, System.Text.RegularExpressions.Regex.Matches(sql, "CREATE OR ALTER TRIGGER").Count);
            Assert.Contains("CREATE OR ALTER TRIGGER dbo.TR_SeedSowings_SupervisorRole", sql);
            Assert.Contains("CREATE OR ALTER TRIGGER dbo.TR_ReadyConfirmations_AssignedSupervisor", sql);
            foreach (var forbidden in new[] { "INSERT INTO dbo.", "UPDATE dbo.", "DELETE FROM", "ALTER TABLE", "CREATE TABLE", "DROP ", "TRUNCATE", "sp_rename", "RolePermissions (" })
                Assert.DoesNotContain(forbidden, sql);
            // approved and applied 2026-09-28: it ends in COMMIT; the only ROLLBACK left is the error path in CATCH
            Assert.Matches(@"(?m)^COMMIT TRANSACTION CuttingSowingRecorderSupervisor;", sql);
            Assert.Single(System.Text.RegularExpressions.Regex.Matches(sql, @"(?m)^[^-\r\n].*\bROLLBACK TRANSACTION\b"));
            Assert.Contains("IF XACT_STATE() <> 0 ROLLBACK TRANSACTION CuttingSowingRecorderSupervisor;", sql);
        }

        [Fact]
        public void Migration_CuttingBranchDropsTheRecorderClause_SeedBranchKeepsIt_AndEligibilityStays()
        {
            var sql = Repo("Database", "Migrations", "2026-09-28_CuttingSowingRecorderAsSupervisor.sql");
            var cuttingStart = sql.IndexOf("-- CUTTING TRAY sowing", StringComparison.Ordinal);
            var cuttingEnd = sql.IndexOf("END');", cuttingStart, StringComparison.Ordinal);
            var cutting = sql.Substring(cuttingStart, cuttingEnd - cuttingStart);
            var seed = sql.Substring(sql.IndexOf("-- SEED sowing (unchanged)", StringComparison.Ordinal), cuttingStart - sql.IndexOf("-- SEED sowing (unchanged)", StringComparison.Ordinal));
            Assert.DoesNotContain("CreatedById", cutting);                                       // recorder not excluded
            Assert.Contains("i.SupervisorId = i.CreatedById", seed);                              // seed: still excluded
            Assert.Contains("= N''Sowing Supervisor''", seed);                                    // seed: role rule intact
            // cutting: still active + Area-assigned + approval permission + present
            Assert.Contains("i.SupervisorId IS NULL", cutting);
            Assert.Contains("ur.AreaId = i.AreaId", cutting);
            Assert.Contains("ISNULL(u.IsActive, 0) = 1", cutting);
            Assert.Contains("p.Code = N''ReadyStock.Confirm''", cutting);
        }

        [Fact]
        public void Migration_ApprovalTrigger_StillRequiresTheAssignedSupervisor_RecorderRuleSeedOnly()
        {
            var sql = Repo("Database", "Migrations", "2026-09-28_CuttingSowingRecorderAsSupervisor.sql");
            var start = sql.IndexOf("CREATE OR ALTER TRIGGER dbo.TR_ReadyConfirmations_AssignedSupervisor", StringComparison.Ordinal);
            var body = sql.Substring(start, sql.IndexOf("END');", start, StringComparison.Ordinal) - start);
            Assert.Contains("i.ApprovedById IS NULL", body);
            Assert.Contains("sw.SupervisorId IS NULL", body);
            Assert.Contains("i.ApprovedById <> sw.SupervisorId", body);                           // only the ASSIGNED supervisor
            Assert.Contains("sw.SourceType <> N''Cutting'' AND sw.CreatedById IS NOT NULL AND i.ApprovedById = sw.CreatedById", body);
        }
    }
}
