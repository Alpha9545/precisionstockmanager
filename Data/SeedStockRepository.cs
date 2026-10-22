using Microsoft.Data.SqlClient;
using PlantStockManager.Models;
using PlantStockManager.Services;

namespace PlantStockManager.Data
{
    // Phase 22/Phase H: dbo.SeedStock + dbo.SeedStockTransactions.
    // Mirrors PottedPlantStockRepository/EmptyPotInventoryRepository's
    // established shape exactly (RecordTransactionAsync/
    // ReserveInTransitAsync/ReleaseInTransitAsync/GetOrCreateLockedAsync)
    // -- new tables, but a faithful, deliberate mirror of the existing
    // pattern rather than a novel design.
    public class SeedStockRepository
    {
        private readonly DatabaseHelper _dbHelper;

        public SeedStockRepository(DatabaseHelper dbHelper)
        {
            _dbHelper = dbHelper;
        }

        private const string BaseSelect = @"
SELECT
    ss.Id, ss.SpeciesId, ps.Name AS SpeciesName, pt.Name AS PlantTypeName,
    ss.AreaId, a.Name AS AreaName, a.AreaType,
    ss.BatchNo, ss.SeedSourceId, src.Name AS SeedSourceName, ss.Unit,
    ss.PhysicalQuantity, ss.InTransitQuantity, ss.AvailableQuantity,
    ss.CreatedDate, ss.CreatedBy, ss.ModifiedDate, ss.ModifiedBy
FROM dbo.SeedStock ss
INNER JOIN dbo.PlantSpecies ps ON ss.SpeciesId = ps.Id
INNER JOIN dbo.PlantTypes pt ON ps.PlantTypeId = pt.Id
INNER JOIN dbo.Area a ON ss.AreaId = a.Id
LEFT JOIN dbo.SeedSources src ON ss.SeedSourceId = src.Id";

        public async Task<List<SeedStock>> GetAllAsync()
        {
            var list = new List<SeedStock>();
            using var conn = _dbHelper.GetConnection();
            await conn.OpenAsync();

            var sql = BaseSelect + " ORDER BY a.Name, ps.Name, ss.BatchNo";
            using var cmd = new SqlCommand(sql, conn);
            using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                list.Add(Map(reader));
            }
            return list;
        }

        // Phase D: searchable seed selection for 1,000+ varieties. Filters in
        // SQL (never loads the whole list into a dropdown): free text matches
        // the variety, crop/class, colour, supplier or lot number.
        public async Task<List<SeedStock>> SearchAsync(string? text, int? plantTypeId = null, int? seedSourceId = null,
            bool onlyAvailable = false, bool mainOfficeOnly = false, int max = 200)
        {
            var list = new List<SeedStock>();
            using var conn = _dbHelper.GetConnection();
            await conn.OpenAsync();

            var sql = BaseSelect.Replace("SELECT\n", $"SELECT TOP ({Math.Clamp(max, 1, 1000)})\n").Replace("SELECT\r\n", $"SELECT TOP ({Math.Clamp(max, 1, 1000)})\r\n") + @"
WHERE (@Text IS NULL OR ps.Name LIKE @Like OR pt.Name LIKE @Like OR ISNULL(ps.Color, N'') LIKE @Like
       OR ISNULL(src.Name, N'') LIKE @Like OR ss.BatchNo LIKE @Like)
  AND (@PlantTypeId IS NULL OR ps.PlantTypeId = @PlantTypeId)
  AND (@SeedSourceId IS NULL OR ss.SeedSourceId = @SeedSourceId)
  AND (@OnlyAvailable = 0 OR ss.AvailableQuantity > 0)
  AND (@MainOfficeOnly = 0 OR (a.AreaType = N'MainOffice' AND a.IsActive = 1))
ORDER BY ps.Name, ss.BatchNo";
            var term = string.IsNullOrWhiteSpace(text) ? null : text.Trim();
            using var cmd = new SqlCommand(sql, conn);
            cmd.Parameters.AddWithValue("@Text", (object?)term ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@Like", term == null ? DBNull.Value : "%" + term.Replace("[", "[[]").Replace("%", "[%]").Replace("_", "[_]") + "%");
            cmd.Parameters.AddWithValue("@PlantTypeId", (object?)plantTypeId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@SeedSourceId", (object?)seedSourceId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@OnlyAvailable", onlyAvailable);
            cmd.Parameters.AddWithValue("@MainOfficeOnly", mainOfficeOnly);
            using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
                list.Add(Map(reader));
            return list;
        }

        public async Task<SeedStock?> GetByIdAsync(int id)
        {
            using var conn = _dbHelper.GetConnection();
            await conn.OpenAsync();

            var sql = BaseSelect + " WHERE ss.Id = @Id";
            using var cmd = new SqlCommand(sql, conn);
            cmd.Parameters.AddWithValue("@Id", id);
            using var reader = await cmd.ExecuteReaderAsync();
            if (await reader.ReadAsync())
            {
                return Map(reader);
            }
            return null;
        }

        // Creates a new, empty (PhysicalQuantity = 0) pool for a
        // Species+Area+Lot combination that does not exist yet.
        // AddStockAsync is used afterwards to bring in physical
        // quantity. Mirrors EmptyPotInventoryRepository.InsertAsync.
        public async Task<(bool Success, string? Message, int Id)> InsertAsync(SeedStock entry)
        {
            if (entry.SpeciesId <= 0)
                return (false, "Species/Variety is required.", 0);
            if (entry.AreaId <= 0)
                return (false, "Area is required.", 0);

            using var conn = _dbHelper.GetConnection();
            await conn.OpenAsync();

            try
            {
                // Phase B: new seed is received ONLY into Main Office Seed
                // Stock (the single operational source for Direct Sowing).
                using (var areaCmd = new SqlCommand("SELECT AreaType, IsActive FROM dbo.Area WHERE Id = @AreaId", conn))
                {
                    areaCmd.Parameters.AddWithValue("@AreaId", entry.AreaId);
                    using var areaReader = await areaCmd.ExecuteReaderAsync();
                    if (!await areaReader.ReadAsync()
                        || !PlantStockManager.Services.DirectSowingRules.IsMainOfficeSeedLocation(
                               areaReader.IsDBNull(0) ? null : areaReader.GetString(0), areaReader.GetBoolean(1)))
                        return (false, "Seed Stock can only be created at an active Main Office Area.", 0);
                }

                const string insertSql = @"
INSERT INTO dbo.SeedStock (SpeciesId, AreaId, BatchNo, SeedSourceId, Unit, PhysicalQuantity, InTransitQuantity, CreatedDate, CreatedBy)
VALUES (@SpeciesId, @AreaId, @BatchNo, @SeedSourceId, @Unit, 0, 0, SYSUTCDATETIME(), @CreatedBy);
SELECT CAST(SCOPE_IDENTITY() AS INT);";

                using var cmd = new SqlCommand(insertSql, conn);
                cmd.Parameters.AddWithValue("@SpeciesId", entry.SpeciesId);
                cmd.Parameters.AddWithValue("@AreaId", entry.AreaId);
                cmd.Parameters.AddWithValue("@BatchNo", entry.BatchNo ?? string.Empty);
                cmd.Parameters.AddWithValue("@SeedSourceId", (object?)entry.SeedSourceId ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@Unit", string.IsNullOrWhiteSpace(entry.Unit) ? "pcs" : entry.Unit);
                cmd.Parameters.AddWithValue("@CreatedBy", (object?)entry.CreatedBy ?? DBNull.Value);

                var newId = (int)await cmd.ExecuteScalarAsync();
                return (true, null, newId);
            }
            catch (SqlException ex) when (ex.Number == 2627 || ex.Number == 2601)
            {
                return (false, "A Seed Stock pool for this Species/Area/Lot combination already exists.", 0);
            }
            catch (Exception ex)
            {
                return (false, DbErrorMessage.For(ex), 0);
            }
        }

        // Records a manual stock receipt ('StockIn') directly against an
        // existing pool -- e.g. Main Office receiving seed from a
        // supplier. Mirrors EmptyPotInventoryRepository.AddStockAsync.
        public async Task<(bool Success, string? Message)> AddStockAsync(int seedStockId, decimal quantity, int? userId, string? remarks)
        {
            if (quantity <= 0)
                return (false, "Quantity to add must be greater than zero.");
            if (!PlantStockManager.Services.DirectSowingRules.IsWholeNumber(quantity))
                return (false, "Quantity to add must be a whole number.");

            using var conn = _dbHelper.GetConnection();
            await conn.OpenAsync();
            using var tx = conn.BeginTransaction();

            try
            {
                var (success, message) = await RecordTransactionAsync(conn, tx, seedStockId, quantity, "StockIn", "SeedStock", seedStockId, userId, remarks);
                if (!success)
                {
                    tx.Rollback();
                    return (false, message);
                }

                tx.Commit();
                return (true, null);
            }
            catch (Exception ex)
            {
                tx.Rollback();
                return (false, DbErrorMessage.For(ex));
            }
        }

        // Finds-or-creates the destination pool at (SpeciesId, AreaId,
        // BatchNo) -- called under the caller's own transaction, while
        // the source row is already locked, mirroring
        // PottedPlantStockRepository.GetOrCreateLockedAsync exactly.
        // unit is only used the FIRST time a pool is created for this
        // (Species, Area, BatchNo); an existing pool's Unit is never
        // overwritten by a later receipt.
        public async Task<int> GetOrCreateLockedAsync(
            SqlConnection conn, SqlTransaction tx, int speciesId, int areaId, string batchNo, int? seedSourceId, string? createdBy, string unit = "pcs")
        {
            batchNo ??= string.Empty;

            var lockCmd = new SqlCommand(
                "SELECT Id FROM dbo.SeedStock WITH (UPDLOCK, HOLDLOCK) WHERE SpeciesId = @SpeciesId AND AreaId = @AreaId AND BatchNo = @BatchNo",
                conn, tx);
            lockCmd.Parameters.AddWithValue("@SpeciesId", speciesId);
            lockCmd.Parameters.AddWithValue("@AreaId", areaId);
            lockCmd.Parameters.AddWithValue("@BatchNo", batchNo);
            var existingId = await lockCmd.ExecuteScalarAsync();
            if (existingId != null && existingId != DBNull.Value)
            {
                return (int)existingId;
            }

            const string insertSql = @"
INSERT INTO dbo.SeedStock (SpeciesId, AreaId, BatchNo, SeedSourceId, Unit, PhysicalQuantity, InTransitQuantity, CreatedDate, CreatedBy)
VALUES (@SpeciesId, @AreaId, @BatchNo, @SeedSourceId, @Unit, 0, 0, SYSUTCDATETIME(), @CreatedBy);
SELECT CAST(SCOPE_IDENTITY() AS INT);";
            var insertCmd = new SqlCommand(insertSql, conn, tx);
            insertCmd.Parameters.AddWithValue("@SpeciesId", speciesId);
            insertCmd.Parameters.AddWithValue("@AreaId", areaId);
            insertCmd.Parameters.AddWithValue("@BatchNo", batchNo);
            insertCmd.Parameters.AddWithValue("@SeedSourceId", (object?)seedSourceId ?? DBNull.Value);
            insertCmd.Parameters.AddWithValue("@Unit", string.IsNullOrWhiteSpace(unit) ? "pcs" : unit);
            insertCmd.Parameters.AddWithValue("@CreatedBy", (object?)createdBy ?? DBNull.Value);
            return (int)await insertCmd.ExecuteScalarAsync();
        }

        // Combined "receive new seed stock" for a simple, single-page nursery
        // workflow: finds-or-creates the (Species, Area, BatchNo) pool and
        // credits it with a 'StockIn' ledger entry, in ONE atomic transaction.
        // Composes the two existing primitives above exactly the way
        // CuttingProductionRepository.InsertAsync already composes
        // CuttingStockRepository.GetOrCreateLockedAsync + RecordTransactionAsync --
        // never a second, independent stock system.
        public async Task<(bool Success, string? Message, int SeedStockId)> ReceiveAsync(
            int speciesId, int areaId, string? batchNo, int? seedSourceId, string unit,
            decimal quantity, DateTime receivedOn, string? notes, int? userId, string? createdBy)
        {
            if (speciesId <= 0)
                return (false, "Seed / Variety is required.", 0);
            if (areaId <= 0)
                return (false, "Area is required.", 0);
            if (quantity <= 0)
                return (false, "Quantity must be greater than zero.", 0);
            if (!PlantStockManager.Services.DirectSowingRules.IsWholeNumber(quantity))
                return (false, "Quantity must be a whole number.", 0);
            if (receivedOn.Date > DateTime.Today)
                return (false, "Received On cannot be in the future.", 0);

            using var conn = _dbHelper.GetConnection();
            await conn.OpenAsync();

            // Same "seed is received only at an active Main Office Area" rule
            // InsertAsync already enforces -- Direct Sowing's single operational source.
            using (var areaCmd = new SqlCommand("SELECT AreaType, IsActive FROM dbo.Area WHERE Id = @AreaId", conn))
            {
                areaCmd.Parameters.AddWithValue("@AreaId", areaId);
                using var areaReader = await areaCmd.ExecuteReaderAsync();
                if (!await areaReader.ReadAsync()
                    || !PlantStockManager.Services.DirectSowingRules.IsMainOfficeSeedLocation(
                           areaReader.IsDBNull(0) ? null : areaReader.GetString(0), areaReader.GetBoolean(1)))
                    return (false, "Seed Stock can only be received at an active Main Office Area.", 0);
            }

            using var tx = conn.BeginTransaction();
            try
            {
                var seedStockId = await GetOrCreateLockedAsync(conn, tx, speciesId, areaId, batchNo ?? string.Empty, seedSourceId, createdBy, unit);

                // receivedOn is the employee's local business time (the form's
                // datetime-local input); TransactionDate is stored in UTC like
                // every other ledger writer (DateTime.UtcNow / SYSUTCDATETIME()),
                // and pages convert it back with ToLocalTime() for display.
                var transactionDateUtc = receivedOn.Kind == DateTimeKind.Utc ? receivedOn : receivedOn.ToUniversalTime();

                var (success, message) = await RecordTransactionAsync(
                    conn, tx, seedStockId, quantity, "StockIn", "SeedStock", seedStockId, userId, notes, transactionDateUtc);
                if (!success)
                {
                    tx.Rollback();
                    return (false, message, 0);
                }

                tx.Commit();
                return (true, null, seedStockId);
            }
            catch (Exception ex)
            {
                try { tx.Rollback(); } catch { }
                return (false, DbErrorMessage.For(ex), 0);
            }
        }

        // 2026-10-12: seeds AVAILABLE to a deduction dated atUtc: the LOWEST balance of the lot from that
        // instant onward, in business-date order. Computed BACKWARD from the current balance
        // (balance at atUtc = PhysicalQuantity - every row dated after it), so a lot whose opening
        // stock has no ledger row is still judged correctly. Call under the lot lock.
        public static async Task<decimal> AvailableFromAsync(SqlConnection conn, SqlTransaction tx, int seedStockId, DateTime? atUtc)
        {
            var cmd = new SqlCommand(@"
DECLARE @B DECIMAL(18, 2) = (SELECT PhysicalQuantity FROM dbo.SeedStock WHERE Id = @Id)
    - (SELECT ISNULL(SUM(Quantity), 0) FROM dbo.SeedStockTransactions WHERE SeedStockId = @Id AND TransactionDate > @At);
SELECT CASE WHEN m.MinRunning IS NULL OR m.MinRunning > @B THEN @B ELSE m.MinRunning END
FROM (SELECT MIN(r.Running) AS MinRunning
      FROM (SELECT @B + SUM(Quantity) OVER (ORDER BY TransactionDate, Id ROWS UNBOUNDED PRECEDING) AS Running
            FROM dbo.SeedStockTransactions WHERE SeedStockId = @Id AND TransactionDate > @At) r) m;", conn, tx);
            cmd.Parameters.AddWithValue("@Id", seedStockId);
            cmd.Parameters.Add("@At", System.Data.SqlDbType.DateTime2).Value = (object?)atUtc ?? DateTime.UtcNow;
            return (decimal)(await cmd.ExecuteScalarAsync())!;
        }

        // Records a signed stock movement and writes the matching
        // ledger row in the SAME transaction -- PhysicalQuantity is
        // never changed any other way. Enforces PhysicalQuantity >= 0
        // under lock. Mirrors PottedPlantStockRepository.RecordTransactionAsync.
        // transactionDate: the business date the movement actually happened
        // (e.g. "Received On" for a manual receipt) -- defaults to now, same
        // as every existing caller already got before this parameter existed.
        // CreatedAt (the row's own audit timestamp) is always "now" regardless.
        public async Task<(bool Success, string? Message)> RecordTransactionAsync(
            SqlConnection conn, SqlTransaction tx, int seedStockId, decimal quantityDelta,
            string transactionType, string? referenceType, int? referenceId, int? userId, string? remarks,
            DateTime? transactionDate = null)
        {
            var lockCmd = new SqlCommand("SELECT PhysicalQuantity FROM dbo.SeedStock WITH (UPDLOCK, HOLDLOCK) WHERE Id = @Id", conn, tx);
            lockCmd.Parameters.AddWithValue("@Id", seedStockId);
            var beforeObj = await lockCmd.ExecuteScalarAsync();
            if (beforeObj == null || beforeObj == DBNull.Value)
                return (false, "Seed Stock record not found.");

            var before = (decimal)beforeObj;
            var after = before + quantityDelta;
            if (after < 0)
                return (false, $"This movement would take Physical Quantity negative (current {QuantityFormat.Qty(before)}, change {QuantityFormat.Qty(quantityDelta)}).");
            // 2026-10-12: a BACK-DATED deduction (only Bed Sowing passes one) must also fit the lot's
            // balance on its business date and at every later point -- the history never goes negative.
            if (quantityDelta < 0 && TrayStockRepository.IsBackDated(transactionDate))
            {
                var availableThen = await AvailableFromAsync(conn, tx, seedStockId, transactionDate);
                if (-quantityDelta > availableThen)
                    return (false, $"Only {QuantityFormat.Qty(Math.Max(availableThen, 0))} seeds were in this lot from {AppClock.ToIndiaTime(transactionDate!.Value):dd/MM/yyyy} onward "
                        + $"(counting every later movement); {QuantityFormat.Qty(-quantityDelta)} needed. Record the seed's arrival first, or use a later date.");
            }

            var updateCmd = new SqlCommand(
                "UPDATE dbo.SeedStock SET PhysicalQuantity = @After, ModifiedDate = SYSUTCDATETIME() WHERE Id = @Id",
                conn, tx);
            updateCmd.Parameters.AddWithValue("@After", after);
            updateCmd.Parameters.AddWithValue("@Id", seedStockId);
            await updateCmd.ExecuteNonQueryAsync();

            const string insertTxSql = @"
INSERT INTO dbo.SeedStockTransactions
(SeedStockId, TransactionDate, TransactionType, ReferenceType, ReferenceId, Quantity, BeforeQuantity, UserId, Remarks, CreatedAt)
VALUES
(@SeedStockId, @TransactionDate, @TransactionType, @ReferenceType, @ReferenceId, @Quantity, @BeforeQuantity, @UserId, @Remarks, SYSUTCDATETIME());";
            var insertTxCmd = new SqlCommand(insertTxSql, conn, tx);
            insertTxCmd.Parameters.AddWithValue("@SeedStockId", seedStockId);
            insertTxCmd.Parameters.AddWithValue("@TransactionDate", transactionDate ?? DateTime.UtcNow);
            insertTxCmd.Parameters.AddWithValue("@TransactionType", transactionType);
            insertTxCmd.Parameters.AddWithValue("@ReferenceType", (object?)referenceType ?? DBNull.Value);
            insertTxCmd.Parameters.AddWithValue("@ReferenceId", (object?)referenceId ?? DBNull.Value);
            insertTxCmd.Parameters.AddWithValue("@Quantity", quantityDelta);
            insertTxCmd.Parameters.AddWithValue("@BeforeQuantity", before);
            insertTxCmd.Parameters.AddWithValue("@UserId", (object?)userId ?? DBNull.Value);
            insertTxCmd.Parameters.AddWithValue("@Remarks", (object?)remarks ?? DBNull.Value);
            await insertTxCmd.ExecuteNonQueryAsync();

            return (true, null);
        }

        // Raises InTransitQuantity only -- PhysicalQuantity and the
        // ledger are untouched (nothing has actually moved yet).
        // Refuses if not enough is available so the same physical seed
        // stock can't be issued twice while an earlier issue is still
        // awaiting confirmation. Returns the pool's AreaId/SpeciesId/
        // BatchNo/SeedSourceId so the caller doesn't need a second
        // lookup. Mirrors PottedPlantStockRepository.ReserveInTransitAsync
        // exactly (this table has no ReservedQuantity concept at all, so
        // "available to issue" is simply Physical - InTransit here).
        public async Task<(bool Success, string? Message, int AreaId, int SpeciesId, string BatchNo, int? SeedSourceId)> ReserveInTransitAsync(
            SqlConnection conn, SqlTransaction tx, int seedStockId, decimal quantity)
        {
            if (quantity <= 0)
                return (false, "Quantity must be greater than zero.", 0, 0, string.Empty, null);

            var lockCmd = new SqlCommand(
                "SELECT AreaId, SpeciesId, BatchNo, SeedSourceId, PhysicalQuantity, InTransitQuantity FROM dbo.SeedStock WITH (UPDLOCK, HOLDLOCK) WHERE Id = @Id",
                conn, tx);
            lockCmd.Parameters.AddWithValue("@Id", seedStockId);
            using var reader = await lockCmd.ExecuteReaderAsync();
            if (!await reader.ReadAsync())
            {
                reader.Close();
                return (false, "Seed Stock record not found.", 0, 0, string.Empty, null);
            }
            var areaId = reader.GetInt32(reader.GetOrdinal("AreaId"));
            var speciesId = reader.GetInt32(reader.GetOrdinal("SpeciesId"));
            var batchNo = reader.GetString(reader.GetOrdinal("BatchNo"));
            var seedSourceId = reader.IsDBNull(reader.GetOrdinal("SeedSourceId")) ? (int?)null : reader.GetInt32(reader.GetOrdinal("SeedSourceId"));
            var physical = reader.GetDecimal(reader.GetOrdinal("PhysicalQuantity"));
            var inTransit = reader.GetDecimal(reader.GetOrdinal("InTransitQuantity"));
            reader.Close();

            var availableToIssue = physical - inTransit;
            if (quantity > availableToIssue)
                return (false, $"Insufficient available Seed Stock to issue (available {QuantityFormat.Qty(availableToIssue)}, requested {QuantityFormat.Qty(quantity)}).", 0, 0, string.Empty, null);

            var updateCmd = new SqlCommand(
                "UPDATE dbo.SeedStock SET InTransitQuantity = InTransitQuantity + @Quantity, ModifiedDate = SYSUTCDATETIME() WHERE Id = @Id",
                conn, tx);
            updateCmd.Parameters.AddWithValue("@Quantity", quantity);
            updateCmd.Parameters.AddWithValue("@Id", seedStockId);
            await updateCmd.ExecuteNonQueryAsync();

            return (true, null, areaId, speciesId, batchNo, seedSourceId);
        }

        // Releases a previously-reserved quantity back to "available to
        // issue" without touching PhysicalQuantity or the ledger --
        // used when a pending Seed Issue is rejected outright (releases
        // the full issued quantity, since nothing ever left) or when a
        // discrepancy is confirmed (releases just the shortfall).
        // Mirrors PottedPlantStockRepository.ReleaseInTransitAsync exactly.
        public async Task<(bool Success, string? Message)> ReleaseInTransitAsync(
            SqlConnection conn, SqlTransaction tx, int seedStockId, decimal quantity)
        {
            if (quantity < 0)
                return (false, "Release quantity cannot be negative.");
            if (quantity == 0)
                return (true, null);

            var lockCmd = new SqlCommand(
                "SELECT InTransitQuantity FROM dbo.SeedStock WITH (UPDLOCK, HOLDLOCK) WHERE Id = @Id",
                conn, tx);
            lockCmd.Parameters.AddWithValue("@Id", seedStockId);
            var inTransitObj = await lockCmd.ExecuteScalarAsync();
            if (inTransitObj == null || inTransitObj == DBNull.Value)
                return (false, "Seed Stock record not found.");

            var inTransit = (decimal)inTransitObj;
            if (quantity > inTransit)
                return (false, $"Cannot release {QuantityFormat.Qty(quantity)} -- only {QuantityFormat.Qty(inTransit)} is currently in transit for this pool.");

            var updateCmd = new SqlCommand(
                "UPDATE dbo.SeedStock SET InTransitQuantity = InTransitQuantity - @Quantity, ModifiedDate = SYSUTCDATETIME() WHERE Id = @Id",
                conn, tx);
            updateCmd.Parameters.AddWithValue("@Quantity", quantity);
            updateCmd.Parameters.AddWithValue("@Id", seedStockId);
            await updateCmd.ExecuteNonQueryAsync();

            return (true, null);
        }

        public async Task<List<SeedStockTransaction>> GetTransactionsAsync(int seedStockId)
        {
            var list = new List<SeedStockTransaction>();
            using var conn = _dbHelper.GetConnection();
            await conn.OpenAsync();

            const string sql = @"
SELECT t.Id, t.SeedStockId, ps.Name AS SpeciesName, a.Name AS AreaName, t.TransactionDate, t.TransactionType,
       t.ReferenceType, t.ReferenceId, t.Quantity, t.BeforeQuantity, t.AfterQuantity, t.UserId, u.Name AS UserName,
       t.Remarks, t.CreatedAt
FROM dbo.SeedStockTransactions t
INNER JOIN dbo.SeedStock s ON t.SeedStockId = s.Id
INNER JOIN dbo.PlantSpecies ps ON s.SpeciesId = ps.Id
INNER JOIN dbo.Area a ON s.AreaId = a.Id
LEFT JOIN dbo.IMSUsers u ON t.UserId = u.Id
WHERE t.SeedStockId = @Id
ORDER BY t.CreatedAt DESC";
            using var cmd = new SqlCommand(sql, conn);
            cmd.Parameters.AddWithValue("@Id", seedStockId);
            using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                list.Add(MapTransaction(reader));
            }
            return list;
        }

        // 2026-10-20 (administrator receipt correction, Services/SeedStockEditRules.cs): a lot's receipts that can still be
        // edited -- its positive 'StockIn' / 'SeedStock' rows that no reversal points at -- newest business date first.
        public async Task<List<SeedStockTransaction>> GetEditableReceiptsAsync(int seedStockId)
        {
            var list = new List<SeedStockTransaction>();
            using var conn = _dbHelper.GetConnection();
            await conn.OpenAsync();

            const string sql = @"
SELECT t.Id, t.SeedStockId, ps.Name AS SpeciesName, a.Name AS AreaName, t.TransactionDate, t.TransactionType,
       t.ReferenceType, t.ReferenceId, t.Quantity, t.BeforeQuantity, t.AfterQuantity, t.UserId, u.Name AS UserName,
       t.Remarks, t.CreatedAt
FROM dbo.SeedStockTransactions t
INNER JOIN dbo.SeedStock s ON t.SeedStockId = s.Id
INNER JOIN dbo.PlantSpecies ps ON s.SpeciesId = ps.Id
INNER JOIN dbo.Area a ON s.AreaId = a.Id
LEFT JOIN dbo.IMSUsers u ON t.UserId = u.Id
WHERE t.SeedStockId = @Id" + EditableReceiptFilter + @"
ORDER BY t.TransactionDate DESC, t.Id DESC";
            using var cmd = new SqlCommand(sql, conn);
            cmd.Parameters.AddWithValue("@Id", seedStockId);
            cmd.Parameters.AddWithValue("@Receipt", SeedStockEditRules.ReceiptReference);
            cmd.Parameters.AddWithValue("@Reversal", SeedStockEditRules.ReversalReference);
            using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
                list.Add(MapTransaction(reader));
            return list;
        }

        private const string EditableReceiptFilter = @"
  AND t.TransactionType = N'StockIn' AND t.ReferenceType = @Receipt AND t.Quantity > 0
  AND NOT EXISTS (SELECT 1 FROM dbo.SeedStockTransactions r
                  WHERE r.SeedStockId = t.SeedStockId AND r.ReferenceType = @Reversal AND r.ReferenceId = t.Id)";

        public sealed record ReceiptEdit(int SeedStockId, int ReceiptId, decimal Quantity, DateTime ReceivedOnLocal, string? Notes, string Reason);

        // 2026-10-20: corrects one receipt of a lot (Received On, Quantity, Notes) WITHOUT rewriting the ledger -- appends the
        // corrected receipt and the reversal of the original in ONE transaction under the lot lock (see SeedStockEditRules).
        // Refused, with nothing written, when the receipt is already superseded, nothing changed, or the lot could not have
        // supplied what was already used / is reserved for a pending Seed Issue (the history never goes negative).
        // Authorization is the page's (FeatureAuthorizationConventions: System Administrator only).
        public async Task<(bool Success, string? Message, bool Changed, int NewReceiptId)> EditReceiptAsync(
            ReceiptEdit edit, int? userId, string? modifiedBy)
        {
            var errors = SeedStockEditRules.Validate(edit.Quantity, edit.ReceivedOnLocal, edit.Notes, edit.Reason, DateTime.Today);
            if (errors.Count > 0)
                return (false, errors[0].Message, false, 0);

            var notes = string.IsNullOrWhiteSpace(edit.Notes) ? null : edit.Notes.Trim();
            var newDateUtc = edit.ReceivedOnLocal.Kind == DateTimeKind.Utc ? edit.ReceivedOnLocal : edit.ReceivedOnLocal.ToUniversalTime();

            using var conn = _dbHelper.GetConnection();
            await conn.OpenAsync();
            using var tx = conn.BeginTransaction();
            try
            {
                // the lot, locked for the whole correction (every other writer takes the same lock)
                var lockCmd = new SqlCommand(
                    "SELECT PhysicalQuantity, InTransitQuantity FROM dbo.SeedStock WITH (UPDLOCK, HOLDLOCK) WHERE Id = @Id", conn, tx);
                lockCmd.Parameters.AddWithValue("@Id", edit.SeedStockId);
                decimal physical, inTransit;
                using (var r = await lockCmd.ExecuteReaderAsync())
                {
                    if (!await r.ReadAsync())
                    {
                        r.Close();
                        tx.Rollback();
                        return (false, "Seed Stock record not found.", false, 0);
                    }
                    (physical, inTransit) = (r.GetDecimal(0), r.GetDecimal(1));
                }

                // the receipt: still an editable receipt OF THIS LOT (re-read under the lock -- never from the form)
                var receiptCmd = new SqlCommand(@"
SELECT t.Quantity, t.TransactionDate, t.Remarks FROM dbo.SeedStockTransactions t
WHERE t.Id = @Receipt_Id AND t.SeedStockId = @Id" + EditableReceiptFilter, conn, tx);
                receiptCmd.Parameters.AddWithValue("@Receipt_Id", edit.ReceiptId);
                receiptCmd.Parameters.AddWithValue("@Id", edit.SeedStockId);
                receiptCmd.Parameters.AddWithValue("@Receipt", SeedStockEditRules.ReceiptReference);
                receiptCmd.Parameters.AddWithValue("@Reversal", SeedStockEditRules.ReversalReference);
                decimal oldQuantity;
                DateTime oldDateUtc;
                string? oldNotes;
                using (var r = await receiptCmd.ExecuteReaderAsync())
                {
                    if (!await r.ReadAsync())
                    {
                        r.Close();
                        tx.Rollback();
                        return (false, SeedStockEditRules.StaleMessage, false, 0);
                    }
                    (oldQuantity, oldDateUtc, oldNotes) = (r.GetDecimal(0), DateTime.SpecifyKind(r.GetDateTime(1), DateTimeKind.Utc), r.IsDBNull(2) ? null : r.GetString(2));
                }
                var oldDateLocal = oldDateUtc.ToLocalTime();

                if (SeedStockEditRules.IsUnchanged(oldQuantity, oldDateLocal, oldNotes, edit.Quantity, edit.ReceivedOnLocal, notes))
                {
                    tx.Rollback();
                    return (true, SeedStockEditRules.NoChangeMessage, false, edit.ReceiptId);
                }

                // seeds reserved for a pending Seed Issue must stay covered (a negative result is the "already used" case below)
                var physicalAfter = physical - oldQuantity + edit.Quantity;
                if (inTransit > 0 && physicalAfter >= 0 && physicalAfter < inTransit)
                {
                    tx.Rollback();
                    return (false, $"{QuantityFormat.Qty(inTransit)} seeds of this lot are reserved for a pending Seed Issue; the corrected receipt would leave only "
                        + $"{QuantityFormat.Qty(physicalAfter)} in the lot.", false, 0);
                }

                // 1. the corrected receipt (an ordinary receipt on the new date)
                var (inOk, inMessage) = await RecordTransactionAsync(conn, tx, edit.SeedStockId, edit.Quantity, "StockIn",
                    SeedStockEditRules.ReceiptReference, edit.SeedStockId, userId, notes, newDateUtc);
                if (!inOk)
                {
                    tx.Rollback();
                    return (false, inMessage, false, 0);
                }
                // the row just written (the lot is locked, so no other writer can add one to it meanwhile)
                var newIdCmd = new SqlCommand("SELECT MAX(Id) FROM dbo.SeedStockTransactions WHERE SeedStockId = @Id", conn, tx);
                newIdCmd.Parameters.AddWithValue("@Id", edit.SeedStockId);
                var newReceiptId = (int)(await newIdCmd.ExecuteScalarAsync())!;

                // 2. the original must be removable on ITS date, counting every later movement (incl. the corrected receipt):
                //    otherwise seeds already used after that date would have come from nowhere.
                var availableThen = await AvailableFromAsync(conn, tx, edit.SeedStockId, oldDateUtc);
                if (oldQuantity > availableThen)
                {
                    tx.Rollback();
                    return (false, $"Seeds from this receipt were already used: from {oldDateLocal:dd/MM/yyyy} onward the lot would be short by "
                        + $"{QuantityFormat.Qty(oldQuantity - Math.Max(availableThen, 0))} with the corrected receipt "
                        + $"({QuantityFormat.Qty(edit.Quantity)} on {edit.ReceivedOnLocal:dd/MM/yyyy}). Use a larger quantity or an earlier date.", false, 0);
                }

                var audit = SeedStockEditRules.AuditRemarks(edit.ReceiptId, oldQuantity, oldDateLocal, edit.Quantity, edit.ReceivedOnLocal, edit.Reason);
                var (outOk, outMessage) = await RecordTransactionAsync(conn, tx, edit.SeedStockId, -oldQuantity, "StockIn",
                    SeedStockEditRules.ReversalReference, edit.ReceiptId, userId, audit, oldDateUtc);
                if (!outOk)
                {
                    tx.Rollback();
                    return (false, outMessage, false, 0);
                }
                // RecordTransactionAsync sends the date as SQL datetime (~3 ms), but an original receipt may carry full
                // datetime2 precision (data-entry scripts): give the reversal EXACTLY the original's instant, so it can
                // never sort before the receipt it reverses (a false dip in AvailableFromAsync's running balance).
                var alignCmd = new SqlCommand(@"
UPDATE r SET r.TransactionDate = o.TransactionDate
FROM dbo.SeedStockTransactions r JOIN dbo.SeedStockTransactions o ON o.Id = @Receipt_Id
WHERE r.Id = (SELECT MAX(Id) FROM dbo.SeedStockTransactions WHERE SeedStockId = @Id AND ReferenceType = @Reversal AND ReferenceId = @Receipt_Id)", conn, tx);
                alignCmd.Parameters.AddWithValue("@Receipt_Id", edit.ReceiptId);
                alignCmd.Parameters.AddWithValue("@Id", edit.SeedStockId);
                alignCmd.Parameters.AddWithValue("@Reversal", SeedStockEditRules.ReversalReference);
                await alignCmd.ExecuteNonQueryAsync();

                var touch = new SqlCommand("UPDATE dbo.SeedStock SET ModifiedDate = SYSUTCDATETIME(), ModifiedBy = @By WHERE Id = @Id", conn, tx);
                touch.Parameters.AddWithValue("@By", (object?)modifiedBy ?? DBNull.Value);
                touch.Parameters.AddWithValue("@Id", edit.SeedStockId);
                await touch.ExecuteNonQueryAsync();

                tx.Commit();
                return (true, null, true, newReceiptId);
            }
            catch (Exception ex)
            {
                try { tx.Rollback(); } catch { }
                return (false, DbErrorMessage.For(ex), false, 0);
            }
        }

        private static SeedStockTransaction MapTransaction(SqlDataReader reader)
        {
            return new SeedStockTransaction
            {
                Id = reader.GetInt32(reader.GetOrdinal("Id")),
                SeedStockId = reader.GetInt32(reader.GetOrdinal("SeedStockId")),
                SpeciesName = reader.GetString(reader.GetOrdinal("SpeciesName")),
                AreaName = reader.GetString(reader.GetOrdinal("AreaName")),
                TransactionDate = reader.GetDateTime(reader.GetOrdinal("TransactionDate")),
                TransactionType = reader.GetString(reader.GetOrdinal("TransactionType")),
                ReferenceType = reader.IsDBNull(reader.GetOrdinal("ReferenceType")) ? null : reader.GetString(reader.GetOrdinal("ReferenceType")),
                ReferenceId = reader.IsDBNull(reader.GetOrdinal("ReferenceId")) ? null : reader.GetInt32(reader.GetOrdinal("ReferenceId")),
                Quantity = reader.GetDecimal(reader.GetOrdinal("Quantity")),
                BeforeQuantity = reader.GetDecimal(reader.GetOrdinal("BeforeQuantity")),
                AfterQuantity = reader.GetDecimal(reader.GetOrdinal("AfterQuantity")),
                UserId = reader.IsDBNull(reader.GetOrdinal("UserId")) ? null : reader.GetInt32(reader.GetOrdinal("UserId")),
                UserName = reader.IsDBNull(reader.GetOrdinal("UserName")) ? null : reader.GetString(reader.GetOrdinal("UserName")),
                Remarks = reader.IsDBNull(reader.GetOrdinal("Remarks")) ? null : reader.GetString(reader.GetOrdinal("Remarks")),
                CreatedAt = reader.GetDateTime(reader.GetOrdinal("CreatedAt"))
            };
        }

        private static SeedStock Map(SqlDataReader reader)
        {
            return new SeedStock
            {
                Id = reader.GetInt32(reader.GetOrdinal("Id")),
                SpeciesId = reader.GetInt32(reader.GetOrdinal("SpeciesId")),
                SpeciesName = reader.GetString(reader.GetOrdinal("SpeciesName")),
                PlantTypeName = reader.GetString(reader.GetOrdinal("PlantTypeName")),
                AreaId = reader.GetInt32(reader.GetOrdinal("AreaId")),
                AreaName = reader.GetString(reader.GetOrdinal("AreaName")),
                AreaType = reader.IsDBNull(reader.GetOrdinal("AreaType")) ? null : reader.GetString(reader.GetOrdinal("AreaType")),
                BatchNo = reader.GetString(reader.GetOrdinal("BatchNo")),
                SeedSourceId = reader.IsDBNull(reader.GetOrdinal("SeedSourceId")) ? null : reader.GetInt32(reader.GetOrdinal("SeedSourceId")),
                SeedSourceName = reader.IsDBNull(reader.GetOrdinal("SeedSourceName")) ? null : reader.GetString(reader.GetOrdinal("SeedSourceName")),
                Unit = reader.GetString(reader.GetOrdinal("Unit")),
                PhysicalQuantity = reader.GetDecimal(reader.GetOrdinal("PhysicalQuantity")),
                InTransitQuantity = reader.GetDecimal(reader.GetOrdinal("InTransitQuantity")),
                AvailableQuantity = reader.GetDecimal(reader.GetOrdinal("AvailableQuantity")),
                CreatedDate = reader.GetDateTime(reader.GetOrdinal("CreatedDate")),
                CreatedBy = reader.IsDBNull(reader.GetOrdinal("CreatedBy")) ? null : reader.GetString(reader.GetOrdinal("CreatedBy")),
                ModifiedDate = reader.IsDBNull(reader.GetOrdinal("ModifiedDate")) ? null : reader.GetDateTime(reader.GetOrdinal("ModifiedDate")),
                ModifiedBy = reader.IsDBNull(reader.GetOrdinal("ModifiedBy")) ? null : reader.GetString(reader.GetOrdinal("ModifiedBy"))
            };
        }
    }
}
