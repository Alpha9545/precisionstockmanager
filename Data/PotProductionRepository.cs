using Microsoft.Data.SqlClient;
using PlantStockManager.Models;

namespace PlantStockManager.Data
{
    // Orchestrates the Phase 7 production event: consumes empty pots
    // from dbo.EmptyPotInventory and produces potted plants into
    // dbo.PottedPlantStock, alongside the dbo.PotProduction record
    // itself. Every quantity change on any stock table goes through its
    // own repository's RecordTransactionAsync (ledger write + physical
    // quantity update, together) inside ONE database transaction, so a
    // failure at any step rolls back everything.
    //
    // Phase 19 ("Phase D"): PotProduction now has TWO possible sources
    // of planting material -- the legacy dbo.PropagationBatches path
    // (InsertAsync, unchanged) and a new dbo.CuttingStock path
    // (InsertFromCuttingStockAsync, additive). Both still consume
    // EmptyPotInventory and produce PottedPlantStock through the exact
    // same mechanism; only where the input comes from differs.
    public class PotProductionRepository
    {
        private readonly DatabaseHelper _dbHelper;
        private readonly BatchNumberRepository _batchNumberRepo;
        private readonly EmptyPotInventoryRepository _emptyPotInventoryRepo;
        private readonly PottedPlantStockRepository _pottedPlantStockRepo;
        private readonly CuttingStockRepository _cuttingStockRepo;

        public PotProductionRepository(
            DatabaseHelper dbHelper,
            BatchNumberRepository batchNumberRepo,
            EmptyPotInventoryRepository emptyPotInventoryRepo,
            PottedPlantStockRepository pottedPlantStockRepo,
            CuttingStockRepository cuttingStockRepo)
        {
            _dbHelper = dbHelper;
            _batchNumberRepo = batchNumberRepo;
            _emptyPotInventoryRepo = emptyPotInventoryRepo;
            _pottedPlantStockRepo = pottedPlantStockRepo;
            _cuttingStockRepo = cuttingStockRepo;
        }

        // PropagationBatches/MotherPlants are now LEFT JOINed (Phase 19)
        // since a Cutting Stock-sourced row has PropagationBatchId/
        // MotherPlantId = NULL. CuttingStock/GrowingPartners are new
        // LEFT JOINs for the new path's own display fields.
        // GrowingPartnerName is resolved via THIS row's own AreaId (not
        // via the CuttingStock row) so it also works, in principle, for
        // any future legacy row whose Area happens to belong to a
        // Growing Partner.
        private const string BaseSelect = @"
SELECT
    pp.Id, pp.ProductionCode, pp.PropagationBatchId, pb.BatchCode AS PropagationBatchCode,
    pp.MotherPlantId, mp.MotherPlantCode, pp.SpeciesId, ps.Name AS SpeciesName, pt.Name AS PlantTypeName,
    pp.SourceCuttingStockId, pp.CuttingQuantityConsumed,
    pp.PotSize, pp.EmptyPotInventoryId, pp.AreaId, a.Name AS AreaName, gp.Name AS GrowingPartnerName,
    pp.ProductionDate, pp.Quantity, pp.Status,
    pp.ResponsiblePersonId, r.Name AS ResponsiblePersonName,
    pp.SupervisorId, sup.Name AS SupervisorName,
    pp.Remarks, pp.CreatedDate, pp.CreatedBy, pp.ModifiedDate, pp.ModifiedBy
FROM dbo.PotProduction pp
LEFT JOIN dbo.PropagationBatches pb ON pp.PropagationBatchId = pb.Id
LEFT JOIN dbo.MotherPlants mp ON pp.MotherPlantId = mp.Id
INNER JOIN dbo.PlantSpecies ps ON pp.SpeciesId = ps.Id
INNER JOIN dbo.PlantTypes pt ON ps.PlantTypeId = pt.Id
LEFT JOIN dbo.Area a ON pp.AreaId = a.Id
LEFT JOIN dbo.GrowingPartners gp ON a.GrowingPartnerId = gp.Id
LEFT JOIN dbo.IMSUsers r ON pp.ResponsiblePersonId = r.Id
LEFT JOIN dbo.IMSUsers sup ON pp.SupervisorId = sup.Id";

        public async Task<List<PotProduction>> GetAllAsync(int? propagationBatchId = null, string? status = null)
        {
            var list = new List<PotProduction>();
            using var conn = _dbHelper.GetConnection();
            await conn.OpenAsync();

            var sql = BaseSelect + @"
WHERE (@PropagationBatchId IS NULL OR pp.PropagationBatchId = @PropagationBatchId)
  AND (@Status IS NULL OR pp.Status = @Status)
ORDER BY pp.CreatedDate DESC";

            using var cmd = new SqlCommand(sql, conn);
            cmd.Parameters.AddWithValue("@PropagationBatchId", (object?)propagationBatchId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@Status", (object?)status ?? DBNull.Value);

            using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                list.Add(Map(reader));
            }
            return list;
        }

        public async Task<PotProduction?> GetByIdAsync(int id)
        {
            using var conn = _dbHelper.GetConnection();
            await conn.OpenAsync();

            var sql = BaseSelect + " WHERE pp.Id = @Id";
            using var cmd = new SqlCommand(sql, conn);
            cmd.Parameters.AddWithValue("@Id", id);
            using var reader = await cmd.ExecuteReaderAsync();
            if (await reader.ReadAsync())
            {
                return Map(reader);
            }
            return null;
        }

        // Legacy path -- unchanged in behavior. Requires
        // entry.PropagationBatchId to be set; every existing caller
        // (Pages/Production/PotProduction/Create.cshtml.cs) already only
        // ever uses this path.
        public async Task<(bool Success, string? Message, int Id)> InsertAsync(PotProduction entry, int? userId)
        {
            if (!entry.PropagationBatchId.HasValue || entry.PropagationBatchId.Value <= 0)
                return (false, "Propagation Batch is required.", 0);

            using var conn = _dbHelper.GetConnection();
            await conn.OpenAsync();
            using var tx = conn.BeginTransaction();

            try
            {
                // 1) Lock the parent Propagation Batch and validate it is
                // assessed and has remaining Survived capacity -- same
                // HOLDLOCK discipline used throughout this workflow.
                var pbLockCmd = new SqlCommand(
                    "SELECT SurvivedQuantity, MotherPlantId, SpeciesId, Status FROM dbo.PropagationBatches WITH (UPDLOCK, HOLDLOCK) WHERE Id = @Id",
                    conn, tx);
                pbLockCmd.Parameters.AddWithValue("@Id", entry.PropagationBatchId.Value);
                using var pbReader = await pbLockCmd.ExecuteReaderAsync();
                if (!await pbReader.ReadAsync())
                {
                    pbReader.Close();
                    tx.Rollback();
                    return (false, "Propagation Batch not found.", 0);
                }
                var survivedQuantity = pbReader.GetDecimal(pbReader.GetOrdinal("SurvivedQuantity"));
                var motherPlantId = pbReader.GetInt32(pbReader.GetOrdinal("MotherPlantId"));
                var speciesId = pbReader.GetInt32(pbReader.GetOrdinal("SpeciesId"));
                var pbStatus = pbReader.GetString(pbReader.GetOrdinal("Status"));
                pbReader.Close();

                if (pbStatus != "ReadyForPotting" && pbStatus != "Completed")
                {
                    tx.Rollback();
                    return (false, "The Propagation Batch must be Ready For Potting or Completed (Survived/Loss assessed) before Pot Production can draw from it.", 0);
                }

                var alreadyPotted = await GetRecordedQuantityForBatchAsync(conn, tx, entry.PropagationBatchId.Value, excludeId: null);
                if (alreadyPotted + entry.Quantity > survivedQuantity)
                {
                    tx.Rollback();
                    return (false, $"Quantity ({entry.Quantity:N2}) would push the total potted against this Propagation Batch to {(alreadyPotted + entry.Quantity):N2}, exceeding its Survived Quantity of {survivedQuantity:N2}.", 0);
                }

                // 2) Validate the requested Pot Size exists in the
                // requested Area, is active, and (as an early,
                // non-authoritative check) has enough physical stock --
                // the authoritative check happens under lock inside
                // RecordTransactionAsync below. Stock is tracked per-Area
                // since Phase 8, so PotSize alone is no longer enough to
                // identify the pool.
                var potCmd = new SqlCommand(
                    "SELECT Id, PhysicalQuantity, IsActive FROM dbo.EmptyPotInventory WHERE PotSize = @PotSize AND (AreaId = @AreaId OR (AreaId IS NULL AND @AreaId IS NULL))",
                    conn, tx);
                potCmd.Parameters.AddWithValue("@PotSize", entry.PotSize);
                potCmd.Parameters.AddWithValue("@AreaId", (object?)entry.AreaId ?? DBNull.Value);
                using var potReader = await potCmd.ExecuteReaderAsync();
                if (!await potReader.ReadAsync())
                {
                    potReader.Close();
                    tx.Rollback();
                    return (false, "Selected Pot Size does not exist in Empty Pot Inventory for the selected Area.", 0);
                }
                var emptyPotInventoryId = potReader.GetInt32(potReader.GetOrdinal("Id"));
                var potIsActive = potReader.GetBoolean(potReader.GetOrdinal("IsActive"));
                var potPhysical = potReader.GetDecimal(potReader.GetOrdinal("PhysicalQuantity"));
                potReader.Close();

                if (!potIsActive)
                {
                    tx.Rollback();
                    return (false, "Selected Pot Size is inactive.", 0);
                }
                if (potPhysical < entry.Quantity)
                {
                    tx.Rollback();
                    return (false, $"Not enough Empty Pot stock for '{entry.PotSize}' (available {potPhysical:N2}, requested {entry.Quantity:N2}).", 0);
                }

                // Traceability fields are always derived from the locked
                // Propagation Batch row, never trusted from the caller.
                entry.MotherPlantId = motherPlantId;
                entry.SpeciesId = speciesId;

                var productionCode = await _batchNumberRepo.GetNextBatchNumberAsync(conn, tx, "POT", entry.ProductionDate.Year);

                // 3) Insert the Pot Production record itself FIRST, so
                // its Id is available as the ReferenceId on both ledger
                // entries below. If anything after this point fails, the
                // whole transaction (including this insert) rolls back.
                const string insertSql = @"
INSERT INTO dbo.PotProduction
(ProductionCode, PropagationBatchId, MotherPlantId, SpeciesId, PotSize, EmptyPotInventoryId, AreaId, ProductionDate, Quantity, Status,
 ResponsiblePersonId, SupervisorId, Remarks, CreatedDate, CreatedBy)
VALUES
(@ProductionCode, @PropagationBatchId, @MotherPlantId, @SpeciesId, @PotSize, @EmptyPotInventoryId, @AreaId, @ProductionDate, @Quantity, 'Completed',
 @ResponsiblePersonId, @SupervisorId, @Remarks, SYSUTCDATETIME(), @CreatedBy);
SELECT CAST(SCOPE_IDENTITY() AS INT);";

                using var cmd = new SqlCommand(insertSql, conn, tx);
                cmd.Parameters.AddWithValue("@ProductionCode", productionCode);
                cmd.Parameters.AddWithValue("@PropagationBatchId", entry.PropagationBatchId.Value);
                cmd.Parameters.AddWithValue("@MotherPlantId", entry.MotherPlantId.Value);
                cmd.Parameters.AddWithValue("@SpeciesId", entry.SpeciesId);
                cmd.Parameters.AddWithValue("@PotSize", entry.PotSize);
                cmd.Parameters.AddWithValue("@EmptyPotInventoryId", emptyPotInventoryId);
                cmd.Parameters.AddWithValue("@AreaId", (object?)entry.AreaId ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@ProductionDate", entry.ProductionDate.Date);
                cmd.Parameters.AddWithValue("@Quantity", entry.Quantity);
                cmd.Parameters.AddWithValue("@ResponsiblePersonId", (object?)entry.ResponsiblePersonId ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@SupervisorId", (object?)entry.SupervisorId ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@Remarks", (object?)entry.Remarks ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@CreatedBy", (object?)entry.CreatedBy ?? DBNull.Value);

                var newId = (int)await cmd.ExecuteScalarAsync();

                // 4) Consume the empty pots.
                var (potSuccess, potMessage) = await _emptyPotInventoryRepo.RecordTransactionAsync(
                    conn, tx, emptyPotInventoryId, -entry.Quantity, "Consumption", "PotProduction", newId, userId, entry.Remarks);
                if (!potSuccess)
                {
                    tx.Rollback();
                    return (false, potMessage, 0);
                }

                // 5) Produce the potted plant stock (get-or-create the
                // Species+PotSize+Area row, linked to the same Empty Pot
                // pool it was produced from, then increase it).
                var stockId = await _pottedPlantStockRepo.GetOrCreateLockedAsync(conn, tx, entry.SpeciesId, entry.PotSize, entry.AreaId, emptyPotInventoryId, entry.CreatedBy);
                var (stockSuccess, stockMessage) = await _pottedPlantStockRepo.RecordTransactionAsync(
                    conn, tx, stockId, entry.Quantity, "Production", "PotProduction", newId, userId, entry.Remarks);
                if (!stockSuccess)
                {
                    tx.Rollback();
                    return (false, stockMessage, 0);
                }

                tx.Commit();
                entry.Id = newId;
                entry.ProductionCode = productionCode;
                entry.Status = "Completed";
                return (true, null, newId);
            }
            catch (Exception ex)
            {
                tx.Rollback();
                return (false, ex.Message, 0);
            }
        }

        // Phase 19 ("Phase D") -- new path. Consumes a specific Growing
        // Partner dbo.CuttingStock row directly into Pot Production,
        // instead of a Propagation Batch. AreaId/SpeciesId are ALWAYS
        // derived server-side from the locked CuttingStock row (never
        // trusted from the caller), exactly mirroring how the legacy
        // path derives MotherPlantId/SpeciesId from the locked
        // Propagation Batch. Area authorization (does this user have
        // access to the CuttingStock row's Area?) is the CALLER's
        // responsibility (Pages/Production/PotProduction/CreateFromCutting.cshtml.cs),
        // since AreaAccessService needs the ClaimsPrincipal, which this
        // repository never sees -- this method re-derives every fact
        // from the database under lock regardless, as defense-in-depth
        // against a tampered/stale POST.
        public async Task<(bool Success, string? Message, int Id)> InsertFromCuttingStockAsync(PotProduction entry, int? userId)
        {
            if (!entry.SourceCuttingStockId.HasValue || entry.SourceCuttingStockId.Value <= 0)
                return (false, "Source Cutting Stock is required.", 0);
            if (!entry.CuttingQuantityConsumed.HasValue || entry.CuttingQuantityConsumed.Value <= 0)
                return (false, "Cuttings Consumed must be greater than zero.", 0);
            if (entry.Quantity <= 0)
                return (false, "Quantity must be greater than zero.", 0);
            if (entry.CuttingQuantityConsumed.Value < entry.Quantity)
                return (false, $"Cuttings Consumed ({entry.CuttingQuantityConsumed.Value:N2}) cannot be less than Potted Plants Produced ({entry.Quantity:N2}) -- loss cannot be negative.", 0);

            using var conn = _dbHelper.GetConnection();
            await conn.OpenAsync();
            using var tx = conn.BeginTransaction();

            try
            {
                // 1) Lock the source Cutting Stock row and validate it has
                // enough AVAILABLE quantity (Physical - InTransit, not raw
                // Physical -- the same cuttings must not be simultaneously
                // reserved for an outbound transfer AND consumed here).
                // Same HOLDLOCK discipline the legacy path uses on its own
                // parent row.
                var csLockCmd = new SqlCommand(
                    "SELECT AreaId, SpeciesId, PhysicalQuantity, InTransitQuantity FROM dbo.CuttingStock WITH (UPDLOCK, HOLDLOCK) WHERE Id = @Id",
                    conn, tx);
                csLockCmd.Parameters.AddWithValue("@Id", entry.SourceCuttingStockId.Value);
                using var csReader = await csLockCmd.ExecuteReaderAsync();
                if (!await csReader.ReadAsync())
                {
                    csReader.Close();
                    tx.Rollback();
                    return (false, "Source Cutting Stock record not found.", 0);
                }
                var sourceAreaId = csReader.GetInt32(csReader.GetOrdinal("AreaId"));
                var sourceSpeciesId = csReader.GetInt32(csReader.GetOrdinal("SpeciesId"));
                var csPhysical = csReader.GetDecimal(csReader.GetOrdinal("PhysicalQuantity"));
                var csInTransit = csReader.GetDecimal(csReader.GetOrdinal("InTransitQuantity"));
                csReader.Close();

                var csAvailable = csPhysical - csInTransit;
                if (entry.CuttingQuantityConsumed.Value > csAvailable)
                {
                    tx.Rollback();
                    return (false, $"Insufficient available Cutting Stock (available {csAvailable:N2}, requested {entry.CuttingQuantityConsumed.Value:N2}).", 0);
                }

                // Traceability fields are always derived from the locked
                // Cutting Stock row, never trusted from the caller. The
                // source Area remains the authoritative Area throughout.
                entry.AreaId = sourceAreaId;
                entry.SpeciesId = sourceSpeciesId;
                entry.PropagationBatchId = null;
                entry.MotherPlantId = null;

                // 2) Validate the requested Pot Size exists in the
                // Cutting Stock's own Area, is active, and (as an early,
                // non-authoritative check) has enough physical stock --
                // the authoritative check happens under lock inside
                // RecordTransactionAsync below. Identical rule to the
                // legacy path, just always scoped to the source Area.
                var potCmd = new SqlCommand(
                    "SELECT Id, PhysicalQuantity, IsActive FROM dbo.EmptyPotInventory WHERE PotSize = @PotSize AND AreaId = @AreaId",
                    conn, tx);
                potCmd.Parameters.AddWithValue("@PotSize", entry.PotSize);
                potCmd.Parameters.AddWithValue("@AreaId", entry.AreaId.Value);
                using var potReader = await potCmd.ExecuteReaderAsync();
                if (!await potReader.ReadAsync())
                {
                    potReader.Close();
                    tx.Rollback();
                    return (false, "Selected Pot Size does not exist in Empty Pot Inventory for this Cutting Stock's Area.", 0);
                }
                var emptyPotInventoryId = potReader.GetInt32(potReader.GetOrdinal("Id"));
                var potIsActive = potReader.GetBoolean(potReader.GetOrdinal("IsActive"));
                var potPhysical = potReader.GetDecimal(potReader.GetOrdinal("PhysicalQuantity"));
                potReader.Close();

                if (!potIsActive)
                {
                    tx.Rollback();
                    return (false, "Selected Pot Size is inactive.", 0);
                }
                if (potPhysical < entry.Quantity)
                {
                    tx.Rollback();
                    return (false, $"Not enough Empty Pot stock for '{entry.PotSize}' (available {potPhysical:N2}, requested {entry.Quantity:N2}).", 0);
                }

                var productionCode = await _batchNumberRepo.GetNextBatchNumberAsync(conn, tx, "POT", entry.ProductionDate.Year);

                // 3) Insert the Pot Production record itself FIRST, so
                // its Id is available as the ReferenceId on every ledger
                // entry below. If anything after this point fails, the
                // whole transaction (including this insert) rolls back.
                const string insertSql = @"
INSERT INTO dbo.PotProduction
(ProductionCode, PropagationBatchId, MotherPlantId, SpeciesId, SourceCuttingStockId, CuttingQuantityConsumed,
 PotSize, EmptyPotInventoryId, AreaId, ProductionDate, Quantity, Status,
 ResponsiblePersonId, SupervisorId, Remarks, CreatedDate, CreatedBy)
VALUES
(@ProductionCode, NULL, NULL, @SpeciesId, @SourceCuttingStockId, @CuttingQuantityConsumed,
 @PotSize, @EmptyPotInventoryId, @AreaId, @ProductionDate, @Quantity, 'Completed',
 @ResponsiblePersonId, @SupervisorId, @Remarks, SYSUTCDATETIME(), @CreatedBy);
SELECT CAST(SCOPE_IDENTITY() AS INT);";

                using var cmd = new SqlCommand(insertSql, conn, tx);
                cmd.Parameters.AddWithValue("@ProductionCode", productionCode);
                cmd.Parameters.AddWithValue("@SpeciesId", entry.SpeciesId);
                cmd.Parameters.AddWithValue("@SourceCuttingStockId", entry.SourceCuttingStockId.Value);
                cmd.Parameters.AddWithValue("@CuttingQuantityConsumed", entry.CuttingQuantityConsumed.Value);
                cmd.Parameters.AddWithValue("@PotSize", entry.PotSize);
                cmd.Parameters.AddWithValue("@EmptyPotInventoryId", emptyPotInventoryId);
                cmd.Parameters.AddWithValue("@AreaId", entry.AreaId.Value);
                cmd.Parameters.AddWithValue("@ProductionDate", entry.ProductionDate.Date);
                cmd.Parameters.AddWithValue("@Quantity", entry.Quantity);
                cmd.Parameters.AddWithValue("@ResponsiblePersonId", (object?)entry.ResponsiblePersonId ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@SupervisorId", (object?)entry.SupervisorId ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@Remarks", (object?)entry.Remarks ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@CreatedBy", (object?)entry.CreatedBy ?? DBNull.Value);

                var newId = (int)await cmd.ExecuteScalarAsync();

                // 4) Consume the Cutting Stock ('Potted' -- reserved for
                // exactly this purpose since Phase 15).
                var (cutSuccess, cutMessage) = await _cuttingStockRepo.RecordTransactionAsync(
                    conn, tx, entry.SourceCuttingStockId.Value, -entry.CuttingQuantityConsumed.Value, "Potted", "PotProduction", newId, userId, entry.Remarks);
                if (!cutSuccess)
                {
                    tx.Rollback();
                    return (false, cutMessage, 0);
                }

                // 5) Consume the empty pots -- identical mechanism to the
                // legacy path.
                var (potSuccess, potMessage) = await _emptyPotInventoryRepo.RecordTransactionAsync(
                    conn, tx, emptyPotInventoryId, -entry.Quantity, "Consumption", "PotProduction", newId, userId, entry.Remarks);
                if (!potSuccess)
                {
                    tx.Rollback();
                    return (false, potMessage, 0);
                }

                // 6) Produce the potted plant stock -- identical
                // mechanism to the legacy path.
                var stockId = await _pottedPlantStockRepo.GetOrCreateLockedAsync(conn, tx, entry.SpeciesId, entry.PotSize, entry.AreaId, emptyPotInventoryId, entry.CreatedBy);
                var (stockSuccess, stockMessage) = await _pottedPlantStockRepo.RecordTransactionAsync(
                    conn, tx, stockId, entry.Quantity, "Production", "PotProduction", newId, userId, entry.Remarks);
                if (!stockSuccess)
                {
                    tx.Rollback();
                    return (false, stockMessage, 0);
                }

                tx.Commit();
                entry.Id = newId;
                entry.ProductionCode = productionCode;
                entry.Status = "Completed";
                return (true, null, newId);
            }
            catch (Exception ex)
            {
                tx.Rollback();
                return (false, ex.Message, 0);
            }
        }

        // Updates non-stock-affecting fields only (ResponsiblePersonId,
        // SupervisorId, Remarks). The source fields / PotSize / Quantity
        // / CuttingQuantityConsumed are immutable after creation -- use
        // CancelAsync to reverse a production event entirely. Unchanged
        // by Phase 19 -- works identically for either source.
        public async Task<(bool Success, string? Message)> UpdateDetailsAsync(PotProduction entry)
        {
            using var conn = _dbHelper.GetConnection();
            await conn.OpenAsync();

            try
            {
                const string updateSql = @"
UPDATE dbo.PotProduction
SET ResponsiblePersonId = @ResponsiblePersonId,
    SupervisorId = @SupervisorId,
    Remarks = @Remarks,
    ModifiedDate = SYSUTCDATETIME(),
    ModifiedBy = @ModifiedBy
WHERE Id = @Id AND Status <> 'Cancelled'";

                using var cmd = new SqlCommand(updateSql, conn);
                cmd.Parameters.AddWithValue("@Id", entry.Id);
                cmd.Parameters.AddWithValue("@ResponsiblePersonId", (object?)entry.ResponsiblePersonId ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@SupervisorId", (object?)entry.SupervisorId ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@Remarks", (object?)entry.Remarks ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@ModifiedBy", (object?)entry.ModifiedBy ?? DBNull.Value);

                var rows = await cmd.ExecuteNonQueryAsync();
                return rows > 0 ? (true, null) : (false, "Pot Production record not found, or it is already Cancelled.");
            }
            catch (Exception ex)
            {
                return (false, ex.Message);
            }
        }

        // Reverses a completed Pot Production: returns the consumed
        // empty pots to dbo.EmptyPotInventory and removes the produced
        // quantity from dbo.PottedPlantStock, exactly as before Phase 19
        // -- unchanged for the legacy path. Phase 19 adds ONE additional
        // step, only when SourceCuttingStockId is set: credit the
        // consumed cuttings back to dbo.CuttingStock via a
        // 'ReversalReturn' entry. Never deletes the PotProduction row --
        // marks it Cancelled, same as before.
        public async Task<(bool Success, string? Message)> CancelAsync(int id, string? modifiedBy, int? userId)
        {
            using var conn = _dbHelper.GetConnection();
            await conn.OpenAsync();
            using var tx = conn.BeginTransaction();

            try
            {
                var lockCmd = new SqlCommand(
                    "SELECT SpeciesId, PotSize, EmptyPotInventoryId, AreaId, Quantity, Status, SourceCuttingStockId, CuttingQuantityConsumed FROM dbo.PotProduction WITH (UPDLOCK, HOLDLOCK) WHERE Id = @Id",
                    conn, tx);
                lockCmd.Parameters.AddWithValue("@Id", id);
                using var reader = await lockCmd.ExecuteReaderAsync();
                if (!await reader.ReadAsync())
                {
                    reader.Close();
                    tx.Rollback();
                    return (false, "Pot Production record not found.");
                }
                var speciesId = reader.GetInt32(reader.GetOrdinal("SpeciesId"));
                var potSize = reader.GetString(reader.GetOrdinal("PotSize"));
                // The production record itself already records exactly
                // which Empty Pot pool it consumed from and which Area it
                // produced into, so reversal targets those directly
                // rather than re-resolving them by PotSize/Species alone
                // (which, since Phase 8, may match more than one Area's
                // pool).
                var emptyPotInventoryIdN = reader.IsDBNull(reader.GetOrdinal("EmptyPotInventoryId")) ? (int?)null : reader.GetInt32(reader.GetOrdinal("EmptyPotInventoryId"));
                var areaId = reader.IsDBNull(reader.GetOrdinal("AreaId")) ? (int?)null : reader.GetInt32(reader.GetOrdinal("AreaId"));
                var quantity = reader.GetDecimal(reader.GetOrdinal("Quantity"));
                var status = reader.GetString(reader.GetOrdinal("Status"));
                var sourceCuttingStockId = reader.IsDBNull(reader.GetOrdinal("SourceCuttingStockId")) ? (int?)null : reader.GetInt32(reader.GetOrdinal("SourceCuttingStockId"));
                var cuttingQuantityConsumed = reader.IsDBNull(reader.GetOrdinal("CuttingQuantityConsumed")) ? (decimal?)null : reader.GetDecimal(reader.GetOrdinal("CuttingQuantityConsumed"));
                reader.Close();

                if (status == "Cancelled")
                {
                    tx.Rollback();
                    return (false, "This Pot Production record is already Cancelled.");
                }

                int emptyPotInventoryId;
                if (emptyPotInventoryIdN.HasValue)
                {
                    emptyPotInventoryId = emptyPotInventoryIdN.Value;
                }
                else
                {
                    // Legacy pre-Phase-8 record with no stored pool
                    // reference -- fall back to the old PotSize-only
                    // lookup (matches the unassigned-location pool).
                    var potCmd = new SqlCommand("SELECT Id FROM dbo.EmptyPotInventory WHERE PotSize = @PotSize AND AreaId IS NULL", conn, tx);
                    potCmd.Parameters.AddWithValue("@PotSize", potSize);
                    var emptyPotInventoryIdObj = await potCmd.ExecuteScalarAsync();
                    if (emptyPotInventoryIdObj == null || emptyPotInventoryIdObj == DBNull.Value)
                    {
                        tx.Rollback();
                        return (false, "Empty Pot Inventory record for this Pot Size no longer exists; cannot reverse automatically.");
                    }
                    emptyPotInventoryId = (int)emptyPotInventoryIdObj;
                }

                var stockCmd = new SqlCommand(
                    "SELECT Id FROM dbo.PottedPlantStock WHERE SpeciesId = @SpeciesId AND PotSize = @PotSize AND (AreaId = @AreaId OR (AreaId IS NULL AND @AreaId IS NULL))",
                    conn, tx);
                stockCmd.Parameters.AddWithValue("@SpeciesId", speciesId);
                stockCmd.Parameters.AddWithValue("@PotSize", potSize);
                stockCmd.Parameters.AddWithValue("@AreaId", (object?)areaId ?? DBNull.Value);
                var stockIdObj = await stockCmd.ExecuteScalarAsync();
                if (stockIdObj == null || stockIdObj == DBNull.Value)
                {
                    tx.Rollback();
                    return (false, "Potted Plant Stock record for this Species/Pot Size/Area no longer exists; cannot reverse automatically.");
                }
                var stockId = (int)stockIdObj;

                // Phase 19: credit the consumed cuttings back FIRST, only
                // for a Cutting-sourced row. Blocked (like every other
                // reversal step) if it would somehow take the ledger
                // negative -- can't happen in practice for a credit, but
                // RecordTransactionAsync's own check is the same
                // defense-in-depth used everywhere else.
                if (sourceCuttingStockId.HasValue && cuttingQuantityConsumed.HasValue)
                {
                    var (cutSuccess, cutMessage) = await _cuttingStockRepo.RecordTransactionAsync(
                        conn, tx, sourceCuttingStockId.Value, cuttingQuantityConsumed.Value, "ReversalReturn", "PotProduction", id, userId, "Reversal of cancelled Pot Production");
                    if (!cutSuccess)
                    {
                        tx.Rollback();
                        return (false, cutMessage);
                    }
                }

                // Return the pots.
                var (potSuccess, potMessage) = await _emptyPotInventoryRepo.RecordTransactionAsync(
                    conn, tx, emptyPotInventoryId, quantity, "ReversalReturn", "PotProduction", id, userId, "Reversal of cancelled Pot Production");
                if (!potSuccess)
                {
                    tx.Rollback();
                    return (false, potMessage);
                }

                // Remove the produced potted stock -- blocked if it has
                // already been reserved beyond what remains.
                var (stockSuccess, stockMessage) = await _pottedPlantStockRepo.RecordTransactionAsync(
                    conn, tx, stockId, -quantity, "ReversalRemoval", "PotProduction", id, userId, "Reversal of cancelled Pot Production");
                if (!stockSuccess)
                {
                    tx.Rollback();
                    return (false, $"Cannot cancel: {stockMessage}");
                }

                var updateCmd = new SqlCommand(
                    "UPDATE dbo.PotProduction SET Status = 'Cancelled', ModifiedDate = SYSUTCDATETIME(), ModifiedBy = @ModifiedBy WHERE Id = @Id",
                    conn, tx);
                updateCmd.Parameters.AddWithValue("@ModifiedBy", (object?)modifiedBy ?? DBNull.Value);
                updateCmd.Parameters.AddWithValue("@Id", id);
                await updateCmd.ExecuteNonQueryAsync();

                tx.Commit();
                return (true, null);
            }
            catch (Exception ex)
            {
                tx.Rollback();
                return (false, ex.Message);
            }
        }

        private static async Task<decimal> GetRecordedQuantityForBatchAsync(SqlConnection conn, SqlTransaction? tx, int propagationBatchId, int? excludeId)
        {
            const string sqlText = @"
SELECT ISNULL(SUM(Quantity), 0)
FROM dbo.PotProduction
WHERE PropagationBatchId = @PropagationBatchId
  AND Status <> 'Cancelled'
  AND (@ExcludeId IS NULL OR Id <> @ExcludeId)";

            var cmd = tx != null ? new SqlCommand(sqlText, conn, tx) : new SqlCommand(sqlText, conn);
            cmd.Parameters.AddWithValue("@PropagationBatchId", propagationBatchId);
            cmd.Parameters.AddWithValue("@ExcludeId", (object?)excludeId ?? DBNull.Value);
            var result = await cmd.ExecuteScalarAsync();
            return result == null || result == DBNull.Value ? 0 : (decimal)result;
        }

        private static PotProduction Map(SqlDataReader reader)
        {
            return new PotProduction
            {
                Id = reader.GetInt32(reader.GetOrdinal("Id")),
                ProductionCode = reader.GetString(reader.GetOrdinal("ProductionCode")),
                PropagationBatchId = reader.IsDBNull(reader.GetOrdinal("PropagationBatchId")) ? null : reader.GetInt32(reader.GetOrdinal("PropagationBatchId")),
                PropagationBatchCode = reader.IsDBNull(reader.GetOrdinal("PropagationBatchCode")) ? null : reader.GetString(reader.GetOrdinal("PropagationBatchCode")),
                MotherPlantId = reader.IsDBNull(reader.GetOrdinal("MotherPlantId")) ? null : reader.GetInt32(reader.GetOrdinal("MotherPlantId")),
                MotherPlantCode = reader.IsDBNull(reader.GetOrdinal("MotherPlantCode")) ? null : reader.GetString(reader.GetOrdinal("MotherPlantCode")),
                SpeciesId = reader.GetInt32(reader.GetOrdinal("SpeciesId")),
                SourceCuttingStockId = reader.IsDBNull(reader.GetOrdinal("SourceCuttingStockId")) ? null : reader.GetInt32(reader.GetOrdinal("SourceCuttingStockId")),
                CuttingQuantityConsumed = reader.IsDBNull(reader.GetOrdinal("CuttingQuantityConsumed")) ? null : reader.GetDecimal(reader.GetOrdinal("CuttingQuantityConsumed")),
                SpeciesName = reader.GetString(reader.GetOrdinal("SpeciesName")),
                PlantTypeName = reader.GetString(reader.GetOrdinal("PlantTypeName")),
                PotSize = reader.GetString(reader.GetOrdinal("PotSize")),
                EmptyPotInventoryId = reader.IsDBNull(reader.GetOrdinal("EmptyPotInventoryId")) ? 0 : reader.GetInt32(reader.GetOrdinal("EmptyPotInventoryId")),
                AreaId = reader.IsDBNull(reader.GetOrdinal("AreaId")) ? null : reader.GetInt32(reader.GetOrdinal("AreaId")),
                AreaName = reader.IsDBNull(reader.GetOrdinal("AreaName")) ? null : reader.GetString(reader.GetOrdinal("AreaName")),
                GrowingPartnerName = reader.IsDBNull(reader.GetOrdinal("GrowingPartnerName")) ? null : reader.GetString(reader.GetOrdinal("GrowingPartnerName")),
                ProductionDate = reader.GetDateTime(reader.GetOrdinal("ProductionDate")),
                Quantity = reader.GetDecimal(reader.GetOrdinal("Quantity")),
                Status = reader.GetString(reader.GetOrdinal("Status")),
                ResponsiblePersonId = reader.IsDBNull(reader.GetOrdinal("ResponsiblePersonId")) ? null : reader.GetInt32(reader.GetOrdinal("ResponsiblePersonId")),
                ResponsiblePersonName = reader.IsDBNull(reader.GetOrdinal("ResponsiblePersonName")) ? null : reader.GetString(reader.GetOrdinal("ResponsiblePersonName")),
                SupervisorId = reader.IsDBNull(reader.GetOrdinal("SupervisorId")) ? null : reader.GetInt32(reader.GetOrdinal("SupervisorId")),
                SupervisorName = reader.IsDBNull(reader.GetOrdinal("SupervisorName")) ? null : reader.GetString(reader.GetOrdinal("SupervisorName")),
                Remarks = reader.IsDBNull(reader.GetOrdinal("Remarks")) ? null : reader.GetString(reader.GetOrdinal("Remarks")),
                CreatedDate = reader.GetDateTime(reader.GetOrdinal("CreatedDate")),
                CreatedBy = reader.IsDBNull(reader.GetOrdinal("CreatedBy")) ? null : reader.GetString(reader.GetOrdinal("CreatedBy")),
                ModifiedDate = reader.IsDBNull(reader.GetOrdinal("ModifiedDate")) ? null : reader.GetDateTime(reader.GetOrdinal("ModifiedDate")),
                ModifiedBy = reader.IsDBNull(reader.GetOrdinal("ModifiedBy")) ? null : reader.GetString(reader.GetOrdinal("ModifiedBy"))
            };
        }
    }
}
