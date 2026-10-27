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
            // (+ Labour.View / Labour.Enter, 2026-10-04_DailyLabourCounts.sql)
            // (+ Maintenance.Raise / Maintenance.View, 2026-10-06_MaintenanceTickets.sql)
            // (+ Store.Request / Store.View, 2026-10-06_StoreInventory.sql)
            // (+ ReadyStock.ReceiveTransfer / Transplant.Enter / Transplant.View, 2026-10-09_ReadyStockTransplanting.sql)
            foreach (var p in new[] { "ReadyStock.ReceiveTransfer", "Transplant.Enter", "Transplant.View" })
                Assert.Contains(p, permissions);
            Assert.DoesNotContain("Transplant.Reverse", permissions);              // reversal stays full-access only
            Assert.DoesNotContain("ReadyStock.TransferToMotherPlant", permissions); // receivers do not send
            // (+ TrayStock.ReceiveTransfer, 2026-10-10_TrayTransfers.sql: receives empty-tray transfers; never sends or adjusts)
            var trayTransfers = await TrayTransfersAppliedAsync(conn);
            if (trayTransfers)
                Assert.Contains("TrayStock.ReceiveTransfer", permissions);
            // (+ TrayStock.ViewMotherPlant, 2026-10-11_TrayAccessByLocation.sql: READ-ONLY trays of own Mother Plant Areas)
            var alignment = await TrayAccessAppliedAsync(conn);
            if (alignment)
                Assert.Contains("TrayStock.ViewMotherPlant", permissions);
            Assert.DoesNotContain("TrayStock.ReceiveTransferMainArea", permissions);   // receives only in Mother Plant Areas
            // (+ TrayStock.Enter, 2026-10-16_MotherPlantSupervisorTrayEnter.sql, owner approval 2026-10-12: empty trays for his OWN
            //  Mother Plant Area(s) only -- TrayAreaScope; MotherPlantSupervisorTrayEnterTests)
            var trayEnter = await TrayEnterGrantedAsync(conn);
            if (trayEnter)
                Assert.Contains("TrayStock.Enter", permissions);
            else
                Assert.DoesNotContain("TrayStock.Enter", permissions);
            // never: adjusting or sending trays, trays for every Mother Plant Area, or growing material
            foreach (var p in new[] { "TrayStock.Transfer", "TrayStock.Adjust", "TrayStock.EnterMotherPlant", "MaterialStock.Add", "MaterialStock.View" })
                Assert.DoesNotContain(p, permissions);
            // (+ ReadyStock.Overgrowth, 2026-10-24_ReadyStockOvergrowth.sql: mark / unmark Overgrowth batches in his own Area)
            var overgrowth = await OvergrowthGrantedAsync(conn, "Mother Plant Supervisor");
            if (overgrowth)
                Assert.Contains("ReadyStock.Overgrowth", permissions);
            else
                Assert.DoesNotContain("ReadyStock.Overgrowth", permissions);
            Assert.Equal(18 + (trayTransfers ? 1 : 0) + (alignment ? 1 : 0) + (trayEnter ? 1 : 0) + (overgrowth ? 1 : 0), permissions.Count);
        }

        private static async Task<bool> TrayEnterGrantedAsync(SqlConnection conn)
        {
            using var cmd = new SqlCommand(@"SELECT CASE WHEN OBJECT_ID(N'dbo.RolePermissionChangeLog', N'U') IS NOT NULL
 AND EXISTS (SELECT 1 FROM dbo.RolePermissionChangeLog WHERE Script = N'2026-10-16_MotherPlantSupervisorTrayEnter.sql' AND Change = N'Added')
 THEN 1 ELSE 0 END", conn);
            return (int)(await cmd.ExecuteScalarAsync())! == 1;
        }

        // 2026-10-24_ReadyStockOvergrowth.sql granted ReadyStock.Overgrowth to this role (its own 'Added' row, not since removed).
        private static async Task<bool> OvergrowthGrantedAsync(SqlConnection conn, string roleName)
        {
            using var cmd = new SqlCommand(@"SELECT CASE WHEN OBJECT_ID(N'dbo.RolePermissionChangeLog', N'U') IS NOT NULL
 AND (SELECT TOP 1 Change FROM dbo.RolePermissionChangeLog WHERE RoleName = @Role AND PermissionCode = N'ReadyStock.Overgrowth'
      AND Script IN (N'2026-10-24_ReadyStockOvergrowth.sql', N'2026-10-24_ReadyStockOvergrowth_Rollback.sql') ORDER BY Id DESC) = N'Added'
 THEN 1 ELSE 0 END", conn);
            cmd.Parameters.AddWithValue("@Role", roleName);
            return (int)(await cmd.ExecuteScalarAsync())! == 1;
        }

        // 2026-10-23: a grant made by a migration that logs it (and whose rollback logs its removal) -- the LAST log row decides.
        private static async Task<bool> GrantedByAsync(SqlConnection conn, string roleName, string code, string script)
        {
            using var cmd = new SqlCommand(@"SELECT CASE WHEN OBJECT_ID(N'dbo.RolePermissionChangeLog', N'U') IS NOT NULL
 AND (SELECT TOP 1 Change FROM dbo.RolePermissionChangeLog WHERE RoleName = @Role AND PermissionCode = @Code
      AND Script IN (@Script, REPLACE(@Script, N'.sql', N'_Rollback.sql')) ORDER BY Id DESC) = N'Added'
 THEN 1 ELSE 0 END", conn);
            cmd.Parameters.AddWithValue("@Role", roleName);
            cmd.Parameters.AddWithValue("@Code", code);
            cmd.Parameters.AddWithValue("@Script", script);
            return (int)(await cmd.ExecuteScalarAsync())! == 1;
        }

        private static async Task<bool> TrayTransfersAppliedAsync(SqlConnection conn)
        {
            using var cmd = new SqlCommand("SELECT CASE WHEN OBJECT_ID(N'dbo.TrayTransfers', N'U') IS NULL THEN 0 ELSE 1 END", conn);
            return (int)(await cmd.ExecuteScalarAsync())! == 1;
        }

        private static async Task<bool> TrayAccessAppliedAsync(SqlConnection conn)
        {
            using var cmd = new SqlCommand("SELECT CASE WHEN EXISTS (SELECT 1 FROM dbo.Permissions WHERE Code = N'TrayStock.ReceiveTransferMainArea') THEN 1 ELSE 0 END", conn);
            return (int)(await cmd.ExecuteScalarAsync())! == 1;
        }

        [SkippableFact]
        public async Task OtherRoles_PermissionSets_AreUnaffectedByThisFix()
        {
            using var conn = await OpenAsync();
            // The EXACT permission set of every other role -- a regression guard that no migration
            // widened a role it was not meant to. 2026-10-10: compares sets (not counts) and reports
            // EVERY role that differs at once; before, the loop stopped at the first role, so the
            // Booking Executive difference hid the others (Sowing Supervisor, Sowing Operator).
            // Sources: Database/PhaseA_RoleBasedAccess.sql, the migrations named, and -- where stated --
            // grants present in the data copy since at least 2026-10-01 that no script makes.
            var trayTransfers = await TrayTransfersAppliedAsync(conn);
            string[] Tray1010(params string[] codes) => trayTransfers ? codes : Array.Empty<string>();
            var trayAccess = await TrayAccessAppliedAsync(conn);
            string[] Tray1011(params string[] codes) => trayAccess ? codes : Array.Empty<string>();
            // 2026-10-24_ReadyStockOvergrowth.sql: ReadyStock.Overgrowth for the Sowing Supervisor (Ready Stock approver, own Areas)
            var overgrowth = await OvergrowthGrantedAsync(conn, "Sowing Supervisor");
            string[] Og1024(params string[] codes) => overgrowth ? codes : Array.Empty<string>();
            // 2026-10-25_MaterialStockTransfers.sql: MaterialStock.Transfer for the Sowing Supervisor (from own Areas);
            // 2026-10-26_LabourAttendanceOutlet.sql: Labour.Attendance (attendance only) for Outlet Sales
            var materialTransfer = await GrantedByAsync(conn, "Sowing Supervisor", "MaterialStock.Transfer", "2026-10-25_MaterialStockTransfers.sql");
            var outletAttendance = await GrantedByAsync(conn, "Outlet Sales", "Labour.Attendance", "2026-10-26_LabourAttendanceOutlet.sql");
            var expected = new Dictionary<string, string[]>
            {
                // INTENDED set (owner decision 2026-10-10): PhaseA (4) + TrayStock.EnterMotherPlant (2026-10-09).
                // NO Dispatch.Enter (the Dispatch Executive enters dispatches) and NO Dispatch.View (booking pages
                // show dispatch status with Booking.View). Both were granted in the data through the Roles page
                // between 2026-10-01 and 2026-10-04; until 2026-10-11_RolePermissionAlignment.sql is approved and
                // applied, this test fails and names them -- deliberately not hidden.
                ["Booking Executive"] = new[] { "Booking.Enter", "Booking.View", "Dashboard.View", "ReadyStock.View", "TrayStock.EnterMotherPlant" },
                ["Dispatch Executive"] = new[] { "Booking.View", "Dashboard.View", "Dispatch.Enter", "Dispatch.View", "ReadyStock.View" },
                // + Maintenance.* (10-06_MaintenanceTickets), Store.* (10-06_StoreInventory)
                ["Fertilizer Supervisor"] = new[] { "Dashboard.View", "Fertilizer.Enter", "Fertilizer.View", "Maintenance.Raise", "Maintenance.View", "Store.Request", "Store.View" },
                // + Maintenance.* / Store.* (10-06); ReadyStock.TransferToMotherPlant, TrayStock.EnterMotherPlant (10-09); TrayStock.Transfer (10-10)
                ["Office Coordinator"] = new[] { "Booking.View", "Dashboard.View", "Dispatch.View", "Maintenance.Raise", "Maintenance.View", "ReadyStock.TransferToMotherPlant",
                    "ReadyStock.View", "Reports.View", "SeedStock.View", "Sowing.View", "Store.Request", "Store.View", "TrayStock.EnterMotherPlant" }.Concat(Tray1010("TrayStock.Transfer")).ToArray(),
                // no Labour.View / Labour.Enter; Labour.Attendance (attendance only) with 2026-10-26_LabourAttendanceOutlet.sql
                ["Outlet Sales"] = (outletAttendance ? new[] { "Labour.Attendance" } : Array.Empty<string>())
                    .Concat(new[] { "Dashboard.View", "Outlet.Confirm", "Outlet.Purchase", "Outlet.Sell", "Outlet.View" }).ToArray(),
                ["Pot Production Operator"] = new[] { "Dashboard.View", "InternalTransfer.Enter", "InternalTransfer.View", "PotProduction.Enter", "PotProduction.View" },
                ["Purchase Officer"] = new[] { "Dashboard.View", "Purchase.Enter", "Purchase.View" },
                // + TrayStock.Enter, Labour.* (10-04); Maintenance.* / Store.* (10-06); MaterialStock.View / Add / History (10-08);
                //   ReadyStock.TransferToMotherPlant (10-09); TrayStock.Adjust, TrayStock.Transfer (10-10);
                //   TrayStock.ReceiveTransferMainArea (10-11_TrayAccessByLocation: receives into own Main Office Areas)
                ["Sowing Supervisor"] = new[] { "Dashboard.View", "Labour.Enter", "Labour.View", "MainOffice.Confirm", "Maintenance.Raise", "Maintenance.View",
                    "MaterialStock.Add", "MaterialStock.History", "MaterialStock.View", "ReadyStock.Confirm", "ReadyStock.TransferToMotherPlant", "ReadyStock.View",
                    "SeedStock.View", "Sowing.View", "Store.Request", "Store.View", "TrayStock.Enter" }
                    .Concat(Tray1010("TrayStock.Adjust", "TrayStock.Transfer")).Concat(Tray1011("TrayStock.ReceiveTransferMainArea"))
                    .Concat(Og1024("ReadyStock.Overgrowth")).Concat(materialTransfer ? new[] { "MaterialStock.Transfer" } : Array.Empty<string>()).ToArray(),
                // + Store.Manage (10-06_StoreInventory)
                ["Main Office Store Keeper"] = new[] { "Dashboard.View", "InternalTransfer.Enter", "InternalTransfer.View", "MainOffice.Confirm", "MainOffice.View",
                    "PotProduction.View", "Purchase.Enter", "Purchase.View", "Reports.View", "SeedStock.Enter", "SeedStock.View", "Sowing.Enter", "Sowing.View", "Store.Manage" },
                // PhaseA (5) + the cutting / main-office / dispatch grants in the data since at least 2026-10-01
                ["Sowing Operator"] = new[] { "CuttingDelivery.Enter", "CuttingDelivery.View", "CuttingPlan.View", "Dashboard.View", "Dispatch.Enter",
                    "MainOffice.Confirm", "MainOffice.View", "ReadyStock.View", "SeedStock.View", "Sowing.Enter", "Sowing.View" },
            };
            var differences = new List<string>();
            foreach (var (role, codes) in expected)
            {
                var actual = await PermissionsForRoleAsync(conn, role);
                var extra = actual.Except(codes).ToList();
                var missing = codes.Except(actual).ToList();
                if (extra.Count > 0 || missing.Count > 0)
                    differences.Add($"{role}: unexpected [{string.Join(", ", extra)}] missing [{string.Join(", ", missing)}]");
            }
            Assert.True(differences.Count == 0, "Role permission sets differ from the migrations:\n" + string.Join("\n", differences));
        }
    }
}
