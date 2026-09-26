using Microsoft.Data.SqlClient;
using PlantStockManager.Models;
using PlantStockManager.Services;

namespace PlantStockManager.Data
{
    // Phase D: Cutting -> Pot Production Batch -> Daily Production -> READY
    // -> Potted Plant Stock. Every stock change goes through the stock's own
    // ledger inside one locked transaction:
    //   batch start      Cutting Stock      -allocated   ('Potted')
    //   daily production Empty Pot (Area)   -pots        ('Consumption')
    //   READY            Potted Plant Stock +ready pots  ('Production')
    //                    Cutting Stock      +unused      ('ReversalReturn', when returned)
    //   cancel (no production yet) Cutting Stock +allocated ('ReversalReturn')
    public class PotBatchRepository
    {
        private readonly DatabaseHelper _dbHelper;
        private readonly BatchNumberRepository _batchNumberRepo;
        private readonly CuttingStockRepository _cuttingStockRepo;
        private readonly EmptyPotInventoryRepository _emptyPotRepo;
        private readonly PottedPlantStockRepository _pottedStockRepo;
        private readonly UserRoleRepository _userRoleRepo;

        public PotBatchRepository(DatabaseHelper dbHelper, BatchNumberRepository batchNumberRepo, CuttingStockRepository cuttingStockRepo,
            EmptyPotInventoryRepository emptyPotRepo, PottedPlantStockRepository pottedStockRepo, UserRoleRepository userRoleRepo)
        {
            _dbHelper = dbHelper;
            _batchNumberRepo = batchNumberRepo;
            _cuttingStockRepo = cuttingStockRepo;
            _emptyPotRepo = emptyPotRepo;
            _pottedStockRepo = pottedStockRepo;
            _userRoleRepo = userRoleRepo;
        }

        private const string BaseSelect = @"
SELECT b.Id, b.BatchCode, b.SourceCuttingStockId, b.SpeciesId, ps.Name AS SpeciesName, ps.Color, pt.Name AS PlantTypeName,
       b.AreaId, a.Name AS AreaName, csa.Name AS SourceAreaName, b.PotSize, b.EmptyPotInventoryId, epi.PhysicalQuantity AS EmptyPotsAvailable,
       b.CuttingAllocated, b.ProductionStartDate, b.ExpectedReadyDate, b.SupervisorId, sup.Name AS SupervisorName,
       b.Status, b.ReadyQuantity, b.WastageReason, b.UnusedCuttingAction, b.ReadyConfirmedById, rcb.Name AS ReadyConfirmedByName,
       b.ReadyDate, b.ReadyRemarks, b.PottedPlantStockId, b.Remarks, b.CreatedById, cb.Name AS CreatedByName, b.CreatedBy, b.CreatedDate,
       ISNULL(pe.Potted, 0) AS PottedQuantity
FROM dbo.PotProductionBatches b
INNER JOIN dbo.PlantSpecies ps ON ps.Id = b.SpeciesId
INNER JOIN dbo.PlantTypes pt ON pt.Id = ps.PlantTypeId
INNER JOIN dbo.Area a ON a.Id = b.AreaId
INNER JOIN dbo.CuttingStock cs ON cs.Id = b.SourceCuttingStockId
INNER JOIN dbo.Area csa ON csa.Id = cs.AreaId
INNER JOIN dbo.EmptyPotInventory epi ON epi.Id = b.EmptyPotInventoryId
LEFT JOIN dbo.IMSUsers sup ON sup.Id = b.SupervisorId
LEFT JOIN dbo.IMSUsers rcb ON rcb.Id = b.ReadyConfirmedById
LEFT JOIN dbo.IMSUsers cb ON cb.Id = b.CreatedById
OUTER APPLY (SELECT SUM(e.Quantity) AS Potted FROM dbo.PotProductionEntries e WHERE e.BatchId = b.Id) pe";

        public async Task<List<PotProductionBatch>> GetAllAsync(string? status = null)
        {
            var list = new List<PotProductionBatch>();
            using var conn = _dbHelper.GetConnection();
            await conn.OpenAsync();
            using var cmd = new SqlCommand(BaseSelect + " WHERE (@Status IS NULL OR b.Status = @Status) ORDER BY b.ExpectedReadyDate, b.Id", conn);
            cmd.Parameters.AddWithValue("@Status", (object?)status ?? DBNull.Value);
            using var r = await cmd.ExecuteReaderAsync();
            while (await r.ReadAsync())
                list.Add(Map(r));
            return list;
        }

        public async Task<PotProductionBatch?> GetByIdAsync(int id)
        {
            using var conn = _dbHelper.GetConnection();
            await conn.OpenAsync();
            using var cmd = new SqlCommand(BaseSelect + " WHERE b.Id = @Id", conn);
            cmd.Parameters.AddWithValue("@Id", id);
            using var r = await cmd.ExecuteReaderAsync();
            return await r.ReadAsync() ? Map(r) : null;
        }

        public async Task<List<PotProductionEntry>> GetEntriesAsync(int batchId)
        {
            var list = new List<PotProductionEntry>();
            using var conn = _dbHelper.GetConnection();
            await conn.OpenAsync();
            using var cmd = new SqlCommand(
                "SELECT Id, BatchId, ProductionDate, Quantity, Remarks, CreatedById, CreatedBy, CreatedDate FROM dbo.PotProductionEntries WHERE BatchId = @Id ORDER BY ProductionDate, Id", conn);
            cmd.Parameters.AddWithValue("@Id", batchId);
            using var r = await cmd.ExecuteReaderAsync();
            while (await r.ReadAsync())
            {
                list.Add(new PotProductionEntry
                {
                    Id = r.GetInt32(0),
                    BatchId = r.GetInt32(1),
                    ProductionDate = r.GetDateTime(2),
                    Quantity = r.GetDecimal(3),
                    Remarks = r.IsDBNull(4) ? null : r.GetString(4),
                    CreatedById = r.IsDBNull(5) ? null : r.GetInt32(5),
                    CreatedBy = r.IsDBNull(6) ? null : r.GetString(6),
                    CreatedDate = r.GetDateTime(7)
                });
            }
            return list;
        }

        // Starts a batch: the cuttings leave Cutting Stock now (allocated to the
        // batch) and the batch is tied to the production Area's own empty pots.
        public async Task<(bool Success, string? Message, int Id)> CreateAsync(PotProductionBatch entry, int userId)
        {
            using var conn = _dbHelper.GetConnection();
            await conn.OpenAsync();
            using var tx = conn.BeginTransaction();
            try
            {
                var csCmd = new SqlCommand("SELECT SpeciesId, PhysicalQuantity, InTransitQuantity FROM dbo.CuttingStock WITH (UPDLOCK, HOLDLOCK) WHERE Id = @Id", conn, tx);
                csCmd.Parameters.AddWithValue("@Id", entry.SourceCuttingStockId);
                int speciesId; decimal physical, inTransit;
                using (var r = await csCmd.ExecuteReaderAsync())
                {
                    if (!await r.ReadAsync())
                    {
                        r.Close();
                        tx.Rollback();
                        return (false, "Cutting Stock not found.", 0);
                    }
                    speciesId = r.GetInt32(0);
                    physical = r.GetDecimal(1);
                    inTransit = r.GetDecimal(2);
                }

                var areaCmd = new SqlCommand("SELECT IsActive, AreaType FROM dbo.Area WHERE Id = @Id", conn, tx);
                areaCmd.Parameters.AddWithValue("@Id", entry.AreaId);
                using (var r = await areaCmd.ExecuteReaderAsync())
                {
                    if (!await r.ReadAsync() || !r.GetBoolean(0) || (!r.IsDBNull(1) && r.GetString(1) == DirectSowingRules.MainOfficeAreaType))
                    {
                        r.Close();
                        tx.Rollback();
                        return (false, "Choose an active production Area (not the Office store).", 0);
                    }
                }

                var sizeCmd = new SqlCommand("SELECT COUNT(*) FROM dbo.PotSizes WHERE Name = @Name AND IsActive = 1", conn, tx);
                sizeCmd.Parameters.AddWithValue("@Name", entry.PotSize ?? "");
                if ((int)(await sizeCmd.ExecuteScalarAsync())! == 0)
                {
                    tx.Rollback();
                    return (false, "Choose a pot size from the list.", 0);
                }

                var eligible = (await _userRoleRepo.GetUsersInRoleAsync(SupervisorRules.MotherPlantSupervisor, entry.AreaId, conn, tx))
                    .Select(u => u.EmployeeID).ToList();
                var (ok, error) = PotBatchRules.ValidateCreate(entry.CuttingAllocated, physical - inTransit, entry.ProductionStartDate,
                    entry.ExpectedReadyDate, entry.SupervisorId, userId, eligible);
                if (!ok)
                {
                    tx.Rollback();
                    return (false, error, 0);
                }

                var poolId = await _emptyPotRepo.GetOrCreateLockedAsync(conn, tx, entry.PotSize!, entry.AreaId, entry.CreatedBy);
                var code = await _batchNumberRepo.GetNextBatchNumberAsync(conn, tx, "PB", entry.ProductionStartDate.Year);

                var insert = new SqlCommand(@"
INSERT INTO dbo.PotProductionBatches
(BatchCode, SourceCuttingStockId, SpeciesId, AreaId, PotSize, EmptyPotInventoryId, CuttingAllocated, ProductionStartDate, ExpectedReadyDate,
 SupervisorId, Status, Remarks, CreatedById, CreatedBy)
VALUES
(@Code, @SourceCuttingStockId, @SpeciesId, @AreaId, @PotSize, @PoolId, @Allocated, @Start, @Expected, @SupervisorId, N'InProduction', @Remarks, @CreatedById, @CreatedBy);
SELECT CAST(SCOPE_IDENTITY() AS INT);", conn, tx);
                insert.Parameters.AddWithValue("@Code", code);
                insert.Parameters.AddWithValue("@SourceCuttingStockId", entry.SourceCuttingStockId);
                insert.Parameters.AddWithValue("@SpeciesId", speciesId);
                insert.Parameters.AddWithValue("@AreaId", entry.AreaId);
                insert.Parameters.AddWithValue("@PotSize", entry.PotSize!);
                insert.Parameters.AddWithValue("@PoolId", poolId);
                insert.Parameters.AddWithValue("@Allocated", entry.CuttingAllocated);
                insert.Parameters.AddWithValue("@Start", entry.ProductionStartDate.Date);
                insert.Parameters.AddWithValue("@Expected", entry.ExpectedReadyDate.Date);
                insert.Parameters.AddWithValue("@SupervisorId", entry.SupervisorId);
                insert.Parameters.AddWithValue("@Remarks", (object?)entry.Remarks ?? DBNull.Value);
                insert.Parameters.AddWithValue("@CreatedById", userId);
                insert.Parameters.AddWithValue("@CreatedBy", (object?)entry.CreatedBy ?? DBNull.Value);
                var newId = (int)(await insert.ExecuteScalarAsync())!;

                var (cutOk, cutMessage) = await _cuttingStockRepo.RecordTransactionAsync(
                    conn, tx, entry.SourceCuttingStockId, -entry.CuttingAllocated, "Potted", "PotProductionBatch", newId, userId, $"Allocated to pot batch {code}");
                if (!cutOk)
                {
                    tx.Rollback();
                    return (false, cutMessage, 0);
                }

                tx.Commit();
                entry.Id = newId;
                entry.BatchCode = code;
                entry.SpeciesId = speciesId;
                entry.EmptyPotInventoryId = poolId;
                return (true, null, newId);
            }
            catch (Exception ex)
            {
                try { tx.Rollback(); } catch { }
                return (false, ex.Message, 0);
            }
        }

        // One day's production: pots made from the batch's cuttings, each using
        // one empty pot of the batch size from the batch Area's own stock.
        public async Task<(bool Success, string? Message)> AddEntryAsync(int batchId, DateTime productionDate, decimal quantity, string? remarks, int? userId, string? createdBy)
        {
            using var conn = _dbHelper.GetConnection();
            await conn.OpenAsync();
            using var tx = conn.BeginTransaction();
            try
            {
                var bCmd = new SqlCommand(@"
SELECT Status, CuttingAllocated, ProductionStartDate, EmptyPotInventoryId, BatchCode
FROM dbo.PotProductionBatches WITH (UPDLOCK, HOLDLOCK) WHERE Id = @Id", conn, tx);
                bCmd.Parameters.AddWithValue("@Id", batchId);
                string status, code; decimal allocated; DateTime start; int poolId;
                using (var r = await bCmd.ExecuteReaderAsync())
                {
                    if (!await r.ReadAsync())
                    {
                        r.Close();
                        tx.Rollback();
                        return (false, "Pot batch not found.");
                    }
                    status = r.GetString(0);
                    allocated = r.GetDecimal(1);
                    start = r.GetDateTime(2);
                    poolId = r.GetInt32(3);
                    code = r.GetString(4);
                }
                var sumCmd = new SqlCommand("SELECT ISNULL(SUM(Quantity), 0) FROM dbo.PotProductionEntries WHERE BatchId = @Id", conn, tx);
                sumCmd.Parameters.AddWithValue("@Id", batchId);
                var produced = (decimal)(await sumCmd.ExecuteScalarAsync())!;
                var potCmd = new SqlCommand("SELECT PhysicalQuantity FROM dbo.EmptyPotInventory WITH (UPDLOCK, HOLDLOCK) WHERE Id = @Id", conn, tx);
                potCmd.Parameters.AddWithValue("@Id", poolId);
                var pots = (decimal)(await potCmd.ExecuteScalarAsync())!;

                var (ok, error) = PotBatchRules.ValidateEntry(status, quantity, produced, allocated, pots, productionDate, start, DateTime.Today);
                if (!ok)
                {
                    tx.Rollback();
                    return (false, error);
                }

                var insert = new SqlCommand(@"
INSERT INTO dbo.PotProductionEntries (BatchId, ProductionDate, Quantity, Remarks, CreatedById, CreatedBy)
VALUES (@BatchId, @Date, @Quantity, @Remarks, @CreatedById, @CreatedBy);
SELECT CAST(SCOPE_IDENTITY() AS INT);", conn, tx);
                insert.Parameters.AddWithValue("@BatchId", batchId);
                insert.Parameters.AddWithValue("@Date", productionDate.Date);
                insert.Parameters.AddWithValue("@Quantity", quantity);
                insert.Parameters.AddWithValue("@Remarks", (object?)remarks ?? DBNull.Value);
                insert.Parameters.AddWithValue("@CreatedById", (object?)userId ?? DBNull.Value);
                insert.Parameters.AddWithValue("@CreatedBy", (object?)createdBy ?? DBNull.Value);
                var entryId = (int)(await insert.ExecuteScalarAsync())!;

                var (potOk, potMessage) = await _emptyPotRepo.RecordTransactionAsync(
                    conn, tx, poolId, -quantity, "Consumption", "PotProductionEntry", entryId, userId, $"Pot batch {code}");
                if (!potOk)
                {
                    tx.Rollback();
                    return (false, potMessage);
                }

                tx.Commit();
                return (true, null);
            }
            catch (Exception ex)
            {
                try { tx.Rollback(); } catch { }
                return (false, ex.Message);
            }
        }

        // READY: only the assigned supervisor (never the batch creator), who
        // must still be an active Mother Plant Supervisor of the batch Area.
        // Ready pots become Potted Plant Stock of the batch Area; lost pots are
        // wastage; cuttings never potted go back to Cutting Stock or are
        // recorded as wastage. Zero ready pots closes the batch as a complete
        // loss (status Lost, mandatory reason, nothing added to stock).
        public async Task<(bool Success, string? Message)> ConfirmReadyAsync(int batchId, decimal readyQuantity, string? wastageReason,
            string? unusedCuttingAction, string? remarks, int userId, string? modifiedBy)
        {
            using var conn = _dbHelper.GetConnection();
            await conn.OpenAsync();
            using var tx = conn.BeginTransaction();
            try
            {
                var bCmd = new SqlCommand(@"
SELECT Status, CuttingAllocated, SupervisorId, CreatedById, SpeciesId, PotSize, AreaId, EmptyPotInventoryId, SourceCuttingStockId, BatchCode
FROM dbo.PotProductionBatches WITH (UPDLOCK, HOLDLOCK) WHERE Id = @Id", conn, tx);
                bCmd.Parameters.AddWithValue("@Id", batchId);
                string status, potSize, code; decimal allocated; int supervisorId, createdById, speciesId, areaId, poolId, cuttingStockId;
                using (var r = await bCmd.ExecuteReaderAsync())
                {
                    if (!await r.ReadAsync())
                    {
                        r.Close();
                        tx.Rollback();
                        return (false, "Pot batch not found.");
                    }
                    status = r.GetString(0);
                    allocated = r.GetDecimal(1);
                    supervisorId = r.GetInt32(2);
                    createdById = r.GetInt32(3);
                    speciesId = r.GetInt32(4);
                    potSize = r.GetString(5);
                    areaId = r.GetInt32(6);
                    poolId = r.GetInt32(7);
                    cuttingStockId = r.GetInt32(8);
                    code = r.GetString(9);
                }
                if (userId != supervisorId || userId == createdById)
                {
                    tx.Rollback();
                    return (false, "Only the supervisor assigned to this batch can confirm it READY.");
                }
                var stillSupervisor = (await _userRoleRepo.GetUsersInRoleAsync(SupervisorRules.MotherPlantSupervisor, areaId, conn, tx))
                    .Any(u => u.EmployeeID == userId);
                if (!stillSupervisor)
                {
                    tx.Rollback();
                    return (false, "You are no longer an active Mother Plant Supervisor of this batch's Area.");
                }

                var sumCmd = new SqlCommand("SELECT ISNULL(SUM(Quantity), 0) FROM dbo.PotProductionEntries WHERE BatchId = @Id", conn, tx);
                sumCmd.Parameters.AddWithValue("@Id", batchId);
                var produced = (decimal)(await sumCmd.ExecuteScalarAsync())!;

                var (ok, wastage, unused, error) = PotBatchRules.ValidateReady(status, readyQuantity, produced, allocated, wastageReason, unusedCuttingAction);
                if (!ok)
                {
                    tx.Rollback();
                    return (false, error);
                }

                var newStatus = PotBatchRules.StatusFor(readyQuantity);
                int? stockId = null;
                if (newStatus == PotBatchRules.Ready)
                {
                    stockId = await _pottedStockRepo.GetOrCreateLockedAsync(conn, tx, speciesId, potSize, areaId, poolId, modifiedBy);
                    var (stockOk, stockMessage) = await _pottedStockRepo.RecordTransactionAsync(
                        conn, tx, stockId.Value, readyQuantity, "Production", "PotProductionBatch", batchId, userId, $"READY: pot batch {code}");
                    if (!stockOk)
                    {
                        tx.Rollback();
                        return (false, stockMessage);
                    }
                }

                if (unused > 0 && unusedCuttingAction == PotBatchRules.ReturnedToStock)
                {
                    var (cutOk, cutMessage) = await _cuttingStockRepo.RecordTransactionAsync(
                        conn, tx, cuttingStockId, unused, "ReversalReturn", "PotProductionBatch", batchId, userId, $"Unused cuttings of pot batch {code}");
                    if (!cutOk)
                    {
                        tx.Rollback();
                        return (false, cutMessage);
                    }
                }

                var update = new SqlCommand(@"
UPDATE dbo.PotProductionBatches
SET Status = @Status, ReadyQuantity = @Ready, WastageReason = @Reason, UnusedCuttingAction = @Unused,
    ReadyConfirmedById = @UserId, ReadyDate = SYSUTCDATETIME(), ReadyRemarks = @Remarks, PottedPlantStockId = @StockId,
    ModifiedBy = @ModifiedBy, ModifiedDate = SYSUTCDATETIME()
WHERE Id = @Id", conn, tx);
                update.Parameters.AddWithValue("@Status", newStatus);
                update.Parameters.AddWithValue("@Ready", readyQuantity);
                update.Parameters.AddWithValue("@Reason", wastage > 0 || newStatus == PotBatchRules.Lost ? wastageReason! : DBNull.Value);
                update.Parameters.AddWithValue("@Unused", unused > 0 ? unusedCuttingAction! : DBNull.Value);
                update.Parameters.AddWithValue("@UserId", userId);
                update.Parameters.AddWithValue("@Remarks", (object?)remarks ?? DBNull.Value);
                update.Parameters.AddWithValue("@StockId", (object?)stockId ?? DBNull.Value);
                update.Parameters.AddWithValue("@ModifiedBy", (object?)modifiedBy ?? DBNull.Value);
                update.Parameters.AddWithValue("@Id", batchId);
                await update.ExecuteNonQueryAsync();

                tx.Commit();
                return (true, null);
            }
            catch (Exception ex)
            {
                try { tx.Rollback(); } catch { }
                return (false, ex.Message);
            }
        }

        // Before any production: the batch is cancelled and its cuttings go
        // back to Cutting Stock. After production it must be confirmed READY.
        public async Task<(bool Success, string? Message)> CancelAsync(int batchId, int userId, string? modifiedBy)
        {
            using var conn = _dbHelper.GetConnection();
            await conn.OpenAsync();
            using var tx = conn.BeginTransaction();
            try
            {
                var bCmd = new SqlCommand(@"
SELECT Status, CuttingAllocated, SourceCuttingStockId, CreatedById, SupervisorId, BatchCode
FROM dbo.PotProductionBatches WITH (UPDLOCK, HOLDLOCK) WHERE Id = @Id", conn, tx);
                bCmd.Parameters.AddWithValue("@Id", batchId);
                string status, code; decimal allocated; int cuttingStockId, createdById, supervisorId;
                using (var r = await bCmd.ExecuteReaderAsync())
                {
                    if (!await r.ReadAsync())
                    {
                        r.Close();
                        tx.Rollback();
                        return (false, "Pot batch not found.");
                    }
                    status = r.GetString(0);
                    allocated = r.GetDecimal(1);
                    cuttingStockId = r.GetInt32(2);
                    createdById = r.GetInt32(3);
                    supervisorId = r.GetInt32(4);
                    code = r.GetString(5);
                }
                if (userId != createdById && userId != supervisorId)
                {
                    tx.Rollback();
                    return (false, "Only the batch creator or its assigned supervisor can cancel it.");
                }
                if (status != PotBatchRules.InProduction)
                {
                    tx.Rollback();
                    return (false, "This batch is already closed.");
                }
                var countCmd = new SqlCommand("SELECT COUNT(*) FROM dbo.PotProductionEntries WHERE BatchId = @Id", conn, tx);
                countCmd.Parameters.AddWithValue("@Id", batchId);
                if ((int)(await countCmd.ExecuteScalarAsync())! > 0)
                {
                    tx.Rollback();
                    return (false, "This batch already has production. Confirm it READY instead of cancelling.");
                }

                var (ok, message) = await _cuttingStockRepo.RecordTransactionAsync(
                    conn, tx, cuttingStockId, allocated, "ReversalReturn", "PotProductionBatch", batchId, userId, $"Cancelled pot batch {code}");
                if (!ok)
                {
                    tx.Rollback();
                    return (false, message);
                }
                var update = new SqlCommand(
                    "UPDATE dbo.PotProductionBatches SET Status = N'Cancelled', ModifiedBy = @ModifiedBy, ModifiedDate = SYSUTCDATETIME() WHERE Id = @Id", conn, tx);
                update.Parameters.AddWithValue("@ModifiedBy", (object?)modifiedBy ?? DBNull.Value);
                update.Parameters.AddWithValue("@Id", batchId);
                await update.ExecuteNonQueryAsync();
                tx.Commit();
                return (true, null);
            }
            catch (Exception ex)
            {
                try { tx.Rollback(); } catch { }
                return (false, ex.Message);
            }
        }

        public async Task<(bool Success, string? Message)> UpdateExpectedReadyDateAsync(int batchId, DateTime expectedReadyDate, int userId, string? modifiedBy)
        {
            using var conn = _dbHelper.GetConnection();
            await conn.OpenAsync();
            using var cmd = new SqlCommand(@"
UPDATE dbo.PotProductionBatches
SET ExpectedReadyDate = @Date, ModifiedBy = @ModifiedBy, ModifiedDate = SYSUTCDATETIME()
WHERE Id = @Id AND Status = N'InProduction' AND ProductionStartDate <= @Date AND (CreatedById = @UserId OR SupervisorId = @UserId)", conn);
            cmd.Parameters.AddWithValue("@Date", expectedReadyDate.Date);
            cmd.Parameters.AddWithValue("@ModifiedBy", (object?)modifiedBy ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@Id", batchId);
            cmd.Parameters.AddWithValue("@UserId", userId);
            var rows = await cmd.ExecuteNonQueryAsync();
            return rows > 0 ? (true, null) : (false, "The expected ready date can be changed only by the batch creator or supervisor, while the batch is In Production, and not before its start date.");
        }

        private static PotProductionBatch Map(SqlDataReader r) => new()
        {
            Id = r.GetInt32(r.GetOrdinal("Id")),
            BatchCode = r.GetString(r.GetOrdinal("BatchCode")),
            SourceCuttingStockId = r.GetInt32(r.GetOrdinal("SourceCuttingStockId")),
            SpeciesId = r.GetInt32(r.GetOrdinal("SpeciesId")),
            SpeciesName = r.GetString(r.GetOrdinal("SpeciesName")).Trim(),
            Color = r.IsDBNull(r.GetOrdinal("Color")) ? null : r.GetString(r.GetOrdinal("Color")),
            PlantTypeName = r.GetString(r.GetOrdinal("PlantTypeName")),
            AreaId = r.GetInt32(r.GetOrdinal("AreaId")),
            AreaName = r.GetString(r.GetOrdinal("AreaName")),
            SourceAreaName = r.GetString(r.GetOrdinal("SourceAreaName")),
            PotSize = r.GetString(r.GetOrdinal("PotSize")),
            EmptyPotInventoryId = r.GetInt32(r.GetOrdinal("EmptyPotInventoryId")),
            EmptyPotsAvailable = r.GetDecimal(r.GetOrdinal("EmptyPotsAvailable")),
            CuttingAllocated = r.GetDecimal(r.GetOrdinal("CuttingAllocated")),
            ProductionStartDate = r.GetDateTime(r.GetOrdinal("ProductionStartDate")),
            ExpectedReadyDate = r.GetDateTime(r.GetOrdinal("ExpectedReadyDate")),
            SupervisorId = r.GetInt32(r.GetOrdinal("SupervisorId")),
            SupervisorName = r.IsDBNull(r.GetOrdinal("SupervisorName")) ? null : r.GetString(r.GetOrdinal("SupervisorName")),
            Status = r.GetString(r.GetOrdinal("Status")),
            ReadyQuantity = r.IsDBNull(r.GetOrdinal("ReadyQuantity")) ? null : r.GetDecimal(r.GetOrdinal("ReadyQuantity")),
            WastageReason = r.IsDBNull(r.GetOrdinal("WastageReason")) ? null : r.GetString(r.GetOrdinal("WastageReason")),
            UnusedCuttingAction = r.IsDBNull(r.GetOrdinal("UnusedCuttingAction")) ? null : r.GetString(r.GetOrdinal("UnusedCuttingAction")),
            ReadyConfirmedById = r.IsDBNull(r.GetOrdinal("ReadyConfirmedById")) ? null : r.GetInt32(r.GetOrdinal("ReadyConfirmedById")),
            ReadyConfirmedByName = r.IsDBNull(r.GetOrdinal("ReadyConfirmedByName")) ? null : r.GetString(r.GetOrdinal("ReadyConfirmedByName")),
            ReadyDate = r.IsDBNull(r.GetOrdinal("ReadyDate")) ? null : r.GetDateTime(r.GetOrdinal("ReadyDate")),
            ReadyRemarks = r.IsDBNull(r.GetOrdinal("ReadyRemarks")) ? null : r.GetString(r.GetOrdinal("ReadyRemarks")),
            PottedPlantStockId = r.IsDBNull(r.GetOrdinal("PottedPlantStockId")) ? null : r.GetInt32(r.GetOrdinal("PottedPlantStockId")),
            Remarks = r.IsDBNull(r.GetOrdinal("Remarks")) ? null : r.GetString(r.GetOrdinal("Remarks")),
            CreatedById = r.GetInt32(r.GetOrdinal("CreatedById")),
            CreatedByName = r.IsDBNull(r.GetOrdinal("CreatedByName")) ? null : r.GetString(r.GetOrdinal("CreatedByName")),
            CreatedBy = r.IsDBNull(r.GetOrdinal("CreatedBy")) ? null : r.GetString(r.GetOrdinal("CreatedBy")),
            CreatedDate = r.GetDateTime(r.GetOrdinal("CreatedDate")),
            PottedQuantity = r.GetDecimal(r.GetOrdinal("PottedQuantity"))
        };
    }
}
