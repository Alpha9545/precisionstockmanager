using PlantStockManager.Models;
using PlantStockManager.Services;
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

        // Admin > Areas only: active AND inactive Areas, so a deactivated Area
        // stays visible and can be activated again. Every other screen keeps
        // using the active-only GetAllAreas.
        public async Task<List<Area>> GetAllAreasForAdminAsync()
        {
            var areas = new List<Area>();
            using var conn = _dbHelper.GetConnection();
            await conn.OpenAsync();
            using var cmd = new SqlCommand(BaseSelect + " ORDER BY a.IsActive DESC, a.Name", conn);
            using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
                areas.Add(Map(reader));
            return areas;
        }

        // Read-only: how many records still use this Area, per relationship.
        public async Task<List<DependencyCount>> GetDeletionCheckAsync(int id)
        {
            using var conn = _dbHelper.GetConnection();
            await conn.OpenAsync();
            return await DependencyChecker.CountAsync(conn, null, DeletionRules.AreaTable, DeletionRules.AreaDependencies, id);
        }

        public async Task<DeleteResult> DeleteAsync(int id)
        {
            using var conn = _dbHelper.GetConnection();
            await conn.OpenAsync();
            using var tx = conn.BeginTransaction();
            try
            {
                var result = await DeleteAsync(conn, tx, id);
                if (result.Succeeded) tx.Commit(); else tx.Rollback();
                return result;
            }
            catch
            {
                try { tx.Rollback(); } catch { }
                throw;
            }
        }

        // Deletes the Area only if nothing references it. The Area row is
        // locked first, so no new reference can slip in between the check
        // and the DELETE; if the database still refuses (a foreign key the
        // check did not see), that single statement is rolled back to the
        // savepoint and reported -- nothing is ever partially deleted and no
        // child row is touched. Runs on the caller's transaction; the caller
        // commits or rolls back.
        public async Task<DeleteResult> DeleteAsync(SqlConnection conn, SqlTransaction tx, int id)
        {
            string? name;
            using (var lockCmd = new SqlCommand("SELECT Name FROM dbo.Area WITH (UPDLOCK, HOLDLOCK) WHERE Id = @Id", conn, tx))
            {
                lockCmd.Parameters.AddWithValue("@Id", id);
                name = await lockCmd.ExecuteScalarAsync() as string;
            }
            if (name == null)
                return new DeleteResult(DeleteOutcome.NotFound, "Area not found.", Array.Empty<DependencyCount>());

            var dependencies = await DependencyChecker.CountAsync(conn, tx, DeletionRules.AreaTable, DeletionRules.AreaDependencies, id);
            if (!DeletionRules.CanDelete(dependencies))
                return new DeleteResult(DeleteOutcome.Blocked, DeletionRules.BlockedMessage("Area", dependencies), dependencies);

            const string savepoint = "BeforeAreaDelete";
            tx.Save(savepoint);
            try
            {
                using var delete = new SqlCommand("DELETE FROM dbo.Area WHERE Id = @Id", conn, tx);
                delete.Parameters.AddWithValue("@Id", id);
                await delete.ExecuteNonQueryAsync();
            }
            catch (SqlException ex) when (ex.Number == 547)
            {
                tx.Rollback(savepoint);
                var again = await DependencyChecker.CountAsync(conn, tx, DeletionRules.AreaTable, DeletionRules.AreaDependencies, id);
                return new DeleteResult(DeleteOutcome.Blocked, DeletionRules.BlockedMessage("Area", again), again);
            }
            return new DeleteResult(DeleteOutcome.Deleted, DeletionRules.DeletedMessage("Area", name.Trim()), dependencies);
        }

        // Read-only: current stock / work in progress still held in this Area.
        public async Task<List<DependencyCount>> GetStockCheckAsync(int id)
        {
            using var conn = _dbHelper.GetConnection();
            await conn.OpenAsync();
            return await DependencyChecker.CountStockAsync(conn, null, DeletionRules.AreaStockChecks, id);
        }

        // Deactivate: only IsActive changes, every record that uses the Area is
        // kept -- and it is refused while the Area still holds current stock.
        public async Task<AreaDeactivateResult> DeactivateAsync(int id)
        {
            using var conn = _dbHelper.GetConnection();
            await conn.OpenAsync();
            using var tx = conn.BeginTransaction();
            try
            {
                var result = await DeactivateAsync(conn, tx, id);
                if (result.Succeeded) tx.Commit(); else tx.Rollback();
                return result;
            }
            catch
            {
                try { tx.Rollback(); } catch { }
                throw;
            }
        }

        // The UPDATE runs first so its exclusive lock holds the Area row while
        // the stock is counted; if stock is found the change is rolled back to
        // the savepoint. Runs on the caller's transaction.
        public async Task<AreaDeactivateResult> DeactivateAsync(SqlConnection conn, SqlTransaction tx, int id)
        {
            const string savepoint = "BeforeAreaDeactivate";
            tx.Save(savepoint);

            using (var update = new SqlCommand("UPDATE dbo.Area SET IsActive = 0 WHERE Id = @Id", conn, tx))
            {
                update.Parameters.AddWithValue("@Id", id);
                if (await update.ExecuteNonQueryAsync() == 0)
                {
                    tx.Rollback(savepoint);
                    return new AreaDeactivateResult(false, true, "Area not found.", Array.Empty<DependencyCount>());
                }
            }

            var stock = await DependencyChecker.CountStockAsync(conn, tx, DeletionRules.AreaStockChecks, id);
            if (!DeletionRules.CanDeactivateArea(stock))
            {
                tx.Rollback(savepoint);
                return new AreaDeactivateResult(false, false, DeletionRules.AreaDeactivationBlockedMessage(stock), stock);
            }
            return new AreaDeactivateResult(true, false, "Area deactivated.", stock);
        }

        public async Task<bool> ActivateAsync(int id)
        {
            using var conn = _dbHelper.GetConnection();
            await conn.OpenAsync();
            return await ActivateAsync(conn, null, id);
        }

        public async Task<bool> ActivateAsync(SqlConnection conn, SqlTransaction? tx, int id)
        {
            using var cmd = new SqlCommand("UPDATE dbo.Area SET IsActive = 1 WHERE Id = @Id", conn, tx);
            cmd.Parameters.AddWithValue("@Id", id);
            return await cmd.ExecuteNonQueryAsync() > 0;
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
