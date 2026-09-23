using Microsoft.Data.SqlClient;
using PlantStockManager.Models;

namespace PlantStockManager.Data
{
    // Stock repository for dbo.PottedPlantStock, with its own dedicated
    // ledger (dbo.PottedPlantStockTransactions) per the approved
    // "separate ledger per stock entity" decision. PhysicalQuantity on
    // the stock row is NEVER updated directly outside of a matching
    // ledger insert in the SAME transaction.
    public class PottedPlantStockRepository
    {
        private readonly DatabaseHelper _dbHelper;

        public PottedPlantStockRepository(DatabaseHelper dbHelper)
        {
            _dbHelper = dbHelper;
        }

        // Phase E: adds GrowingPartnerName (Area.GrowingPartnerId -> Growing
        // Partners.Name) and two read-only traceability/dashboard fields
        // (LastProductionCode/LastProductionSource, LastTransactionDate),
        // all via LEFT JOIN/OUTER APPLY over the existing ledger -- zero new
        // columns on dbo.PottedPlantStock itself (spec items 9, 13).
        private const string BaseSelect = @"
SELECT s.Id, s.SpeciesId, ps.Name AS SpeciesName, pt.Name AS PlantTypeName, s.PotSize,
       s.EmptyPotInventoryId, s.AreaId, a.Name AS AreaName, gp.Name AS GrowingPartnerName,
       s.PhysicalQuantity, s.ReservedQuantity, s.SoldDispatchedQuantity, s.WastedQuantity, s.InTransitQuantity,
       s.CreatedDate, s.CreatedBy, s.ModifiedDate, s.ModifiedBy,
       lastProd.Id AS LastProductionId,
       lastProd.ProductionCode AS LastProductionCode,
       CASE WHEN lastProd.Id IS NULL THEN NULL
            WHEN lastProd.SourceCuttingStockId IS NOT NULL THEN 'Cutting Stock'
            ELSE 'Propagation Batch' END AS LastProductionSource,
       lastTx.TransactionDate AS LastTransactionDate
FROM dbo.PottedPlantStock s
INNER JOIN dbo.PlantSpecies ps ON s.SpeciesId = ps.Id
INNER JOIN dbo.PlantTypes pt ON ps.PlantTypeId = pt.Id
LEFT JOIN dbo.Area a ON s.AreaId = a.Id
LEFT JOIN dbo.GrowingPartners gp ON a.GrowingPartnerId = gp.Id
OUTER APPLY (
    SELECT TOP 1 t.ReferenceId
    FROM dbo.PottedPlantStockTransactions t
    WHERE t.PottedPlantStockId = s.Id AND t.TransactionType = 'Production' AND t.ReferenceType = 'PotProduction'
    ORDER BY t.CreatedAt DESC
) AS lastProdTx
LEFT JOIN dbo.PotProduction lastProd ON lastProd.Id = lastProdTx.ReferenceId
OUTER APPLY (
    SELECT TOP 1 t.TransactionDate
    FROM dbo.PottedPlantStockTransactions t
    WHERE t.PottedPlantStockId = s.Id
    ORDER BY t.CreatedAt DESC
) AS lastTx";

        public async Task<List<PottedPlantStock>> GetAllAsync()
        {
            var list = new List<PottedPlantStock>();
            using var conn = _dbHelper.GetConnection();
            await conn.OpenAsync();

            var sql = BaseSelect + " ORDER BY ps.Name, s.PotSize";
            using var cmd = new SqlCommand(sql, conn);
            using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                list.Add(Map(reader));
            }
            return list;
        }

        public async Task<PottedPlantStock?> GetByIdAsync(int id)
        {
            using var conn = _dbHelper.GetConnection();
            await conn.OpenAsync();

            var sql = BaseSelect + " WHERE s.Id = @Id";
            using var cmd = new SqlCommand(sql, conn);
            cmd.Parameters.AddWithValue("@Id", id);
            using var reader = await cmd.ExecuteReaderAsync();
            if (await reader.ReadAsync())
            {
                return Map(reader);
            }
            return null;
        }

        // Finds the stock row for a Species+PotSize+Area combination
        // WITHOUT creating one -- used by read-only pages. areaId = null
        // matches the legacy/"unassigned location" pool.
        public async Task<PottedPlantStock?> GetBySpeciesAndPotSizeAsync(int speciesId, string potSize, int? areaId)
        {
            using var conn = _dbHelper.GetConnection();
            await conn.OpenAsync();
            return await GetBySpeciesAndPotSizeAsync(conn, null, speciesId, potSize, areaId);
        }

        private async Task<PottedPlantStock?> GetBySpeciesAndPotSizeAsync(SqlConnection conn, SqlTransaction? tx, int speciesId, string potSize, int? areaId)
        {
            var sql = BaseSelect + " WHERE s.SpeciesId = @SpeciesId AND s.PotSize = @PotSize AND (s.AreaId = @AreaId OR (s.AreaId IS NULL AND @AreaId IS NULL))";
            var cmd = tx != null ? new SqlCommand(sql, conn, tx) : new SqlCommand(sql, conn);
            cmd.Parameters.AddWithValue("@SpeciesId", speciesId);
            cmd.Parameters.AddWithValue("@PotSize", potSize);
            cmd.Parameters.AddWithValue("@AreaId", (object?)areaId ?? DBNull.Value);
            using var reader = await cmd.ExecuteReaderAsync();
            if (await reader.ReadAsync())
            {
                return Map(reader);
            }
            return null;
        }

        // Gets the existing stock row for this Species+PotSize+Area,
        // locked for the duration of the caller's transaction, or
        // creates a new zeroed row (linked to the given EmptyPotInventory
        // pool) and locks that instead. Must be called from inside an
        // existing transaction (e.g. PotProductionRepository.InsertAsync,
        // InternalTransferRepository.InsertAsync).
        public async Task<int> GetOrCreateLockedAsync(SqlConnection conn, SqlTransaction tx, int speciesId, string potSize, int? areaId, int emptyPotInventoryId, string? createdBy)
        {
            var lockCmd = new SqlCommand(
                "SELECT Id FROM dbo.PottedPlantStock WITH (UPDLOCK, HOLDLOCK) WHERE SpeciesId = @SpeciesId AND PotSize = @PotSize AND (AreaId = @AreaId OR (AreaId IS NULL AND @AreaId IS NULL))",
                conn, tx);
            lockCmd.Parameters.AddWithValue("@SpeciesId", speciesId);
            lockCmd.Parameters.AddWithValue("@PotSize", potSize);
            lockCmd.Parameters.AddWithValue("@AreaId", (object?)areaId ?? DBNull.Value);
            var existingId = await lockCmd.ExecuteScalarAsync();
            if (existingId != null && existingId != DBNull.Value)
            {
                return (int)existingId;
            }

            const string insertSql = @"
INSERT INTO dbo.PottedPlantStock (SpeciesId, PotSize, AreaId, EmptyPotInventoryId, PhysicalQuantity, ReservedQuantity, SoldDispatchedQuantity, WastedQuantity, InTransitQuantity, CreatedDate, CreatedBy)
VALUES (@SpeciesId, @PotSize, @AreaId, @EmptyPotInventoryId, 0, 0, 0, 0, 0, SYSUTCDATETIME(), @CreatedBy);
SELECT CAST(SCOPE_IDENTITY() AS INT);";

            var insertCmd = new SqlCommand(insertSql, conn, tx);
            insertCmd.Parameters.AddWithValue("@SpeciesId", speciesId);
            insertCmd.Parameters.AddWithValue("@PotSize", potSize);
            insertCmd.Parameters.AddWithValue("@AreaId", (object?)areaId ?? DBNull.Value);
            insertCmd.Parameters.AddWithValue("@EmptyPotInventoryId", emptyPotInventoryId);
            insertCmd.Parameters.AddWithValue("@CreatedBy", (object?)createdBy ?? DBNull.Value);
            return (int)await insertCmd.ExecuteScalarAsync();
        }

        // Records a stock movement against an existing (locked) row and
        // writes the matching ledger row in the SAME transaction --
        // PhysicalQuantity is never changed any other way. Positive
        // quantityDelta increases physical stock (e.g. 'Production');
        // negative reduces it (e.g. future Dispatch/Wastage/
        // ReversalRemoval).
        public async Task<(bool Success, string? Message)> RecordTransactionAsync(
            SqlConnection conn, SqlTransaction tx, int pottedPlantStockId, decimal quantityDelta,
            string transactionType, string? referenceType, int? referenceId, int? userId, string? remarks)
        {
            var lockCmd = new SqlCommand("SELECT PhysicalQuantity, ReservedQuantity FROM dbo.PottedPlantStock WITH (UPDLOCK, HOLDLOCK) WHERE Id = @Id", conn, tx);
            lockCmd.Parameters.AddWithValue("@Id", pottedPlantStockId);
            using var reader = await lockCmd.ExecuteReaderAsync();
            if (!await reader.ReadAsync())
            {
                reader.Close();
                return (false, "Potted Plant Stock record not found.");
            }
            var before = reader.GetDecimal(reader.GetOrdinal("PhysicalQuantity"));
            var reserved = reader.GetDecimal(reader.GetOrdinal("ReservedQuantity"));
            reader.Close();

            var after = before + quantityDelta;
            if (after < 0)
                return (false, $"This would take Potted Plant Stock negative (physical {before:N2}, requested change {quantityDelta:N2}).");
            if (after < reserved)
                return (false, $"This would take Physical Quantity ({after:N2}) below what is already Reserved ({reserved:N2}).");

            var updateCmd = new SqlCommand("UPDATE dbo.PottedPlantStock SET PhysicalQuantity = @After, ModifiedDate = SYSUTCDATETIME() WHERE Id = @Id", conn, tx);
            updateCmd.Parameters.AddWithValue("@After", after);
            updateCmd.Parameters.AddWithValue("@Id", pottedPlantStockId);
            await updateCmd.ExecuteNonQueryAsync();

            const string ledgerSql = @"
INSERT INTO dbo.PottedPlantStockTransactions
(PottedPlantStockId, TransactionDate, TransactionType, ReferenceType, ReferenceId, Quantity, BeforeQuantity, UserId, Remarks, CreatedAt)
VALUES
(@PottedPlantStockId, SYSUTCDATETIME(), @TransactionType, @ReferenceType, @ReferenceId, @Quantity, @BeforeQuantity, @UserId, @Remarks, SYSUTCDATETIME());";

            var ledgerCmd = new SqlCommand(ledgerSql, conn, tx);
            ledgerCmd.Parameters.AddWithValue("@PottedPlantStockId", pottedPlantStockId);
            ledgerCmd.Parameters.AddWithValue("@TransactionType", transactionType);
            ledgerCmd.Parameters.AddWithValue("@ReferenceType", (object?)referenceType ?? DBNull.Value);
            ledgerCmd.Parameters.AddWithValue("@ReferenceId", (object?)referenceId ?? DBNull.Value);
            ledgerCmd.Parameters.AddWithValue("@Quantity", quantityDelta);
            ledgerCmd.Parameters.AddWithValue("@BeforeQuantity", before);
            ledgerCmd.Parameters.AddWithValue("@UserId", (object?)userId ?? DBNull.Value);
            ledgerCmd.Parameters.AddWithValue("@Remarks", (object?)remarks ?? DBNull.Value);
            await ledgerCmd.ExecuteNonQueryAsync();

            return (true, null);
        }

        // Records a RESERVATION change (Phase 9 Booking) against an
        // existing (locked) row -- adjusts ReservedQuantity, NEVER
        // PhysicalQuantity, and writes the matching ledger row in the
        // SAME transaction via the same dedicated ledger Phase 7
        // created. Positive reservedDelta reserves more ('Reservation');
        // negative releases a reservation ('ReservationRelease', e.g.
        // booking cancelled). For these two TransactionTypes only, the
        // ledger's BeforeQuantity/AfterQuantity represent
        // ReservedQuantity at that moment, not PhysicalQuantity (every
        // other TransactionType on this ledger tracks PhysicalQuantity
        // instead) -- distinguished by TransactionType when reading it.
        public async Task<(bool Success, string? Message)> RecordReservationAsync(
            SqlConnection conn, SqlTransaction tx, int pottedPlantStockId, decimal reservedDelta,
            string transactionType, string? referenceType, int? referenceId, int? userId, string? remarks)
        {
            var lockCmd = new SqlCommand("SELECT PhysicalQuantity, ReservedQuantity FROM dbo.PottedPlantStock WITH (UPDLOCK, HOLDLOCK) WHERE Id = @Id", conn, tx);
            lockCmd.Parameters.AddWithValue("@Id", pottedPlantStockId);
            using var reader = await lockCmd.ExecuteReaderAsync();
            if (!await reader.ReadAsync())
            {
                reader.Close();
                return (false, "Potted Plant Stock record not found.");
            }
            var physical = reader.GetDecimal(reader.GetOrdinal("PhysicalQuantity"));
            var before = reader.GetDecimal(reader.GetOrdinal("ReservedQuantity"));
            reader.Close();

            var after = before + reservedDelta;
            if (after < 0)
                return (false, $"This would take Reserved Quantity negative (currently reserved {before:N2}, requested change {reservedDelta:N2}).");
            if (after > physical)
                return (false, $"Not enough Available stock to reserve (Physical {physical:N2}, already Reserved {before:N2}, Available {(physical - before):N2}, requested {reservedDelta:N2}).");

            var updateCmd = new SqlCommand("UPDATE dbo.PottedPlantStock SET ReservedQuantity = @After, ModifiedDate = SYSUTCDATETIME() WHERE Id = @Id", conn, tx);
            updateCmd.Parameters.AddWithValue("@After", after);
            updateCmd.Parameters.AddWithValue("@Id", pottedPlantStockId);
            await updateCmd.ExecuteNonQueryAsync();

            const string ledgerSql = @"
INSERT INTO dbo.PottedPlantStockTransactions
(PottedPlantStockId, TransactionDate, TransactionType, ReferenceType, ReferenceId, Quantity, BeforeQuantity, UserId, Remarks, CreatedAt)
VALUES
(@PottedPlantStockId, SYSUTCDATETIME(), @TransactionType, @ReferenceType, @ReferenceId, @Quantity, @BeforeQuantity, @UserId, @Remarks, SYSUTCDATETIME());";

            var ledgerCmd = new SqlCommand(ledgerSql, conn, tx);
            ledgerCmd.Parameters.AddWithValue("@PottedPlantStockId", pottedPlantStockId);
            ledgerCmd.Parameters.AddWithValue("@TransactionType", transactionType);
            ledgerCmd.Parameters.AddWithValue("@ReferenceType", (object?)referenceType ?? DBNull.Value);
            ledgerCmd.Parameters.AddWithValue("@ReferenceId", (object?)referenceId ?? DBNull.Value);
            ledgerCmd.Parameters.AddWithValue("@Quantity", reservedDelta);
            ledgerCmd.Parameters.AddWithValue("@BeforeQuantity", before);
            ledgerCmd.Parameters.AddWithValue("@UserId", (object?)userId ?? DBNull.Value);
            ledgerCmd.Parameters.AddWithValue("@Remarks", (object?)remarks ?? DBNull.Value);
            await ledgerCmd.ExecuteNonQueryAsync();

            return (true, null);
        }

        // Phase 18 (Phase C): reserves quantity against this pool's
        // "available to issue" (PhysicalQuantity - ReservedQuantity -
        // InTransitQuantity) for a NEW outbound 'MainOfficeIssue' transfer
        // being created -- mirrors CuttingStockRepository.
        // ReserveInTransitAsync exactly, except this pool also has a
        // ReservedQuantity (Booking, Phase 9/10) that must stay excluded
        // from what's issuable, on top of what's already in transit.
        // Raises InTransitQuantity only; PhysicalQuantity, ReservedQuantity,
        // and the ledger are all untouched (nothing has actually moved
        // yet). Refuses if not enough is available so the same physical
        // stock can't be issued twice while a transfer is already in
        // flight. Returns the pool's AreaId/SpeciesId so the caller
        // (InternalTransferRepository.InsertAsync) doesn't need a second
        // lookup.
        public async Task<(bool Success, string? Message, int? AreaId, int SpeciesId)> ReserveInTransitAsync(
            SqlConnection conn, SqlTransaction tx, int pottedPlantStockId, decimal quantity)
        {
            if (quantity <= 0)
                return (false, "Quantity must be greater than zero.", null, 0);

            var lockCmd = new SqlCommand(
                "SELECT AreaId, SpeciesId, PhysicalQuantity, ReservedQuantity, InTransitQuantity FROM dbo.PottedPlantStock WITH (UPDLOCK, HOLDLOCK) WHERE Id = @Id",
                conn, tx);
            lockCmd.Parameters.AddWithValue("@Id", pottedPlantStockId);
            using var reader = await lockCmd.ExecuteReaderAsync();
            if (!await reader.ReadAsync())
            {
                reader.Close();
                return (false, "Potted Plant Stock record not found.", null, 0);
            }
            var areaId = reader.IsDBNull(reader.GetOrdinal("AreaId")) ? (int?)null : reader.GetInt32(reader.GetOrdinal("AreaId"));
            var speciesId = reader.GetInt32(reader.GetOrdinal("SpeciesId"));
            var physical = reader.GetDecimal(reader.GetOrdinal("PhysicalQuantity"));
            var reserved = reader.GetDecimal(reader.GetOrdinal("ReservedQuantity"));
            var inTransit = reader.GetDecimal(reader.GetOrdinal("InTransitQuantity"));
            reader.Close();

            var availableToIssue = physical - reserved - inTransit;
            if (quantity > availableToIssue)
                return (false, $"Insufficient available Potted Plant stock to issue (available {availableToIssue:N2}, requested {quantity:N2}).", null, 0);

            var updateCmd = new SqlCommand(
                "UPDATE dbo.PottedPlantStock SET InTransitQuantity = InTransitQuantity + @Quantity, ModifiedDate = SYSUTCDATETIME() WHERE Id = @Id",
                conn, tx);
            updateCmd.Parameters.AddWithValue("@Quantity", quantity);
            updateCmd.Parameters.AddWithValue("@Id", pottedPlantStockId);
            await updateCmd.ExecuteNonQueryAsync();

            return (true, null, areaId, speciesId);
        }

        // Phase 18 (Phase C): releases a previously-reserved quantity back
        // to "available to issue" without touching PhysicalQuantity,
        // ReservedQuantity, or the ledger -- used when Main Office rejects
        // a pending 'MainOfficeIssue' transfer outright (releases the full
        // sent quantity, since nothing ever left) and when a discrepancy
        // is confirmed (releases the shortfall) or the confirmed portion
        // is released back to InTransitQuantity=0 once it is actually
        // moved by ConfirmMainOfficeIssueAsync. Mirrors
        // CuttingStockRepository.ReleaseInTransitAsync exactly.
        public async Task<(bool Success, string? Message)> ReleaseInTransitAsync(
            SqlConnection conn, SqlTransaction tx, int pottedPlantStockId, decimal quantity)
        {
            if (quantity < 0)
                return (false, "Release quantity cannot be negative.");
            if (quantity == 0)
                return (true, null);

            var lockCmd = new SqlCommand(
                "SELECT InTransitQuantity FROM dbo.PottedPlantStock WITH (UPDLOCK, HOLDLOCK) WHERE Id = @Id",
                conn, tx);
            lockCmd.Parameters.AddWithValue("@Id", pottedPlantStockId);
            var inTransitObj = await lockCmd.ExecuteScalarAsync();
            if (inTransitObj == null || inTransitObj == DBNull.Value)
                return (false, "Potted Plant Stock record not found.");

            var inTransit = (decimal)inTransitObj;
            if (quantity > inTransit)
                return (false, $"Cannot release {quantity:N2} -- only {inTransit:N2} is currently in transit for this pool.");

            var updateCmd = new SqlCommand(
                "UPDATE dbo.PottedPlantStock SET InTransitQuantity = InTransitQuantity - @Quantity, ModifiedDate = SYSUTCDATETIME() WHERE Id = @Id",
                conn, tx);
            updateCmd.Parameters.AddWithValue("@Quantity", quantity);
            updateCmd.Parameters.AddWithValue("@Id", pottedPlantStockId);
            await updateCmd.ExecuteNonQueryAsync();

            return (true, null);
        }

        public async Task<List<PottedPlantStockTransaction>> GetTransactionsAsync(int pottedPlantStockId)
        {
            var list = new List<PottedPlantStockTransaction>();
            using var conn = _dbHelper.GetConnection();
            await conn.OpenAsync();

            const string sql = @"
SELECT t.Id, t.PottedPlantStockId, ps.Name AS SpeciesName, s.PotSize, t.TransactionDate, t.TransactionType,
       t.ReferenceType, t.ReferenceId, t.Quantity, t.BeforeQuantity, t.AfterQuantity, t.UserId, u.Name AS UserName,
       t.Remarks, t.CreatedAt
FROM dbo.PottedPlantStockTransactions t
INNER JOIN dbo.PottedPlantStock s ON t.PottedPlantStockId = s.Id
INNER JOIN dbo.PlantSpecies ps ON s.SpeciesId = ps.Id
LEFT JOIN dbo.IMSUsers u ON t.UserId = u.Id
WHERE t.PottedPlantStockId = @Id
ORDER BY t.CreatedAt DESC";

            using var cmd = new SqlCommand(sql, conn);
            cmd.Parameters.AddWithValue("@Id", pottedPlantStockId);
            using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                list.Add(new PottedPlantStockTransaction
                {
                    Id = reader.GetInt32(reader.GetOrdinal("Id")),
                    PottedPlantStockId = reader.GetInt32(reader.GetOrdinal("PottedPlantStockId")),
                    SpeciesName = reader.GetString(reader.GetOrdinal("SpeciesName")),
                    PotSize = reader.GetString(reader.GetOrdinal("PotSize")),
                    TransactionDate = reader.GetDateTime(reader.GetOrdinal("TransactionDate")),
                    TransactionType = reader.GetString(reader.GetOrdinal("TransactionType")),
                    ReferenceType = reader.IsDBNull(reader.GetOrdinal("ReferenceType")) ? null : reader.GetString(reader.GetOrdinal("ReferenceType")),
                    ReferenceId = reader.IsDBNull(reader.GetOrdinal("ReferenceId")) ? null : reader.GetInt32(reader.GetOrdinal("ReferenceId")),
                    Quantity = reader.GetDecimal(reader.GetOrdinal("Quantity")),
                    BeforeQuantity = reader.GetDecimal(reader.GetOrdinal("BeforeQuantity")),
                    UserId = reader.IsDBNull(reader.GetOrdinal("UserId")) ? null : reader.GetInt32(reader.GetOrdinal("UserId")),
                    UserName = reader.IsDBNull(reader.GetOrdinal("UserName")) ? null : reader.GetString(reader.GetOrdinal("UserName")),
                    Remarks = reader.IsDBNull(reader.GetOrdinal("Remarks")) ? null : reader.GetString(reader.GetOrdinal("Remarks")),
                    CreatedAt = reader.GetDateTime(reader.GetOrdinal("CreatedAt"))
                });
            }
            return list;
        }

        private static PottedPlantStock Map(SqlDataReader reader)
        {
            return new PottedPlantStock
            {
                Id = reader.GetInt32(reader.GetOrdinal("Id")),
                SpeciesId = reader.GetInt32(reader.GetOrdinal("SpeciesId")),
                SpeciesName = reader.GetString(reader.GetOrdinal("SpeciesName")),
                PlantTypeName = reader.GetString(reader.GetOrdinal("PlantTypeName")),
                PotSize = reader.GetString(reader.GetOrdinal("PotSize")),
                // Backfilled for every pre-existing row by Phase 8's
                // migration (matched against the legacy AreaId-IS-NULL
                // pool of the same PotSize); guarded here in case of a
                // pre-migration data anomaly rather than throwing.
                EmptyPotInventoryId = reader.IsDBNull(reader.GetOrdinal("EmptyPotInventoryId")) ? 0 : reader.GetInt32(reader.GetOrdinal("EmptyPotInventoryId")),
                AreaId = reader.IsDBNull(reader.GetOrdinal("AreaId")) ? null : reader.GetInt32(reader.GetOrdinal("AreaId")),
                AreaName = reader.IsDBNull(reader.GetOrdinal("AreaName")) ? null : reader.GetString(reader.GetOrdinal("AreaName")),
                PhysicalQuantity = reader.GetDecimal(reader.GetOrdinal("PhysicalQuantity")),
                ReservedQuantity = reader.GetDecimal(reader.GetOrdinal("ReservedQuantity")),
                SoldDispatchedQuantity = reader.GetDecimal(reader.GetOrdinal("SoldDispatchedQuantity")),
                WastedQuantity = reader.GetDecimal(reader.GetOrdinal("WastedQuantity")),
                InTransitQuantity = reader.GetDecimal(reader.GetOrdinal("InTransitQuantity")),
                CreatedDate = reader.GetDateTime(reader.GetOrdinal("CreatedDate")),
                CreatedBy = reader.IsDBNull(reader.GetOrdinal("CreatedBy")) ? null : reader.GetString(reader.GetOrdinal("CreatedBy")),
                ModifiedDate = reader.IsDBNull(reader.GetOrdinal("ModifiedDate")) ? null : reader.GetDateTime(reader.GetOrdinal("ModifiedDate")),
                ModifiedBy = reader.IsDBNull(reader.GetOrdinal("ModifiedBy")) ? null : reader.GetString(reader.GetOrdinal("ModifiedBy")),
                GrowingPartnerName = reader.IsDBNull(reader.GetOrdinal("GrowingPartnerName")) ? null : reader.GetString(reader.GetOrdinal("GrowingPartnerName")),
                LastProductionId = reader.IsDBNull(reader.GetOrdinal("LastProductionId")) ? null : reader.GetInt32(reader.GetOrdinal("LastProductionId")),
                LastProductionCode = reader.IsDBNull(reader.GetOrdinal("LastProductionCode")) ? null : reader.GetString(reader.GetOrdinal("LastProductionCode")),
                LastProductionSource = reader.IsDBNull(reader.GetOrdinal("LastProductionSource")) ? null : reader.GetString(reader.GetOrdinal("LastProductionSource")),
                LastTransactionDate = reader.IsDBNull(reader.GetOrdinal("LastTransactionDate")) ? null : reader.GetDateTime(reader.GetOrdinal("LastTransactionDate"))
            };
        }
    }
}
