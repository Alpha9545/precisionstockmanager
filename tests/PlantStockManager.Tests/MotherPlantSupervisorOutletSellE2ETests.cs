using Microsoft.Data.SqlClient;

namespace PlantStockManager.Tests
{
    // Issue 2: DB-level proof that Database/Migrations/
    // 2026-10-01_MotherPlantSupervisorOutletSell.sql was applied correctly --
    // read-only, against a SCRATCH COPY of the test database (same safety
    // rules as every other E2E class in this project: PSM_SCRATCH_CONNECTION,
    // database name must start with PlantsIMS2_Scratch_). Never touches
    // PlantsIMS2_Test directly.
    [Collection("ScratchDb")]
    public class MotherPlantSupervisorOutletSellE2ETests
    {
        private const string ConnectionVariable = "PSM_SCRATCH_CONNECTION";
        private const string RequiredPrefix = "PlantsIMS2_Scratch_";

        private static async Task<SqlConnection> OpenAsync()
        {
            var cs = Environment.GetEnvironmentVariable(ConnectionVariable);
            Skip.If(string.IsNullOrWhiteSpace(cs), $"Set {ConnectionVariable} to a {RequiredPrefix}* database connection string to run the end-to-end tests.");
            var conn = new SqlConnection(cs);
            await conn.OpenAsync();
            using var nameCmd = new SqlCommand("SELECT DB_NAME()", conn);
            var name = (string)(await nameCmd.ExecuteScalarAsync())!;
            if (!name.StartsWith(RequiredPrefix, StringComparison.Ordinal))
                throw new InvalidOperationException($"Refusing to run end-to-end tests against '{name}'. Only databases named {RequiredPrefix}* are allowed.");
            return conn;
        }

        private static async Task<List<string>> PermissionsForRoleAsync(SqlConnection conn, string roleName)
        {
            var list = new List<string>();
            using var cmd = new SqlCommand(@"
SELECT p.Code
FROM dbo.RolePermissions rp
JOIN dbo.Roles r ON r.Id = rp.RoleId
JOIN dbo.Permissions p ON p.Id = rp.PermissionId
WHERE COALESCE(NULLIF(LTRIM(RTRIM(r.Name)), N''), r.RoleName) = @RoleName
ORDER BY p.Code", conn);
            cmd.Parameters.AddWithValue("@RoleName", roleName);
            using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
                list.Add(reader.GetString(0));
            return list;
        }

        [SkippableFact]
        public async Task MotherPlantSupervisor_HasOutletSell_Live()
        {
            using var conn = await OpenAsync();
            var permissions = await PermissionsForRoleAsync(conn, "Mother Plant Supervisor");
            Assert.Contains("Outlet.Sell", permissions);
        }

        [SkippableFact]
        public async Task MotherPlantSupervisor_DoesNotHaveOutletView_NotGrantedByThisFix()
        {
            using var conn = await OpenAsync();
            var permissions = await PermissionsForRoleAsync(conn, "Mother Plant Supervisor");
            Assert.DoesNotContain("Outlet.View", permissions);
        }

        [SkippableFact]
        public async Task MotherPlantSupervisor_RetainsAllItsPriorPermissions()
        {
            using var conn = await OpenAsync();
            var permissions = await PermissionsForRoleAsync(conn, "Mother Plant Supervisor");
            var expectedPriorPermissions = new[]
            {
                "Dashboard.View", "MotherPlant.View", "MotherPlant.Enter",
                "InternalTransfer.View", "InternalTransfer.Enter", "PotProduction.View",
                "ReadyStock.Confirm", "ReadyStock.View"
            };
            foreach (var p in expectedPriorPermissions)
                Assert.Contains(p, permissions);
            // exactly the prior 8 plus the one new grant -- nothing else slipped in
            Assert.Equal(9, permissions.Count);
        }

        [SkippableFact]
        public async Task OtherRoles_PermissionSets_AreUnaffectedByThisFix()
        {
            using var conn = await OpenAsync();
            // Known, pre-existing permission counts (as seeded by Database/PhaseA_RoleBasedAccess.sql
            // and later migrations, none of which this fix touches) -- a regression guard that this
            // migration never widened ANY role other than Mother Plant Supervisor.
            var expectedCounts = new Dictionary<string, int>
            {
                ["Booking Executive"] = 4,
                ["Dispatch Executive"] = 5,
                ["Fertilizer Supervisor"] = 3,
                ["Office Coordinator"] = 7,
                ["Outlet Sales"] = 5,
                ["Pot Production Operator"] = 5,
                ["Purchase Officer"] = 3,
                ["Sowing Supervisor"] = 6,
                ["Main Office Store Keeper"] = 13,
                ["Sowing Operator"] = 10,
            };
            foreach (var (role, expectedCount) in expectedCounts)
            {
                var permissions = await PermissionsForRoleAsync(conn, role);
                Assert.Equal(expectedCount, permissions.Count);
            }
        }
    }
}
