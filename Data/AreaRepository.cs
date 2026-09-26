using PlantStockManager.Models;
using System.Collections.Generic;
using Microsoft.Data.SqlClient;
using System.Threading.Tasks;

namespace PlantStockManager.Data
{
    // Area -> Polyhouse is ONE relationship: dbo.Polyhouses.AreaId (each
    // Polyhouse belongs to an Area; an Area has many Polyhouses). The old
    // dbo.Area.PolyhouseId column is retired: never read or written here
    // (its values were copied to Polyhouses.AreaId by PhaseD).
    public class AreaRepository
    {
        private readonly DatabaseHelper _dbHelper;

        public AreaRepository(DatabaseHelper dbHelper)
        {
            _dbHelper = dbHelper;
        }

        private const string BaseSelect = @"
SELECT a.Id,
       (SELECT STRING_AGG(RTRIM(p.Name), N', ') WITHIN GROUP (ORDER BY p.Name) FROM dbo.Polyhouses p WHERE p.AreaId = a.Id) AS PolyhouseName,
       a.Name, a.AreaCode,
       a.AreaSize, a.AreaUnit, a.Capacity, a.CapacityUnit, a.IsActive, a.CreatedAt,
       a.AreaType, a.SupervisorId, sup.Name AS SupervisorName, a.Location, a.Remarks,
       a.GrowingPartnerId, gp.Name AS GrowingPartnerName
FROM dbo.Area a
LEFT JOIN dbo.IMSUsers sup ON a.SupervisorId = sup.Id
LEFT JOIN dbo.GrowingPartners gp ON a.GrowingPartnerId = gp.Id";

        public async Task<List<Area>> GetAllAreas()
        {
            var areas = new List<Area>();
            using (var conn = _dbHelper.GetConnection())
            {
                await conn.OpenAsync();
                var sql = BaseSelect + " WHERE a.IsActive = 1 ORDER BY a.Name";
                var cmd = new SqlCommand(sql, conn);
                using (var reader = await cmd.ExecuteReaderAsync())
                {
                    while (await reader.ReadAsync())
                    {
                        areas.Add(Map(reader));
                    }
                }
            }
            return areas;
        }

        // The Area a Polyhouse belongs to (Polyhouses.AreaId), as a list.
        public async Task<List<Area>> GetAreasByPolyhouseId(int polyhouseId)
        {
            var areas = new List<Area>();
            using (var conn = _dbHelper.GetConnection())
            {
                await conn.OpenAsync();
                var sql = BaseSelect + " WHERE a.IsActive = 1 AND a.Id = (SELECT p.AreaId FROM dbo.Polyhouses p WHERE p.Id = @PolyhouseId) ORDER BY a.Name";
                var cmd = new SqlCommand(sql, conn);
                cmd.Parameters.AddWithValue("@PolyhouseId", polyhouseId);
                using (var reader = await cmd.ExecuteReaderAsync())
                {
                    while (await reader.ReadAsync())
                    {
                        areas.Add(Map(reader));
                    }
                }
            }
            return areas;
        }

        // Used for server-side validation that a submitted Area actually
        // belongs to the submitted Polyhouse before a Mother Plant batch
        // is saved -- the client-side cascade can be bypassed, so this is
        // re-checked on the server regardless.
        public async Task<Area?> GetAreaById(int id)
        {
            using (var conn = _dbHelper.GetConnection())
            {
                await conn.OpenAsync();
                var sql = BaseSelect + " WHERE a.Id = @Id";
                var cmd = new SqlCommand(sql, conn);
                cmd.Parameters.AddWithValue("@Id", id);
                using (var reader = await cmd.ExecuteReaderAsync())
                {
                    if (await reader.ReadAsync())
                    {
                        return Map(reader);
                    }
                }
            }
            return null;
        }

        public async Task AddArea(Area area)
        {
            using (var conn = _dbHelper.GetConnection())
            {
                await conn.OpenAsync();
                const string sql = @"
INSERT INTO dbo.Area (Name, AreaCode, AreaSize, AreaUnit, Capacity, CapacityUnit, IsActive, CreatedAt,
                       AreaType, SupervisorId, Location, Remarks)
VALUES (@Name, @AreaCode, @AreaSize, @AreaUnit, @Capacity, @CapacityUnit, @IsActive, SYSUTCDATETIME(),
        @AreaType, @SupervisorId, @Location, @Remarks)";
                var cmd = new SqlCommand(sql, conn);
                cmd.Parameters.AddWithValue("@Name", area.Name);
                cmd.Parameters.AddWithValue("@AreaCode", (object?)area.AreaCode ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@AreaSize", (object?)area.AreaSize ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@AreaUnit", (object?)area.AreaUnit ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@Capacity", (object?)area.Capacity ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@CapacityUnit", (object?)area.CapacityUnit ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@IsActive", area.IsActive);
                cmd.Parameters.AddWithValue("@AreaType", (object?)area.AreaType ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@SupervisorId", (object?)area.SupervisorId ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@Location", (object?)area.Location ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@Remarks", (object?)area.Remarks ?? DBNull.Value);
                await cmd.ExecuteNonQueryAsync();
            }
        }

        public async Task UpdateArea(Area area)
        {
            using (var conn = _dbHelper.GetConnection())
            {
                await conn.OpenAsync();
                const string sql = @"
UPDATE dbo.Area
SET Name = @Name,
    AreaCode = @AreaCode,
    AreaSize = @AreaSize,
    AreaUnit = @AreaUnit,
    Capacity = @Capacity,
    CapacityUnit = @CapacityUnit,
    IsActive = @IsActive,
    AreaType = @AreaType,
    SupervisorId = @SupervisorId,
    Location = @Location,
    Remarks = @Remarks
WHERE Id = @Id";   // the retired Growing Partner link is never overwritten
                var cmd = new SqlCommand(sql, conn);
                cmd.Parameters.AddWithValue("@Id", area.Id);
                cmd.Parameters.AddWithValue("@Name", area.Name);
                cmd.Parameters.AddWithValue("@AreaCode", (object?)area.AreaCode ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@AreaSize", (object?)area.AreaSize ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@AreaUnit", (object?)area.AreaUnit ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@Capacity", (object?)area.Capacity ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@CapacityUnit", (object?)area.CapacityUnit ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@IsActive", area.IsActive);
                cmd.Parameters.AddWithValue("@AreaType", (object?)area.AreaType ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@SupervisorId", (object?)area.SupervisorId ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@Location", (object?)area.Location ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@Remarks", (object?)area.Remarks ?? DBNull.Value);
                await cmd.ExecuteNonQueryAsync();
            }
        }

        // Every Area whose AreaType matches one of the given types --
        // e.g. GetByAreaTypesAsync("MainOffice") to find the destination
        // pool(s) for a confirmed cutting delivery. Used by later redesign
        // phases (Mother Plant / Kunjir / Kiran / Outlet workflows).
        public async Task<List<Area>> GetByAreaTypesAsync(params string[] areaTypes)
        {
            var areas = new List<Area>();
            if (areaTypes == null || areaTypes.Length == 0)
            {
                return areas;
            }

            using (var conn = _dbHelper.GetConnection())
            {
                await conn.OpenAsync();
                var placeholders = string.Join(",", areaTypes.Select((_, i) => $"@Type{i}"));
                var sql = BaseSelect + $" WHERE a.IsActive = 1 AND a.AreaType IN ({placeholders}) ORDER BY a.Name";
                var cmd = new SqlCommand(sql, conn);
                for (int i = 0; i < areaTypes.Length; i++)
                {
                    cmd.Parameters.AddWithValue($"@Type{i}", areaTypes[i]);
                }
                using (var reader = await cmd.ExecuteReaderAsync())
                {
                    while (await reader.ReadAsync())
                    {
                        areas.Add(Map(reader));
                    }
                }
            }
            return areas;
        }

        private static Area Map(SqlDataReader reader)
        {
            return new Area
            {
                Id = reader.GetInt32(reader.GetOrdinal("Id")),
                PolyhouseName = reader.IsDBNull(reader.GetOrdinal("PolyhouseName")) ? null : reader.GetString(reader.GetOrdinal("PolyhouseName")),
                Name = reader.GetString(reader.GetOrdinal("Name")),
                AreaCode = reader.IsDBNull(reader.GetOrdinal("AreaCode")) ? null : reader.GetString(reader.GetOrdinal("AreaCode")),
                AreaSize = reader.IsDBNull(reader.GetOrdinal("AreaSize")) ? null : reader.GetDecimal(reader.GetOrdinal("AreaSize")),
                AreaUnit = reader.IsDBNull(reader.GetOrdinal("AreaUnit")) ? null : reader.GetString(reader.GetOrdinal("AreaUnit")),
                Capacity = reader.IsDBNull(reader.GetOrdinal("Capacity")) ? null : reader.GetDecimal(reader.GetOrdinal("Capacity")),
                CapacityUnit = reader.IsDBNull(reader.GetOrdinal("CapacityUnit")) ? null : reader.GetString(reader.GetOrdinal("CapacityUnit")),
                IsActive = reader.GetBoolean(reader.GetOrdinal("IsActive")),
                CreatedAt = reader.IsDBNull(reader.GetOrdinal("CreatedAt")) ? DateTime.UtcNow : reader.GetDateTime(reader.GetOrdinal("CreatedAt")),
                AreaType = reader.IsDBNull(reader.GetOrdinal("AreaType")) ? null : reader.GetString(reader.GetOrdinal("AreaType")),
                SupervisorId = reader.IsDBNull(reader.GetOrdinal("SupervisorId")) ? (int?)null : reader.GetInt32(reader.GetOrdinal("SupervisorId")),
                SupervisorName = reader.IsDBNull(reader.GetOrdinal("SupervisorName")) ? null : reader.GetString(reader.GetOrdinal("SupervisorName")),
                Location = reader.IsDBNull(reader.GetOrdinal("Location")) ? null : reader.GetString(reader.GetOrdinal("Location")),
                Remarks = reader.IsDBNull(reader.GetOrdinal("Remarks")) ? null : reader.GetString(reader.GetOrdinal("Remarks")),
                GrowingPartnerId = reader.IsDBNull(reader.GetOrdinal("GrowingPartnerId")) ? (int?)null : reader.GetInt32(reader.GetOrdinal("GrowingPartnerId")),
                GrowingPartnerName = reader.IsDBNull(reader.GetOrdinal("GrowingPartnerName")) ? null : reader.GetString(reader.GetOrdinal("GrowingPartnerName"))
            };
        }
    }
}
