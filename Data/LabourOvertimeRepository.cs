using System.Text;
using Microsoft.Data.SqlClient;
using PlantStockManager.Models;
using PlantStockManager.Services;

namespace PlantStockManager.Data
{
    // dbo.LabourOvertime (Database/Migrations/2026-10-06_LabourOvertime.sql): one row per
    // overtime ENTRY. Entries are never merged -- several on the same date/location, even
    // with overlapping times, are separate rows. Duration and worker-minutes are computed
    // columns (never written here). Every insert / correction is copied to
    // dbo.LabourOvertimeHistory by TR_LabourOvertime_Audit. Authorization is decided by the
    // page (AreaAccessService) BEFORE calling here; the location rule is repeated by
    // TR_LabourOvertime_Location and every value rule by CHECK constraints.
    public class LabourOvertimeRepository
    {
        private readonly DatabaseHelper _dbHelper;

        public LabourOvertimeRepository(DatabaseHelper dbHelper)
        {
            _dbHelper = dbHelper;
        }

        public enum UpdateOutcome { Corrected, Unchanged, NotFound }

        private const string BaseSelect = @"
SELECT o.Id, o.LabourDate, o.AreaId, a.Name AS AreaName, a.AreaType, o.PolyhouseId, p.Name AS PolyhouseName,
       o.MaleWorkers, o.FemaleWorkers, o.StartTime, o.EndTime, o.DurationMinutes, o.MaleWorkerMinutes, o.FemaleWorkerMinutes,
       o.WorkDescription, o.Remarks, o.CreatedDate, o.CreatedById, cu.Name, o.ModifiedDate, o.ModifiedById, mu.Name
FROM dbo.LabourOvertime o
INNER JOIN dbo.Area a ON a.Id = o.AreaId
LEFT JOIN dbo.Polyhouses p ON p.Id = o.PolyhouseId
LEFT JOIN dbo.IMSUsers cu ON cu.Id = o.CreatedById
LEFT JOIN dbo.IMSUsers mu ON mu.Id = o.ModifiedById";

        public async Task<LabourOvertimeEntry?> GetByIdAsync(int id)
        {
            using var conn = _dbHelper.GetConnection();
            await conn.OpenAsync();
            using var cmd = new SqlCommand(BaseSelect + " WHERE o.Id = @Id", conn);
            cmd.Parameters.AddWithValue("@Id", id);
            using var r = await cmd.ExecuteReaderAsync();
            return await r.ReadAsync() ? Map(r) : null;
        }

        public async Task<int> CreateAsync(DateTime labourDate, int areaId, int? polyhouseId, LabourOvertimeInput input, int? userId)
        {
            using var conn = _dbHelper.GetConnection();
            await conn.OpenAsync();
            using var cmd = new SqlCommand(@"
INSERT INTO dbo.LabourOvertime (LabourDate, AreaId, PolyhouseId, MaleWorkers, FemaleWorkers, StartTime, EndTime, WorkDescription, Remarks, CreatedDate, CreatedById)
VALUES (@LabourDate, @AreaId, @PolyhouseId, @MaleWorkers, @FemaleWorkers, @StartTime, @EndTime, @WorkDescription, @Remarks, SYSUTCDATETIME(), @UserId);
SELECT CAST(SCOPE_IDENTITY() AS INT);", conn);   // not OUTPUT: SQL Server forbids OUTPUT without INTO on a table with a trigger
            cmd.Parameters.AddWithValue("@LabourDate", labourDate.Date);
            cmd.Parameters.AddWithValue("@AreaId", areaId);
            cmd.Parameters.AddWithValue("@PolyhouseId", (object?)polyhouseId ?? DBNull.Value);
            AddValues(cmd, input, userId);
            return (int)(await cmd.ExecuteScalarAsync())!;
        }

        // Corrects the workers, times, description and remark of an entry. Its date and
        // location are its identity and are never changed here. Unchanged values write nothing
        // (and so add no audit row).
        public async Task<UpdateOutcome> UpdateAsync(int id, LabourOvertimeInput input, int? userId)
        {
            using var conn = _dbHelper.GetConnection();
            await conn.OpenAsync();
            using var tx = conn.BeginTransaction();
            try
            {
                using var read = new SqlCommand(@"
SELECT MaleWorkers, FemaleWorkers, StartTime, EndTime, WorkDescription, Remarks
FROM dbo.LabourOvertime WITH (UPDLOCK, HOLDLOCK) WHERE Id = @Id", conn, tx);
                read.Parameters.AddWithValue("@Id", id);
                LabourOvertimeInput current;
                using (var r = await read.ExecuteReaderAsync())
                {
                    if (!await r.ReadAsync())
                    {
                        r.Close();
                        tx.Rollback();
                        return UpdateOutcome.NotFound;
                    }
                    current = new LabourOvertimeInput(r.GetInt32(0), r.GetInt32(1), TimeOnly.FromTimeSpan(r.GetTimeSpan(2)), TimeOnly.FromTimeSpan(r.GetTimeSpan(3)),
                        r.GetString(4), r.IsDBNull(5) ? null : r.GetString(5));
                }
                var next = Normalize(input);
                if (current == next)
                {
                    tx.Commit();
                    return UpdateOutcome.Unchanged;
                }

                using var update = new SqlCommand(@"
UPDATE dbo.LabourOvertime
SET MaleWorkers = @MaleWorkers, FemaleWorkers = @FemaleWorkers, StartTime = @StartTime, EndTime = @EndTime,
    WorkDescription = @WorkDescription, Remarks = @Remarks, ModifiedDate = SYSUTCDATETIME(), ModifiedById = @UserId
WHERE Id = @Id", conn, tx);
                update.Parameters.AddWithValue("@Id", id);
                AddValues(update, input, userId);
                await update.ExecuteNonQueryAsync();
                tx.Commit();
                return UpdateOutcome.Corrected;
            }
            catch
            {
                if (tx.Connection != null) tx.Rollback();
                throw;
            }
        }

        // Every matching entry, newest first. areaScope: null = every Area (full-access user);
        // otherwise only these Area ids (an empty list returns nothing).
        public async Task<List<LabourOvertimeEntry>> SearchAsync(LabourOvertimeFilter filter, IReadOnlyCollection<int>? areaScope)
        {
            var list = new List<LabourOvertimeEntry>();
            if (areaScope != null && areaScope.Count == 0)
                return list;
            using var conn = _dbHelper.GetConnection();
            await conn.OpenAsync();
            using var cmd = new SqlCommand { Connection = conn };
            cmd.CommandText = BaseSelect + BuildWhere(cmd, filter, areaScope) + " ORDER BY o.LabourDate DESC, o.StartTime DESC, o.Id DESC";
            using var r = await cmd.ExecuteReaderAsync();
            while (await r.ReadAsync())
                list.Add(Map(r));
            return list;
        }

        // The audit trail of one entry, oldest first.
        public async Task<List<LabourOvertimeChange>> GetChangesAsync(int overtimeId)
        {
            var list = new List<LabourOvertimeChange>();
            using var conn = _dbHelper.GetConnection();
            await conn.OpenAsync();
            using var cmd = new SqlCommand(@"
SELECT h.Id, h.Action, h.OldMaleWorkers, h.OldFemaleWorkers, h.OldStartTime, h.OldEndTime, h.OldWorkDescription, h.OldRemarks,
       h.NewMaleWorkers, h.NewFemaleWorkers, h.NewStartTime, h.NewEndTime, h.NewWorkDescription, h.NewRemarks, u.Name, h.ChangedDate
FROM dbo.LabourOvertimeHistory h
LEFT JOIN dbo.IMSUsers u ON u.Id = h.ChangedById
WHERE h.OvertimeId = @Id
ORDER BY h.Id", conn);
            cmd.Parameters.AddWithValue("@Id", overtimeId);
            using var r = await cmd.ExecuteReaderAsync();
            while (await r.ReadAsync())
            {
                list.Add(new LabourOvertimeChange
                {
                    Id = r.GetInt32(0),
                    Action = r.GetString(1),
                    OldMaleWorkers = r.IsDBNull(2) ? null : r.GetInt32(2),
                    OldFemaleWorkers = r.IsDBNull(3) ? null : r.GetInt32(3),
                    OldStartTime = r.IsDBNull(4) ? null : TimeOnly.FromTimeSpan(r.GetTimeSpan(4)),
                    OldEndTime = r.IsDBNull(5) ? null : TimeOnly.FromTimeSpan(r.GetTimeSpan(5)),
                    OldWorkDescription = r.IsDBNull(6) ? null : r.GetString(6),
                    OldRemarks = r.IsDBNull(7) ? null : r.GetString(7),
                    NewMaleWorkers = r.IsDBNull(8) ? null : r.GetInt32(8),
                    NewFemaleWorkers = r.IsDBNull(9) ? null : r.GetInt32(9),
                    NewStartTime = r.IsDBNull(10) ? null : TimeOnly.FromTimeSpan(r.GetTimeSpan(10)),
                    NewEndTime = r.IsDBNull(11) ? null : TimeOnly.FromTimeSpan(r.GetTimeSpan(11)),
                    NewWorkDescription = r.IsDBNull(12) ? null : r.GetString(12),
                    NewRemarks = r.IsDBNull(13) ? null : r.GetString(13),
                    ChangedByName = r.IsDBNull(14) ? null : r.GetString(14),
                    ChangedDate = DateTime.SpecifyKind(r.GetDateTime(15), DateTimeKind.Utc),
                });
            }
            return list;
        }

        // Trimmed text and whole-minute times, exactly as stored.
        private static LabourOvertimeInput Normalize(LabourOvertimeInput i) => i with
        {
            StartTime = LabourOvertimeRules.ToMinute(i.StartTime)!.Value,
            EndTime = LabourOvertimeRules.ToMinute(i.EndTime)!.Value,
            WorkDescription = i.WorkDescription.Trim(),
            Remarks = string.IsNullOrWhiteSpace(i.Remarks) ? null : i.Remarks.Trim(),
        };

        private static void AddValues(SqlCommand cmd, LabourOvertimeInput input, int? userId)
        {
            var n = Normalize(input);
            cmd.Parameters.AddWithValue("@MaleWorkers", n.MaleWorkers);
            cmd.Parameters.AddWithValue("@FemaleWorkers", n.FemaleWorkers);
            cmd.Parameters.Add("@StartTime", System.Data.SqlDbType.Time).Value = n.StartTime.ToTimeSpan();
            cmd.Parameters.Add("@EndTime", System.Data.SqlDbType.Time).Value = n.EndTime.ToTimeSpan();
            cmd.Parameters.AddWithValue("@WorkDescription", n.WorkDescription);
            cmd.Parameters.AddWithValue("@Remarks", (object?)n.Remarks ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@UserId", (object?)userId ?? DBNull.Value);
        }

        private static string BuildWhere(SqlCommand cmd, LabourOvertimeFilter filter, IReadOnlyCollection<int>? areaScope)
        {
            var sb = new StringBuilder(" WHERE (a.AreaType IS NULL OR a.AreaType <> @OutletType)");
            cmd.Parameters.AddWithValue("@OutletType", OutletRules.AreaType);
            if (filter.FromDate.HasValue)
            {
                sb.Append(" AND o.LabourDate >= @FromDate");
                cmd.Parameters.AddWithValue("@FromDate", filter.FromDate.Value.Date);
            }
            if (filter.ToDate.HasValue)
            {
                sb.Append(" AND o.LabourDate <= @ToDate");
                cmd.Parameters.AddWithValue("@ToDate", filter.ToDate.Value.Date);
            }
            if (filter.AreaId.HasValue)
            {
                sb.Append(" AND o.AreaId = @AreaId");
                cmd.Parameters.AddWithValue("@AreaId", filter.AreaId.Value);
            }
            if (filter.PolyhouseId.HasValue)
            {
                sb.Append(" AND o.PolyhouseId = @PolyhouseId");
                cmd.Parameters.AddWithValue("@PolyhouseId", filter.PolyhouseId.Value);
            }
            if (filter.CreatedById.HasValue)
            {
                sb.Append(" AND o.CreatedById = @CreatedById");
                cmd.Parameters.AddWithValue("@CreatedById", filter.CreatedById.Value);
            }
            if (areaScope != null)
            {
                var names = areaScope.Select((id, i) =>
                {
                    cmd.Parameters.AddWithValue("@Scope" + i, id);
                    return "@Scope" + i;
                }).ToList();
                sb.Append(" AND o.AreaId IN (").Append(string.Join(", ", names)).Append(')');
            }
            return sb.ToString();
        }

        private static LabourOvertimeEntry Map(SqlDataReader r) => new()
        {
            Id = r.GetInt32(0),
            LabourDate = r.GetDateTime(1),
            AreaId = r.GetInt32(2),
            AreaName = r.IsDBNull(3) ? null : r.GetString(3),
            AreaType = r.IsDBNull(4) ? null : r.GetString(4),
            PolyhouseId = r.IsDBNull(5) ? null : r.GetInt32(5),
            PolyhouseName = r.IsDBNull(6) ? null : r.GetString(6),
            MaleWorkers = r.GetInt32(7),
            FemaleWorkers = r.GetInt32(8),
            StartTime = TimeOnly.FromTimeSpan(r.GetTimeSpan(9)),
            EndTime = TimeOnly.FromTimeSpan(r.GetTimeSpan(10)),
            DurationMinutes = r.GetInt32(11),
            MaleWorkerMinutes = r.GetInt32(12),
            FemaleWorkerMinutes = r.GetInt32(13),
            WorkDescription = r.GetString(14),
            Remarks = r.IsDBNull(15) ? null : r.GetString(15),
            CreatedDate = DateTime.SpecifyKind(r.GetDateTime(16), DateTimeKind.Utc),
            CreatedById = r.IsDBNull(17) ? null : r.GetInt32(17),
            CreatedByName = r.IsDBNull(18) ? null : r.GetString(18),
            ModifiedDate = r.IsDBNull(19) ? null : DateTime.SpecifyKind(r.GetDateTime(19), DateTimeKind.Utc),
            ModifiedById = r.IsDBNull(20) ? null : r.GetInt32(20),
            ModifiedByName = r.IsDBNull(21) ? null : r.GetString(21),
        };
    }
}
