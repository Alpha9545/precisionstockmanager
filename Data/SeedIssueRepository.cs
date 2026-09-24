using Microsoft.Data.SqlClient;
using PlantStockManager.Models;

namespace PlantStockManager.Data
{
    // Phase 22/Phase H: Main Office -> Polyhouse/Growing Area Seed
    // Issue. Orchestrates dbo.SeedIssues against dbo.SeedStock, mirroring
    // InternalTransferRepository's MainOfficeIssue lifecycle shape
    // (InsertAsync reserves InTransit only; ConfirmAsync is the single
    // step that actually moves stock; RejectAsync releases InTransit
    // with no stock movement) but as its OWN dedicated table/repository
    // -- see Database/Phase22_MainOfficeSeedIssue.sql's header comment
    // for why this was not folded into dbo.InternalTransfers.
    public class SeedIssueRepository
    {
        private readonly DatabaseHelper _dbHelper;
        private readonly BatchNumberRepository _batchNumberRepo;
        private readonly SeedStockRepository _seedStockRepo;
        private readonly AreaRepository _areaRepo;

        // The Area types this app already recognizes as general
        // growing/production locations where a Growing Area Supervisor
        // performs production work (Kunjir/Kiran -- Phase 14), as
        // distinct from 'MotherPlant' (dedicated genetic-stock holding,
        // not a general sowing destination), 'Outlet' (sales-facing,
        // explicitly out of scope per spec item 19) and 'MainOffice'
        // (the source itself, and a central, not growing, location).
        // See PROJECT_DOCUMENTATION.md Decision 19 for the full
        // reasoning behind this specific choice, and why it is a small,
        // easily-widened array rather than a schema change if the
        // business owner later confirms Mother Plant Areas should also
        // be an eligible seed-sowing destination.
        private static readonly string[] ValidDestinationAreaTypes = { "Kunjir", "Kiran" };

        public SeedIssueRepository(
            DatabaseHelper dbHelper,
            BatchNumberRepository batchNumberRepo,
            SeedStockRepository seedStockRepo,
            AreaRepository areaRepo)
        {
            _dbHelper = dbHelper;
            _batchNumberRepo = batchNumberRepo;
            _seedStockRepo = seedStockRepo;
            _areaRepo = areaRepo;
        }

        private const string BaseSelect = @"
SELECT
    si.Id, si.IssueCode, si.SourceSeedStockId, si.SpeciesId, ps.Name AS SpeciesName, pt.Name AS PlantTypeName,
    ss.BatchNo, ss.SeedSourceId, src.Name AS SeedSourceName,
    si.SourceAreaId, sa.Name AS SourceAreaName,
    si.DestinationAreaId, da.Name AS DestinationAreaName,
    si.IssuedQuantity, si.IssueDate, si.Status,
    si.ConfirmedQuantity, si.ConfirmedBy, cb.Name AS ConfirmedByName, si.ConfirmedDate, si.DiscrepancyReason,
    si.ResponsiblePersonId, r.Name AS ResponsiblePersonName,
    si.SupervisorId, sup.Name AS SupervisorName,
    si.Remarks, si.CreatedDate, si.CreatedBy, si.ModifiedDate, si.ModifiedBy,
    ss.AvailableQuantity AS SourceAvailableQuantity
FROM dbo.SeedIssues si
INNER JOIN dbo.PlantSpecies ps ON si.SpeciesId = ps.Id
INNER JOIN dbo.PlantTypes pt ON ps.PlantTypeId = pt.Id
INNER JOIN dbo.SeedStock ss ON si.SourceSeedStockId = ss.Id
LEFT JOIN dbo.SeedSources src ON ss.SeedSourceId = src.Id
INNER JOIN dbo.Area sa ON si.SourceAreaId = sa.Id
INNER JOIN dbo.Area da ON si.DestinationAreaId = da.Id
LEFT JOIN dbo.IMSUsers r ON si.ResponsiblePersonId = r.Id
LEFT JOIN dbo.IMSUsers sup ON si.SupervisorId = sup.Id
LEFT JOIN dbo.IMSUsers cb ON si.ConfirmedBy = cb.Id";

        public async Task<List<SeedIssue>> GetAllAsync()
        {
            var list = new List<SeedIssue>();
            using var conn = _dbHelper.GetConnection();
            await conn.OpenAsync();

            var sql = BaseSelect + " ORDER BY si.CreatedDate DESC";
            using var cmd = new SqlCommand(sql, conn);
            using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                list.Add(Map(reader));
            }
            return list;
        }

        public async Task<SeedIssue?> GetByIdAsync(int id)
        {
            using var conn = _dbHelper.GetConnection();
            await conn.OpenAsync();

            var sql = BaseSelect + " WHERE si.Id = @Id";
            using var cmd = new SqlCommand(sql, conn);
            cmd.Parameters.AddWithValue("@Id", id);
            using var reader = await cmd.ExecuteReaderAsync();
            if (await reader.ReadAsync())
            {
                return Map(reader);
            }
            return null;
        }

        // Every issue where the given Area is either the Main Office
        // sender or the receiving Polyhouse -- feeds "My Seed Issues".
        public async Task<List<SeedIssue>> GetByAreaAsync(int areaId)
        {
            var list = new List<SeedIssue>();
            using var conn = _dbHelper.GetConnection();
            await conn.OpenAsync();

            var sql = BaseSelect + " WHERE si.SourceAreaId = @AreaId OR si.DestinationAreaId = @AreaId ORDER BY si.CreatedDate DESC";
            using var cmd = new SqlCommand(sql, conn);
            cmd.Parameters.AddWithValue("@AreaId", areaId);
            using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                list.Add(Map(reader));
            }
            return list;
        }

        // Still-pending issues addressed to the given destination
        // Area -- feeds the receiving Polyhouse Supervisor's queue.
        // destinationAreaId narrows to one Area; null returns every
        // pending Seed Issue regardless of destination.
        public async Task<List<SeedIssue>> GetPendingReceiptsAsync(int? destinationAreaId = null)
        {
            var list = new List<SeedIssue>();
            using var conn = _dbHelper.GetConnection();
            await conn.OpenAsync();

            var sql = BaseSelect + @"
WHERE si.Status = 'PendingConfirmation'
  AND (@AreaId IS NULL OR si.DestinationAreaId = @AreaId)
ORDER BY si.CreatedDate ASC";
            using var cmd = new SqlCommand(sql, conn);
            cmd.Parameters.AddWithValue("@AreaId", (object?)destinationAreaId ?? DBNull.Value);
            using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                list.Add(Map(reader));
            }
            return list;
        }

        // Creates a new Seed Issue in 'PendingConfirmation'. Reserves
        // the issued quantity into InTransit only -- PhysicalQuantity
        // and the ledger are untouched until the destination Area
        // confirms receipt (ConfirmAsync). Both Source (must be a
        // genuine, active Main Office pool) and Destination (must be
        // an active Kunjir/Kiran Area) are re-verified here,
        // server-side, from the locked stock row and the freshly
        // fetched Area rows -- never trusted from a dropdown or a
        // posted Id alone (spec item 11: "Do not trust posted AreaId/
        // UserId/SeedStockId. Re-fetch the actual database records and
        // validate them.").
        //
        // Phase B: Seed Issue is retired -- sowing consumes Main Office Seed
        // Stock directly. No NEW issue can be created; existing records,
        // ConfirmAsync/RejectAsync (to drain still-pending issues) and the
        // table itself are kept.
        public static bool NewIssuesEnabled => false;

        public async Task<(bool Success, string? Message, int Id)> InsertAsync(SeedIssue entry, int? userId)
        {
            if (!NewIssuesEnabled)
                return (false, "Seed Issue has been retired. Record sowing directly from Main Office Seed Stock.", 0);
            if (entry.IssuedQuantity <= 0)
                return (false, "Quantity must be greater than zero.", 0);
            if (entry.SourceSeedStockId <= 0)
                return (false, "Source Main Office Seed Stock pool is required.", 0);
            if (entry.DestinationAreaId <= 0)
                return (false, "Destination Polyhouse/Growing Area is required.", 0);

            using var conn = _dbHelper.GetConnection();
            await conn.OpenAsync();
            using var tx = conn.BeginTransaction();

            try
            {
                // 1) Lock the source Seed Stock pool, validate available
                // quantity, and raise InTransitQuantity -- nothing else
                // moves yet.
                var (reserveSuccess, reserveMessage, srcAreaId, speciesId, batchNo, seedSourceId) =
                    await _seedStockRepo.ReserveInTransitAsync(conn, tx, entry.SourceSeedStockId, entry.IssuedQuantity);
                if (!reserveSuccess)
                {
                    tx.Rollback();
                    return (false, reserveMessage, 0);
                }

                // 2) Force the source to a genuine, active Main Office
                // Area, server-side -- never trust that the caller only
                // offered Main Office pools in a dropdown.
                var srcArea = await _areaRepo.GetAreaById(srcAreaId);
                if (srcArea == null || !srcArea.IsActive || srcArea.AreaType != "MainOffice")
                {
                    tx.Rollback();
                    return (false, "The selected source stock is not held at an active Main Office Area.", 0);
                }

                // 3) Force the destination to an active, genuine
                // Polyhouse/Growing Area (Kunjir or Kiran), server-side
                // -- this is what defeats a tampered POST/URL Area Id
                // for a non-growing destination (Outlet, Main Office,
                // another Species' pool's Area, etc.) that was never
                // offered in the destination dropdown at all.
                var destArea = await _areaRepo.GetAreaById(entry.DestinationAreaId);
                if (destArea == null || !destArea.IsActive || destArea.AreaType == null || !ValidDestinationAreaTypes.Contains(destArea.AreaType))
                {
                    tx.Rollback();
                    return (false, "The selected Destination Area is not an active Polyhouse/Growing Area.", 0);
                }

                entry.SpeciesId = speciesId;
                entry.SourceAreaId = srcAreaId;

                if (entry.SourceAreaId == entry.DestinationAreaId)
                {
                    tx.Rollback();
                    return (false, "Source and Destination Area must be different.", 0);
                }

                var issueCode = await _batchNumberRepo.GetNextBatchNumberAsync(conn, tx, "SI", entry.IssueDate.Year);

                const string insertSql = @"
INSERT INTO dbo.SeedIssues
(IssueCode, SourceSeedStockId, SpeciesId, SourceAreaId, DestinationAreaId, IssuedQuantity, IssueDate, Status,
 ResponsiblePersonId, SupervisorId, Remarks, CreatedDate, CreatedBy)
VALUES
(@IssueCode, @SourceSeedStockId, @SpeciesId, @SourceAreaId, @DestinationAreaId, @IssuedQuantity, @IssueDate, 'PendingConfirmation',
 @ResponsiblePersonId, @SupervisorId, @Remarks, SYSUTCDATETIME(), @CreatedBy);
SELECT CAST(SCOPE_IDENTITY() AS INT);";

                using var cmd = new SqlCommand(insertSql, conn, tx);
                cmd.Parameters.AddWithValue("@IssueCode", issueCode);
                cmd.Parameters.AddWithValue("@SourceSeedStockId", entry.SourceSeedStockId);
                cmd.Parameters.AddWithValue("@SpeciesId", entry.SpeciesId);
                cmd.Parameters.AddWithValue("@SourceAreaId", entry.SourceAreaId);
                cmd.Parameters.AddWithValue("@DestinationAreaId", entry.DestinationAreaId);
                cmd.Parameters.AddWithValue("@IssuedQuantity", entry.IssuedQuantity);
                cmd.Parameters.AddWithValue("@IssueDate", entry.IssueDate);
                cmd.Parameters.AddWithValue("@ResponsiblePersonId", (object?)entry.ResponsiblePersonId ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@SupervisorId", (object?)entry.SupervisorId ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@Remarks", (object?)entry.Remarks ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@CreatedBy", (object?)entry.CreatedBy ?? DBNull.Value);

                var newId = (int)await cmd.ExecuteScalarAsync();

                tx.Commit();
                entry.Id = newId;
                entry.IssueCode = issueCode;
                entry.Status = "PendingConfirmation";
                return (true, null, newId);
            }
            catch (Exception ex)
            {
                tx.Rollback();
                return (false, ex.Message, 0);
            }
        }

        // The destination Area confirms RECEIPT of a pending Seed
        // Issue -- enters the ACTUAL quantity received (which may
        // differ from what was issued) and, in ONE step: releases the
        // FULL issued quantity from Main Office's InTransit, decrements
        // Main Office's own PhysicalQuantity by only what was actually
        // confirmed (a shortfall is simply never taken out -- it never
        // left), and credits the destination Area's own Seed Stock pool
        // (same Species + same BatchNo/SeedSource, get-or-created) by
        // that same confirmed quantity. Mirrors
        // InternalTransferRepository.ConfirmMainOfficeIssueAsync's
        // shape exactly.
        public async Task<(bool Success, string? Message)> ConfirmAsync(
            int id, decimal confirmedQuantity, string? discrepancyReason, int? confirmedByUserId, string? modifiedBy)
        {
            if (confirmedQuantity < 0)
                return (false, "Confirmed quantity cannot be negative.");

            using var conn = _dbHelper.GetConnection();
            await conn.OpenAsync();
            using var tx = conn.BeginTransaction();

            try
            {
                var lockCmd = new SqlCommand(
                    "SELECT SourceSeedStockId, DestinationAreaId, IssuedQuantity, Status FROM dbo.SeedIssues WITH (UPDLOCK, HOLDLOCK) WHERE Id = @Id",
                    conn, tx);
                lockCmd.Parameters.AddWithValue("@Id", id);
                using var reader = await lockCmd.ExecuteReaderAsync();
                if (!await reader.ReadAsync())
                {
                    reader.Close();
                    tx.Rollback();
                    return (false, "Seed Issue record not found.");
                }
                var sourceSeedStockId = reader.GetInt32(reader.GetOrdinal("SourceSeedStockId"));
                var destinationAreaId = reader.GetInt32(reader.GetOrdinal("DestinationAreaId"));
                var issuedQuantity = reader.GetDecimal(reader.GetOrdinal("IssuedQuantity"));
                var status = reader.GetString(reader.GetOrdinal("Status"));
                reader.Close();

                if (status != "PendingConfirmation")
                {
                    tx.Rollback();
                    return (false, $"This Seed Issue is already '{status}' and cannot be confirmed again.");
                }
                if (confirmedQuantity > issuedQuantity)
                {
                    tx.Rollback();
                    return (false, $"Confirmed quantity ({confirmedQuantity:N2}) cannot exceed the quantity actually issued ({issuedQuantity:N2}).");
                }
                if (confirmedQuantity != issuedQuantity && string.IsNullOrWhiteSpace(discrepancyReason))
                {
                    tx.Rollback();
                    return (false, $"Confirmed quantity ({confirmedQuantity:N2}) differs from issued quantity ({issuedQuantity:N2}) -- a reason is required.");
                }

                // Release the FULL issued quantity from InTransit --
                // nothing stays "in transit" once this commits.
                var (releaseSuccess, releaseMessage) = await _seedStockRepo.ReleaseInTransitAsync(conn, tx, sourceSeedStockId, issuedQuantity);
                if (!releaseSuccess)
                {
                    tx.Rollback();
                    return (false, releaseMessage);
                }

                if (confirmedQuantity > 0)
                {
                    var (srcSuccess, srcMessage) = await _seedStockRepo.RecordTransactionAsync(
                        conn, tx, sourceSeedStockId, -confirmedQuantity, "Transfer", "SeedIssue", id, confirmedByUserId, discrepancyReason);
                    if (!srcSuccess)
                    {
                        tx.Rollback();
                        return (false, srcMessage);
                    }

                    var srcInfoCmd = new SqlCommand("SELECT SpeciesId, BatchNo, SeedSourceId FROM dbo.SeedStock WHERE Id = @Id", conn, tx);
                    srcInfoCmd.Parameters.AddWithValue("@Id", sourceSeedStockId);
                    using var srcInfoReader = await srcInfoCmd.ExecuteReaderAsync();
                    if (!await srcInfoReader.ReadAsync())
                    {
                        srcInfoReader.Close();
                        tx.Rollback();
                        return (false, "Source Seed Stock pool no longer exists.");
                    }
                    var srcSpeciesId = srcInfoReader.GetInt32(srcInfoReader.GetOrdinal("SpeciesId"));
                    var srcBatchNo = srcInfoReader.GetString(srcInfoReader.GetOrdinal("BatchNo"));
                    var srcSeedSourceId = srcInfoReader.IsDBNull(srcInfoReader.GetOrdinal("SeedSourceId")) ? (int?)null : srcInfoReader.GetInt32(srcInfoReader.GetOrdinal("SeedSourceId"));
                    srcInfoReader.Close();

                    var destId = await _seedStockRepo.GetOrCreateLockedAsync(conn, tx, srcSpeciesId, destinationAreaId, srcBatchNo, srcSeedSourceId, modifiedBy);
                    var (destSuccess, destMessage) = await _seedStockRepo.RecordTransactionAsync(
                        conn, tx, destId, confirmedQuantity, "Transfer", "SeedIssue", id, confirmedByUserId, discrepancyReason);
                    if (!destSuccess)
                    {
                        tx.Rollback();
                        return (false, destMessage);
                    }
                }

                var updateCmd = new SqlCommand(@"
UPDATE dbo.SeedIssues
SET Status = 'Completed',
    ConfirmedQuantity = @ConfirmedQuantity,
    ConfirmedBy = @ConfirmedBy,
    ConfirmedDate = SYSUTCDATETIME(),
    DiscrepancyReason = @DiscrepancyReason,
    ModifiedDate = SYSUTCDATETIME(),
    ModifiedBy = @ModifiedBy
WHERE Id = @Id", conn, tx);
                updateCmd.Parameters.AddWithValue("@ConfirmedQuantity", confirmedQuantity);
                updateCmd.Parameters.AddWithValue("@ConfirmedBy", (object?)confirmedByUserId ?? DBNull.Value);
                updateCmd.Parameters.AddWithValue("@DiscrepancyReason", (object?)discrepancyReason ?? DBNull.Value);
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

        // The destination Area rejects a pending Seed Issue outright.
        // No PhysicalQuantity ever moved for a pending issue, so there
        // is nothing to reverse there -- but the full issued quantity
        // IS currently held in InTransitQuantity (reserved at
        // InsertAsync), and that must be released back to Main
        // Office's Available. The record is never deleted -- only its
        // Status changes, and the reason is recorded in
        // DiscrepancyReason (spec item 9: "a rejection reason must be
        // recorded").
        public async Task<(bool Success, string? Message)> RejectAsync(int id, string reason, string? modifiedBy)
        {
            if (string.IsNullOrWhiteSpace(reason))
                return (false, "A reason is required to reject a pending Seed Issue.");

            using var conn = _dbHelper.GetConnection();
            await conn.OpenAsync();
            using var tx = conn.BeginTransaction();

            try
            {
                var lockCmd = new SqlCommand(
                    "SELECT SourceSeedStockId, IssuedQuantity, Status FROM dbo.SeedIssues WITH (UPDLOCK, HOLDLOCK) WHERE Id = @Id",
                    conn, tx);
                lockCmd.Parameters.AddWithValue("@Id", id);
                using var reader = await lockCmd.ExecuteReaderAsync();
                if (!await reader.ReadAsync())
                {
                    reader.Close();
                    tx.Rollback();
                    return (false, "Seed Issue record not found.");
                }
                var sourceSeedStockId = reader.GetInt32(reader.GetOrdinal("SourceSeedStockId"));
                var issuedQuantity = reader.GetDecimal(reader.GetOrdinal("IssuedQuantity"));
                var status = reader.GetString(reader.GetOrdinal("Status"));
                reader.Close();

                if (status != "PendingConfirmation")
                {
                    tx.Rollback();
                    return (false, "Seed Issue record not found, or it is no longer pending.");
                }

                var (releaseSuccess, releaseMessage) = await _seedStockRepo.ReleaseInTransitAsync(conn, tx, sourceSeedStockId, issuedQuantity);
                if (!releaseSuccess)
                {
                    tx.Rollback();
                    return (false, releaseMessage);
                }

                var updateCmd = new SqlCommand(@"
UPDATE dbo.SeedIssues
SET Status = 'Rejected', DiscrepancyReason = @Reason, ModifiedDate = SYSUTCDATETIME(), ModifiedBy = @ModifiedBy
WHERE Id = @Id AND Status = 'PendingConfirmation'", conn, tx);
                updateCmd.Parameters.AddWithValue("@Reason", reason);
                updateCmd.Parameters.AddWithValue("@ModifiedBy", (object?)modifiedBy ?? DBNull.Value);
                updateCmd.Parameters.AddWithValue("@Id", id);
                var rows = await updateCmd.ExecuteNonQueryAsync();
                if (rows == 0)
                {
                    tx.Rollback();
                    return (false, "Seed Issue record not found, or it is no longer pending.");
                }

                tx.Commit();
                return (true, null);
            }
            catch (Exception ex)
            {
                tx.Rollback();
                return (false, ex.Message);
            }
        }

        // Updates non-stock-affecting fields only (ResponsiblePersonId,
        // SupervisorId, Remarks). Source/Destination/Quantity are
        // immutable after creation.
        public async Task<(bool Success, string? Message)> UpdateDetailsAsync(SeedIssue entry)
        {
            using var conn = _dbHelper.GetConnection();
            await conn.OpenAsync();

            try
            {
                const string updateSql = @"
UPDATE dbo.SeedIssues
SET ResponsiblePersonId = @ResponsiblePersonId,
    SupervisorId = @SupervisorId,
    Remarks = @Remarks,
    ModifiedDate = SYSUTCDATETIME(),
    ModifiedBy = @ModifiedBy
WHERE Id = @Id AND Status = 'PendingConfirmation'";

                using var cmd = new SqlCommand(updateSql, conn);
                cmd.Parameters.AddWithValue("@Id", entry.Id);
                cmd.Parameters.AddWithValue("@ResponsiblePersonId", (object?)entry.ResponsiblePersonId ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@SupervisorId", (object?)entry.SupervisorId ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@Remarks", (object?)entry.Remarks ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@ModifiedBy", (object?)entry.ModifiedBy ?? DBNull.Value);

                var rows = await cmd.ExecuteNonQueryAsync();
                return rows > 0 ? (true, null) : (false, "Seed Issue record not found, or it is no longer pending.");
            }
            catch (Exception ex)
            {
                return (false, ex.Message);
            }
        }

        private static SeedIssue Map(SqlDataReader reader)
        {
            return new SeedIssue
            {
                Id = reader.GetInt32(reader.GetOrdinal("Id")),
                IssueCode = reader.GetString(reader.GetOrdinal("IssueCode")),
                SourceSeedStockId = reader.GetInt32(reader.GetOrdinal("SourceSeedStockId")),
                SpeciesId = reader.GetInt32(reader.GetOrdinal("SpeciesId")),
                SpeciesName = reader.GetString(reader.GetOrdinal("SpeciesName")),
                PlantTypeName = reader.GetString(reader.GetOrdinal("PlantTypeName")),
                BatchNo = reader.GetString(reader.GetOrdinal("BatchNo")),
                SeedSourceName = reader.IsDBNull(reader.GetOrdinal("SeedSourceName")) ? null : reader.GetString(reader.GetOrdinal("SeedSourceName")),
                SourceAreaId = reader.GetInt32(reader.GetOrdinal("SourceAreaId")),
                SourceAreaName = reader.GetString(reader.GetOrdinal("SourceAreaName")),
                DestinationAreaId = reader.GetInt32(reader.GetOrdinal("DestinationAreaId")),
                DestinationAreaName = reader.GetString(reader.GetOrdinal("DestinationAreaName")),
                IssuedQuantity = reader.GetDecimal(reader.GetOrdinal("IssuedQuantity")),
                IssueDate = reader.GetDateTime(reader.GetOrdinal("IssueDate")),
                Status = reader.GetString(reader.GetOrdinal("Status")),
                ConfirmedQuantity = reader.IsDBNull(reader.GetOrdinal("ConfirmedQuantity")) ? null : reader.GetDecimal(reader.GetOrdinal("ConfirmedQuantity")),
                ConfirmedBy = reader.IsDBNull(reader.GetOrdinal("ConfirmedBy")) ? null : reader.GetInt32(reader.GetOrdinal("ConfirmedBy")),
                ConfirmedByName = reader.IsDBNull(reader.GetOrdinal("ConfirmedByName")) ? null : reader.GetString(reader.GetOrdinal("ConfirmedByName")),
                ConfirmedDate = reader.IsDBNull(reader.GetOrdinal("ConfirmedDate")) ? null : reader.GetDateTime(reader.GetOrdinal("ConfirmedDate")),
                DiscrepancyReason = reader.IsDBNull(reader.GetOrdinal("DiscrepancyReason")) ? null : reader.GetString(reader.GetOrdinal("DiscrepancyReason")),
                ResponsiblePersonId = reader.IsDBNull(reader.GetOrdinal("ResponsiblePersonId")) ? null : reader.GetInt32(reader.GetOrdinal("ResponsiblePersonId")),
                ResponsiblePersonName = reader.IsDBNull(reader.GetOrdinal("ResponsiblePersonName")) ? null : reader.GetString(reader.GetOrdinal("ResponsiblePersonName")),
                SupervisorId = reader.IsDBNull(reader.GetOrdinal("SupervisorId")) ? null : reader.GetInt32(reader.GetOrdinal("SupervisorId")),
                SupervisorName = reader.IsDBNull(reader.GetOrdinal("SupervisorName")) ? null : reader.GetString(reader.GetOrdinal("SupervisorName")),
                Remarks = reader.IsDBNull(reader.GetOrdinal("Remarks")) ? null : reader.GetString(reader.GetOrdinal("Remarks")),
                CreatedDate = reader.GetDateTime(reader.GetOrdinal("CreatedDate")),
                CreatedBy = reader.IsDBNull(reader.GetOrdinal("CreatedBy")) ? null : reader.GetString(reader.GetOrdinal("CreatedBy")),
                ModifiedDate = reader.IsDBNull(reader.GetOrdinal("ModifiedDate")) ? null : reader.GetDateTime(reader.GetOrdinal("ModifiedDate")),
                ModifiedBy = reader.IsDBNull(reader.GetOrdinal("ModifiedBy")) ? null : reader.GetString(reader.GetOrdinal("ModifiedBy")),
                SourceAvailableQuantity = reader.IsDBNull(reader.GetOrdinal("SourceAvailableQuantity")) ? null : reader.GetDecimal(reader.GetOrdinal("SourceAvailableQuantity"))
            };
        }
    }
}
