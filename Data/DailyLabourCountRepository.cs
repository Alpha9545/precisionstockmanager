using System.Text;
using Microsoft.Data.SqlClient;
using PlantStockManager.Models;

namespace PlantStockManager.Data
{
    // dbo.DailyLabourCounts (Database/Migrations/2026-10-04_DailyLabourCounts.sql).
    // The location + date is the identity of a record, so a "new" entry for a
    // location/date that already has one never creates a second row:
    // CreateAsync checks under UPDLOCK/HOLDLOCK (two simultaneous submits are
    // serialized; the second finds the first) and the filtered unique indexes
    // are the final guard. Authorization is decided by the page
    // (AreaAccessService) BEFORE calling here; the location rule is repeated
    // by TR_DailyLabourCounts_Location, which also rejects Outlet Areas (Outlet
    // is not part of Daily Labour; its Areas are left out of every list here).
    public class DailyLabourCountRepository
    {
        public const int MaxListRows = 1000;

        private readonly DatabaseHelper _dbHelper;

        public DailyLabourCountRepository(DatabaseHelper dbHelper)
        {
            _dbHelper = dbHelper;
        }

        public enum CreateOutcome { Created, AlreadyExists }

        private const string BaseSelect = @"
SELECT l.Id, l.LabourDate, l.AreaId, a.Name AS AreaName, a.AreaType, l.PolyhouseId, p.Name AS PolyhouseName,
       l.MaleFullDay, l.MaleHalfDay, l.FemaleFullDay, l.FemaleHalfDay,
       l.CreatedDate, l.CreatedById, cu.Name AS CreatedByName, l.ModifiedDate, l.ModifiedById, mu.Name AS ModifiedByName,
       l.Remarks
FROM dbo.DailyLabourCounts l
INNER JOIN dbo.Area a ON a.Id = l.AreaId
LEFT JOIN dbo.Polyhouses p ON p.Id = l.PolyhouseId
LEFT JOIN dbo.IMSUsers cu ON cu.Id = l.CreatedById
LEFT JOIN dbo.IMSUsers mu ON mu.Id = l.ModifiedById";

        public async Task<DailyLabourCount?> GetByIdAsync(int id)
        {
            using var conn = _dbHelper.GetConnection();
            await conn.OpenAsync();
            using var cmd = new SqlCommand(BaseSelect + " WHERE l.Id = @Id", conn);
            cmd.Parameters.AddWithValue("@Id", id);
            using var reader = await cmd.ExecuteReaderAsync();
            return await reader.ReadAsync() ? Map(reader) : null;
        }

        // The existing record of a location on a date, if any.
        public async Task<int?> FindIdAsync(DateTime labourDate, int areaId, int? polyhouseId)
        {
            using var conn = _dbHelper.GetConnection();
            await conn.OpenAsync();
            return await FindIdAsync(conn, null, labourDate, areaId, polyhouseId, lockRow: false);
        }

        // areaScope: null = every Area (full-access user); otherwise only
        // these Area ids (an empty list returns nothing). Newest first, at
        // most MaxListRows rows; the summary always covers ALL matching rows.
        public async Task<(List<DailyLabourCount> Rows, DailyLabourSummary Summary)> SearchAsync(DailyLabourFilter filter, IReadOnlyCollection<int>? areaScope)
        {
            var rows = new List<DailyLabourCount>();
            var summary = new DailyLabourSummary();
            if (areaScope != null && areaScope.Count == 0)
                return (rows, summary);

            using var conn = _dbHelper.GetConnection();
            await conn.OpenAsync();

            using (var cmd = new SqlCommand())
            {
                cmd.Connection = conn;
                var where = BuildWhere(cmd, filter, areaScope);
                cmd.CommandText = BaseSelect.Replace("SELECT l.Id", $"SELECT TOP ({MaxListRows}) l.Id") + where
                    + " ORDER BY l.LabourDate DESC, a.Name, p.Name";
                using var reader = await cmd.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                    rows.Add(Map(reader));
            }

            using (var cmd = new SqlCommand())
            {
                cmd.Connection = conn;
                var where = BuildWhere(cmd, filter, areaScope);
                cmd.CommandText = @"
SELECT COUNT(*), ISNULL(SUM(l.MaleFullDay), 0), ISNULL(SUM(l.MaleHalfDay), 0), ISNULL(SUM(l.FemaleFullDay), 0), ISNULL(SUM(l.FemaleHalfDay), 0)
FROM dbo.DailyLabourCounts l
INNER JOIN dbo.Area a ON a.Id = l.AreaId" + where;
                using var reader = await cmd.ExecuteReaderAsync();
                if (await reader.ReadAsync())
                {
                    summary.Records = reader.GetInt32(0);
                    summary.MaleFullDay = reader.GetInt32(1);
                    summary.MaleHalfDay = reader.GetInt32(2);
                    summary.FemaleFullDay = reader.GetInt32(3);
                    summary.FemaleHalfDay = reader.GetInt32(4);
                }
            }
            return (rows, summary);
        }

        // Creates the record of a location/date, or -- when one already
        // exists (including one saved a moment ago by a simultaneous submit)
        // -- creates nothing and returns the existing Id.
        public async Task<(CreateOutcome Outcome, int Id)> CreateAsync(DailyLabourCount entry, int? userId)
        {
            using var conn = _dbHelper.GetConnection();
            await conn.OpenAsync();
            using var tx = conn.BeginTransaction();
            try
            {
                var existing = await FindIdAsync(conn, tx, entry.LabourDate, entry.AreaId, entry.PolyhouseId, lockRow: true);
                if (existing.HasValue)
                {
                    tx.Commit();
                    return (CreateOutcome.AlreadyExists, existing.Value);
                }

                using var cmd = new SqlCommand(@"
INSERT INTO dbo.DailyLabourCounts (LabourDate, AreaId, PolyhouseId, MaleFullDay, MaleHalfDay, FemaleFullDay, FemaleHalfDay, Remarks, CreatedDate, CreatedById)
VALUES (@LabourDate, @AreaId, @PolyhouseId, @MaleFullDay, @MaleHalfDay, @FemaleFullDay, @FemaleHalfDay, @Remarks, SYSUTCDATETIME(), @UserId);
SELECT CAST(SCOPE_IDENTITY() AS INT);", conn, tx);   // not OUTPUT: SQL Server forbids OUTPUT without INTO on a table with a trigger
                cmd.Parameters.AddWithValue("@LabourDate", entry.LabourDate.Date);
                cmd.Parameters.AddWithValue("@AreaId", entry.AreaId);
                cmd.Parameters.AddWithValue("@PolyhouseId", (object?)entry.PolyhouseId ?? DBNull.Value);
                AddCounts(cmd, entry);
                cmd.Parameters.AddWithValue("@UserId", (object?)userId ?? DBNull.Value);
                var id = (int)(await cmd.ExecuteScalarAsync())!;
                tx.Commit();
                return (CreateOutcome.Created, id);
            }
            catch (SqlException ex) when (ex.Number is 2601 or 2627)
            {
                // Unique index: another submit won the race -- return its record.
                if (tx.Connection != null) tx.Rollback();
                var id = await FindIdAsync(entry.LabourDate, entry.AreaId, entry.PolyhouseId);
                if (id.HasValue)
                    return (CreateOutcome.AlreadyExists, id.Value);
                throw;
            }
            catch
            {
                if (tx.Connection != null) tx.Rollback();
                throw;
            }
        }

        // Corrects the four counts and the remark of an existing record. Date
        // and location are the record's identity and are never changed here.
        public async Task<bool> UpdateCountsAsync(int id, DailyLabourCount counts, int? userId)
        {
            using var conn = _dbHelper.GetConnection();
            await conn.OpenAsync();
            using var cmd = new SqlCommand(@"
UPDATE dbo.DailyLabourCounts
SET MaleFullDay = @MaleFullDay, MaleHalfDay = @MaleHalfDay, FemaleFullDay = @FemaleFullDay, FemaleHalfDay = @FemaleHalfDay,
    Remarks = @Remarks, ModifiedDate = SYSUTCDATETIME(), ModifiedById = @UserId
WHERE Id = @Id", conn);
            cmd.Parameters.AddWithValue("@Id", id);
            AddCounts(cmd, counts);
            cmd.Parameters.AddWithValue("@UserId", (object?)userId ?? DBNull.Value);
            return await cmd.ExecuteNonQueryAsync() == 1;
        }

        private static async Task<int?> FindIdAsync(SqlConnection conn, SqlTransaction? tx, DateTime labourDate, int areaId, int? polyhouseId, bool lockRow)
        {
            var hint = lockRow ? " WITH (UPDLOCK, HOLDLOCK)" : "";
            var sql = polyhouseId.HasValue
                ? $"SELECT Id FROM dbo.DailyLabourCounts{hint} WHERE LabourDate = @LabourDate AND AreaId = @AreaId AND PolyhouseId = @PolyhouseId"
                : $"SELECT Id FROM dbo.DailyLabourCounts{hint} WHERE LabourDate = @LabourDate AND AreaId = @AreaId AND PolyhouseId IS NULL";
            using var cmd = new SqlCommand(sql, conn, tx);
            cmd.Parameters.AddWithValue("@LabourDate", labourDate.Date);
            cmd.Parameters.AddWithValue("@AreaId", areaId);
            if (polyhouseId.HasValue)
                cmd.Parameters.AddWithValue("@PolyhouseId", polyhouseId.Value);
            var result = await cmd.ExecuteScalarAsync();
            return result == null || result == DBNull.Value ? null : (int)result;
        }

        private static string BuildWhere(SqlCommand cmd, DailyLabourFilter filter, IReadOnlyCollection<int>? areaScope)
        {
            var sb = new StringBuilder(" WHERE (a.AreaType IS NULL OR a.AreaType <> @OutletType)");
            cmd.Parameters.AddWithValue("@OutletType", Services.OutletRules.AreaType);
            if (filter.FromDate.HasValue)
            {
                sb.Append(" AND l.LabourDate >= @FromDate");
                cmd.Parameters.AddWithValue("@FromDate", filter.FromDate.Value.Date);
            }
            if (filter.ToDate.HasValue)
            {
                sb.Append(" AND l.LabourDate <= @ToDate");
                cmd.Parameters.AddWithValue("@ToDate", filter.ToDate.Value.Date);
            }
            if (filter.AreaId.HasValue)
            {
                sb.Append(" AND l.AreaId = @AreaId");
                cmd.Parameters.AddWithValue("@AreaId", filter.AreaId.Value);
            }
            if (filter.PolyhouseId.HasValue)
            {
                sb.Append(" AND l.PolyhouseId = @PolyhouseId");
                cmd.Parameters.AddWithValue("@PolyhouseId", filter.PolyhouseId.Value);
            }
            if (areaScope != null)
            {
                var names = areaScope.Select((id, i) =>
                {
                    cmd.Parameters.AddWithValue("@Scope" + i, id);
                    return "@Scope" + i;
                }).ToList();
                sb.Append(" AND l.AreaId IN (").Append(string.Join(", ", names)).Append(')');
            }
            return sb.ToString();
        }

        private static void AddCounts(SqlCommand cmd, DailyLabourCount c)
        {
            cmd.Parameters.AddWithValue("@MaleFullDay", c.MaleFullDay);
            cmd.Parameters.AddWithValue("@MaleHalfDay", c.MaleHalfDay);
            cmd.Parameters.AddWithValue("@FemaleFullDay", c.FemaleFullDay);
            cmd.Parameters.AddWithValue("@FemaleHalfDay", c.FemaleHalfDay);
            cmd.Parameters.AddWithValue("@Remarks", string.IsNullOrWhiteSpace(c.Remarks) ? DBNull.Value : c.Remarks.Trim());
        }

        private static DailyLabourCount Map(SqlDataReader r) => new()
        {
            Id = r.GetInt32(0),
            LabourDate = r.GetDateTime(1),
            AreaId = r.GetInt32(2),
            AreaName = r.IsDBNull(3) ? null : r.GetString(3),
            AreaType = r.IsDBNull(4) ? null : r.GetString(4),
            PolyhouseId = r.IsDBNull(5) ? null : r.GetInt32(5),
            PolyhouseName = r.IsDBNull(6) ? null : r.GetString(6),
            MaleFullDay = r.GetInt32(7),
            MaleHalfDay = r.GetInt32(8),
            FemaleFullDay = r.GetInt32(9),
            FemaleHalfDay = r.GetInt32(10),
            CreatedDate = DateTime.SpecifyKind(r.GetDateTime(11), DateTimeKind.Utc),
            CreatedById = r.IsDBNull(12) ? null : r.GetInt32(12),
            CreatedByName = r.IsDBNull(13) ? null : r.GetString(13),
            ModifiedDate = r.IsDBNull(14) ? null : DateTime.SpecifyKind(r.GetDateTime(14), DateTimeKind.Utc),
            ModifiedById = r.IsDBNull(15) ? null : r.GetInt32(15),
            ModifiedByName = r.IsDBNull(16) ? null : r.GetString(16),
            Remarks = r.IsDBNull(17) ? null : r.GetString(17),
        };
    }
}
