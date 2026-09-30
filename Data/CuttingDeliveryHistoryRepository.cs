using System.Data;
using Microsoft.Data.SqlClient;
using PlantStockManager.Models;
using PlantStockManager.Services;

namespace PlantStockManager.Data
{
    // Correction #7 -- READ-ONLY reporting over data that already exists (dbo.CuttingProductions, dbo.InternalTransfers,
    // dbo.MotherPlants, dbo.Polyhouses, dbo.Area, dbo.IMSUsers). It writes nothing and adds no columns.
    //
    // One query, two branches (see CuttingDeliveryHistoryRow):
    //   E  every Cutting Entry, with its delivery when it has one (InternalTransfers.SourceCuttingProductionId);
    //   D  every older cutting delivery that no Cutting Entry is linked to.
    // Together they list every cutting delivery exactly once, plus the entries that were NOT delivered (kept for Pot
    // Production, or made before destinations existed) so nothing is assumed to have become a delivery.
    //
    // FILTERS and AREA SECURITY are part of the SQL (parameters only; OPTION (RECOMPILE)); nothing is filtered in C#.
    // allowedAreaIds: null = full Area access; otherwise a row is visible only when the user may access its SOURCE Area, its
    // DESTINATION Area or -- while it is still unconfirmed -- the Main Office Area it is waiting at (a NULL Area never grants
    // access). An empty list returns nothing. Selecting another Area in a filter can therefore only narrow what the user may
    // already see. The dropdown choices come from the same visible rows.
    public class CuttingDeliveryHistoryRepository
    {
        private readonly DatabaseHelper _dbHelper;

        public CuttingDeliveryHistoryRepository(DatabaseHelper dbHelper) => _dbHelper = dbHelper;

        // "record date" in the server's local time (the delivery stamp is stored in UTC), like the rest of the app shows it
        private const string LocalDeliveryDate = "CAST(DATEADD(MINUTE, DATEDIFF(MINUTE, SYSUTCDATETIME(), SYSDATETIME()), t.CreatedDate) AS DATE)";

        internal const string Cte = @"
WITH h AS (
    SELECT CAST('E' AS CHAR(1)) AS Kind,
           CAST(cp.CuttingDate AS DATE) AS RecordDate,
           cp.Id AS ProductionId, cp.ProductionCode, CAST(cp.CuttingDate AS DATE) AS ProductionDate,
           t.Id AS TransferId, t.TransferCode, t.Status AS TransferStatus,
           CASE WHEN t.Id IS NULL THEN N'None'
                WHEN t.Status = N'Completed' AND t.ConfirmedQuantity < t.Quantity THEN N'CompletedShortfall'
                ELSE t.Status END AS StatusKey,
           cp.SpeciesId, ps.Name AS SpeciesName, ps.Color, pt.Name AS PlantTypeName,
           cp.Quantity AS Quantity, t.ConfirmedQuantity,
           cp.AreaId AS SourceAreaId, sa.Name AS SourceAreaName,
           cp.MotherPlantId, mp.MotherPlantCode, mp.PolyhouseId AS SourcePolyhouseId, ph.Name AS SourcePolyhouseName, pha.Name AS SourcePolyhouseAreaName,
           cp.SupervisorId, sup.Name AS SupervisorName,
           ISNULL(cp.DestinationType, N'NotRecorded') AS DestinationType,
           COALESCE(t.DestinationAreaId, t.PendingConfirmationAreaId, cp.DestinationAreaId) AS DestinationAreaId, da.Name AS DestinationAreaName,
           t.PendingConfirmationAreaId AS PendingAreaId,
           COALESCE(t.CreatedBy, cp.CreatedBy) AS EnteredBy,
           (SELECT TOP 1 u.Name FROM dbo.IMSUsers u WHERE u.Username = COALESCE(t.CreatedBy, cp.CreatedBy) ORDER BY u.Id) AS EnteredByName,
           t.CreatedDate AS DeliveryDate, t.ConfirmedBy AS ConfirmedById, cb.Name AS ConfirmedByName, t.ConfirmedDate,
           CASE WHEN t.Status = N'Rejected' THEN t.ModifiedBy END AS RejectedBy,
           CASE WHEN t.Status = N'Rejected' THEN t.ModifiedDate END AS RejectedDate,
           t.DiscrepancyReason AS Reason, cp.Remarks AS EntryRemarks, t.Remarks AS DeliveryRemarks
    FROM dbo.CuttingProductions cp
    INNER JOIN dbo.MotherPlants mp ON mp.Id = cp.MotherPlantId
    INNER JOIN dbo.PlantSpecies ps ON ps.Id = cp.SpeciesId
    INNER JOIN dbo.PlantTypes pt ON pt.Id = ps.PlantTypeId
    INNER JOIN dbo.Area sa ON sa.Id = cp.AreaId
    LEFT JOIN dbo.Polyhouses ph ON ph.Id = mp.PolyhouseId
    LEFT JOIN dbo.Area pha ON pha.Id = ph.AreaId
    LEFT JOIN dbo.IMSUsers sup ON sup.Id = cp.SupervisorId
    LEFT JOIN dbo.InternalTransfers t ON t.SourceCuttingProductionId = cp.Id
    LEFT JOIN dbo.IMSUsers cb ON cb.Id = t.ConfirmedBy
    LEFT JOIN dbo.Area da ON da.Id = COALESCE(t.DestinationAreaId, t.PendingConfirmationAreaId, cp.DestinationAreaId)

    UNION ALL

    SELECT CAST('D' AS CHAR(1)), " + LocalDeliveryDate + @",
           CAST(NULL AS INT), CAST(NULL AS NVARCHAR(50)), CAST(NULL AS DATE),
           t.Id, t.TransferCode, t.Status,
           CASE WHEN t.Status = N'Completed' AND t.ConfirmedQuantity < t.Quantity THEN N'CompletedShortfall' ELSE t.Status END,
           cs.SpeciesId, cps.Name, cps.Color, cpt.Name,
           t.Quantity, t.ConfirmedQuantity,
           t.SourceAreaId, sa.Name,
           CAST(NULL AS INT), CAST(NULL AS NVARCHAR(50)), CAST(NULL AS INT), CAST(NULL AS NVARCHAR(100)), CAST(NULL AS NVARCHAR(100)),
           CAST(NULL AS INT), CAST(NULL AS NVARCHAR(100)),
           N'MainOffice',
           COALESCE(t.DestinationAreaId, t.PendingConfirmationAreaId), da.Name,
           t.PendingConfirmationAreaId,
           t.CreatedBy,
           (SELECT TOP 1 u.Name FROM dbo.IMSUsers u WHERE u.Username = t.CreatedBy ORDER BY u.Id),
           t.CreatedDate, t.ConfirmedBy, cb.Name, t.ConfirmedDate,
           CASE WHEN t.Status = N'Rejected' THEN t.ModifiedBy END,
           CASE WHEN t.Status = N'Rejected' THEN t.ModifiedDate END,
           t.DiscrepancyReason, CAST(NULL AS NVARCHAR(1000)), t.Remarks
    FROM dbo.InternalTransfers t
    INNER JOIN dbo.CuttingStock cs ON cs.Id = t.SourceCuttingStockId
    INNER JOIN dbo.PlantSpecies cps ON cps.Id = cs.SpeciesId
    INNER JOIN dbo.PlantTypes cpt ON cpt.Id = cps.PlantTypeId
    LEFT JOIN dbo.Area sa ON sa.Id = t.SourceAreaId
    LEFT JOIN dbo.Area da ON da.Id = COALESCE(t.DestinationAreaId, t.PendingConfirmationAreaId)
    LEFT JOIN dbo.IMSUsers cb ON cb.Id = t.ConfirmedBy
    WHERE t.StockType = N'Cutting' AND t.SourceCuttingProductionId IS NULL
)";

        // The user's Areas: source, destination, or the Main Office Area an unconfirmed delivery waits at (NULL never grants access).
        private const string Scope = @"(@Allowed IS NULL
       OR h.SourceAreaId IN (SELECT CAST(value AS INT) FROM STRING_SPLIT(@Allowed, ','))
       OR h.DestinationAreaId IN (SELECT CAST(value AS INT) FROM STRING_SPLIT(@Allowed, ','))
       OR h.PendingAreaId IN (SELECT CAST(value AS INT) FROM STRING_SPLIT(@Allowed, ',')))";

        private static string? AllowedList(IReadOnlyCollection<int>? allowedAreaIds)
            => allowedAreaIds == null ? null : string.Join(",", allowedAreaIds.Distinct());

        private static void AddAllowed(SqlCommand cmd, IReadOnlyCollection<int>? allowedAreaIds)
            => cmd.Parameters.Add("@Allowed", SqlDbType.NVarChar, -1).Value = (object?)AllowedList(allowedAreaIds) ?? DBNull.Value;

        public async Task<List<CuttingDeliveryHistoryRow>> SearchAsync(CuttingDeliveryHistoryFilter filter, IReadOnlyCollection<int>? allowedAreaIds)
        {
            var list = new List<CuttingDeliveryHistoryRow>();
            if (allowedAreaIds != null && allowedAreaIds.Count == 0)
                return list;

            using var conn = _dbHelper.GetConnection();
            await conn.OpenAsync();
            using var cmd = new SqlCommand(Cte + @"
SELECT h.* FROM h
WHERE (@From IS NULL OR h.RecordDate >= @From)
  AND (@To IS NULL OR h.RecordDate <= @To)
  AND (@SourceAreaId IS NULL OR h.SourceAreaId = @SourceAreaId)
  AND (@SourcePolyhouseId IS NULL OR h.SourcePolyhouseId = @SourcePolyhouseId)
  AND (@MotherPlantId IS NULL OR h.MotherPlantId = @MotherPlantId)
  AND (@SupervisorId IS NULL OR h.SupervisorId = @SupervisorId)
  AND (@Destination IS NULL OR h.DestinationType = @Destination)
  AND (@DestinationAreaId IS NULL OR h.DestinationAreaId = @DestinationAreaId)
  AND (@Status IS NULL OR h.StatusKey = @Status)
  AND (@EnteredBy IS NULL OR h.EnteredBy = @EnteredBy)
  AND (@ReceivedById IS NULL OR h.ConfirmedById = @ReceivedById)
  AND " + Scope + @"
ORDER BY h.RecordDate DESC, h.Kind, COALESCE(h.ProductionId, h.TransferId) DESC
OPTION (RECOMPILE)", conn);
            cmd.Parameters.AddWithValue("@From", (object?)filter.From?.Date ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@To", (object?)filter.To?.Date ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@SourceAreaId", (object?)filter.SourceAreaId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@SourcePolyhouseId", (object?)filter.SourcePolyhouseId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@MotherPlantId", (object?)filter.MotherPlantId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@SupervisorId", (object?)filter.SupervisorId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@Destination", (object?)filter.Destination ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@DestinationAreaId", (object?)filter.DestinationAreaId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@Status", (object?)filter.DeliveryStatus ?? DBNull.Value);
            cmd.Parameters.Add("@EnteredBy", SqlDbType.NVarChar, CuttingDeliveryHistoryFilter.MaxNameLength).Value = (object?)filter.EnteredBy ?? DBNull.Value;
            cmd.Parameters.AddWithValue("@ReceivedById", (object?)filter.ReceivedById ?? DBNull.Value);
            AddAllowed(cmd, allowedAreaIds);
            using var r = await cmd.ExecuteReaderAsync();
            while (await r.ReadAsync())
                list.Add(Map(r));
            return list;
        }

        public sealed record IdOption(int Id, string Label);
        public sealed record TextOption(string Value, string Label);

        public sealed class FilterOptions
        {
            public List<IdOption> SourceAreas { get; } = new();
            public List<IdOption> SourcePolyhouses { get; } = new();
            public List<IdOption> MotherPlants { get; } = new();
            public List<IdOption> Supervisors { get; } = new();
            public List<IdOption> DestinationAreas { get; } = new();
            public List<TextOption> EnteredBy { get; } = new();
            public List<IdOption> ReceivedBy { get; } = new();
        }

        // Dropdown values that actually occur in the rows the user may see -- never a name from an Area they cannot access.
        public async Task<FilterOptions> GetFilterOptionsAsync(IReadOnlyCollection<int>? allowedAreaIds)
        {
            var options = new FilterOptions();
            if (allowedAreaIds != null && allowedAreaIds.Count == 0)
                return options;

            using var conn = _dbHelper.GetConnection();
            await conn.OpenAsync();
            async Task Load(string select, Action<SqlDataReader> read)
            {
                using var cmd = new SqlCommand(Cte + " " + select + " AND " + Scope + " OPTION (RECOMPILE)", conn);
                AddAllowed(cmd, allowedAreaIds);
                using var r = await cmd.ExecuteReaderAsync();
                while (await r.ReadAsync()) read(r);
            }
            static string S(SqlDataReader r, int i) => r.IsDBNull(i) ? "" : r.GetString(i).Trim();

            await Load("SELECT DISTINCT h.SourceAreaId, h.SourceAreaName FROM h WHERE h.SourceAreaId IS NOT NULL", r => options.SourceAreas.Add(new IdOption(r.GetInt32(0), S(r, 1))));
            await Load("SELECT DISTINCT h.SourcePolyhouseId, h.SourcePolyhouseName, h.SourcePolyhouseAreaName FROM h WHERE h.SourcePolyhouseId IS NOT NULL",
                r => options.SourcePolyhouses.Add(new IdOption(r.GetInt32(0), S(r, 1) + (S(r, 2) == "" ? "" : " - " + S(r, 2)))));
            await Load("SELECT DISTINCT h.MotherPlantId, h.MotherPlantCode FROM h WHERE h.MotherPlantId IS NOT NULL", r => options.MotherPlants.Add(new IdOption(r.GetInt32(0), S(r, 1))));
            await Load("SELECT DISTINCT h.SupervisorId, h.SupervisorName FROM h WHERE h.SupervisorId IS NOT NULL", r => options.Supervisors.Add(new IdOption(r.GetInt32(0), S(r, 1))));
            await Load("SELECT DISTINCT h.DestinationAreaId, h.DestinationAreaName FROM h WHERE h.DestinationAreaId IS NOT NULL", r => options.DestinationAreas.Add(new IdOption(r.GetInt32(0), S(r, 1))));
            await Load("SELECT DISTINCT h.EnteredBy, h.EnteredByName FROM h WHERE h.EnteredBy IS NOT NULL AND LTRIM(RTRIM(h.EnteredBy)) <> ''",
                r => options.EnteredBy.Add(new TextOption(S(r, 0), S(r, 1) == "" ? S(r, 0) : $"{S(r, 1)} ({S(r, 0)})")));
            await Load("SELECT DISTINCT h.ConfirmedById, h.ConfirmedByName FROM h WHERE h.ConfirmedById IS NOT NULL", r => options.ReceivedBy.Add(new IdOption(r.GetInt32(0), S(r, 1) == "" ? $"User #{r.GetInt32(0)}" : S(r, 1))));

            static void Sort<T>(List<T> l, Func<T, string> key) => l.Sort((a, b) => StringComparer.CurrentCultureIgnoreCase.Compare(key(a), key(b)));
            Sort(options.SourceAreas, o => o.Label); Sort(options.SourcePolyhouses, o => o.Label); Sort(options.MotherPlants, o => o.Label);
            Sort(options.Supervisors, o => o.Label); Sort(options.DestinationAreas, o => o.Label); Sort(options.EnteredBy, o => o.Label); Sort(options.ReceivedBy, o => o.Label);
            return options;
        }

        private static CuttingDeliveryHistoryRow Map(SqlDataReader r)
        {
            string? S(string c) => r.IsDBNull(r.GetOrdinal(c)) ? null : r.GetString(r.GetOrdinal(c));
            int? I(string c) => r.IsDBNull(r.GetOrdinal(c)) ? null : r.GetInt32(r.GetOrdinal(c));
            decimal? D(string c) => r.IsDBNull(r.GetOrdinal(c)) ? null : r.GetDecimal(r.GetOrdinal(c));
            DateTime? T(string c) => r.IsDBNull(r.GetOrdinal(c)) ? null : r.GetDateTime(r.GetOrdinal(c));
            return new CuttingDeliveryHistoryRow
            {
                Kind = r.GetString(r.GetOrdinal("Kind")),
                RecordDate = r.GetDateTime(r.GetOrdinal("RecordDate")),
                ProductionId = I("ProductionId"), ProductionCode = S("ProductionCode"), ProductionDate = T("ProductionDate"),
                TransferId = I("TransferId"), TransferCode = S("TransferCode"), TransferStatus = S("TransferStatus"), StatusKey = S("StatusKey") ?? "None",
                SpeciesId = r.GetInt32(r.GetOrdinal("SpeciesId")), SpeciesName = S("SpeciesName")?.Trim(), Color = S("Color"), PlantTypeName = S("PlantTypeName"),
                Quantity = r.GetDecimal(r.GetOrdinal("Quantity")), ConfirmedQuantity = D("ConfirmedQuantity"),
                SourceAreaId = r.GetInt32(r.GetOrdinal("SourceAreaId")), SourceAreaName = S("SourceAreaName"),
                MotherPlantId = I("MotherPlantId"), MotherPlantCode = S("MotherPlantCode"),
                SourcePolyhouseId = I("SourcePolyhouseId"), SourcePolyhouseName = S("SourcePolyhouseName"),
                SupervisorId = I("SupervisorId"), SupervisorName = S("SupervisorName"),
                DestinationType = S("DestinationType") ?? "NotRecorded", DestinationAreaId = I("DestinationAreaId"), DestinationAreaName = S("DestinationAreaName"),
                PendingAreaId = I("PendingAreaId"),
                EnteredBy = S("EnteredBy"), EnteredByName = S("EnteredByName"), DeliveryDate = T("DeliveryDate"),
                ConfirmedById = I("ConfirmedById"), ConfirmedByName = S("ConfirmedByName"), ConfirmedDate = T("ConfirmedDate"),
                RejectedBy = S("RejectedBy"), RejectedDate = T("RejectedDate"), Reason = S("Reason"),
                EntryRemarks = S("EntryRemarks"), DeliveryRemarks = S("DeliveryRemarks")
            };
        }
    }
}
