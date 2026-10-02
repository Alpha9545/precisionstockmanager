using System.Text.RegularExpressions;
using PlantStockManager.Authorization;

namespace PlantStockManager.Tests
{
    // Issue 2: Mother Plant Supervisor could not use /Production/OutletSale/Create
    // ("Direct Customer Sale" -- send pots to customers). Root cause: that page
    // requires the permission 'Outlet.Sell' (FeatureAuthorizationConventions),
    // which dbo.RolePermissions never granted to this role. Fix is a single,
    // idempotent permission-grant migration -- no C# authorization code changed,
    // because the route rule itself was already correct; only the role's
    // permission SET was missing one entry. These tests verify the route rule
    // (unchanged) and the migration's shape/safety (written, dry-run first,
    // approved and applied 2026-10-01). DB-level proof that the grant actually
    // exists live is in MotherPlantSupervisorOutletSellE2ETests.cs.
    public class MotherPlantSupervisorOutletSellTests
    {
        // ---- the route rule itself: unchanged, confirmed exactly what's required ----

        [Fact]
        public void OutletSaleCreate_Requires_OutletSell_Only()
        {
            var rule = FeatureAuthorizationConventions.GetRule("/Production/OutletSale/Create");
            Assert.Equal("Outlet.Sell", rule.Read);
        }

        [Fact]
        public void OutletSaleIndex_And_Details_StillRequire_OutletView_Unchanged()
        {
            // Regression guard: this change must not have touched the View-only
            // pages' own requirement, and must not have granted Outlet.View to
            // Mother Plant Supervisor as a side effect (see the migration test below).
            Assert.Equal("Outlet.View", FeatureAuthorizationConventions.GetRule("/Production/OutletSale/Index").Read);
            Assert.Equal("Outlet.View", FeatureAuthorizationConventions.GetRule("/Production/OutletSale/Details").Read);
        }

        // ---- the database migration (written, dry-run first, then approved and applied) ----

        private static string Repo(params string[] parts)
        {
            var dir = AppContext.BaseDirectory;
            while (dir != null && !Directory.GetFiles(dir, "*.sln").Any())
                dir = Path.GetDirectoryName(dir);
            Assert.NotNull(dir);
            return File.ReadAllText(Path.Combine(new[] { dir! }.Concat(parts).ToArray()));
        }

        private static string MigrationSql() => Repo("Database", "Migrations", "2026-10-01_MotherPlantSupervisorOutletSell.sql");

        [Fact]
        public void Migration_IsGuardedToTestDatabase_AndTouchesNoSchemaOrUnrelatedData()
        {
            var sql = MigrationSql();
            Assert.Contains("IF DB_NAME() <> N'PlantsIMS2_Test'", sql);
            foreach (var forbidden in new[] { "ALTER TABLE", "CREATE TABLE", "DROP TABLE", "DROP CONSTRAINT",
                "DELETE FROM", "UPDATE dbo.", "TRUNCATE", "sp_rename", "CREATE TRIGGER", "ALTER TRIGGER" })
                Assert.DoesNotContain(forbidden, sql);
            // the only data write anywhere in the script: one RolePermissions grant
            Assert.Single(Regex.Matches(sql, @"INSERT INTO dbo\."));
            Assert.Single(Regex.Matches(sql, @"INSERT INTO dbo\.RolePermissions \(RoleId, PermissionId\)"));
        }

        [Fact]
        public void Migration_GrantsExactlyOnePermission_ToExactlyTheMotherPlantSupervisorRole_Idempotently()
        {
            var sql = MigrationSql();
            var start = sql.IndexOf("INSERT INTO dbo.RolePermissions", StringComparison.Ordinal);
            var block = sql.Substring(start, sql.IndexOf("@GrantsAdded", start, StringComparison.Ordinal) - start);

            Assert.Contains("= N'Mother Plant Supervisor'", block);          // exactly one role, by name
            Assert.Contains("p.Code = N'Outlet.Sell'", block);               // exactly one permission, by code
            Assert.DoesNotContain("Outlet.View", block);                    // NOT granted -- not asked for
            Assert.Contains("NOT EXISTS (SELECT 1 FROM dbo.RolePermissions rp WHERE rp.RoleId = r.Id AND rp.PermissionId = p.Id)", block);

            // No other role name appears anywhere the grant is scoped -- only
            // Mother Plant Supervisor's permission set can change.
            foreach (var otherRole in new[] { "Sowing Supervisor", "Fertilizer Supervisor", "Office Coordinator",
                "Dispatch Executive", "Booking Executive", "Outlet Sales", "Pot Production Operator", "Purchase Officer",
                "Main Office Store Keeper", "Sowing Operator" })
                Assert.DoesNotContain($"N'{otherRole}'", block);
        }

        [Fact]
        public void Migration_AbortsRatherThanGuesses_IfRoleOrPermissionAreMissingOrAmbiguous()
        {
            var sql = MigrationSql();
            Assert.Contains("<> 1", sql);          // "exactly one role" / "exactly one permission" checks
            Assert.Contains("THROW 51202", sql);   // role not found/ambiguous
            Assert.Contains("THROW 51203", sql);   // permission not found
        }

        [Fact]
        public void Migration_IsTransactional_AndApproved()
        {
            var sql = MigrationSql();
            Assert.Contains("BEGIN TRANSACTION MotherPlantSupervisorOutletSell;", sql);
            // approved and applied 2026-10-01: ends in COMMIT; the only ROLLBACK left is the error path in CATCH
            Assert.Matches(@"(?m)^COMMIT TRANSACTION MotherPlantSupervisorOutletSell;", sql);
            Assert.Single(Regex.Matches(sql, @"(?m)^[^-\r\n].*\bROLLBACK TRANSACTION\b"));
            Assert.Contains("IF XACT_STATE() <> 0 ROLLBACK TRANSACTION MotherPlantSupervisorOutletSell;", sql);
        }
    }
}
