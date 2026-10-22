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
    // 2026-10-15 (2026-10-15_PotProductionFromReadyStock.sql, PotBatchSchema): the plants may instead be
    // SEEDLINGS of a Ready Stock batch received in a Mother Plant Area for the purpose "Pot Production":
    //   batch start      Ready Stock batch  -allocated   ('PotProductionOut')
    //   READY / cancel   Ready Stock batch  +unused / +allocated ('ReversalReturn')
    // Everything else (pots, entries, READY into Potted Plant Stock) is the same flow.
    public class PotBatchRepository
    {
        private readonly DatabaseHelper _dbHelper;
        private readonly BatchNumberRepository _batchNumberRepo;
        private readonly CuttingStockRepository _cuttingStockRepo;
        private readonly EmptyPotInventoryRepository _emptyPotRepo;
        private readonly PottedPlantStockRepository _pottedStockRepo;
        private readonly UserRoleRepository _userRoleRepo;
        private readonly ReadyStockRepository _readyStockRepo;

        public const string ReadyStockAllocationType = "PotProductionOut";
        public const string ReferenceType = "PotProductionBatch";
        public const string NotForPotProductionMessage =
            "Only Ready Stock received in a Mother Plant Area for the purpose \"Pot Production\" can start a pot batch.";

        public PotBatchRepository(DatabaseHelper dbHelper, BatchNumberRepository batchNumberRepo, CuttingStockRepository cuttingStockRepo,
            EmptyPotInventoryRepository emptyPotRepo, PottedPlantStockRepository pottedStockRepo, UserRoleRepository userRoleRepo,
            ReadyStockRepository readyStockRepo)
        {
            _readyStockRepo = readyStockRepo;
            _dbHelper = dbHelper;
            _batchNumberRepo = batchNumberRepo;
            _cuttingStockRepo = cuttingStockRepo;
            _emptyPotRepo = emptyPotRepo;
            _pottedStockRepo = pottedStockRepo;
            _userRoleRepo = userRoleRepo;
        }

        // rsSource (PotBatchSchema): the Ready Stock source column exists. Without it every batch is from cuttings and
        // the LEFT JOINs return exactly what the former INNER JOINs did.
        private static string BaseSelectFor(bool rsSource) => @"
SELECT b.Id, b.BatchCode, b.SourceCuttingStockId, " + (rsSource
    ? "b.SourceReadyStockId, rsrc.BatchNo AS SourceReadyStockBatchNo,"
    : "CAST(NULL AS INT) AS SourceReadyStockId, CAST(NULL AS NVARCHAR(100)) AS SourceReadyStockBatchNo,") + @" b.SpeciesId, ps.Name AS SpeciesName, ps.Color, pt.Name AS PlantTypeName,
       b.AreaId, a.Name AS AreaName, csa.Name AS SourceAreaName, b.PolyhouseId, ph.Name AS PolyhouseName, b.PotSize, b.EmptyPotInventoryId, epi.PhysicalQuantity AS EmptyPotsAvailable,
       b.CuttingAllocated, b.ProductionStartDate, b.ExpectedReadyDate, b.SupervisorId, sup.Name AS SupervisorName,
       b.Status, b.ReadyQuantity, b.WastageReason, b.UnusedCuttingAction, b.ReadyConfirmedById, rcb.Name AS ReadyConfirmedByName,
       b.ReadyDate, b.ReadyRemarks, b.PottedPlantStockId, b.Remarks, b.CreatedById, cb.Name AS CreatedByName, b.CreatedBy, b.CreatedDate,
       ISNULL(pe.Potted, 0) AS PottedQuantity
FROM dbo.PotProductionBatches b
INNER JOIN dbo.PlantSpecies ps ON ps.Id = b.SpeciesId
INNER JOIN dbo.PlantTypes pt ON pt.Id = ps.PlantTypeId
INNER JOIN dbo.Area a ON a.Id = b.AreaId
LEFT JOIN dbo.CuttingStock cs ON cs.Id = b.SourceCuttingStockId" + (rsSource ? @"
LEFT JOIN dbo.ReadyStock rsrc ON rsrc.Id = b.SourceReadyStockId
LEFT JOIN dbo.Area csa ON csa.Id = COALESCE(cs.AreaId, rsrc.AreaId)" : @"
LEFT JOIN dbo.Area csa ON csa.Id = cs.AreaId") + @"
INNER JOIN dbo.EmptyPotInventory epi ON epi.Id = b.EmptyPotInventoryId
LEFT JOIN dbo.Polyhouses ph ON ph.Id = b.PolyhouseId
LEFT JOIN dbo.IMSUsers sup ON sup.Id = b.SupervisorId
LEFT JOIN dbo.IMSUsers rcb ON rcb.Id = b.ReadyConfirmedById
LEFT JOIN dbo.IMSUsers cb ON cb.Id = b.CreatedById
OUTER APPLY (SELECT SUM(e.Quantity) AS Potted FROM dbo.PotProductionEntries e WHERE e.BatchId = b.Id) pe";

        private async Task<string> BaseSelectAsync(SqlConnection conn) => BaseSelectFor(await PotBatchSchema.ReadyStockSourceAsync(conn));

        public async Task<List<PotProductionBatch>> GetAllAsync(string? status = null)
        {
            var list = new List<PotProductionBatch>();
            using var conn = _dbHelper.GetConnection();
            await conn.OpenAsync();
            using var cmd = new SqlCommand(await BaseSelectAsync(conn) + " WHERE (@Status IS NULL OR b.Status = @Status) ORDER BY b.ExpectedReadyDate, b.Id", conn);
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
            using var cmd = new SqlCommand(await BaseSelectAsync(conn) + " WHERE b.Id = @Id", conn);
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
        // canUseSourceArea (Cutting Stock AREA ISOLATION, CuttingRules.CanUseAsSource):
        // may this user use cuttings held in the POOL's own Area? Checked under the
        // pool's lock, before anything is written; null = no per-user check (callers
        // without a signed-in user).
        public async Task<(bool Success, string? Message, int Id)> CreateAsync(PotProductionBatch entry, int userId, Func<int, bool>? canUseSourceArea = null)
        {
            if (entry.SourceCuttingStockId is not > 0)
                return (false, "Choose the Cutting Stock.", 0);
            using var conn = _dbHelper.GetConnection();
            await conn.OpenAsync();
            using var tx = conn.BeginTransaction();
            try
            {
                var csCmd = new SqlCommand("SELECT SpeciesId, PhysicalQuantity, InTransitQuantity, AreaId FROM dbo.CuttingStock WITH (UPDLOCK, HOLDLOCK) WHERE Id = @Id", conn, tx);
                csCmd.Parameters.AddWithValue("@Id", entry.SourceCuttingStockId);
                int speciesId, poolAreaId; decimal physical, inTransit;
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
                    poolAreaId = r.GetInt32(3);
                }
                if (canUseSourceArea != null && !canUseSourceArea(poolAreaId))
                {
                    tx.Rollback();
                    return (false, "You are not authorized to use cuttings from this Area.", 0);
                }
                if (await StartedByTokenAsync(conn, tx, entry.SubmissionToken) is int startedCutting)
                {
                    tx.Rollback();
                    entry.Id = startedCutting;
                    return (true, AlreadyStartedMessage, startedCutting);
                }
                var withToken = entry.SubmissionToken.HasValue && await WorkflowCorrectionsSchema.PotTokenAsync(conn, tx);

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

                var eligible = (await _userRoleRepo.GetReadyConfirmersAsync(entry.AreaId, conn, tx))
                    .Select(u => u.EmployeeID).ToList();
                var (ok, error) = PotBatchRules.ValidateCreate(entry.CuttingAllocated, physical - inTransit, entry.ProductionStartDate,
                    entry.ExpectedReadyDate, entry.SupervisorId, eligible);
                if (!ok)
                {
                    tx.Rollback();
                    return (false, error, 0);
                }

                // Polyhouse is optional; when given it must belong to the
                // batch's Area -- never trust a posted PolyhouseId alone.
                // Reuses the exact same cross-check every other Area ->
                // Polyhouse cascade in this app already uses.
                if (entry.PolyhouseId is > 0)
                {
                    var phCmd = new SqlCommand("SELECT AreaId FROM dbo.Polyhouses WHERE Id = @PolyhouseId", conn, tx);
                    phCmd.Parameters.AddWithValue("@PolyhouseId", entry.PolyhouseId.Value);
                    var phAreaObj = await phCmd.ExecuteScalarAsync();
                    if (phAreaObj == null)
                    {
                        tx.Rollback();
                        return (false, "Selected Polyhouse does not exist.", 0);
                    }
                    var polyhouseAreaId = phAreaObj == DBNull.Value ? (int?)null : (int)phAreaObj;
                    var (locationOk, _, _, locationError) = DirectSowingRules.ResolveGrowingLocation(
                        entry.AreaId, entry.AreaId, entry.PolyhouseId, polyhouseAreaId);
                    if (!locationOk)
                    {
                        tx.Rollback();
                        return (false, locationError, 0);
                    }
                }

                var poolId = await _emptyPotRepo.GetOrCreateLockedAsync(conn, tx, entry.PotSize!, entry.AreaId, entry.CreatedBy);
                var code = await _batchNumberRepo.GetNextBatchNumberAsync(conn, tx, "PB", entry.ProductionStartDate.Year);

                var insert = new SqlCommand(@"
INSERT INTO dbo.PotProductionBatches
(BatchCode, SourceCuttingStockId, SpeciesId, AreaId, PolyhouseId, PotSize, EmptyPotInventoryId, CuttingAllocated, ProductionStartDate, ExpectedReadyDate,
 SupervisorId, Status, Remarks, CreatedById, CreatedBy" + (withToken ? ", SubmissionToken" : "") + @")
VALUES
(@Code, @SourceCuttingStockId, @SpeciesId, @AreaId, @PolyhouseId, @PotSize, @PoolId, @Allocated, @Start, @Expected, @SupervisorId, N'InProduction', @Remarks, @CreatedById, @CreatedBy" + (withToken ? ", @Token" : "") + @");
SELECT CAST(SCOPE_IDENTITY() AS INT);", conn, tx);
                insert.Parameters.AddWithValue("@Token", (object?)entry.SubmissionToken ?? DBNull.Value);
                insert.Parameters.AddWithValue("@Code", code);
                insert.Parameters.AddWithValue("@SourceCuttingStockId", entry.SourceCuttingStockId.Value);
                insert.Parameters.AddWithValue("@SpeciesId", speciesId);
                insert.Parameters.AddWithValue("@AreaId", entry.AreaId);
                insert.Parameters.AddWithValue("@PolyhouseId", (object?)entry.PolyhouseId ?? DBNull.Value);
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
                    conn, tx, entry.SourceCuttingStockId.Value, -entry.CuttingAllocated, "Potted", "PotProductionBatch", newId, userId, $"Allocated to pot batch {code}");
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
            catch (SqlException ex) when (ex.Number is 2601 or 2627 && entry.SubmissionToken.HasValue)
            {
                try { tx.Rollback(); } catch { }
                var started = await StartedByTokenAsync(entry.SubmissionToken);
                return started.HasValue ? (true, AlreadyStartedMessage, started.Value) : (false, DbErrorMessage.For(ex), 0);
            }
            catch (Exception ex)
            {
                try { tx.Rollback(); } catch { }
                return (false, DbErrorMessage.For(ex), 0);
            }
        }

        // 2026-10-16: "Main Office Seedlings" as a pot production source -- Ready Stock that came to a Mother Plant Area
        // through a Ready Stock transfer FROM A MAIN OFFICE AREA with the purpose "Pot Production" (the received batch, or a
        // batch derived from it), now in an active Mother Plant Area. Lineage from the recorded transfer (columns, never
        // remarks text). @R NULL = every such batch; otherwise only that one (the create re-checks it under lock).
        private const string MainOfficeSeedlingsSql = @"
WITH pot AS (
    SELECT rs.Id, t.Id AS TransferId, 0 AS Depth
    FROM dbo.ReadyStock rs
    JOIN dbo.InternalTransfers t ON t.Id = rs.OriginReferenceId AND t.StockType = N'MotherPlantTransfer' AND t.TransferPurpose = N'PotProduction'
    JOIN dbo.Area sa ON sa.Id = t.SourceAreaId AND sa.AreaType = N'MainOffice'
    WHERE rs.OriginType = N'Transfer'
    UNION ALL
    SELECT c.Id, p.TransferId, p.Depth + 1 FROM dbo.ReadyStock c JOIN pot p ON c.ParentReadyStockId = p.Id WHERE p.Depth < 20)
SELECT rs.Id, rs.AreaId, a.Name, rs.SpeciesId, RTRIM(ps.Name), pt.Name, rs.BatchNo, rs.CavityType,
       rs.Quantity - rs.ReservedQuantity - rs.DispatchedQuantity - rs.InTransitQuantity AS Available,
       t.TransferCode, src.BatchNo AS SourceBatchNo, sa.Name AS SourceAreaName, t.TransferDate,
       ISNULL(rs.FirstConfirmationDate, rs.CreatedDate) AS InAreaSinceUtc, rs.SowingDate
FROM (SELECT Id, MIN(TransferId) AS TransferId FROM pot GROUP BY Id) p
JOIN dbo.ReadyStock rs ON rs.Id = p.Id
JOIN dbo.Area a ON a.Id = rs.AreaId AND a.AreaType = N'MotherPlant' AND a.IsActive = 1
JOIN dbo.PlantSpecies ps ON ps.Id = rs.SpeciesId
JOIN dbo.PlantTypes pt ON pt.Id = ps.PlantTypeId
JOIN dbo.InternalTransfers t ON t.Id = p.TransferId
JOIN dbo.Area sa ON sa.Id = t.SourceAreaId
LEFT JOIN dbo.ReadyStock src ON src.Id = t.SourceReadyStockId
WHERE (@R IS NULL OR rs.Id = @R)";

        public sealed record SeedlingSource(int ReadyStockId, int AreaId, string AreaName, int SpeciesId, string SpeciesName, string PlantTypeName,
            string BatchNo, string CavityType, decimal Available, string TransferCode, string? SourceBatchNo, string SourceAreaName,
            DateTime TransferDate, DateTime InAreaSinceUtc, DateTime SowingDate);

        // The Main Office seedling batches a user may pot: in the given Areas (null = every Area), with plants available.
        public async Task<List<SeedlingSource>> GetMainOfficeSeedlingSourcesAsync(IReadOnlyCollection<int>? areaIds)
        {
            var list = new List<SeedlingSource>();
            if (areaIds != null && areaIds.Count == 0) return list;
            using var conn = _dbHelper.GetConnection();
            await conn.OpenAsync();
            if (!await PotBatchSchema.ReadyStockSourceAsync(conn)) return list;
            using var cmd = new SqlCommand(MainOfficeSeedlingsSql + @"
  AND rs.Quantity - rs.ReservedQuantity - rs.DispatchedQuantity - rs.InTransitQuantity > 0
  AND (@Areas IS NULL OR rs.AreaId IN (SELECT CAST(value AS INT) FROM STRING_SPLIT(@Areas, ',')))
ORDER BY a.Name, RTRIM(ps.Name), rs.Id
OPTION (MAXRECURSION 100)", conn);
            cmd.Parameters.AddWithValue("@R", DBNull.Value);
            cmd.Parameters.AddWithValue("@Areas", areaIds == null ? DBNull.Value : string.Join(",", areaIds));
            using var r = await cmd.ExecuteReaderAsync();
            while (await r.ReadAsync())
                list.Add(new SeedlingSource(r.GetInt32(0), r.GetInt32(1), r.GetString(2), r.GetInt32(3), r.GetString(4), r.GetString(5),
                    r.GetString(6), r.GetString(7), r.GetDecimal(8), r.GetString(9), r.IsDBNull(10) ? null : r.GetString(10), r.GetString(11),
                    r.GetDateTime(12), r.GetDateTime(13), r.GetDateTime(14)));
            return list;
        }

        // Inside the caller's transaction: is this batch a Main Office seedling source (see MainOfficeSeedlingsSql)?
        private static async Task<bool> IsMainOfficeSeedlingSourceAsync(SqlConnection conn, SqlTransaction tx, int readyStockId)
        {
            using var cmd = new SqlCommand(MainOfficeSeedlingsSql + " OPTION (MAXRECURSION 100)", conn, tx);
            cmd.Parameters.AddWithValue("@R", readyStockId);
            return await cmd.ExecuteScalarAsync() is int;   // the batch's own row, or nothing
        }

        public const string NotMainOfficeSeedlingsMessage =
            "Only seedlings received from Main Office Ready Stock for the purpose \"Pot Production\" can start a pot batch.";

        public const string AlreadyStartedMessage = "This form was already saved -- the pot batch was started once; nothing was taken twice.";

        // 2026-10-16: the batch an earlier submit of the same start form created (token schema applied; call AFTER the
        // source row is locked, so two submits of one form run one after the other and the second finds the first).
        private static async Task<int?> StartedByTokenAsync(SqlConnection conn, SqlTransaction tx, Guid? token)
        {
            if (token is not Guid t || t == Guid.Empty || !await WorkflowCorrectionsSchema.PotTokenAsync(conn, tx))
                return null;
            using var cmd = new SqlCommand("SELECT Id FROM dbo.PotProductionBatches WITH (UPDLOCK, HOLDLOCK) WHERE SubmissionToken = @T", conn, tx);
            cmd.Parameters.AddWithValue("@T", t);
            return await cmd.ExecuteScalarAsync() as int?;
        }

        private async Task<int?> StartedByTokenAsync(Guid? token)
        {
            using var conn = _dbHelper.GetConnection();
            await conn.OpenAsync();
            using var tx = conn.BeginTransaction();
            var id = await StartedByTokenAsync(conn, tx, token);
            tx.Commit();
            return id;
        }

        // 2026-10-15: start a pot batch from SEEDLINGS of a Ready Stock batch received in a Mother Plant Area for the
        // purpose "Pot Production" (2026-10-14 transfer purpose, any transfer in the batch's lineage). The pots are
        // that Area's own empty pots; the seedlings leave the Ready Stock batch now ('PotProductionOut', dated the
        // production start). canUseArea: may this user work in the batch's Area (Mother Plant work is Area-scoped).
        public async Task<(bool Success, string? Message, int Id)> CreateFromReadyStockAsync(PotProductionBatch entry, int userId, Func<int, bool> canUseArea)
        {
            if (entry.SourceReadyStockId is not > 0)
                return (false, "Choose the received Ready Stock batch.", 0);
            using var conn = _dbHelper.GetConnection();
            await conn.OpenAsync();
            if (!await PotBatchSchema.ReadyStockSourceAsync(conn))
                return (false, PotBatchSchema.MigrationMessage, 0);
            using var tx = conn.BeginTransaction();
            try
            {
                var rsCmd = new SqlCommand(@"
SELECT rs.SpeciesId, rs.AreaId, a.AreaType, a.IsActive, rs.BatchNo,
       rs.Quantity - rs.ReservedQuantity - rs.DispatchedQuantity - rs.InTransitQuantity,
       ISNULL(rs.FirstConfirmationDate, rs.CreatedDate)
FROM dbo.ReadyStock rs WITH (UPDLOCK, HOLDLOCK)
JOIN dbo.Area a ON a.Id = rs.AreaId
WHERE rs.Id = @Id", conn, tx);
                rsCmd.Parameters.AddWithValue("@Id", entry.SourceReadyStockId.Value);
                int speciesId, rsAreaId; string? areaType, batchNo; bool areaActive; decimal available; DateTime availableFromUtc;
                using (var r = await rsCmd.ExecuteReaderAsync())
                {
                    if (!await r.ReadAsync())
                    {
                        r.Close();
                        tx.Rollback();
                        return (false, "Ready Stock batch not found.", 0);
                    }
                    speciesId = r.GetInt32(0);
                    rsAreaId = r.GetInt32(1);
                    areaType = r.IsDBNull(2) ? null : r.GetString(2);
                    areaActive = r.GetBoolean(3);
                    batchNo = r.IsDBNull(4) ? null : r.GetString(4);
                    available = r.GetDecimal(5);
                    availableFromUtc = r.GetDateTime(6);
                }
                if (!canUseArea(rsAreaId))
                {
                    tx.Rollback();
                    return (false, "You are not authorized to use Ready Stock of this Area.", 0);
                }
                if (await StartedByTokenAsync(conn, tx, entry.SubmissionToken) is int startedSeedlings)
                {
                    tx.Rollback();
                    entry.Id = startedSeedlings;
                    return (true, AlreadyStartedMessage, startedSeedlings);
                }
                var withToken = entry.SubmissionToken.HasValue && await WorkflowCorrectionsSchema.PotTokenAsync(conn, tx);
                if (areaType != MotherPlantAreaSchema.MotherPlantAreaType || !areaActive)
                {
                    tx.Rollback();
                    return (false, "Pot Production from Ready Stock is done in an active Mother Plant Area.", 0);
                }
                if (await MotherPlantTransferRepository.ReceivedBatchPurposeAsync(conn, tx, entry.SourceReadyStockId.Value) != MotherPlantTransferRules.PurposePotProduction)
                {
                    tx.Rollback();
                    return (false, NotForPotProductionMessage, 0);
                }
                // 2026-10-16: and it came from MAIN OFFICE Ready Stock (the recorded transfer's source Area), never another Area.
                if (!await IsMainOfficeSeedlingSourceAsync(conn, tx, entry.SourceReadyStockId.Value))
                {
                    tx.Rollback();
                    return (false, NotMainOfficeSeedlingsMessage, 0);
                }
                // the pots and the batch are in the seedlings' own Area -- never a posted one
                entry.AreaId = rsAreaId;
                if (entry.ProductionStartDate.Date > AppClock.Today)
                {
                    tx.Rollback();
                    return (false, "The production start date cannot be in the future.", 0);
                }
                var availableFromIndia = AppClock.ToIndiaTime(availableFromUtc).Date;
                if (entry.ProductionStartDate.Date < availableFromIndia)
                {
                    tx.Rollback();
                    return (false, $"This Ready Stock batch is in the Area from {availableFromIndia:dd/MM/yyyy}; start the pot batch on or after that date.", 0);
                }

                var sizeCmd = new SqlCommand("SELECT COUNT(*) FROM dbo.PotSizes WHERE Name = @Name AND IsActive = 1", conn, tx);
                sizeCmd.Parameters.AddWithValue("@Name", entry.PotSize ?? "");
                if ((int)(await sizeCmd.ExecuteScalarAsync())! == 0)
                {
                    tx.Rollback();
                    return (false, "Choose a pot size from the list.", 0);
                }

                var eligible = (await _userRoleRepo.GetReadyConfirmersAsync(rsAreaId, conn, tx)).Select(u => u.EmployeeID).ToList();
                var (ok, error) = PotBatchRules.ValidateCreate(entry.CuttingAllocated, available, entry.ProductionStartDate,
                    entry.ExpectedReadyDate, entry.SupervisorId, eligible, fromReadyStock: true);
                if (!ok)
                {
                    tx.Rollback();
                    return (false, error, 0);
                }

                var poolId = await _emptyPotRepo.GetOrCreateLockedAsync(conn, tx, entry.PotSize!, rsAreaId, entry.CreatedBy);
                var code = await _batchNumberRepo.GetNextBatchNumberAsync(conn, tx, "PB", entry.ProductionStartDate.Year);
                var insert = new SqlCommand(@"
INSERT INTO dbo.PotProductionBatches
(BatchCode, SourceCuttingStockId, SourceReadyStockId, SpeciesId, AreaId, PolyhouseId, PotSize, EmptyPotInventoryId, CuttingAllocated,
 ProductionStartDate, ExpectedReadyDate, SupervisorId, Status, Remarks, CreatedById, CreatedBy" + (withToken ? ", SubmissionToken" : "") + @")
VALUES
(@Code, NULL, @Source, @SpeciesId, @AreaId, NULL, @PotSize, @PoolId, @Allocated, @Start, @Expected, @SupervisorId, N'InProduction', @Remarks, @CreatedById, @CreatedBy" + (withToken ? ", @Token" : "") + @");
SELECT CAST(SCOPE_IDENTITY() AS INT);", conn, tx);
                insert.Parameters.AddWithValue("@Token", (object?)entry.SubmissionToken ?? DBNull.Value);
                insert.Parameters.AddWithValue("@Code", code);
                insert.Parameters.AddWithValue("@Source", entry.SourceReadyStockId.Value);
                insert.Parameters.AddWithValue("@SpeciesId", speciesId);
                insert.Parameters.AddWithValue("@AreaId", rsAreaId);
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

                var (rsOk, rsMessage) = await _readyStockRepo.RecordMovementsAsync(conn, tx, entry.SourceReadyStockId.Value,
                    new List<(string, decimal, string?)> { (ReadyStockAllocationType, -entry.CuttingAllocated, $"Allocated to pot batch {code}") },
                    ReferenceType, newId, userId, entry.ProductionStartDate.Date);
                if (!rsOk)
                {
                    tx.Rollback();
                    return (false, rsMessage, 0);
                }

                tx.Commit();
                entry.Id = newId;
                entry.BatchCode = code;
                entry.SpeciesId = speciesId;
                entry.EmptyPotInventoryId = poolId;
                return (true, $"Pot batch {code} started: {QuantityFormat.Qty(entry.CuttingAllocated)} seedlings of Ready Stock batch {batchNo} allocated.", newId);
            }
            catch (SqlException ex) when (ex.Number is 2601 or 2627 && entry.SubmissionToken.HasValue)
            {
                try { tx.Rollback(); } catch { }
                var started = await StartedByTokenAsync(entry.SubmissionToken);
                return started.HasValue ? (true, AlreadyStartedMessage, started.Value) : (false, DbErrorMessage.For(ex), 0);
            }
            catch (Exception ex)
            {
                try { tx.Rollback(); } catch { }
                return (false, DbErrorMessage.For(ex), 0);
            }
        }

        // Plants back to where the batch took them from: Cutting Stock, or (2026-10-15) the Ready Stock batch.
        private async Task<(bool Ok, string? Message)> ReturnToSourceAsync(SqlConnection conn, SqlTransaction tx, int? cuttingStockId, int? readyStockId,
            decimal quantity, int batchId, int userId, string remarks)
        {
            if (readyStockId.HasValue)
                return await _readyStockRepo.RecordMovementsAsync(conn, tx, readyStockId.Value,
                    new List<(string, decimal, string?)> { ("ReversalReturn", quantity, remarks) }, ReferenceType, batchId, userId);
            return await _cuttingStockRepo.RecordTransactionAsync(conn, tx, cuttingStockId!.Value, quantity, "ReversalReturn", ReferenceType, batchId, userId, remarks);
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
                return (false, DbErrorMessage.For(ex));
            }
        }

        // READY: any active user assigned to the batch Area (the page has
        // already checked the Pot Production permission; the batch creator may
        // confirm too). The actual confirming user is recorded in
        // ReadyConfirmedById with ReadyDate / ModifiedBy / ModifiedDate.
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
SELECT Status, CuttingAllocated, SpeciesId, PotSize, AreaId, EmptyPotInventoryId, SourceCuttingStockId, BatchCode, "
                    + (await PotBatchSchema.ReadyStockSourceAsync(conn, tx) ? "SourceReadyStockId" : "CAST(NULL AS INT)") + @"
FROM dbo.PotProductionBatches WITH (UPDLOCK, HOLDLOCK) WHERE Id = @Id", conn, tx);
                bCmd.Parameters.AddWithValue("@Id", batchId);
                string status, potSize, code; decimal allocated; int speciesId, areaId, poolId; int? cuttingStockId, readyStockId;
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
                    speciesId = r.GetInt32(2);
                    potSize = r.GetString(3);
                    areaId = r.GetInt32(4);
                    poolId = r.GetInt32(5);
                    cuttingStockId = r.IsDBNull(6) ? null : r.GetInt32(6);
                    code = r.GetString(7);
                    readyStockId = r.IsDBNull(8) ? null : r.GetInt32(8);
                }
                // The batch Area is the boundary: the confirming user must be
                // active and assigned to THIS batch's Area, verified against the
                // live role assignments (same test as TR_PotBatches_Update).
                var grants = await _userRoleRepo.GetReadyConfirmerGrantsAsync(conn, tx);
                if (!PotBatchRules.IsActiveAssignedToArea(grants, userId, areaId))
                {
                    tx.Rollback();
                    return (false, "You are not authorized to confirm READY for this batch's Area: you must be an active user assigned to it.");
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
                    var (cutOk, cutMessage) = await ReturnToSourceAsync(conn, tx, cuttingStockId, readyStockId, unused, batchId, userId,
                        $"Unused {(readyStockId.HasValue ? "seedlings" : "cuttings")} of pot batch {code}");
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
                return (false, DbErrorMessage.For(ex));
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
SELECT Status, CuttingAllocated, SourceCuttingStockId, CreatedById, SupervisorId, BatchCode, "
                    + (await PotBatchSchema.ReadyStockSourceAsync(conn, tx) ? "SourceReadyStockId" : "CAST(NULL AS INT)") + @"
FROM dbo.PotProductionBatches WITH (UPDLOCK, HOLDLOCK) WHERE Id = @Id", conn, tx);
                bCmd.Parameters.AddWithValue("@Id", batchId);
                string status, code; decimal allocated; int createdById, supervisorId; int? cuttingStockId, readyStockId;
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
                    cuttingStockId = r.IsDBNull(2) ? null : r.GetInt32(2);
                    createdById = r.GetInt32(3);
                    supervisorId = r.GetInt32(4);
                    code = r.GetString(5);
                    readyStockId = r.IsDBNull(6) ? null : r.GetInt32(6);
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

                var (ok, message) = await ReturnToSourceAsync(conn, tx, cuttingStockId, readyStockId, allocated, batchId, userId, $"Cancelled pot batch {code}");
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
                return (false, DbErrorMessage.For(ex));
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
            SourceCuttingStockId = r.IsDBNull(r.GetOrdinal("SourceCuttingStockId")) ? null : r.GetInt32(r.GetOrdinal("SourceCuttingStockId")),
            SourceReadyStockId = r.IsDBNull(r.GetOrdinal("SourceReadyStockId")) ? null : r.GetInt32(r.GetOrdinal("SourceReadyStockId")),
            SourceReadyStockBatchNo = r.IsDBNull(r.GetOrdinal("SourceReadyStockBatchNo")) ? null : r.GetString(r.GetOrdinal("SourceReadyStockBatchNo")),
            SpeciesId = r.GetInt32(r.GetOrdinal("SpeciesId")),
            SpeciesName = r.GetString(r.GetOrdinal("SpeciesName")).Trim(),
            Color = r.IsDBNull(r.GetOrdinal("Color")) ? null : r.GetString(r.GetOrdinal("Color")),
            PlantTypeName = r.GetString(r.GetOrdinal("PlantTypeName")),
            AreaId = r.GetInt32(r.GetOrdinal("AreaId")),
            AreaName = r.GetString(r.GetOrdinal("AreaName")),
            SourceAreaName = r.IsDBNull(r.GetOrdinal("SourceAreaName")) ? null : r.GetString(r.GetOrdinal("SourceAreaName")),
            PolyhouseId = r.IsDBNull(r.GetOrdinal("PolyhouseId")) ? null : r.GetInt32(r.GetOrdinal("PolyhouseId")),
            PolyhouseName = r.IsDBNull(r.GetOrdinal("PolyhouseName")) ? null : r.GetString(r.GetOrdinal("PolyhouseName")),
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
