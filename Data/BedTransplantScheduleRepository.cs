using Microsoft.Data.SqlClient;
using PlantStockManager.Models;

namespace PlantStockManager.Data
{
    // 2026-11-07 Bed Sowing Alerts (2026-11-07_BedTransplantDays.sql; rules in Services/BedTransplantAlertRules.cs).
    // Read-only: computed on every request from dbo.BedSowings + dbo.PlantSpecies.BedTransplantDays -- no alert table, so an
    // alert can never be stored twice or go stale. Used by Production/BedSowing/Alerts and the Management Dashboard.
    public class BedTransplantScheduleRepository
    {
        private readonly DatabaseHelper _dbHelper;

        public BedTransplantScheduleRepository(DatabaseHelper dbHelper)
        {
            _dbHelper = dbHelper;
        }

        // Both migrations: the Bed Sowing tables and the Bed Transplant Days column.
        public async Task<bool> SchemaReadyAsync()
        {
            using var conn = _dbHelper.GetConnection();
            await conn.OpenAsync();
            return await BedSowingSchema.ReadyAsync(conn) && await BedTransplantScheduleSchema.ReadyAsync(conn);
        }

        // Every OPEN bed (Pending / Partially transplanted, seedlings still in it) in the given Areas (null = every Area) that
        // is due no later than `horizon`, plus every open bed whose variety has no Bed Transplant Days (listed, never dated).
        // A bed whose due date is further out than the horizon is not returned. speciesId narrows (dashboard filter) and
        // never widens the Area scope. Empty when either migration is missing.
        public async Task<List<BedTransplantAlert>> GetAlertCandidatesAsync(IReadOnlyCollection<int>? areaIds, DateTime horizon, int? speciesId = null)
        {
            var list = new List<BedTransplantAlert>();
            using var conn = _dbHelper.GetConnection();
            await conn.OpenAsync();
            if (!await BedSowingSchema.ReadyAsync(conn) || !await BedTransplantScheduleSchema.ReadyAsync(conn))
                return list;
            using var cmd = new SqlCommand(@"
SELECT b.Id, b.BedSowingCode, b.SpeciesId, RTRIM(ps.Name), pt.Name, b.AreaId, a.Name, p.Name, b.SeedLotNo,
       b.SowingDate, b.Quantity, b.TransplantedQuantity, b.LostQuantity, b.Status, ps.BedTransplantDays
FROM dbo.BedSowings b
JOIN dbo.PlantSpecies ps ON ps.Id = b.SpeciesId
LEFT JOIN dbo.PlantTypes pt ON pt.Id = ps.PlantTypeId
JOIN dbo.Area a ON a.Id = b.AreaId
JOIN dbo.Polyhouses p ON p.Id = b.PolyhouseId
WHERE b.Status IN (N'PendingTransplant', N'PartiallyTransplanted')
  AND b.Quantity - b.TransplantedQuantity - b.LostQuantity > 0
  AND (@Areas IS NULL OR b.AreaId IN (SELECT CAST(value AS INT) FROM STRING_SPLIT(@Areas, ',')))
  AND (@Species IS NULL OR b.SpeciesId = @Species)
  AND (ps.BedTransplantDays IS NULL OR DATEADD(DAY, ps.BedTransplantDays, b.SowingDate) <= @Horizon)
ORDER BY b.SowingDate, b.Id", conn);
            cmd.Parameters.AddWithValue("@Areas", areaIds == null ? DBNull.Value : string.Join(",", areaIds.DefaultIfEmpty(-1)));
            cmd.Parameters.Add("@Species", System.Data.SqlDbType.Int).Value = (object?)speciesId ?? DBNull.Value;
            cmd.Parameters.Add("@Horizon", System.Data.SqlDbType.Date).Value = horizon.Date;   // SowingDate is DATE
            using var r = await cmd.ExecuteReaderAsync();
            while (await r.ReadAsync())
            {
                list.Add(new BedTransplantAlert
                {
                    BedSowingId = r.GetInt32(0),
                    BedSowingCode = r.GetString(1),
                    SpeciesId = r.GetInt32(2),
                    SpeciesName = r.GetString(3),
                    PlantTypeName = r.IsDBNull(4) ? null : r.GetString(4),
                    AreaId = r.GetInt32(5),
                    AreaName = r.GetString(6),
                    PolyhouseName = r.GetString(7),
                    SeedLotNo = r.IsDBNull(8) ? null : r.GetString(8),
                    SowingDate = r.GetDateTime(9),
                    Quantity = r.GetDecimal(10),
                    TransplantedQuantity = r.GetDecimal(11),
                    LostQuantity = r.GetDecimal(12),
                    Status = r.GetString(13),
                    BedTransplantDays = r.IsDBNull(14) ? null : r.GetInt32(14),
                });
            }
            return list;
        }
    }
}
