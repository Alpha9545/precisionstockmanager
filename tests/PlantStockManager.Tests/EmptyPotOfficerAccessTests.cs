using System.Linq;

namespace PlantStockManager.Tests
{
    // Phase 2 (Empty Pot Inventory / Area-specific Pot Issue): the audit
    // found that the 'Purchase.View'/'Purchase.Enter' permission codes
    // (Phase 14) gating every purchase/Empty-Pot-stock-in page had never
    // been granted to any operational role. Database/Phase27_
    // MainOfficeOfficerPurchaseAccess.sql grants both to MainOfficeOfficer
    // (the existing "Office Officer" role). These tests check the script
    // itself, not a live database (no database is reachable from here) --
    // they guard against the grant being edited away or the idempotent
    // guard being dropped by a future change.
    public class EmptyPotOfficerAccessTests
    {
        private static string ScriptText()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "PlantStockManager.csproj")))
                dir = dir.Parent;
            if (dir == null)
                throw new InvalidOperationException("PlantStockManager.csproj not found above the test output folder.");
            return File.ReadAllText(Path.Combine(dir.FullName, "Database", "Phase27_MainOfficeOfficerPurchaseAccess.sql"));
        }

        [Fact]
        public void Script_GrantsBothPurchasePermissions_ToMainOfficeOfficer()
        {
            var sql = ScriptText();
            Assert.Contains("r.Name = 'MainOfficeOfficer'", sql);
            Assert.Contains("'Purchase.View'", sql);
            Assert.Contains("'Purchase.Enter'", sql);
        }

        [Fact]
        public void Script_IsGuardedAgainstDuplicateGrants()
        {
            var sql = ScriptText();
            // The same idempotent pattern as every other Phase 14 grant --
            // re-running the script must not attempt (or fail on) a
            // duplicate INSERT into dbo.RolePermissions.
            Assert.Contains("NOT EXISTS (SELECT 1 FROM dbo.RolePermissions rp WHERE rp.RoleId = r.Id AND rp.PermissionId = p.Id)", sql);
        }

        [Fact]
        public void Script_NeverRevokesOrAltersAnything()
        {
            var sql = ScriptText();
            Assert.DoesNotContain("DELETE", sql, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("UPDATE", sql, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("DROP", sql, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("ALTER TABLE", sql, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("CREATE TABLE", sql, StringComparison.OrdinalIgnoreCase);
        }

        // Documents the finding itself: before this phase, only Admin (every
        // permission) and Management (every '*.View') could reach the
        // purchase / Empty Pot stock-in pages -- confirmed by reading every
        // grant of Purchase.View/Purchase.Enter across the whole Database/
        // folder as it stood at the start of Phase 2.
        [Fact]
        public void Baseline_NoOperationalRoleGrantedPurchasePermissions_BeforeThisScript()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "PlantStockManager.csproj")))
                dir = dir.Parent;
            var databaseDir = Path.Combine(dir!.FullName, "Database");

            var operationalRoles = new[] { "MainOfficeOfficer", "MotherPlantSupervisor", "KunjirSupervisor", "KiranSupervisor", "OutletSupervisor", "LabWorker" };
            var grantsPurchaseToOperationalRole = false;
            foreach (var file in Directory.GetFiles(databaseDir, "*.sql"))
            {
                if (Path.GetFileName(file) == "Phase27_MainOfficeOfficerPurchaseAccess.sql")
                    continue; // this phase's own (intentional) grant
                // Each `... GO` block is one statement -- a role's grant
                // block and the Phase 14 permission-seed VALUES list (which
                // also mentions 'Purchase.View'/'Purchase.Enter', just to
                // define the codes) must not be confused for each other.
                foreach (var statement in File.ReadAllText(file).Split("GO", StringSplitOptions.None))
                {
                    if ((statement.Contains("'Purchase.View'") || statement.Contains("'Purchase.Enter'"))
                        && operationalRoles.Any(role => statement.Contains($"r.Name = '{role}'")))
                    {
                        grantsPurchaseToOperationalRole = true;
                    }
                }
            }
            Assert.False(grantsPurchaseToOperationalRole, "An operational role already had Purchase.View/Purchase.Enter before Phase 27 -- update this test's assumptions.");
        }
    }
}
