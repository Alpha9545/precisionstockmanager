using Microsoft.Data.SqlClient;
using PlantStockManager.Data;
using PlantStockManager.Services;

namespace PlantStockManager.Authorization
{
    // 2026-10-12: Bed Sowing transplanting belongs to ONE named person (the Bed Sowing Transplanter,
    // assigned by Database/Migrations/2026-10-12_BedSowing.sql). The role-management pages
    // (Admin > User Roles, Admin > Users, Admin > Roles) therefore never:
    //   * give or remove a role that carries BedSowing.Transplant,
    //   * change the permissions of such a role,
    //   * add BedSowing.Transplant to any other role.
    // Changes go through a reviewed database script. The database repeats the rule
    // (TR_UserRoles_BedTransplanter: at most one holder; TR_RolePermissions_BedTransplant: only that
    // role) and logs every change in dbo.BedSowingAuditLog. Before the migration the permission does
    // not exist and nothing here applies.
    public class BedSowingRoleGuard
    {
        private readonly DatabaseHelper _db;

        public BedSowingRoleGuard(DatabaseHelper db) => _db = db;

        public async Task<int?> TransplantPermissionIdAsync()
        {
            using var conn = _db.GetConnection();
            await conn.OpenAsync();
            using var cmd = new SqlCommand("SELECT Id FROM dbo.Permissions WHERE Code = @Code", conn);
            cmd.Parameters.AddWithValue("@Code", BedSowingRules.TransplantPermission);
            return await cmd.ExecuteScalarAsync() as int?;
        }

        public async Task<bool> RoleCarriesTransplantAsync(int roleId)
        {
            using var conn = _db.GetConnection();
            await conn.OpenAsync();
            using var cmd = new SqlCommand(@"
SELECT CASE WHEN EXISTS (SELECT 1 FROM dbo.RolePermissions rp JOIN dbo.Permissions p ON p.Id = rp.PermissionId
                         WHERE rp.RoleId = @R AND p.Code = @Code) THEN 1 ELSE 0 END", conn);
            cmd.Parameters.AddWithValue("@R", roleId);
            cmd.Parameters.AddWithValue("@Code", BedSowingRules.TransplantPermission);
            return (int)(await cmd.ExecuteScalarAsync())! == 1;
        }

        public async Task<bool> UserHoldsTransplantRoleAsync(int userId)
        {
            using var conn = _db.GetConnection();
            await conn.OpenAsync();
            using var cmd = new SqlCommand(@"
SELECT CASE WHEN EXISTS (SELECT 1 FROM dbo.UserRoles ur JOIN dbo.RolePermissions rp ON rp.RoleId = ur.RoleId
                         JOIN dbo.Permissions p ON p.Id = rp.PermissionId
                         WHERE ur.UserId = @U AND p.Code = @Code) THEN 1 ELSE 0 END", conn);
            cmd.Parameters.AddWithValue("@U", userId);
            cmd.Parameters.AddWithValue("@Code", BedSowingRules.TransplantPermission);
            return (int)(await cmd.ExecuteScalarAsync())! == 1;
        }

        // Admin > Roles: the requested permission set of roleId is refused when it would change the
        // grant of BedSowing.Transplant on any role, or change the permissions of the role that carries it.
        public async Task<string?> CheckRolePermissionChangeAsync(int roleId, IReadOnlyCollection<int> requestedPermissionIds)
        {
            var permissionId = await TransplantPermissionIdAsync();
            if (permissionId == null)
                return null;
            var carries = await RoleCarriesTransplantAsync(roleId);
            if (carries)
                return BedSowingRules.ProtectedRoleMessage;
            return requestedPermissionIds.Contains(permissionId.Value) ? BedSowingRules.ProtectedRoleMessage : null;
        }
    }
}
