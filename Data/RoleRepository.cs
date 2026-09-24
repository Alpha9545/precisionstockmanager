using Microsoft.Data.SqlClient;
using PlantStockManager.Models;

namespace PlantStockManager.Data
{
    // dbo.Roles + dbo.RolePermissions. Kept together in one repository
    // because every real use (list a Role with its granted Permissions,
    // save a Role's Permission set) touches both tables at once.
    //
    // Phase A: dbo.Roles has TWO name columns in production -- the legacy
    // "RoleName" (NOT NULL, UNIQUE) and "Name" (added later, what Phase 14's
    // code reads). Database/PhaseA_RoleBasedAccess.sql guarantees both exist
    // and are in sync; this repository reads RoleNameExpression (Name, falling
    // back to RoleName) and writes BOTH on insert, so it works on either
    // schema shape and never leaves one of them empty.
    public class RoleRepository
    {
        public const string RoleNameExpression = "COALESCE(NULLIF(LTRIM(RTRIM(r.Name)), ''), r.RoleName)";

        private const string RoleSelect = @"
SELECT r.Id, " + RoleNameExpression + @" AS Name, r.Description, r.IsSystemRole, r.CreatedDate,
       (SELECT COUNT(*) FROM dbo.RolePermissions rp WHERE rp.RoleId = r.Id) AS PermissionCount,
       (SELECT COUNT(DISTINCT ur.UserId) FROM dbo.UserRoles ur WHERE ur.RoleId = r.Id) AS UserCount
FROM dbo.Roles r";

        private readonly DatabaseHelper _dbHelper;

        public RoleRepository(DatabaseHelper dbHelper)
        {
            _dbHelper = dbHelper;
        }

        public async Task<List<Role>> GetAllRolesAsync()
        {
            var roles = new List<Role>();
            using var conn = _dbHelper.GetConnection();
            await conn.OpenAsync();

            const string sql = RoleSelect + " ORDER BY Name";
            using var cmd = new SqlCommand(sql, conn);
            using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                roles.Add(MapRole(reader));
            }
            return roles;
        }

        public async Task<Role?> GetRoleByIdAsync(int roleId)
        {
            using var conn = _dbHelper.GetConnection();
            await conn.OpenAsync();

            const string sql = RoleSelect + " WHERE r.Id = @Id";
            using var cmd = new SqlCommand(sql, conn);
            cmd.Parameters.AddWithValue("@Id", roleId);
            using var reader = await cmd.ExecuteReaderAsync();
            if (await reader.ReadAsync())
            {
                return MapRole(reader);
            }
            return null;
        }

        // Adds a role, writing BOTH name columns. Returns false (nothing
        // inserted) when a role with the same name already exists.
        public async Task<bool> AddRoleAsync(string name, string? description)
        {
            using var conn = _dbHelper.GetConnection();
            await conn.OpenAsync();

            const string sql = @"
IF EXISTS (SELECT 1 FROM dbo.Roles r WHERE " + RoleNameExpression + @" = @Name OR r.RoleName = @Name)
    SELECT 0;
ELSE
BEGIN
    INSERT INTO dbo.Roles (Name, RoleName, Description, IsSystemRole)
    VALUES (@Name, @Name, @Description, 0);
    SELECT 1;
END";
            using var cmd = new SqlCommand(sql, conn);
            cmd.Parameters.AddWithValue("@Name", name);
            cmd.Parameters.AddWithValue("@Description", (object?)description ?? DBNull.Value);
            var result = await cmd.ExecuteScalarAsync();
            return result != null && Convert.ToInt32(result) == 1;
        }

        // Permission codes currently granted to a role.
        public async Task<List<string>> GetPermissionCodesForRoleAsync(int roleId)
        {
            var codes = new List<string>();
            using var conn = _dbHelper.GetConnection();
            await conn.OpenAsync();
            const string sql = @"
SELECT p.Code FROM dbo.RolePermissions rp
INNER JOIN dbo.Permissions p ON p.Id = rp.PermissionId
WHERE rp.RoleId = @RoleId";
            using var cmd = new SqlCommand(sql, conn);
            cmd.Parameters.AddWithValue("@RoleId", roleId);
            using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
                codes.Add(reader.GetString(0));
            return codes;
        }

        private static Role MapRole(SqlDataReader reader)
        {
            return new Role
            {
                Id = reader.GetInt32(reader.GetOrdinal("Id")),
                Name = reader.IsDBNull(reader.GetOrdinal("Name")) ? string.Empty : reader.GetString(reader.GetOrdinal("Name")),
                Description = reader.IsDBNull(reader.GetOrdinal("Description")) ? null : reader.GetString(reader.GetOrdinal("Description")),
                IsSystemRole = reader.GetBoolean(reader.GetOrdinal("IsSystemRole")),
                CreatedDate = reader.GetDateTime(reader.GetOrdinal("CreatedDate")),
                PermissionCount = reader.GetInt32(reader.GetOrdinal("PermissionCount")),
                UserCount = reader.GetInt32(reader.GetOrdinal("UserCount"))
            };
        }

        // Every Permission that exists, flagged with whether the given Role
        // currently has it -- feeds the checkbox grid on the Roles admin page.
        public async Task<List<Permission>> GetPermissionsWithGrantFlagAsync(int roleId)
        {
            var permissions = new List<Permission>();
            using var conn = _dbHelper.GetConnection();
            await conn.OpenAsync();

            const string sql = @"
SELECT p.Id, p.Code, p.Description,
       CASE WHEN rp.RoleId IS NULL THEN 0 ELSE 1 END AS IsGranted
FROM dbo.Permissions p
LEFT JOIN dbo.RolePermissions rp ON rp.PermissionId = p.Id AND rp.RoleId = @RoleId
ORDER BY p.Code";
            using var cmd = new SqlCommand(sql, conn);
            cmd.Parameters.AddWithValue("@RoleId", roleId);
            using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                permissions.Add(new Permission
                {
                    Id = reader.GetInt32(reader.GetOrdinal("Id")),
                    Code = reader.GetString(reader.GetOrdinal("Code")),
                    Description = reader.IsDBNull(reader.GetOrdinal("Description")) ? null : reader.GetString(reader.GetOrdinal("Description")),
                    IsGrantedToRole = reader.GetInt32(reader.GetOrdinal("IsGranted")) == 1
                });
            }
            return permissions;
        }

        // Replaces the Role's entire Permission set with exactly the given
        // list, in one transaction -- never a partial grant/revoke.
        public async Task SetRolePermissionsAsync(int roleId, List<int> permissionIds)
        {
            using var conn = _dbHelper.GetConnection();
            await conn.OpenAsync();
            using var transaction = conn.BeginTransaction();
            try
            {
                using (var deleteCmd = new SqlCommand("DELETE FROM dbo.RolePermissions WHERE RoleId = @RoleId", conn, transaction))
                {
                    deleteCmd.Parameters.AddWithValue("@RoleId", roleId);
                    await deleteCmd.ExecuteNonQueryAsync();
                }

                foreach (var permissionId in permissionIds)
                {
                    using var insertCmd = new SqlCommand(
                        "INSERT INTO dbo.RolePermissions (RoleId, PermissionId) VALUES (@RoleId, @PermissionId)",
                        conn, transaction);
                    insertCmd.Parameters.AddWithValue("@RoleId", roleId);
                    insertCmd.Parameters.AddWithValue("@PermissionId", permissionId);
                    await insertCmd.ExecuteNonQueryAsync();
                }

                transaction.Commit();
            }
            catch
            {
                transaction.Rollback();
                throw;
            }
        }
    }
}
