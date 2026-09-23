using Microsoft.Data.SqlClient;
using PlantStockManager.Models;

namespace PlantStockManager.Data
{
    // dbo.Roles + dbo.RolePermissions. Kept together in one repository
    // because every real use (list a Role with its granted Permissions,
    // save a Role's Permission set) touches both tables at once.
    public class RoleRepository
    {
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

            const string sql = @"
SELECT Id, Name, Description, IsSystemRole, CreatedDate
FROM dbo.Roles
ORDER BY Name";
            using var cmd = new SqlCommand(sql, conn);
            using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                roles.Add(new Role
                {
                    Id = reader.GetInt32(reader.GetOrdinal("Id")),
                    Name = reader.GetString(reader.GetOrdinal("Name")),
                    Description = reader.IsDBNull(reader.GetOrdinal("Description")) ? null : reader.GetString(reader.GetOrdinal("Description")),
                    IsSystemRole = reader.GetBoolean(reader.GetOrdinal("IsSystemRole")),
                    CreatedDate = reader.GetDateTime(reader.GetOrdinal("CreatedDate"))
                });
            }
            return roles;
        }

        public async Task<Role?> GetRoleByIdAsync(int roleId)
        {
            using var conn = _dbHelper.GetConnection();
            await conn.OpenAsync();

            const string sql = @"SELECT Id, Name, Description, IsSystemRole, CreatedDate FROM dbo.Roles WHERE Id = @Id";
            using var cmd = new SqlCommand(sql, conn);
            cmd.Parameters.AddWithValue("@Id", roleId);
            using var reader = await cmd.ExecuteReaderAsync();
            if (await reader.ReadAsync())
            {
                return new Role
                {
                    Id = reader.GetInt32(reader.GetOrdinal("Id")),
                    Name = reader.GetString(reader.GetOrdinal("Name")),
                    Description = reader.IsDBNull(reader.GetOrdinal("Description")) ? null : reader.GetString(reader.GetOrdinal("Description")),
                    IsSystemRole = reader.GetBoolean(reader.GetOrdinal("IsSystemRole")),
                    CreatedDate = reader.GetDateTime(reader.GetOrdinal("CreatedDate"))
                };
            }
            return null;
        }

        public async Task AddRoleAsync(string name, string? description)
        {
            using var conn = _dbHelper.GetConnection();
            await conn.OpenAsync();

            const string sql = @"
INSERT INTO dbo.Roles (Name, Description, IsSystemRole)
VALUES (@Name, @Description, 0)";
            using var cmd = new SqlCommand(sql, conn);
            cmd.Parameters.AddWithValue("@Name", name);
            cmd.Parameters.AddWithValue("@Description", (object?)description ?? DBNull.Value);
            await cmd.ExecuteNonQueryAsync();
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
