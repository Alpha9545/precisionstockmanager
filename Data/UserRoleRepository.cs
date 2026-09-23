using Microsoft.Data.SqlClient;
using PlantStockManager.Models;

namespace PlantStockManager.Data
{
    // dbo.UserRoles: which Role(s) -- optionally scoped to an Area -- each
    // IMSUsers row currently holds. This is the table the login flow reads
    // to compute permission claims, and the table the Admin/UserRoles page
    // manages.
    public class UserRoleRepository
    {
        private readonly DatabaseHelper _dbHelper;

        public UserRoleRepository(DatabaseHelper dbHelper)
        {
            _dbHelper = dbHelper;
        }

        public async Task<List<UserRoleAssignment>> GetAllAssignmentsAsync()
        {
            var assignments = new List<UserRoleAssignment>();
            using var conn = _dbHelper.GetConnection();
            await conn.OpenAsync();

            const string sql = @"
SELECT ur.Id, ur.UserId, u.Username AS UserName,
       ur.RoleId, r.Name AS RoleName,
       ur.AreaId, a.Name AS AreaName,
       gp.Name AS GrowingPartnerName,
       ur.CreatedDate
FROM dbo.UserRoles ur
INNER JOIN dbo.IMSUsers u ON ur.UserId = u.Id
INNER JOIN dbo.Roles r ON ur.RoleId = r.Id
LEFT JOIN dbo.Area a ON ur.AreaId = a.Id
LEFT JOIN dbo.GrowingPartners gp ON a.GrowingPartnerId = gp.Id
ORDER BY u.Username, r.Name";
            using var cmd = new SqlCommand(sql, conn);
            using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                assignments.Add(Map(reader));
            }
            return assignments;
        }

        public async Task<List<UserRoleAssignment>> GetAssignmentsForUserAsync(int userId)
        {
            var assignments = new List<UserRoleAssignment>();
            using var conn = _dbHelper.GetConnection();
            await conn.OpenAsync();

            const string sql = @"
SELECT ur.Id, ur.UserId, u.Username AS UserName,
       ur.RoleId, r.Name AS RoleName,
       ur.AreaId, a.Name AS AreaName,
       gp.Name AS GrowingPartnerName,
       ur.CreatedDate
FROM dbo.UserRoles ur
INNER JOIN dbo.IMSUsers u ON ur.UserId = u.Id
INNER JOIN dbo.Roles r ON ur.RoleId = r.Id
LEFT JOIN dbo.Area a ON ur.AreaId = a.Id
LEFT JOIN dbo.GrowingPartners gp ON a.GrowingPartnerId = gp.Id
WHERE ur.UserId = @UserId
ORDER BY r.Name";
            using var cmd = new SqlCommand(sql, conn);
            cmd.Parameters.AddWithValue("@UserId", userId);
            using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                assignments.Add(Map(reader));
            }
            return assignments;
        }

        // The set of distinct Permission.Code values the given user holds
        // across every Role assigned to them (any Area scope). Computed
        // once at login time and stamped onto the auth cookie as claims.
        public async Task<List<string>> GetPermissionCodesForUserAsync(int userId)
        {
            var codes = new List<string>();
            using var conn = _dbHelper.GetConnection();
            await conn.OpenAsync();

            const string sql = @"
SELECT DISTINCT p.Code
FROM dbo.UserRoles ur
INNER JOIN dbo.RolePermissions rp ON rp.RoleId = ur.RoleId
INNER JOIN dbo.Permissions p ON p.Id = rp.PermissionId
WHERE ur.UserId = @UserId";
            using var cmd = new SqlCommand(sql, conn);
            cmd.Parameters.AddWithValue("@UserId", userId);
            using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                codes.Add(reader.GetString(0));
            }
            return codes;
        }

        public async Task<(bool success, string? message)> AddAssignmentAsync(int userId, int roleId, int? areaId)
        {
            using var conn = _dbHelper.GetConnection();
            await conn.OpenAsync();

            const string sql = @"
IF EXISTS (SELECT 1 FROM dbo.UserRoles WHERE UserId = @UserId AND RoleId = @RoleId AND
           ((AreaId IS NULL AND @AreaId IS NULL) OR AreaId = @AreaId))
BEGIN
    SELECT 0;
END
ELSE
BEGIN
    INSERT INTO dbo.UserRoles (UserId, RoleId, AreaId) VALUES (@UserId, @RoleId, @AreaId);
    SELECT 1;
END";
            using var cmd = new SqlCommand(sql, conn);
            cmd.Parameters.AddWithValue("@UserId", userId);
            cmd.Parameters.AddWithValue("@RoleId", roleId);
            cmd.Parameters.AddWithValue("@AreaId", (object?)areaId ?? DBNull.Value);

            var result = await cmd.ExecuteScalarAsync();
            var inserted = result != null && Convert.ToInt32(result) == 1;
            return inserted
                ? (true, null)
                : (false, "That user already has that role for that area.");
        }

        public async Task RemoveAssignmentAsync(int userRoleId)
        {
            using var conn = _dbHelper.GetConnection();
            await conn.OpenAsync();

            const string sql = "DELETE FROM dbo.UserRoles WHERE Id = @Id";
            using var cmd = new SqlCommand(sql, conn);
            cmd.Parameters.AddWithValue("@Id", userRoleId);
            await cmd.ExecuteNonQueryAsync();
        }

        private static UserRoleAssignment Map(SqlDataReader reader)
        {
            return new UserRoleAssignment
            {
                Id = reader.GetInt32(reader.GetOrdinal("Id")),
                UserId = reader.GetInt32(reader.GetOrdinal("UserId")),
                UserName = reader.IsDBNull(reader.GetOrdinal("UserName")) ? null : reader.GetString(reader.GetOrdinal("UserName")),
                RoleId = reader.GetInt32(reader.GetOrdinal("RoleId")),
                RoleName = reader.IsDBNull(reader.GetOrdinal("RoleName")) ? null : reader.GetString(reader.GetOrdinal("RoleName")),
                AreaId = reader.IsDBNull(reader.GetOrdinal("AreaId")) ? null : reader.GetInt32(reader.GetOrdinal("AreaId")),
                AreaName = reader.IsDBNull(reader.GetOrdinal("AreaName")) ? null : reader.GetString(reader.GetOrdinal("AreaName")),
                GrowingPartnerName = reader.IsDBNull(reader.GetOrdinal("GrowingPartnerName")) ? null : reader.GetString(reader.GetOrdinal("GrowingPartnerName")),
                CreatedDate = reader.GetDateTime(reader.GetOrdinal("CreatedDate"))
            };
        }
    }
}
