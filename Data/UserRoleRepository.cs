using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;
using PlantStockManager.Authorization;
using PlantStockManager.Models;
using PlantStockManager.Services;

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
        // SecurityOptions). Unchanged behaviour, now expressed through the
        // shared supervisor rule (Services/SupervisorRules.cs) with no Area
        // condition. Pass conn/tx to read inside an existing transaction.
        public async Task<List<Employee>> GetSowingApproversAsync(SqlConnection? conn = null, SqlTransaction? tx = null)
            => SupervisorRules.Eligible(await GetSupervisorCandidatesAsync(SupervisorKind.Sowing, conn, tx), areaId: null);

        // Phase 1: the eligible supervisors of a kind for an Area (null = no
        // Area condition). The single source for every supervisor dropdown and
        // save-time check.
        public async Task<List<Employee>> GetEligibleSupervisorsAsync(SupervisorKind kind, int? areaId, bool enforceArea = true)
            => SupervisorRules.Eligible(await GetSupervisorCandidatesAsync(kind), areaId, enforceArea);

        // Every role assignment that could make a user a supervisor of this
        // kind: the role grants the kind's permission, or the role is a
        // full-access role. Inactive users are returned too (IsActive = 0) so
        // the pure rule decides; IsAllAreas marks full-access roles and the
        // all-Area roles of AreaAccessService.
        public async Task<List<SupervisorCandidate>> GetSupervisorCandidatesAsync(
            SupervisorKind kind, SqlConnection? conn = null, SqlTransaction? tx = null)
        {
            var fullAccess = _security.EffectiveFullAccessRoleNames.ToList();
            var fullList = fullAccess.Count == 0 ? "NULL" : string.Join(", ", fullAccess.Select((_, i) => "@F" + i));
            var allAreaList = string.Join(", ", AreaAccessService.AllAreaRoleNames.Select((_, i) => "@A" + i));
            var sql = $@"
SELECT u.Id, u.Name, ISNULL(d.DesignationName, ''), CAST(ISNULL(u.IsActive, 0) AS BIT), ur.AreaId,
       CAST(CASE WHEN COALESCE(NULLIF(LTRIM(RTRIM(r.Name)), ''), r.RoleName) IN ({fullList})
                   OR COALESCE(NULLIF(LTRIM(RTRIM(r.Name)), ''), r.RoleName) IN ({allAreaList})
                 THEN 1 ELSE 0 END AS BIT) AS IsAllAreas
FROM dbo.UserRoles ur
INNER JOIN dbo.IMSUsers u ON u.Id = ur.UserId
INNER JOIN dbo.Roles r ON r.Id = ur.RoleId
LEFT JOIN dbo.Designation d ON d.DesignationID = u.DesignationID
WHERE COALESCE(NULLIF(LTRIM(RTRIM(r.Name)), ''), r.RoleName) IN ({fullList})
   OR EXISTS (SELECT 1 FROM dbo.RolePermissions rp
              INNER JOIN dbo.Permissions p ON p.Id = rp.PermissionId
              WHERE rp.RoleId = r.Id AND p.Code = @Permission)";

            var owns = conn == null;
            var c = conn ?? _dbHelper.GetConnection();
            try
            {
                if (owns) await c.OpenAsync();
                using var cmd = new SqlCommand(sql, c, tx);
                cmd.Parameters.AddWithValue("@Permission", SupervisorRules.PermissionFor(kind));
                for (var i = 0; i < fullAccess.Count; i++)
                    cmd.Parameters.AddWithValue("@F" + i, fullAccess[i]);
                for (var i = 0; i < AreaAccessService.AllAreaRoleNames.Count; i++)
                    cmd.Parameters.AddWithValue("@A" + i, AreaAccessService.AllAreaRoleNames[i]);
                var list = new List<SupervisorCandidate>();
                using var reader = await cmd.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                    list.Add(new SupervisorCandidate(
                        reader.GetInt32(0), reader.GetString(1), reader.GetString(2), reader.GetBoolean(3),
                        reader.IsDBNull(4) ? null : reader.GetInt32(4), reader.GetBoolean(5)));
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
