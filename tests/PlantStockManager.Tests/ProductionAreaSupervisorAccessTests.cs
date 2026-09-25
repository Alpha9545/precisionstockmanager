using System.Linq;

namespace PlantStockManager.Tests
{
    // Phase 3 (Mother Plant and Cutting Production): the audit found that
    // only KiranSupervisor (of the three cutting-producing roles) could
    // reach '/Production/PotProduction/CreateFromCutting' -- path B of the
    // business workflow ("assign cutting to the appropriate Area -> use it
    // for potted plant production"). Database/Phase28_
    // ProductionAreaSupervisorPotProductionAccess.sql grants
    // MotherPlantSupervisor and KunjirSupervisor the same PotProduction.
    // View/Enter pair KiranSupervisor already has. These tests check the
    // script itself, not a live database (none is reachable from here).
    public class ProductionAreaSupervisorAccessTests
    {
        private static string RepoRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "PlantStockManager.csproj")))
                dir = dir.Parent;
            return dir?.FullName ?? throw new InvalidOperationException("PlantStockManager.csproj not found above the test output folder.");
        }

        private static string ScriptText()
            => File.ReadAllText(Path.Combine(RepoRoot(), "Database", "Phase28_ProductionAreaSupervisorPotProductionAccess.sql"));

        [Fact]
        public void Script_GrantsBothPotProductionPermissions_ToBothRoles()
        {
            var sql = ScriptText();
            Assert.Contains("'MotherPlantSupervisor'", sql);
            Assert.Contains("'KunjirSupervisor'", sql);
            Assert.Contains("'PotProduction.View'", sql);
            Assert.Contains("'PotProduction.Enter'", sql);
        }

        [Fact]
        public void Script_IsGuardedAgainstDuplicateGrants()
        {
            Assert.Contains(
                "NOT EXISTS (SELECT 1 FROM dbo.RolePermissions rp WHERE rp.RoleId = r.Id AND rp.PermissionId = p.Id)",
                ScriptText());
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

        // Documents the finding itself: before this phase, only
        // KiranSupervisor (of the three cutting-producing roles) held
        // PotProduction.View/Enter -- confirmed by reading every grant of
        // those codes across the whole Database/ folder as it stood at the
        // start of Phase 3.
        [Fact]
        public void Baseline_OnlyKiranSupervisorHadPotProductionAccess_BeforeThisScript()
        {
            var databaseDir = Path.Combine(RepoRoot(), "Database");
            var rolesGranted = new HashSet<string>();
            foreach (var file in Directory.GetFiles(databaseDir, "*.sql"))
            {
                if (Path.GetFileName(file) == "Phase28_ProductionAreaSupervisorPotProductionAccess.sql")
                    continue; // this phase's own (intentional) grant
                foreach (var statement in File.ReadAllText(file).Split("GO", StringSplitOptions.None))
                {
                    if (!statement.Contains("'PotProduction.View'") && !statement.Contains("'PotProduction.Enter'"))
                        continue;
                    // The statement already mentions a PotProduction permission
                    // code -- any role name literal in the SAME statement is
                    // the one being granted it (r.Name = 'X' or r.Name IN (...)).
                    foreach (var role in new[] { "MotherPlantSupervisor", "KunjirSupervisor", "KiranSupervisor", "MainOfficeOfficer", "OutletSupervisor", "LabWorker" })
                        if (statement.Contains($"'{role}'"))
                            rolesGranted.Add(role);
                }
            }
            Assert.Equal(new[] { "KiranSupervisor" }, rolesGranted.OrderBy(r => r));
        }
    }
}
