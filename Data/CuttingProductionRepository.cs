using Microsoft.Data.SqlClient;
using PlantStockManager.Models;
using PlantStockManager.Services;

namespace PlantStockManager.Data
{
    // Phase D: Mother Plant -> Cutting Production -> Cutting Stock.
    // One atomic transaction: lock the Mother Plant, credit the
    // (variety, Area) Cutting Stock pool with a 'Harvest' ledger entry and
    // save the production record that references it.
    //
    // DESTINATION (Cutting Entry): every entry says where the cuttings go.
    //   PotProduction  nothing more: the harvest stays in the Mother Plant's own Area pool,
    //                  which that Area's Pot Production / Cutting Tray Sowing draws from.
    //   MainOffice     in the SAME transaction the existing Cutting delivery to Main Office
    //                  is created (InternalTransferRepository.CreateCuttingDeliveryAsync:
    //                  PendingConfirmation, quantity held In-Transit in the source pool).
    //                  It becomes Main Office stock only when Main Office confirms receipt.
    // Both legs commit together or not at all. A one-time SubmissionToken per form makes a
    // repeated submit of the same form a no-op (the stock is never credited twice).
    public class CuttingProductionRepository
    {
        private readonly DatabaseHelper _dbHelper;
        private readonly BatchNumberRepository _batchNumberRepo;
        private readonly CuttingStockRepository _cuttingStockRepo;
        private readonly UserRoleRepository _userRoleRepo;
        private readonly InternalTransferRepository _transferRepo;

        public CuttingProductionRepository(DatabaseHelper dbHelper, BatchNumberRepository batchNumberRepo,
            CuttingStockRepository cuttingStockRepo, UserRoleRepository userRoleRepo, InternalTransferRepository transferRepo)
        {
            _dbHelper = dbHelper;
            _batchNumberRepo = batchNumberRepo;
            _cuttingStockRepo = cuttingStockRepo;
            _userRoleRepo = userRoleRepo;
            _transferRepo = transferRepo;
        }

        private const string BaseSelect = @"
SELECT cp.Id, cp.ProductionCode, cp.MotherPlantId, mp.MotherPlantCode, cp.SpeciesId, ps.Name AS SpeciesName, ps.Color,
       pt.Name AS PlantTypeName, cp.AreaId, a.Name AS AreaName, cp.CuttingStockId, cp.CuttingDate, cp.Quantity,
       cp.SupervisorId, sup.Name AS SupervisorName, cp.Remarks, cp.CreatedById, cp.CreatedBy, cp.CreatedDate,
       cp.DestinationType, cp.DestinationAreaId, da.Name AS DestinationAreaName,
       tr.Id AS TransferId, tr.TransferCode, tr.Status AS TransferStatus,
       mp.PolyhouseId, ph.Name AS PolyhouseName
FROM dbo.CuttingProductions cp
INNER JOIN dbo.MotherPlants mp ON mp.Id = cp.MotherPlantId
LEFT JOIN dbo.Polyhouses ph ON ph.Id = mp.PolyhouseId
INNER JOIN dbo.PlantSpecies ps ON ps.Id = cp.SpeciesId
INNER JOIN dbo.PlantTypes pt ON pt.Id = ps.PlantTypeId
INNER JOIN dbo.Area a ON a.Id = cp.AreaId
LEFT JOIN dbo.Area da ON da.Id = cp.DestinationAreaId
LEFT JOIN dbo.IMSUsers sup ON sup.Id = cp.SupervisorId
LEFT JOIN dbo.InternalTransfers tr ON tr.SourceCuttingProductionId = cp.Id";

        public async Task<List<CuttingProduction>> GetAllAsync(DateTime? from = null, DateTime? to = null, int? motherPlantId = null)
        {
            var list = new List<CuttingProduction>();
            using var conn = _dbHelper.GetConnection();
            await conn.OpenAsync();
            using var cmd = new SqlCommand(BaseSelect + @"
WHERE (@From IS NULL OR cp.CuttingDate >= @From)
  AND (@To IS NULL OR cp.CuttingDate <= @To)
  AND (@MotherPlantId IS NULL OR cp.MotherPlantId = @MotherPlantId)
ORDER BY cp.CuttingDate DESC, cp.Id DESC", conn);
            cmd.Parameters.AddWithValue("@From", (object?)from?.Date ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@To", (object?)to?.Date ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@MotherPlantId", (object?)motherPlantId ?? DBNull.Value);
            using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
                list.Add(Map(reader));
            return list;
        }

        // ---- Correction #5: the filtered register -----------------------------------------------------------------
        // allowedAreaIds is the user's AREA SECURITY and is part of the query itself, not a filter the user can
        // change: null = full Area access (every Area); otherwise only records whose Area (cp.AreaId) is in the list --
        // an empty list returns nothing. A filter on an Area the user may not access therefore simply matches nothing.
        // All filters are optional and combine with AND; they are parameters (never concatenated), evaluated by the
        // database (OPTION (RECOMPILE) keeps the plan right for whichever ones are set), so large registers stay cheap:
        // CuttingDate, MotherPlant and the unique delivery link are indexed.
        public async Task<List<CuttingProduction>> SearchAsync(CuttingProductionFilter filter, IReadOnlyCollection<int>? allowedAreaIds)
        {
            var list = new List<CuttingProduction>();
            if (allowedAreaIds != null && allowedAreaIds.Count == 0)
                return list;

            using var conn = _dbHelper.GetConnection();
            await conn.OpenAsync();
            using var cmd = new SqlCommand(BaseSelect + @"
WHERE (@From IS NULL OR cp.CuttingDate >= @From)
  AND (@To IS NULL OR cp.CuttingDate <= @To)
  AND (@AreaId IS NULL OR cp.AreaId = @AreaId)
  AND (@MotherPlantId IS NULL OR cp.MotherPlantId = @MotherPlantId)
  AND (@SpeciesId IS NULL OR cp.SpeciesId = @SpeciesId)
  AND (@PolyhouseId IS NULL OR mp.PolyhouseId = @PolyhouseId)
  AND (@SupervisorId IS NULL OR cp.SupervisorId = @SupervisorId)
  AND (@Destination IS NULL OR (@Destination = N'NotRecorded' AND cp.DestinationType IS NULL) OR cp.DestinationType = @Destination)
  AND (@DestinationAreaId IS NULL OR cp.DestinationAreaId = @DestinationAreaId)
  AND (@Delivery IS NULL OR (@Delivery = N'None' AND tr.Id IS NULL) OR tr.Status = @Delivery)
  AND (@Allowed IS NULL OR cp.AreaId IN (SELECT CAST(value AS INT) FROM STRING_SPLIT(@Allowed, ',')))
ORDER BY cp.CuttingDate DESC, cp.Id DESC
OPTION (RECOMPILE)", conn);
            cmd.Parameters.AddWithValue("@From", (object?)filter.From?.Date ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@To", (object?)filter.To?.Date ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@AreaId", (object?)filter.AreaId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@MotherPlantId", (object?)filter.MotherPlantId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@SpeciesId", (object?)filter.SpeciesId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@PolyhouseId", (object?)filter.PolyhouseId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@SupervisorId", (object?)filter.SupervisorId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@Destination", (object?)filter.Destination ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@DestinationAreaId", (object?)filter.DestinationAreaId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@Delivery", (object?)filter.DeliveryStatus ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@Allowed", allowedAreaIds == null ? DBNull.Value : string.Join(",", allowedAreaIds.Distinct()));
            using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
                list.Add(Map(reader));
            return list;
        }

        public sealed record FilterOption(int Id, string Label);

        public sealed class FilterOptions
        {
            public List<FilterOption> Areas { get; } = new();
            public List<FilterOption> MotherPlants { get; } = new();
            public List<FilterOption> Varieties { get; } = new();
            public List<FilterOption> Polyhouses { get; } = new();
            public List<FilterOption> Supervisors { get; } = new();
            public List<FilterOption> DestinationAreas { get; } = new();
        }

        // The dropdown choices are the values that ACTUALLY occur in the register, taken from the same tables and the same
        // Area security as the register itself: a user never gets to see the name of an Area, Mother Plant, Polyhouse or
        // supervisor that exists only in Areas they cannot access, and no value appears twice.
        public async Task<FilterOptions> GetFilterOptionsAsync(IReadOnlyCollection<int>? allowedAreaIds)
        {
            var options = new FilterOptions();
            if (allowedAreaIds != null && allowedAreaIds.Count == 0)
                return options;

            using var conn = _dbHelper.GetConnection();
            await conn.OpenAsync();
            async Task Load(string sql, List<FilterOption> target)
            {
                using var cmd = new SqlCommand(sql + " OPTION (RECOMPILE)", conn);
                cmd.Parameters.AddWithValue("@Allowed", allowedAreaIds == null ? DBNull.Value : string.Join(",", allowedAreaIds.Distinct()));
                using var r = await cmd.ExecuteReaderAsync();
                while (await r.ReadAsync())
                    target.Add(new FilterOption(r.GetInt32(0), r.GetString(1).Trim()));
            }
            const string scope = "(@Allowed IS NULL OR cp.AreaId IN (SELECT CAST(value AS INT) FROM STRING_SPLIT(@Allowed, ',')))";

            await Load($@"SELECT DISTINCT a.Id, a.Name FROM dbo.CuttingProductions cp INNER JOIN dbo.Area a ON a.Id = cp.AreaId WHERE {scope} ORDER BY a.Name, a.Id", options.Areas);
            await Load($@"SELECT DISTINCT mp.Id, mp.MotherPlantCode FROM dbo.CuttingProductions cp INNER JOIN dbo.MotherPlants mp ON mp.Id = cp.MotherPlantId WHERE {scope} ORDER BY mp.MotherPlantCode, mp.Id", options.MotherPlants);
            await Load($@"SELECT DISTINCT ps.Id, RTRIM(ps.Name) + ISNULL(' (' + ps.Color + ')', '') FROM dbo.CuttingProductions cp INNER JOIN dbo.PlantSpecies ps ON ps.Id = cp.SpeciesId WHERE {scope} ORDER BY 2, ps.Id", options.Varieties);
            await Load($@"SELECT DISTINCT ph.Id, ph.Name + ISNULL(' - ' + a.Name, '') FROM dbo.CuttingProductions cp INNER JOIN dbo.MotherPlants mp ON mp.Id = cp.MotherPlantId INNER JOIN dbo.Polyhouses ph ON ph.Id = mp.PolyhouseId LEFT JOIN dbo.Area a ON a.Id = ph.AreaId WHERE {scope} ORDER BY 2, ph.Id", options.Polyhouses);
            await Load($@"SELECT DISTINCT u.Id, u.Name FROM dbo.CuttingProductions cp INNER JOIN dbo.IMSUsers u ON u.Id = cp.SupervisorId WHERE {scope} ORDER BY u.Name, u.Id", options.Supervisors);
            await Load($@"SELECT DISTINCT da.Id, da.Name FROM dbo.CuttingProductions cp INNER JOIN dbo.Area da ON da.Id = cp.DestinationAreaId WHERE cp.DestinationType = N'MainOffice' AND {scope} ORDER BY da.Name, da.Id", options.DestinationAreas);
            return options;
        }

        public async Task<(bool Success, string? Message, int Id)> InsertAsync(CuttingProduction entry, int? userId)
        {
            var (quantityOk, quantityError) = CuttingRules.ValidateProductionQuantity(entry.Quantity);
            if (!quantityOk)
                return (false, quantityError, 0);
            if (entry.CuttingDate == default)
                return (false, "Cutting date is required.", 0);
            if (entry.CuttingDate.Date > DateTime.Today)
                return (false, "Cutting date cannot be in the future.", 0);
            if (!CuttingDestination.IsValid(entry.DestinationType))
                return (false, "Choose where the cuttings go: Main Office or Use for Pot Production.", 0);

            using var conn = _dbHelper.GetConnection();
            await conn.OpenAsync();
            using var tx = conn.BeginTransaction();
            try
            {
                // The same form submitted twice (double click, refresh, retry): the first submit
                // already saved it -- report that entry and create nothing.
                if (entry.SubmissionToken.HasValue)
                {
                    var existing = await FindByTokenAsync(conn, tx, entry.SubmissionToken.Value);
                    if (existing != null)
                    {
                        tx.Rollback();
                        MarkDuplicate(entry, existing.Value.Id, existing.Value.Code);
                        return (true, null, existing.Value.Id);
                    }
                }

                // The variety and Area always come from the Mother Plant.
                var mpCmd = new SqlCommand(
                    "SELECT SpeciesId, AreaId, Status FROM dbo.MotherPlants WITH (UPDLOCK, HOLDLOCK) WHERE Id = @Id", conn, tx);
                mpCmd.Parameters.AddWithValue("@Id", entry.MotherPlantId);
                int speciesId; int? areaId; string status;
                using (var reader = await mpCmd.ExecuteReaderAsync())
                {
                    if (!await reader.ReadAsync())
                    {
                        reader.Close();
                        tx.Rollback();
                        return (false, "Mother Plant not found.", 0);
                    }
                    speciesId = reader.GetInt32(0);
                    areaId = reader.IsDBNull(1) ? null : reader.GetInt32(1);
                    status = reader.GetString(2);
                }
                if (!string.Equals(status, "Active", StringComparison.OrdinalIgnoreCase))
                {
                    tx.Rollback();
                    return (false, $"This Mother Plant is '{status}'; cuttings can only be recorded for an Active Mother Plant.", 0);
                }
                if (!areaId.HasValue)
                {
                    tx.Rollback();
                    return (false, "This Mother Plant has no Area. Set its Area (Mother Plants > Edit) before recording cuttings.", 0);
                }

                var supervisors = (await _userRoleRepo.GetUsersInRoleAsync(SupervisorRules.MotherPlantSupervisor, areaId.Value, conn, tx))
                    .Select(u => u.EmployeeID).ToHashSet();
                if (!supervisors.Contains(entry.SupervisorId))
                {
                    tx.Rollback();
                    return (false, "The supervisor must be an active Mother Plant Supervisor of this Mother Plant's Area.", 0);
                }

                // Destination, re-derived here from the database (never trusted from the form): a
                // Main Office destination must be a real, active Main Office Area of its own.
                var mainOfficeAreaIds = await GetActiveMainOfficeAreaIdsAsync(conn, tx);
                var (destOk, destinationAreaId, destError) = CuttingRules.ResolveDestination(
                    entry.DestinationType, entry.DestinationAreaId, areaId.Value, mainOfficeAreaIds);
                if (!destOk)
                {
                    tx.Rollback();
                    return (false, destError, 0);
                }

                var stockId = await _cuttingStockRepo.GetOrCreateLockedAsync(conn, tx, speciesId, areaId.Value, entry.CreatedBy);
                var code = await _batchNumberRepo.GetNextBatchNumberAsync(conn, tx, "CP", entry.CuttingDate.Year);

                var insert = new SqlCommand(@"
INSERT INTO dbo.CuttingProductions
(ProductionCode, MotherPlantId, SpeciesId, AreaId, CuttingStockId, CuttingDate, Quantity, SupervisorId, Remarks, CreatedById, CreatedBy,
 DestinationType, DestinationAreaId, SubmissionToken)
VALUES
(@Code, @MotherPlantId, @SpeciesId, @AreaId, @CuttingStockId, @CuttingDate, @Quantity, @SupervisorId, @Remarks, @CreatedById, @CreatedBy,
 @DestinationType, @DestinationAreaId, @SubmissionToken);
SELECT CAST(SCOPE_IDENTITY() AS INT);", conn, tx);
                insert.Parameters.AddWithValue("@Code", code);
                insert.Parameters.AddWithValue("@MotherPlantId", entry.MotherPlantId);
                insert.Parameters.AddWithValue("@SpeciesId", speciesId);
                insert.Parameters.AddWithValue("@AreaId", areaId.Value);
                insert.Parameters.AddWithValue("@CuttingStockId", stockId);
                insert.Parameters.AddWithValue("@CuttingDate", entry.CuttingDate.Date);
                insert.Parameters.AddWithValue("@Quantity", entry.Quantity);
                insert.Parameters.AddWithValue("@SupervisorId", entry.SupervisorId);
                insert.Parameters.AddWithValue("@Remarks", (object?)entry.Remarks ?? DBNull.Value);
                insert.Parameters.AddWithValue("@CreatedById", (object?)userId ?? DBNull.Value);
                insert.Parameters.AddWithValue("@CreatedBy", (object?)entry.CreatedBy ?? DBNull.Value);
                insert.Parameters.AddWithValue("@DestinationType", entry.DestinationType!);
                insert.Parameters.AddWithValue("@DestinationAreaId", destinationAreaId!.Value);
                insert.Parameters.AddWithValue("@SubmissionToken", (object?)entry.SubmissionToken ?? DBNull.Value);
                var newId = (int)(await insert.ExecuteScalarAsync())!;

                var (ok, message) = await _cuttingStockRepo.RecordTransactionAsync(
                    conn, tx, stockId, entry.Quantity, "Harvest", "CuttingProduction", newId, userId, entry.Remarks);
                if (!ok)
                {
                    tx.Rollback();
                    return (false, message, 0);
                }

                if (entry.DestinationType == CuttingDestination.MainOffice)
                {
                    // Give the harvest to Main Office through the SAME delivery the "Send Cuttings to
                    // Main Office" page creates: pending Main Office confirmation, quantity In-Transit.
                    var delivery = new InternalTransfer
                    {
                        StockType = "Cutting",
                        SourceCuttingStockId = stockId,
                        PendingConfirmationAreaId = destinationAreaId,
                        Quantity = entry.Quantity,
                        Remarks = string.IsNullOrWhiteSpace(entry.Remarks) ? $"Cutting entry {code}" : $"Cutting entry {code}: {entry.Remarks}",
                        CreatedBy = entry.CreatedBy,
                        SourceCuttingProductionId = newId
                    };
                    var (deliveryOk, deliveryMessage, deliveryId) = await _transferRepo.CreateCuttingDeliveryAsync(conn, tx, delivery, userId);
                    if (!deliveryOk)
                    {
                        tx.Rollback();
                        return (false, deliveryMessage, 0);
                    }
                    entry.TransferId = deliveryId;
                    entry.TransferCode = delivery.TransferCode;
                }

                tx.Commit();
                entry.Id = newId;
                entry.ProductionCode = code;
                entry.SpeciesId = speciesId;
                entry.AreaId = areaId.Value;
                entry.CuttingStockId = stockId;
                entry.DestinationAreaId = destinationAreaId;
                return (true, null, newId);
            }
            catch (SqlException ex) when ((ex.Number == 2601 || ex.Number == 2627) && entry.SubmissionToken.HasValue)
            {
                // Two identical submits raced past the check above; the unique index on
                // SubmissionToken let exactly one through. Report that one.
                try { tx.Rollback(); } catch { }
                var saved = await FindByTokenAsync(entry.SubmissionToken.Value);
                if (saved != null)
                {
                    MarkDuplicate(entry, saved.Value.Id, saved.Value.Code);
                    return (true, null, saved.Value.Id);
                }
                return (false, ex.Message, 0);
            }
            catch (Exception ex)
            {
                try { tx.Rollback(); } catch { }
                return (false, ex.Message, 0);
            }
        }

        private static void MarkDuplicate(CuttingProduction entry, int id, string code)
        {
            entry.Id = id;
            entry.ProductionCode = code;
            entry.IsDuplicateSubmission = true;
        }

        private static async Task<(int Id, string Code)?> FindByTokenAsync(SqlConnection conn, SqlTransaction? tx, Guid token)
        {
            using var cmd = new SqlCommand(
                "SELECT Id, ProductionCode FROM dbo.CuttingProductions WITH (UPDLOCK, HOLDLOCK) WHERE SubmissionToken = @Token", conn, tx);
            cmd.Parameters.AddWithValue("@Token", token);
            using var reader = await cmd.ExecuteReaderAsync();
            return await reader.ReadAsync() ? (reader.GetInt32(0), reader.GetString(1)) : null;
        }

        private async Task<(int Id, string Code)?> FindByTokenAsync(Guid token)
        {
            using var conn = _dbHelper.GetConnection();
            await conn.OpenAsync();
            return await FindByTokenAsync(conn, null, token);
        }

        private static async Task<List<int>> GetActiveMainOfficeAreaIdsAsync(SqlConnection conn, SqlTransaction tx)
        {
            var ids = new List<int>();
            using var cmd = new SqlCommand("SELECT Id FROM dbo.Area WHERE AreaType = N'MainOffice' AND IsActive = 1", conn, tx);
            using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
                ids.Add(reader.GetInt32(0));
            return ids;
        }

        private static CuttingProduction Map(SqlDataReader r) => new()
        {
            Id = r.GetInt32(r.GetOrdinal("Id")),
            ProductionCode = r.GetString(r.GetOrdinal("ProductionCode")),
            MotherPlantId = r.GetInt32(r.GetOrdinal("MotherPlantId")),
            MotherPlantCode = r.GetString(r.GetOrdinal("MotherPlantCode")),
            SpeciesId = r.GetInt32(r.GetOrdinal("SpeciesId")),
            SpeciesName = r.GetString(r.GetOrdinal("SpeciesName")).Trim(),
            Color = r.IsDBNull(r.GetOrdinal("Color")) ? null : r.GetString(r.GetOrdinal("Color")),
            PlantTypeName = r.GetString(r.GetOrdinal("PlantTypeName")),
            AreaId = r.GetInt32(r.GetOrdinal("AreaId")),
            AreaName = r.GetString(r.GetOrdinal("AreaName")),
            CuttingStockId = r.GetInt32(r.GetOrdinal("CuttingStockId")),
            CuttingDate = r.GetDateTime(r.GetOrdinal("CuttingDate")),
            Quantity = r.GetDecimal(r.GetOrdinal("Quantity")),
            SupervisorId = r.GetInt32(r.GetOrdinal("SupervisorId")),
            SupervisorName = r.IsDBNull(r.GetOrdinal("SupervisorName")) ? null : r.GetString(r.GetOrdinal("SupervisorName")),
            Remarks = r.IsDBNull(r.GetOrdinal("Remarks")) ? null : r.GetString(r.GetOrdinal("Remarks")),
            CreatedById = r.IsDBNull(r.GetOrdinal("CreatedById")) ? null : r.GetInt32(r.GetOrdinal("CreatedById")),
            CreatedBy = r.IsDBNull(r.GetOrdinal("CreatedBy")) ? null : r.GetString(r.GetOrdinal("CreatedBy")),
            CreatedDate = r.GetDateTime(r.GetOrdinal("CreatedDate")),
            DestinationType = r.IsDBNull(r.GetOrdinal("DestinationType")) ? null : r.GetString(r.GetOrdinal("DestinationType")),
            DestinationAreaId = r.IsDBNull(r.GetOrdinal("DestinationAreaId")) ? null : r.GetInt32(r.GetOrdinal("DestinationAreaId")),
            DestinationAreaName = r.IsDBNull(r.GetOrdinal("DestinationAreaName")) ? null : r.GetString(r.GetOrdinal("DestinationAreaName")),
            TransferId = r.IsDBNull(r.GetOrdinal("TransferId")) ? null : r.GetInt32(r.GetOrdinal("TransferId")),
            TransferCode = r.IsDBNull(r.GetOrdinal("TransferCode")) ? null : r.GetString(r.GetOrdinal("TransferCode")),
            TransferStatus = r.IsDBNull(r.GetOrdinal("TransferStatus")) ? null : r.GetString(r.GetOrdinal("TransferStatus")),
            PolyhouseId = r.IsDBNull(r.GetOrdinal("PolyhouseId")) ? null : r.GetInt32(r.GetOrdinal("PolyhouseId")),
            PolyhouseName = r.IsDBNull(r.GetOrdinal("PolyhouseName")) ? null : r.GetString(r.GetOrdinal("PolyhouseName"))
        };
    }
}
