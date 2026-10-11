using System.Text;
using Microsoft.Data.SqlClient;
using PlantStockManager.Models;
using PlantStockManager.Services;

namespace PlantStockManager.Data
{
    // dbo.DailyLabourAttendance (Database/Migrations/2026-10-06_DailyLabourAttendance.sql):
    // one row per Date + location + attendance time (9:00 AM / 2:00 PM).
    // SaveAsync writes both times of one location/date in ONE transaction, serialized
    // per location/date by sp_getapplock: a time not yet recorded is inserted, a recorded time is
    // corrected only when a value really changes (the audit trigger then writes
    // the old and new values to dbo.DailyLabourAttendanceHistory), an unchanged
    // time is left alone. The filtered unique indexes are the final guard against
    // duplicates. Authorization is decided by the page (AreaAccessService) BEFORE
    // calling here; the location rule and the "no attendance where a manual record
    // exists" rule are repeated by TR_DailyLabourAttendance_Location.
    public class DailyLabourAttendanceRepository
    {
        public const int MaxListRows = 1000;

        private readonly DatabaseHelper _dbHelper;

        public DailyLabourAttendanceRepository(DatabaseHelper dbHelper)
        {
            _dbHelper = dbHelper;
        }

        public enum SaveOutcome { Saved, ManualRecordExists }

        public sealed class SaveResult
        {
            public SaveOutcome Outcome { get; init; }
            public List<string> Created { get; } = new();
            public List<string> Corrected { get; } = new();
            public List<string> Unchanged { get; } = new();
        }

        public async Task<List<DailyLabourAttendanceEntry>> GetEntriesAsync(DateTime labourDate, int areaId, int? polyhouseId)
        {
            using var conn = _dbHelper.GetConnection();
            await conn.OpenAsync();
            return await GetEntriesAsync(conn, null, labourDate, areaId, polyhouseId, lockRows: false);
        }

        // True when the location/date has an older manual dbo.DailyLabourCounts record.
        public async Task<bool> ManualRecordExistsAsync(DateTime labourDate, int areaId, int? polyhouseId)
        {
            using var conn = _dbHelper.GetConnection();
            await conn.OpenAsync();
            return await ManualRecordExistsAsync(conn, null, labourDate, areaId, polyhouseId);
        }

        public async Task<SaveResult> SaveAsync(DateTime labourDate, int areaId, int? polyhouseId, IReadOnlyList<DailyLabourAttendanceInput> inputs, int? userId)
        {
            try
            {
                return await SaveOnceAsync(labourDate, areaId, polyhouseId, inputs, userId);
            }
            catch (SqlException ex) when (ex.Number is 2601 or 2627)
            {
                // A simultaneous submit inserted the same time first: run again -- that
                // time is now an existing row and is corrected (or left unchanged).
                return await SaveOnceAsync(labourDate, areaId, polyhouseId, inputs, userId);
            }
        }

        private async Task<SaveResult> SaveOnceAsync(DateTime labourDate, int areaId, int? polyhouseId, IReadOnlyList<DailyLabourAttendanceInput> inputs, int? userId)
        {
            using var conn = _dbHelper.GetConnection();
            await conn.OpenAsync();
            using var tx = conn.BeginTransaction();
            try
            {
                await LockLocationDayAsync(conn, tx, labourDate, areaId, polyhouseId);
                var existing = await GetEntriesAsync(conn, tx, labourDate, areaId, polyhouseId, lockRows: true);
                if (await ManualRecordExistsAsync(conn, tx, labourDate, areaId, polyhouseId))
                {
                    tx.Rollback();
                    return new SaveResult { Outcome = SaveOutcome.ManualRecordExists };
                }

                var result = new SaveResult { Outcome = SaveOutcome.Saved };
                foreach (var input in inputs)
                {
                    var remarks = string.IsNullOrWhiteSpace(input.Remarks) ? null : input.Remarks.Trim();
                    var row = existing.FirstOrDefault(e => e.AttendanceTime == input.AttendanceTime);
                    if (row == null)
                    {
                        using var insert = new SqlCommand(@"
INSERT INTO dbo.DailyLabourAttendance (LabourDate, AreaId, PolyhouseId, AttendanceTime, MaleCount, FemaleCount, Remarks, CreatedDate, CreatedById)
VALUES (@LabourDate, @AreaId, @PolyhouseId, @AttendanceTime, @MaleCount, @FemaleCount, @Remarks, SYSUTCDATETIME(), @UserId);", conn, tx);
                        insert.Parameters.AddWithValue("@LabourDate", labourDate.Date);
                        insert.Parameters.AddWithValue("@AreaId", areaId);
                        insert.Parameters.AddWithValue("@PolyhouseId", (object?)polyhouseId ?? DBNull.Value);
                        insert.Parameters.AddWithValue("@AttendanceTime", input.AttendanceTime);
                        AddValues(insert, input.MaleCount, input.FemaleCount, remarks, userId);
                        await insert.ExecuteNonQueryAsync();
                        result.Created.Add(input.AttendanceTime);
                    }
                    else if (row.MaleCount != input.MaleCount || row.FemaleCount != input.FemaleCount || (row.Remarks ?? "") != (remarks ?? ""))
                    {
                        using var update = new SqlCommand(@"
UPDATE dbo.DailyLabourAttendance
SET MaleCount = @MaleCount, FemaleCount = @FemaleCount, Remarks = @Remarks, ModifiedDate = SYSUTCDATETIME(), ModifiedById = @UserId
WHERE Id = @Id;", conn, tx);
                        update.Parameters.AddWithValue("@Id", row.Id);
                        AddValues(update, input.MaleCount, input.FemaleCount, remarks, userId);
                        await update.ExecuteNonQueryAsync();
                        result.Corrected.Add(input.AttendanceTime);
                    }
                    else
                    {
                        result.Unchanged.Add(input.AttendanceTime);
                    }
                }
                tx.Commit();
                return result;
            }
            catch (SqlException ex) when (ex.Number == 51725)
            {
                if (tx.Connection != null) tx.Rollback();
                return new SaveResult { Outcome = SaveOutcome.ManualRecordExists };
            }
            catch
            {
                if (tx.Connection != null) tx.Rollback();
                throw;
            }
        }

        // The audit trail of one location/date, oldest first.
        public async Task<List<DailyLabourAttendanceChange>> GetChangesAsync(DateTime labourDate, int areaId, int? polyhouseId)
        {
            var list = new List<DailyLabourAttendanceChange>();
            using var conn = _dbHelper.GetConnection();
            await conn.OpenAsync();
            using var cmd = new SqlCommand(@"
SELECT h.Id, h.AttendanceTime, h.Action, h.OldMaleCount, h.OldFemaleCount, h.OldRemarks,
       h.NewMaleCount, h.NewFemaleCount, h.NewRemarks, u.Name, h.ChangedDate
FROM dbo.DailyLabourAttendanceHistory h
LEFT JOIN dbo.IMSUsers u ON u.Id = h.ChangedById
WHERE h.LabourDate = @LabourDate AND h.AreaId = @AreaId
  AND ((@PolyhouseId IS NULL AND h.PolyhouseId IS NULL) OR h.PolyhouseId = @PolyhouseId)
ORDER BY h.Id", conn);
            cmd.Parameters.AddWithValue("@LabourDate", labourDate.Date);
            cmd.Parameters.AddWithValue("@AreaId", areaId);
            cmd.Parameters.AddWithValue("@PolyhouseId", (object?)polyhouseId ?? DBNull.Value);
            using var r = await cmd.ExecuteReaderAsync();
            while (await r.ReadAsync())
            {
                list.Add(new DailyLabourAttendanceChange
                {
                    Id = r.GetInt32(0),
                    AttendanceTime = r.GetString(1),
                    Action = r.GetString(2),
                    OldMaleCount = r.IsDBNull(3) ? null : r.GetInt32(3),
                    OldFemaleCount = r.IsDBNull(4) ? null : r.GetInt32(4),
                    OldRemarks = r.IsDBNull(5) ? null : r.GetString(5),
                    NewMaleCount = r.IsDBNull(6) ? null : r.GetInt32(6),
                    NewFemaleCount = r.IsDBNull(7) ? null : r.GetInt32(7),
                    NewRemarks = r.IsDBNull(8) ? null : r.GetString(8),
                    ChangedByName = r.IsDBNull(9) ? null : r.GetString(9),
                    ChangedDate = DateTime.SpecifyKind(r.GetDateTime(10), DateTimeKind.Utc),
                });
            }
            return list;
        }

        // Attendance History: attendance days (both times pivoted into one row per
        // location/date) plus the older manual records, newest first. areaScope:
        // null = every Area (full-access user); otherwise only these Area ids (an
        // empty list returns nothing). The summary covers EVERY matching day; at most
        // MaxListRows days are returned for display.
        public async Task<(List<DailyLabourDay> Days, DailyLabourDaySummary Summary)> SearchDaysAsync(DailyLabourFilter filter, IReadOnlyCollection<int>? areaScope)
        {
            var days = new List<DailyLabourDay>();
            var summary = new DailyLabourDaySummary();
            if (areaScope != null && areaScope.Count == 0)
                return (days, summary);

            using var conn = _dbHelper.GetConnection();
            await conn.OpenAsync();
            using var cmd = new SqlCommand { Connection = conn };
            var whereAttendance = BuildWhere(cmd, filter, areaScope, "d");
            var whereManual = BuildWhere(cmd, filter, areaScope, "l", addParameters: false);
            cmd.CommandText = $@"
WITH d AS (
    SELECT LabourDate, AreaId, PolyhouseId,
           MAX(CASE WHEN AttendanceTime = @Morning   THEN MaleCount END)   AS Male9,
           MAX(CASE WHEN AttendanceTime = @Morning   THEN FemaleCount END) AS Female9,
           MAX(CASE WHEN AttendanceTime = @Afternoon THEN MaleCount END)   AS Male2,
           MAX(CASE WHEN AttendanceTime = @Afternoon THEN FemaleCount END) AS Female2,
           MAX(CASE WHEN ModifiedDate IS NOT NULL THEN 1 ELSE 0 END)       AS Corrected
    FROM dbo.DailyLabourAttendance
    GROUP BY LabourDate, AreaId, PolyhouseId
)
SELECT 'Attendance' AS Source, CAST(NULL AS INT) AS ManualId, d.LabourDate, d.AreaId, a.Name, d.PolyhouseId, p.Name,
       d.Male9, d.Female9, d.Male2, d.Female2, d.Corrected, 0, 0, 0, 0
FROM d
INNER JOIN dbo.Area a ON a.Id = d.AreaId
LEFT JOIN dbo.Polyhouses p ON p.Id = d.PolyhouseId
{whereAttendance}
UNION ALL
SELECT 'Manual', l.Id, l.LabourDate, l.AreaId, a.Name, l.PolyhouseId, p.Name,
       NULL, NULL, NULL, NULL, CASE WHEN l.ModifiedDate IS NULL THEN 0 ELSE 1 END,
       l.MaleFullDay, l.MaleHalfDay, l.FemaleFullDay, l.FemaleHalfDay
FROM dbo.DailyLabourCounts l
INNER JOIN dbo.Area a ON a.Id = l.AreaId
LEFT JOIN dbo.Polyhouses p ON p.Id = l.PolyhouseId
{whereManual}
ORDER BY LabourDate DESC, 5, 7;";
            cmd.Parameters.AddWithValue("@Morning", DailyLabourRules.Morning);
            cmd.Parameters.AddWithValue("@Afternoon", DailyLabourRules.Afternoon);

            using var r = await cmd.ExecuteReaderAsync();
            while (await r.ReadAsync())
            {
                var day = new DailyLabourDay
                {
                    Source = r.GetString(0) == DailyLabourDay.ManualSource ? DailyLabourDay.ManualSource : DailyLabourDay.AttendanceSource,
                    ManualId = r.IsDBNull(1) ? null : r.GetInt32(1),
                    LabourDate = r.GetDateTime(2),
                    AreaId = r.GetInt32(3),
                    AreaName = r.IsDBNull(4) ? null : r.GetString(4),
                    PolyhouseId = r.IsDBNull(5) ? null : r.GetInt32(5),
                    PolyhouseName = r.IsDBNull(6) ? null : r.GetString(6),
                    Male9 = r.IsDBNull(7) ? null : r.GetInt32(7),
                    Female9 = r.IsDBNull(8) ? null : r.GetInt32(8),
                    Male2 = r.IsDBNull(9) ? null : r.GetInt32(9),
                    Female2 = r.IsDBNull(10) ? null : r.GetInt32(10),
                    Corrected = r.GetInt32(11) == 1,
                    ManualMaleFullDay = r.GetInt32(12),
                    ManualMaleHalfDay = r.GetInt32(13),
                    ManualFemaleFullDay = r.GetInt32(14),
                    ManualFemaleHalfDay = r.GetInt32(15),
                };
                summary.Add(day);
                if (days.Count < MaxListRows)
                    days.Add(day);
            }
            return (days, summary);
        }

        private static async Task<List<DailyLabourAttendanceEntry>> GetEntriesAsync(SqlConnection conn, SqlTransaction? tx, DateTime labourDate, int areaId, int? polyhouseId, bool lockRows)
        {
            var hint = lockRows ? " WITH (UPDLOCK, HOLDLOCK)" : "";
            var location = polyhouseId.HasValue ? "t.PolyhouseId = @PolyhouseId" : "t.PolyhouseId IS NULL";
            using var cmd = new SqlCommand($@"
SELECT t.Id, t.LabourDate, t.AreaId, t.PolyhouseId, t.AttendanceTime, t.MaleCount, t.FemaleCount, t.Remarks,
       t.CreatedDate, t.CreatedById, cu.Name, t.ModifiedDate, t.ModifiedById, mu.Name
FROM dbo.DailyLabourAttendance t{hint}
LEFT JOIN dbo.IMSUsers cu ON cu.Id = t.CreatedById
LEFT JOIN dbo.IMSUsers mu ON mu.Id = t.ModifiedById
WHERE t.LabourDate = @LabourDate AND t.AreaId = @AreaId AND {location}
ORDER BY t.AttendanceTime", conn, tx);
            cmd.Parameters.AddWithValue("@LabourDate", labourDate.Date);
            cmd.Parameters.AddWithValue("@AreaId", areaId);
            if (polyhouseId.HasValue)
                cmd.Parameters.AddWithValue("@PolyhouseId", polyhouseId.Value);
            var list = new List<DailyLabourAttendanceEntry>();
            using var r = await cmd.ExecuteReaderAsync();
            while (await r.ReadAsync())
            {
                list.Add(new DailyLabourAttendanceEntry
                {
                    Id = r.GetInt32(0),
                    LabourDate = r.GetDateTime(1),
                    AreaId = r.GetInt32(2),
                    PolyhouseId = r.IsDBNull(3) ? null : r.GetInt32(3),
                    AttendanceTime = r.GetString(4),
                    MaleCount = r.GetInt32(5),
                    FemaleCount = r.GetInt32(6),
                    Remarks = r.IsDBNull(7) ? null : r.GetString(7),
                    CreatedDate = DateTime.SpecifyKind(r.GetDateTime(8), DateTimeKind.Utc),
                    CreatedById = r.IsDBNull(9) ? null : r.GetInt32(9),
                    CreatedByName = r.IsDBNull(10) ? null : r.GetString(10),
                    ModifiedDate = r.IsDBNull(11) ? null : DateTime.SpecifyKind(r.GetDateTime(11), DateTimeKind.Utc),
                    ModifiedById = r.IsDBNull(12) ? null : r.GetInt32(12),
                    ModifiedByName = r.IsDBNull(13) ? null : r.GetString(13),
                });
            }
            return list;
        }

        // sp_getapplock: an exclusive, transaction-scoped mutex on this exact location/date,
        // taken FIRST -- a true key lock whether or not any row exists yet. Without it two
        // simultaneous saves of a new day each took a range lock on the empty key range and
        // then deadlocked on their INSERTs (found by SimultaneousSaves_NeverCreateADuplicateTime).
        // Saves of the same location/date now run strictly one after the other; every other
        // location/date uses a different key and is unaffected. Released with the transaction.
        private static async Task LockLocationDayAsync(SqlConnection conn, SqlTransaction tx, DateTime labourDate, int areaId, int? polyhouseId)
        {
            using var cmd = new SqlCommand("sp_getapplock", conn, tx) { CommandType = System.Data.CommandType.StoredProcedure };
            cmd.Parameters.AddWithValue("@Resource", $"DailyLabourAttendance:{labourDate:yyyy-MM-dd}:{areaId}:{polyhouseId?.ToString() ?? "NULL"}");
            cmd.Parameters.AddWithValue("@LockMode", "Exclusive");
            cmd.Parameters.AddWithValue("@LockOwner", "Transaction");
            cmd.Parameters.AddWithValue("@LockTimeout", 15000);
            var returnValue = cmd.Parameters.Add("@ReturnValue", System.Data.SqlDbType.Int);
            returnValue.Direction = System.Data.ParameterDirection.ReturnValue;
            await cmd.ExecuteNonQueryAsync();
            if ((int)returnValue.Value < 0)
                throw new InvalidOperationException("Another attendance save for this date and location is in progress. Please try again.");
        }

        private static void AddValues(SqlCommand cmd, int maleCount, int femaleCount, string? remarks, int? userId)
        {
            cmd.Parameters.AddWithValue("@MaleCount", maleCount);
            cmd.Parameters.AddWithValue("@FemaleCount", femaleCount);
            cmd.Parameters.AddWithValue("@Remarks", (object?)remarks ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@UserId", (object?)userId ?? DBNull.Value);
        }

        private static async Task<bool> ManualRecordExistsAsync(SqlConnection conn, SqlTransaction? tx, DateTime labourDate, int areaId, int? polyhouseId)
        {
            var location = polyhouseId.HasValue ? "PolyhouseId = @PolyhouseId" : "PolyhouseId IS NULL";
            using var cmd = new SqlCommand($"SELECT COUNT(*) FROM dbo.DailyLabourCounts WHERE LabourDate = @LabourDate AND AreaId = @AreaId AND {location}", conn, tx);
            cmd.Parameters.AddWithValue("@LabourDate", labourDate.Date);
            cmd.Parameters.AddWithValue("@AreaId", areaId);
            if (polyhouseId.HasValue)
                cmd.Parameters.AddWithValue("@PolyhouseId", polyhouseId.Value);
            return (int)(await cmd.ExecuteScalarAsync())! > 0;
        }

        // Same filters for both halves of the history query; alias is the
        // location/date row ("d" attendance, "l" manual), "a" is dbo.Area.
        private static string BuildWhere(SqlCommand cmd, DailyLabourFilter filter, IReadOnlyCollection<int>? areaScope, string alias, bool addParameters = true)
        {
            void Add(string name, object value)
            {
                if (addParameters)
                    cmd.Parameters.AddWithValue(name, value);
            }

            var sb = new StringBuilder(" WHERE (a.AreaType IS NULL OR a.AreaType <> @OutletType)");
            Add("@OutletType", OutletRules.AreaType);
            if (filter.FromDate.HasValue)
            {
                sb.Append($" AND {alias}.LabourDate >= @FromDate");
                Add("@FromDate", filter.FromDate.Value.Date);
            }
            if (filter.ToDate.HasValue)
            {
                sb.Append($" AND {alias}.LabourDate <= @ToDate");
                Add("@ToDate", filter.ToDate.Value.Date);
            }
            if (filter.AreaId.HasValue)
            {
                sb.Append($" AND {alias}.AreaId = @AreaId");
                Add("@AreaId", filter.AreaId.Value);
            }
            if (filter.PolyhouseId.HasValue)
            {
                sb.Append($" AND {alias}.PolyhouseId = @PolyhouseId");
                Add("@PolyhouseId", filter.PolyhouseId.Value);
            }
            if (areaScope != null)
            {
                var names = areaScope.Select((id, i) =>
                {
                    Add("@Scope" + i, id);
                    return "@Scope" + i;
                }).ToList();
                sb.Append($" AND {alias}.AreaId IN (").Append(string.Join(", ", names)).Append(')');
            }
            return sb.ToString();
        }
    }
}
