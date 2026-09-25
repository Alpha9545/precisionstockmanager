using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;
using PlantStockManager.Authorization;
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
        private readonly SecurityOptions _security;

        public UserRoleRepository(DatabaseHelper dbHelper, IOptions<SecurityOptions> security)
        {
            _dbHelper = dbHelper;
            _security = security.Value;
        }

        // Sowing approval: the users who can be ASSIGNED as a sowing's
        // supervisor = ACTIVE users holding ReadyStock.Confirm through one of
        // their roles, or holding a full-access role (System Administrator,
        // SecurityOptions). Same sources as the login claims (UserClaimsFactory).
        // Pass conn/tx to read inside an existing transaction.
        public async Task<List<Employee>> GetSowingApproversAsync(SqlConnection? conn = null, SqlTransaction? tx = null)
        {
            var fullAccess = _security.EffectiveFullAccessRoleNames.ToList();
            var inList = fullAccess.Count == 0 ? "NULL" : string.Join(", ", fullAccess.Select((_, i) => "@F" + i));
            var sql = $@"
SELECT u.Id, u.Name, ISNULL(d.DesignationName, '')
FROM dbo.IMSUsers u
LEFT JOIN dbo.Designation d ON d.DesignationID = u.DesignationID
WHERE u.IsActive = 1
  AND EXISTS (
        SELECT 1
        FROM dbo.UserRoles ur
        INNER JOIN dbo.Roles r ON r.Id = ur.RoleId
        WHERE ur.UserId = u.Id
          AND (COALESCE(NULLIF(LTRIM(RTRIM(r.Name)), ''), r.RoleName) IN ({inList})
               OR EXISTS (SELECT 1 FROM dbo.RolePermissions rp
                          INNER JOIN dbo.Permissions p ON p.Id = rp.PermissionId
                          WHERE rp.RoleId = r.Id AND p.Code = N'ReadyStock.Confirm')))
ORDER BY u.Name";

            var owns = conn == null;
            var c = conn ?? _dbHelper.GetConnection();
            try
            {
                if (owns) await c.OpenAsync();
                using var cmd = new SqlCommand(sql, c, tx);
                for (var i = 0; i < fullAccess.Count; i++)
                    cmd.Parameters.AddWithValue("@F" + i, fullAccess[i]);
                var list = new List<Employee>();
                using var reader = await cmd.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                    list.Add(new Employee { EmployeeID = reader.GetInt32(0), Name = reader.GetString(1), Designation = reader.GetString(2) });
                return list;
            }
            finally
            {
                if (owns) c.Dispose();
            }
        }

        public async Task<List<UserRoleAssignment>> GetAllAssignmentsAsync()
        {
            var assignments = new List<UserRoleAssignment>();
            using var conn = _dbHelper.GetConnection();
            await conn.OpenAsync();

            const string sql = @"
SELECT ur.Id, ur.UserId, u.Username AS UserName,
       ur.RoleId, COALESCE(NULLIF(LTRIM(RTRIM(r.Name)), ''), r.RoleName) AS RoleName,
       ur.AreaId, a.Name AS AreaName,
       gp.Name AS GrowingPartnerName,
       ur.CreatedDate
FROM dbo.UserRoles ur
INNER JOIN dbo.IMSUsers u ON ur.UserId = u.Id
INNER JOIN dbo.Roles r ON ur.RoleId = r.Id
LEFT JOIN dbo.Area a ON ur.AreaId = a.Id
LEFT JOIN dbo.GrowingPartners gp ON a.GrowingPartnerId = gp.Id
ORDER BY u.Username, RoleName";
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
       ur.RoleId, COALESCE(NULLIF(LTRIM(RTRIM(r.Name)), ''), r.RoleName) AS RoleName,
       ur.AreaId, a.Name AS AreaName,
       gp.Name AS GrowingPartnerName,
       ur.CreatedDate
FROM dbo.UserRoles ur
INNER JOIN dbo.IMSUsers u ON ur.UserId = u.Id
INNER JOIN dbo.Roles r ON ur.RoleId = r.Id
LEFT JOIN dbo.Area a ON ur.AreaId = a.Id
LEFT JOIN dbo.GrowingPartners gp ON a.GrowingPartnerId = gp.Id
WHERE ur.UserId = @UserId
ORDER BY RoleName";
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

        // Phase A: used by Add/Edit User to write the user's role inside the
        // SAME transaction that creates/updates the dbo.IMSUsers row, so a
        // user is never created without their role (or vice versa).
        public static async Task InsertAssignmentAsync(SqlConnection conn, SqlTransaction tx, int userId, int roleId, int? areaId)
        {
            const string sql = @"
IF NOT EXISTS (SELECT 1 FROM dbo.UserRoles WHERE UserId = @UserId AND RoleId = @RoleId AND
               ((AreaId IS NULL AND @AreaId IS NULL) OR AreaId = @AreaId))
    INSERT INTO dbo.UserRoles (UserId, RoleId, AreaId) VALUES (@UserId, @RoleId, @AreaId);";
            using var cmd = new SqlCommand(sql, conn, tx);
            cmd.Parameters.AddWithValue("@UserId", userId);
            cmd.Parameters.AddWithValue("@RoleId", roleId);
            cmd.Parameters.AddWithValue("@AreaId", (object?)areaId ?? DBNull.Value);
            await cmd.ExecuteNonQueryAsync();
        }

        // Phase A: Edit User's single "Role / Area" selection replaces the
        // user's assignments. Only called when the user has at most one
        // assignment (multi-role users are managed on Admin > User Roles).
        public static async Task ReplaceAssignmentsAsync(SqlConnection conn, SqlTransaction tx, int userId, int roleId, int? areaId)
        {
            using (var del = new SqlCommand("DELETE FROM dbo.UserRoles WHERE UserId = @UserId", conn, tx))
            {
                del.Parameters.AddWithValue("@UserId", userId);
                await del.ExecuteNonQueryAsync();
            }
            await InsertAssignmentAsync(conn, tx, userId, roleId, areaId);
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
