using Microsoft.Data.SqlClient;

namespace PlantStockManager.Data
{
    // Phase D: one Wastage report over the current workflow. Each loss comes
    // from exactly ONE source row, so nothing is counted twice:
    //   Seed tray / Cutting tray  approval: used in trays - ready seedlings  (ReadyConfirmations, Confirmed)
    //   Cutting transit loss      delivered - received at Main Office        (CuttingStockTransactions 'TransitLoss')
    //   Pot batch                 pots produced - ready pots                 (PotProductionBatches, Ready or Lost)
    //   Pot batch unused cuttings allocated - potted, recorded as wastage    (PotProductionBatches, Ready or Lost)
    //   Potted plant stock        'Wastage' ledger entries                   (PottedPlantStockTransactions)
    // Seeds or cuttings left over below one complete tray stay in stock and
    // are never wastage.
    public class WastageRepository
    {
        private readonly DatabaseHelper _dbHelper;

        public WastageRepository(DatabaseHelper dbHelper)
        {
            _dbHelper = dbHelper;
        }

        public sealed class WastageRow
        {
            public DateTime Date { get; set; }
            public string ProductionType { get; set; } = string.Empty;
            public string Material { get; set; } = string.Empty;   // Seed / Cutting / Potted plant
            public string Variety { get; set; } = string.Empty;
            public string? PlantType { get; set; }
            public string? Color { get; set; }
            public string? Cavity { get; set; }
            public string? PotSize { get; set; }
            public int? AreaId { get; set; }
            public string? AreaName { get; set; }
            public decimal Quantity { get; set; }
            public decimal? OutOf { get; set; }                    // the quantity the loss is measured against
            public string? Reason { get; set; }
            public string? Reference { get; set; }                 // sowing / batch / delivery code
            public string? Supervisor { get; set; }
            public string? Remarks { get; set; }
            public decimal? Percent => OutOf is > 0 ? Math.Round(Quantity / OutOf.Value * 100m, 2, MidpointRounding.AwayFromZero) : null;
        }

        private const string Sql = @"
SELECT * FROM (
    SELECT CAST(rc.ConfirmationDate AS date) AS WDate,
           CASE sw.SourceType WHEN N'Cutting' THEN N'Cutting tray' ELSE N'Seed tray' END AS ProductionType,
           CASE sw.SourceType WHEN N'Cutting' THEN N'Cutting' ELSE N'Seed' END AS Material,
           RTRIM(ps.Name) AS Variety, pt.Name AS PlantType, ps.Color, sw.CavityType AS Cavity, CAST(NULL AS nvarchar(50)) AS PotSize,
           sw.AreaId, a.Name AS AreaName, rc.WastageQuantity AS Quantity, sw.QuantitySown AS OutOf,
           rc.WastageReason AS Reason, sw.SowingCode AS Reference, u.Name AS Supervisor, rc.Remarks
    FROM dbo.ReadyConfirmations rc
    INNER JOIN dbo.SeedSowings sw ON sw.Id = rc.SeedSowingId
    INNER JOIN dbo.PlantSpecies ps ON ps.Id = sw.SpeciesId
    INNER JOIN dbo.PlantTypes pt ON pt.Id = ps.PlantTypeId
    INNER JOIN dbo.Area a ON a.Id = sw.AreaId
    LEFT JOIN dbo.IMSUsers u ON u.Id = rc.ApprovedById
    WHERE rc.Status = 'Confirmed' AND rc.WastageQuantity > 0

    UNION ALL
    SELECT CAST(t.TransactionDate AS date), N'Cutting delivery (transit loss)', N'Cutting',
           RTRIM(ps.Name), pt.Name, ps.Color, NULL, NULL,
           c.AreaId, a.Name, -t.Quantity, it.Quantity,
           t.Remarks, it.TransferCode, u.Name, NULL
    FROM dbo.CuttingStockTransactions t
    INNER JOIN dbo.CuttingStock c ON c.Id = t.CuttingStockId
    INNER JOIN dbo.PlantSpecies ps ON ps.Id = c.SpeciesId
    INNER JOIN dbo.PlantTypes pt ON pt.Id = ps.PlantTypeId
    INNER JOIN dbo.Area a ON a.Id = c.AreaId
    LEFT JOIN dbo.InternalTransfers it ON it.Id = t.ReferenceId AND t.ReferenceType = 'InternalTransfer'
    LEFT JOIN dbo.IMSUsers u ON u.Id = t.UserId
    WHERE t.TransactionType = 'TransitLoss'

    UNION ALL
    SELECT CAST(b.ReadyDate AS date), N'Pot production (pots lost)', N'Potted plant',
           RTRIM(ps.Name), pt.Name, ps.Color, NULL, b.PotSize,
           b.AreaId, a.Name, pe.Potted - b.ReadyQuantity, pe.Potted,
           b.WastageReason, b.BatchCode, u.Name, b.ReadyRemarks
    FROM dbo.PotProductionBatches b
    CROSS APPLY (SELECT SUM(e.Quantity) AS Potted FROM dbo.PotProductionEntries e WHERE e.BatchId = b.Id) pe
    INNER JOIN dbo.PlantSpecies ps ON ps.Id = b.SpeciesId
    INNER JOIN dbo.PlantTypes pt ON pt.Id = ps.PlantTypeId
    INNER JOIN dbo.Area a ON a.Id = b.AreaId
    LEFT JOIN dbo.IMSUsers u ON u.Id = b.ReadyConfirmedById
    WHERE b.Status IN ('Ready', 'Lost') AND pe.Potted > b.ReadyQuantity

    UNION ALL
    SELECT CAST(b.ReadyDate AS date), N'Pot production (cuttings not potted)', N'Cutting',
           RTRIM(ps.Name), pt.Name, ps.Color, NULL, b.PotSize,
           b.AreaId, a.Name, b.CuttingAllocated - pe.Potted, b.CuttingAllocated,
           N'Not potted', b.BatchCode, u.Name, b.ReadyRemarks
    FROM dbo.PotProductionBatches b
    CROSS APPLY (SELECT ISNULL(SUM(e.Quantity), 0) AS Potted FROM dbo.PotProductionEntries e WHERE e.BatchId = b.Id) pe
    INNER JOIN dbo.PlantSpecies ps ON ps.Id = b.SpeciesId
    INNER JOIN dbo.PlantTypes pt ON pt.Id = ps.PlantTypeId
    INNER JOIN dbo.Area a ON a.Id = b.AreaId
    LEFT JOIN dbo.IMSUsers u ON u.Id = b.ReadyConfirmedById
    WHERE b.Status IN ('Ready', 'Lost') AND b.UnusedCuttingAction = 'Wastage' AND b.CuttingAllocated > pe.Potted

    UNION ALL
    SELECT CAST(t.TransactionDate AS date), N'Potted plant stock', N'Potted plant',
           RTRIM(ps.Name), pt.Name, ps.Color, NULL, p.PotSize,
           p.AreaId, a.Name, -t.Quantity, NULL,
           t.Remarks, NULL, u.Name, NULL
    FROM dbo.PottedPlantStockTransactions t
    INNER JOIN dbo.PottedPlantStock p ON p.Id = t.PottedPlantStockId
    INNER JOIN dbo.PlantSpecies ps ON ps.Id = p.SpeciesId
    INNER JOIN dbo.PlantTypes pt ON pt.Id = ps.PlantTypeId
    LEFT JOIN dbo.Area a ON a.Id = p.AreaId
    LEFT JOIN dbo.IMSUsers u ON u.Id = t.UserId
    WHERE t.TransactionType = 'Wastage'
) w
WHERE w.WDate BETWEEN @From AND @To
  AND (@Search IS NULL OR w.Variety LIKE @Like OR ISNULL(w.PlantType, '') LIKE @Like OR ISNULL(w.Reference, '') LIKE @Like)
  AND (@AreaId IS NULL OR w.AreaId = @AreaId)
ORDER BY w.WDate DESC";

        public async Task<List<WastageRow>> GetAsync(DateTime from, DateTime to, int? areaId = null, string? search = null)
        {
            var list = new List<WastageRow>();
            using var conn = _dbHelper.GetConnection();
            await conn.OpenAsync();
            using var cmd = new SqlCommand(Sql, conn);
            var term = string.IsNullOrWhiteSpace(search) ? null : search.Trim();
            cmd.Parameters.AddWithValue("@From", from.Date);
            cmd.Parameters.AddWithValue("@To", to.Date);
            cmd.Parameters.AddWithValue("@AreaId", (object?)areaId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@Search", (object?)term ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@Like", term == null ? DBNull.Value : "%" + term.Replace("[", "[[]").Replace("%", "[%]").Replace("_", "[_]") + "%");
            using var r = await cmd.ExecuteReaderAsync();
            while (await r.ReadAsync())
            {
                list.Add(new WastageRow
                {
                    Date = r.GetDateTime(0),
                    ProductionType = r.GetString(1),
                    Material = r.GetString(2),
                    Variety = r.GetString(3),
                    PlantType = r.IsDBNull(4) ? null : r.GetString(4),
                    Color = r.IsDBNull(5) ? null : r.GetString(5),
                    Cavity = r.IsDBNull(6) ? null : r.GetString(6),
                    PotSize = r.IsDBNull(7) ? null : r.GetString(7),
                    AreaId = r.IsDBNull(8) ? null : r.GetInt32(8),
                    AreaName = r.IsDBNull(9) ? null : r.GetString(9),
                    Quantity = r.GetDecimal(10),
                    OutOf = r.IsDBNull(11) ? null : r.GetDecimal(11),
                    Reason = r.IsDBNull(12) ? null : r.GetString(12),
                    Reference = r.IsDBNull(13) ? null : r.GetString(13),
                    Supervisor = r.IsDBNull(14) ? null : r.GetString(14),
                    Remarks = r.IsDBNull(15) ? null : r.GetString(15)
                });
            }
            return list;
        }
    }
}
