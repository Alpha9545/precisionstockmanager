using Microsoft.Data.SqlClient;
using PlantStockManager.Models;

namespace PlantStockManager.Data
{
    // Orchestrates Phase 8's Internal Transfer: moves a quantity of
    // EITHER Empty Pot stock OR Potted Plant stock from one Area's pool
    // to another Area's pool of the same Pot Size (and Species, for
    // Potted Plant), reusing the SAME dedicated ledgers Phase 7 already
    // created for each stock table. A Transfer never bypasses
    // RecordTransactionAsync -- the source decrement and the
    // destination increase are each written together with their own
    // ledger row, inside ONE database transaction, so a failure at
    // either step rolls back everything (including the header row).
    public class InternalTransferRepository
    {
        private readonly DatabaseHelper _dbHelper;
        private readonly BatchNumberRepository _batchNumberRepo;
        private readonly EmptyPotInventoryRepository _emptyPotInventoryRepo;
        private readonly PottedPlantStockRepository _pottedPlantStockRepo;
        private readonly CuttingStockRepository _cuttingStockRepo;
        // Phase 18 (Phase C): needed only to re-verify, server-side, that a
        // 'MainOfficeIssue' transfer's source is a genuine Main Office Area
        // and its destination is an active, Growing-Partner-linked Area --
        // never trusted from a dropdown or a POSTed Id alone. Already
        // registered in DI (Program.cs, Phase 1), so no DI change is
        // needed here.
        private readonly AreaRepository _areaRepo;

        public InternalTransferRepository(
            DatabaseHelper dbHelper,
            BatchNumberRepository batchNumberRepo,
            EmptyPotInventoryRepository emptyPotInventoryRepo,
            PottedPlantStockRepository pottedPlantStockRepo,
            CuttingStockRepository cuttingStockRepo,
            AreaRepository areaRepo)
        {
            _dbHelper = dbHelper;
            _batchNumberRepo = batchNumberRepo;
            _emptyPotInventoryRepo = emptyPotInventoryRepo;
            _pottedPlantStockRepo = pottedPlantStockRepo;
            _cuttingStockRepo = cuttingStockRepo;
            _areaRepo = areaRepo;
        }

        private const string BaseSelect = @"
SELECT
    t.Id, t.TransferCode, t.StockType,
    t.SourceEmptyPotInventoryId, t.SourcePottedPlantStockId, t.SourceCuttingStockId,
    t.SourceAreaId, sa.Name AS SourceAreaName, gpSrc.Name AS SourceGrowingPartnerName,
    t.DestinationAreaId, da.Name AS DestinationAreaName,
    t.PendingConfirmationAreaId, pca.Name AS PendingConfirmationAreaName,
    t.Quantity, t.Status,
    t.ConfirmedQuantity, t.ConfirmedBy, cb.Name AS ConfirmedByName, t.ConfirmedDate, t.DiscrepancyReason,
    t.ResponsiblePersonId, r.Name AS ResponsiblePersonName,
    t.SupervisorId, sup.Name AS SupervisorName,
    t.Remarks, t.CreatedDate, t.CreatedBy, t.ModifiedDate, t.ModifiedBy,
    COALESCE(epi.PotSize, pps.PotSize) AS PotSize,
    COALESCE(ps.Name, cps.Name) AS SpeciesName, COALESCE(pt.Name, cpt.Name) AS PlantTypeName,
    ctp.DestinationSupervisorId AS TransplantDestinationSupervisorId,
    dsup.Name AS TransplantDestinationSupervisorName,
    ctp.TransplantDate, ctp.Remarks AS TransplantRemarks
FROM dbo.InternalTransfers t
LEFT JOIN dbo.EmptyPotInventory epi ON t.SourceEmptyPotInventoryId = epi.Id
LEFT JOIN dbo.PottedPlantStock pps ON t.SourcePottedPlantStockId = pps.Id
LEFT JOIN dbo.PlantSpecies ps ON pps.SpeciesId = ps.Id
LEFT JOIN dbo.PlantTypes pt ON ps.PlantTypeId = pt.Id
LEFT JOIN dbo.CuttingStock cs ON t.SourceCuttingStockId = cs.Id
LEFT JOIN dbo.PlantSpecies cps ON cs.SpeciesId = cps.Id
LEFT JOIN dbo.PlantTypes cpt ON cps.PlantTypeId = cpt.Id
LEFT JOIN dbo.Area sa ON t.SourceAreaId = sa.Id
LEFT JOIN dbo.GrowingPartners gpSrc ON sa.GrowingPartnerId = gpSrc.Id
LEFT JOIN dbo.Area da ON t.DestinationAreaId = da.Id
LEFT JOIN dbo.Area pca ON t.PendingConfirmationAreaId = pca.Id
LEFT JOIN dbo.IMSUsers r ON t.ResponsiblePersonId = r.Id
LEFT JOIN dbo.IMSUsers sup ON t.SupervisorId = sup.Id
LEFT JOIN dbo.IMSUsers cb ON t.ConfirmedBy = cb.Id
LEFT JOIN dbo.CuttingTransplants ctp ON t.Id = ctp.InternalTransferId
LEFT JOIN dbo.IMSUsers dsup ON ctp.DestinationSupervisorId = dsup.Id";

        public async Task<List<InternalTransfer>> GetAllAsync()
        {
            var list = new List<InternalTransfer>();
            using var conn = _dbHelper.GetConnection();
            await conn.OpenAsync();

            var sql = BaseSelect + " ORDER BY t.CreatedDate DESC";
            using var cmd = new SqlCommand(sql, conn);
            using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                list.Add(Map(reader));
            }
            return list;
        }

        public async Task<InternalTransfer?> GetByIdAsync(int id)
        {
            using var conn = _dbHelper.GetConnection();
            await conn.OpenAsync();

            var sql = BaseSelect + " WHERE t.Id = @Id";
            using var cmd = new SqlCommand(sql, conn);
            cmd.Parameters.AddWithValue("@Id", id);
            using var reader = await cmd.ExecuteReaderAsync();
            if (await reader.ReadAsync())
            {
                return Map(reader);
            }
            return null;
        }

        // Every transfer where the given Area is either the sender or
        // the destination -- feeds "My Transactions" on the Mother
        // Plant/Kunjir workflow pages.
        public async Task<List<InternalTransfer>> GetByAreaAsync(int areaId)
        {
            var list = new List<InternalTransfer>();
            using var conn = _dbHelper.GetConnection();
            await conn.OpenAsync();

            var sql = BaseSelect + " WHERE t.SourceAreaId = @AreaId OR t.DestinationAreaId = @AreaId ORDER BY t.CreatedDate DESC";
            using var cmd = new SqlCommand(sql, conn);
            cmd.Parameters.AddWithValue("@AreaId", areaId);
            using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                list.Add(Map(reader));
            }
            return list;
        }

        // Cutting-type transfers still awaiting Main Office's
        // confirm/reject decision -- feeds Main Office's confirmation
        // queue. areaId narrows to one Main Office Area; null returns
        // every pending Cutting transfer regardless of which Main Office
        // Area it's addressed to.
        public async Task<List<InternalTransfer>> GetPendingConfirmationsAsync(int? pendingConfirmationAreaId = null)
        {
            var list = new List<InternalTransfer>();
            using var conn = _dbHelper.GetConnection();
            await conn.OpenAsync();

            // F1: StockType filter added. Without it this "Cutting" queue
            // also listed PendingConfirmation MainOfficeIssue /
            // GrowingPartnerToOutlet rows, and its Reject button (which
            // calls RejectAsync by id) could reject them from outside their
            // own Area-checked pages.
            var sql = BaseSelect + @"
WHERE t.Status = 'PendingConfirmation'
  AND t.StockType = 'Cutting'
  AND (@AreaId IS NULL OR t.PendingConfirmationAreaId = @AreaId)
ORDER BY t.CreatedDate ASC";
            using var cmd = new SqlCommand(sql, conn);
            cmd.Parameters.AddWithValue("@AreaId", (object?)pendingConfirmationAreaId ?? DBNull.Value);
            using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                list.Add(Map(reader));
            }
            return list;
        }

        // Cutting-type transfers whose receipt Main Office has already
        // confirmed (ConfirmedQuantity is known) but which have NOT yet
        // been routed to a destination Polyhouse -- "Pending Transplants".
        // A transfer sits here indefinitely, however long it takes someone
        // to open the Transplant screen and pick a Polyhouse/Supervisor --
        // it is never dropped and never silently counted as done.
        public async Task<List<InternalTransfer>> GetPendingTransplantsAsync(int? pendingConfirmationAreaId = null)
        {
            var list = new List<InternalTransfer>();
            using var conn = _dbHelper.GetConnection();
            await conn.OpenAsync();

            var sql = BaseSelect + @"
WHERE t.Status = 'ConfirmedAwaitingTransplant'
  AND t.StockType = 'Cutting' -- F1: same StockType scoping as above
  AND (@AreaId IS NULL OR t.PendingConfirmationAreaId = @AreaId)
ORDER BY t.ConfirmedDate ASC";
            using var cmd = new SqlCommand(sql, conn);
            cmd.Parameters.AddWithValue("@AreaId", (object?)pendingConfirmationAreaId ?? DBNull.Value);
            using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                list.Add(Map(reader));
            }
            return list;
        }

        // MainOfficeIssue-type transfers still awaiting the Growing
        // Partner Area's confirm/reject decision -- feeds that Area's
        // receiving queue. Mirrors GetPendingConfirmationsAsync's shape
        // exactly, but filters by DestinationAreaId (known up front for
        // this StockType) rather than PendingConfirmationAreaId (which
        // MainOfficeIssue rows never populate). destinationAreaId narrows
        // to one Area; null returns every pending Main Office Issue
        // regardless of destination.
        public async Task<List<InternalTransfer>> GetPendingMainOfficeIssuesAsync(int? destinationAreaId = null)
        {
            var list = new List<InternalTransfer>();
            using var conn = _dbHelper.GetConnection();
            await conn.OpenAsync();

            var sql = BaseSelect + @"
WHERE t.StockType = 'MainOfficeIssue'
  AND t.Status = 'PendingConfirmation'
  AND (@AreaId IS NULL OR t.DestinationAreaId = @AreaId)
ORDER BY t.CreatedDate ASC";
            using var cmd = new SqlCommand(sql, conn);
            cmd.Parameters.AddWithValue("@AreaId", (object?)destinationAreaId ?? DBNull.Value);
            using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                list.Add(Map(reader));
            }
            return list;
        }

        // Phase 20/Phase F: GrowingPartnerToOutlet-type transfers still
        // awaiting the Outlet Area's confirm/reject decision -- feeds that
        // Area's receiving queue. Mirrors GetPendingMainOfficeIssuesAsync's
        // shape exactly (filters by DestinationAreaId, known up front for
        // this StockType too), kept as its own method rather than widening
        // that one, so GetPendingMainOfficeIssuesAsync's own behavior for
        // Phase C stays provably untouched. destinationAreaId narrows to
        // one Outlet Area; null returns every pending GrowingPartnerToOutlet
        // transfer regardless of destination.
        public async Task<List<InternalTransfer>> GetPendingGrowingPartnerToOutletAsync(int? destinationAreaId = null)
        {
            var list = new List<InternalTransfer>();
            using var conn = _dbHelper.GetConnection();
            await conn.OpenAsync();

            var sql = BaseSelect + @"
WHERE t.StockType = 'GrowingPartnerToOutlet'
  AND t.Status = 'PendingConfirmation'
  AND (@AreaId IS NULL OR t.DestinationAreaId = @AreaId)
ORDER BY t.CreatedDate ASC";
            using var cmd = new SqlCommand(sql, conn);
            cmd.Parameters.AddWithValue("@AreaId", (object?)destinationAreaId ?? DBNull.Value);
            using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                list.Add(Map(reader));
            }
            return list;
        }

        public async Task<(bool Success, string? Message, int Id)> InsertAsync(InternalTransfer entry, int? userId)
        {
            if (entry.Quantity <= 0)
                return (false, "Quantity must be greater than zero.", 0);
            if (entry.StockType != "Cutting" && (!entry.DestinationAreaId.HasValue || entry.DestinationAreaId <= 0))
                return (false, "Destination Area is required.", 0);
            if (entry.StockType == "Cutting" && (!entry.PendingConfirmationAreaId.HasValue || entry.PendingConfirmationAreaId <= 0))
                return (false, "The Main Office Area to send this to is required.", 0);

            // Source Area is derived from the selected pool's own AreaId
            // below (never trusted from the form), so the
            // different-Areas rule is re-checked once that is known.

            using var conn = _dbHelper.GetConnection();
            await conn.OpenAsync();
            using var tx = conn.BeginTransaction();

            try
            {
                string potSize;

                if (entry.StockType == "EmptyPot")
                {
                    if (!entry.SourceEmptyPotInventoryId.HasValue)
                    {
                        tx.Rollback();
                        return (false, "Source Pot Size / Area is required.", 0);
                    }

                    // 1) Lock the source pool and derive its Pot Size and
                    // Area directly from the row -- the single source of
                    // truth for which Area it physically sits in. The
                    // caller only needs to pick WHICH pool to draw from;
                    // Source Area is never trusted from the form.
                    var srcLockCmd = new SqlCommand(
                        "SELECT PotSize, AreaId FROM dbo.EmptyPotInventory WITH (UPDLOCK, HOLDLOCK) WHERE Id = @Id",
                        conn, tx);
                    srcLockCmd.Parameters.AddWithValue("@Id", entry.SourceEmptyPotInventoryId.Value);
                    using var srcReader = await srcLockCmd.ExecuteReaderAsync();
                    if (!await srcReader.ReadAsync())
                    {
                        srcReader.Close();
                        tx.Rollback();
                        return (false, "Source Empty Pot pool not found.", 0);
                    }
                    potSize = srcReader.GetString(srcReader.GetOrdinal("PotSize"));
                    var srcAreaId = srcReader.IsDBNull(srcReader.GetOrdinal("AreaId")) ? (int?)null : srcReader.GetInt32(srcReader.GetOrdinal("AreaId"));
                    srcReader.Close();

                    if (srcAreaId == null)
                    {
                        tx.Rollback();
                        return (false, "This Empty Pot pool has no assigned Area (legacy/unassigned stock) and cannot be transferred until it is. Use Add Stock to bring in stock directly against a specific Area instead.", 0);
                    }

                    entry.PotSize = potSize;
                    entry.SourceAreaId = srcAreaId.Value;

                    if (entry.SourceAreaId == entry.DestinationAreaId)
                    {
                        tx.Rollback();
                        return (false, "Source and Destination Area must be different.", 0);
                    }

                    // 2) Insert the header row FIRST so its Id is
                    // available as ReferenceId on both ledger entries.
                    var newId = await InsertHeaderAsync(conn, tx, entry, userId);

                    // 3) Decrement the source pool.
                    var (srcSuccess, srcMessage) = await _emptyPotInventoryRepo.RecordTransactionAsync(
                        conn, tx, entry.SourceEmptyPotInventoryId.Value, -entry.Quantity, "Transfer", "InternalTransfer", newId, userId, entry.Remarks);
                    if (!srcSuccess)
                    {
                        tx.Rollback();
                        return (false, srcMessage, 0);
                    }

                    // 4) Get-or-create the destination pool (same Pot
                    // Size, Destination Area) and increment it.
                    var destId = await _emptyPotInventoryRepo.GetOrCreateLockedAsync(conn, tx, potSize, entry.DestinationAreaId, entry.CreatedBy);
                    var (destSuccess, destMessage) = await _emptyPotInventoryRepo.RecordTransactionAsync(
                        conn, tx, destId, entry.Quantity, "Transfer", "InternalTransfer", newId, userId, entry.Remarks);
                    if (!destSuccess)
                    {
                        tx.Rollback();
                        return (false, destMessage, 0);
                    }

                    tx.Commit();
                    entry.Id = newId;
                    return (true, null, newId);
                }
                else if (entry.StockType == "PottedPlant")
                {
                    if (!entry.SourcePottedPlantStockId.HasValue)
                    {
                        tx.Rollback();
                        return (false, "Source Species / Pot Size / Area is required.", 0);
                    }

                    var srcLockCmd = new SqlCommand(
                        "SELECT SpeciesId, PotSize, AreaId, EmptyPotInventoryId FROM dbo.PottedPlantStock WITH (UPDLOCK, HOLDLOCK) WHERE Id = @Id",
                        conn, tx);
                    srcLockCmd.Parameters.AddWithValue("@Id", entry.SourcePottedPlantStockId.Value);
                    using var srcReader = await srcLockCmd.ExecuteReaderAsync();
                    if (!await srcReader.ReadAsync())
                    {
                        srcReader.Close();
                        tx.Rollback();
                        return (false, "Source Potted Plant Stock pool not found.", 0);
                    }
                    var srcSpeciesId = srcReader.GetInt32(srcReader.GetOrdinal("SpeciesId"));
                    potSize = srcReader.GetString(srcReader.GetOrdinal("PotSize"));
                    var srcAreaId = srcReader.IsDBNull(srcReader.GetOrdinal("AreaId")) ? (int?)null : srcReader.GetInt32(srcReader.GetOrdinal("AreaId"));
                    srcReader.Close();

                    if (srcAreaId == null)
                    {
                        tx.Rollback();
                        return (false, "This Potted Plant Stock pool has no assigned Area (legacy/unassigned stock) and cannot be transferred until it is.", 0);
                    }

                    entry.PotSize = potSize;
                    entry.SourceAreaId = srcAreaId.Value;

                    if (entry.SourceAreaId == entry.DestinationAreaId)
                    {
                        tx.Rollback();
                        return (false, "Source and Destination Area must be different.", 0);
                    }

                    var newId = await InsertHeaderAsync(conn, tx, entry, userId);

                    var (srcSuccess, srcMessage) = await _pottedPlantStockRepo.RecordTransactionAsync(
                        conn, tx, entry.SourcePottedPlantStockId.Value, -entry.Quantity, "Transfer", "InternalTransfer", newId, userId, entry.Remarks);
                    if (!srcSuccess)
                    {
                        tx.Rollback();
                        return (false, srcMessage, 0);
                    }

                    // The destination Potted Plant Stock row still needs
                    // an EmptyPotInventoryId link (schema requirement,
                    // not a physical empty-pot movement) -- resolve or
                    // create a zero-quantity Empty Pot pool for this Pot
                    // Size in the Destination Area to link against.
                    var destEmptyPotId = await _emptyPotInventoryRepo.GetOrCreateLockedAsync(conn, tx, potSize, entry.DestinationAreaId, entry.CreatedBy);
                    var destId = await _pottedPlantStockRepo.GetOrCreateLockedAsync(conn, tx, srcSpeciesId, potSize, entry.DestinationAreaId, destEmptyPotId, entry.CreatedBy);
                    var (destSuccess, destMessage) = await _pottedPlantStockRepo.RecordTransactionAsync(
                        conn, tx, destId, entry.Quantity, "Transfer", "InternalTransfer", newId, userId, entry.Remarks);
                    if (!destSuccess)
                    {
                        tx.Rollback();
                        return (false, destMessage, 0);
                    }

                    tx.Commit();
                    entry.Id = newId;
                    return (true, null, newId);
                }
                else if (entry.StockType == "Cutting")
                {
                    if (!entry.SourceCuttingStockId.HasValue)
                    {
                        tx.Rollback();
                        return (false, "Source Species / Area is required.", 0);
                    }

                    // Reserve against AvailableQuantity (PhysicalQuantity -
                    // InTransitQuantity) rather than a bare lock -- this is
                    // what stops the same physical cuttings being sent
                    // twice while an earlier transfer of theirs is still
                    // in flight. Nothing is written to
                    // CuttingStock.PhysicalQuantity or the ledger here --
                    // only InTransitQuantity rises.
                    var (reserveSuccess, reserveMessage, srcAreaId, _) = await _cuttingStockRepo.ReserveInTransitAsync(
                        conn, tx, entry.SourceCuttingStockId.Value, entry.Quantity);
                    if (!reserveSuccess)
                    {
                        tx.Rollback();
                        return (false, reserveMessage, 0);
                    }

                    entry.SourceAreaId = srcAreaId;

                    if (entry.SourceAreaId == entry.PendingConfirmationAreaId)
                    {
                        tx.Rollback();
                        return (false, "Source Area and the Main Office Area you're sending to must be different.", 0);
                    }

                    // Cutting transfers are ALWAYS created pending -- no
                    // PhysicalQuantity/ledger changes happen here. Quantity
                    // records what was sent and is never altered; the
                    // source pool's PhysicalQuantity is only decremented
                    // once Main Office confirms the Transplant
                    // (ConfirmTransplantAsync), by whatever quantity was
                    // actually confirmed, which may differ from what was
                    // sent.
                    entry.Status = "PendingConfirmation";
                    entry.DestinationAreaId = null;

                    var newId = await InsertHeaderAsync(conn, tx, entry, userId);

                    tx.Commit();
                    entry.Id = newId;
                    return (true, null, newId);
                }
                else if (entry.StockType == "MainOfficeIssue")
                {
                    if (!entry.SourcePottedPlantStockId.HasValue)
                    {
                        tx.Rollback();
                        return (false, "Source Main Office stock pool is required.", 0);
                    }

                    // Reserve against "available to issue" (Physical -
                    // Reserved - InTransit) rather than a bare lock -- this
                    // is what stops the same physical stock being issued
                    // twice while an earlier issue of theirs is still
                    // awaiting the Growing Partner's confirmation. Nothing
                    // is written to PottedPlantStock.PhysicalQuantity or
                    // the ledger here -- only InTransitQuantity rises.
                    var (reserveSuccess, reserveMessage, srcAreaId, _) = await _pottedPlantStockRepo.ReserveInTransitAsync(
                        conn, tx, entry.SourcePottedPlantStockId.Value, entry.Quantity);
                    if (!reserveSuccess)
                    {
                        tx.Rollback();
                        return (false, reserveMessage, 0);
                    }
                    if (!srcAreaId.HasValue)
                    {
                        tx.Rollback();
                        return (false, "This Potted Plant Stock pool has no assigned Area (legacy/unassigned stock) and cannot be issued from until it is.", 0);
                    }

                    // Force the source to a genuine Main Office pool,
                    // server-side -- never trust that the caller only
                    // offered Main Office pools in a dropdown.
                    var srcArea = await _areaRepo.GetAreaById(srcAreaId.Value);
                    if (srcArea == null || srcArea.AreaType != "MainOffice")
                    {
                        tx.Rollback();
                        return (false, "The selected source stock is not held at a Main Office Area.", 0);
                    }

                    // Force the destination to an active, Growing-
                    // Partner-linked Area, server-side -- this is what
                    // defeats a tampered POST/URL Area Id that was never
                    // offered in the destination dropdown at all.
                    var destArea = entry.DestinationAreaId.HasValue ? await _areaRepo.GetAreaById(entry.DestinationAreaId.Value) : null;
                    if (destArea == null || !destArea.IsActive || !destArea.GrowingPartnerId.HasValue)
                    {
                        tx.Rollback();
                        return (false, "The selected Destination Area is not an active Growing Partner Area.", 0);
                    }

                    entry.SourceAreaId = srcAreaId.Value;

                    if (entry.SourceAreaId == entry.DestinationAreaId)
                    {
                        tx.Rollback();
                        return (false, "Source and Destination Area must be different.", 0);
                    }

                    // MainOfficeIssue transfers are ALWAYS created
                    // pending -- no PhysicalQuantity/ledger changes happen
                    // here. Quantity records what was sent and is never
                    // altered; the source pool's PhysicalQuantity is only
                    // decremented once the Growing Partner confirms
                    // receipt (ConfirmMainOfficeIssueAsync), by whatever
                    // quantity was actually confirmed, which may differ
                    // from what was sent.
                    entry.Status = "PendingConfirmation";

                    var newId = await InsertHeaderAsync(conn, tx, entry, userId);

                    tx.Commit();
                    entry.Id = newId;
                    return (true, null, newId);
                }
                else if (entry.StockType == "GrowingPartnerToOutlet")
                {
                    if (!entry.SourcePottedPlantStockId.HasValue)
                    {
                        tx.Rollback();
                        return (false, "Source Growing Partner stock pool is required.", 0);
                    }

                    // Reserve against "available to send" (Physical -
                    // Reserved - InTransit) -- the exact same generic
                    // mechanism MainOfficeIssue already uses (Phase 18),
                    // reused as-is, not a second in-transit column/system.
                    // Nothing is written to PottedPlantStock.PhysicalQuantity
                    // or the ledger here -- only InTransitQuantity rises.
                    var (reserveSuccess, reserveMessage, srcAreaId, _) = await _pottedPlantStockRepo.ReserveInTransitAsync(
                        conn, tx, entry.SourcePottedPlantStockId.Value, entry.Quantity);
                    if (!reserveSuccess)
                    {
                        tx.Rollback();
                        return (false, reserveMessage, 0);
                    }
                    if (!srcAreaId.HasValue)
                    {
                        tx.Rollback();
                        return (false, "This Potted Plant Stock pool has no assigned Area (legacy/unassigned stock) and cannot be sent to an Outlet until it is.", 0);
                    }

                    // Force the source to an active, Growing-Partner-linked
                    // Area, server-side -- the mirror image of
                    // MainOfficeIssue's own source check. Never trust that
                    // the caller only offered Growing Partner pools in a
                    // dropdown, and never trust a posted AreaId alone (the
                    // page layer separately re-derives this from the
                    // fetched stock row before ever calling here -- see
                    // GrowingPartnerToOutlet/Create.cshtml.cs).
                    var srcArea = await _areaRepo.GetAreaById(srcAreaId.Value);
                    if (srcArea == null || !srcArea.IsActive || !srcArea.GrowingPartnerId.HasValue)
                    {
                        tx.Rollback();
                        return (false, "The selected source stock is not held at an active Growing Partner Area.", 0);
                    }

                    // Force the destination to an active Outlet Area,
                    // server-side -- this is what defeats a tampered
                    // POST/URL Area Id for a non-Outlet destination (e.g.
                    // another Growing Partner's Area, or Main Office) that
                    // was never offered in the destination dropdown at all.
                    var destArea = entry.DestinationAreaId.HasValue ? await _areaRepo.GetAreaById(entry.DestinationAreaId.Value) : null;
                    if (destArea == null || !destArea.IsActive || destArea.AreaType != "Outlet")
                    {
                        tx.Rollback();
                        return (false, "The selected Destination Area is not an active Outlet Area.", 0);
                    }

                    entry.SourceAreaId = srcAreaId.Value;

                    if (entry.SourceAreaId == entry.DestinationAreaId)
                    {
                        tx.Rollback();
                        return (false, "Source and Destination Area must be different.", 0);
                    }

                    // GrowingPartnerToOutlet transfers are ALWAYS created
                    // pending -- no PhysicalQuantity/ledger changes happen
                    // here, exactly like MainOfficeIssue. Quantity records
                    // what was sent and is never altered; the source
                    // pool's PhysicalQuantity is only decremented once the
                    // Outlet confirms receipt (ConfirmGrowingPartnerToOutletAsync),
                    // by whatever quantity was actually confirmed, which
                    // may differ from what was sent.
                    entry.Status = "PendingConfirmation";

                    var newId = await InsertHeaderAsync(conn, tx, entry, userId);

                    tx.Commit();
                    entry.Id = newId;
                    return (true, null, newId);
                }
                else
                {
                    tx.Rollback();
                    return (false, "Stock Type must be 'EmptyPot', 'PottedPlant', 'Cutting', 'MainOfficeIssue', or 'GrowingPartnerToOutlet'.", 0);
                }
            }
            catch (Exception ex)
            {
                tx.Rollback();
                return (false, ex.Message, 0);
            }
        }

        private async Task<int> InsertHeaderAsync(SqlConnection conn, SqlTransaction tx, InternalTransfer entry, int? userId)
        {
            var transferCode = await _batchNumberRepo.GetNextBatchNumberAsync(conn, tx, "TR", DateTime.Today.Year);
            var status = string.IsNullOrWhiteSpace(entry.Status) ? "Completed" : entry.Status;

            const string insertSql = @"
INSERT INTO dbo.InternalTransfers
(TransferCode, StockType, SourceEmptyPotInventoryId, SourcePottedPlantStockId, SourceCuttingStockId, SourceAreaId,
 DestinationAreaId, PendingConfirmationAreaId, Quantity, Status, ResponsiblePersonId, SupervisorId, Remarks, CreatedDate, CreatedBy)
VALUES
(@TransferCode, @StockType, @SourceEmptyPotInventoryId, @SourcePottedPlantStockId, @SourceCuttingStockId, @SourceAreaId,
 @DestinationAreaId, @PendingConfirmationAreaId, @Quantity, @Status, @ResponsiblePersonId, @SupervisorId, @Remarks, SYSUTCDATETIME(), @CreatedBy);
SELECT CAST(SCOPE_IDENTITY() AS INT);";

            using var cmd = new SqlCommand(insertSql, conn, tx);
            cmd.Parameters.AddWithValue("@TransferCode", transferCode);
            cmd.Parameters.AddWithValue("@StockType", entry.StockType);
            cmd.Parameters.AddWithValue("@SourceEmptyPotInventoryId", (object?)entry.SourceEmptyPotInventoryId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@SourcePottedPlantStockId", (object?)entry.SourcePottedPlantStockId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@SourceCuttingStockId", (object?)entry.SourceCuttingStockId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@SourceAreaId", entry.SourceAreaId);
            cmd.Parameters.AddWithValue("@DestinationAreaId", (object?)entry.DestinationAreaId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@PendingConfirmationAreaId", (object?)entry.PendingConfirmationAreaId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@Quantity", entry.Quantity);
            cmd.Parameters.AddWithValue("@Status", status);
            cmd.Parameters.AddWithValue("@ResponsiblePersonId", (object?)entry.ResponsiblePersonId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@SupervisorId", (object?)entry.SupervisorId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@Remarks", (object?)entry.Remarks ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@CreatedBy", (object?)entry.CreatedBy ?? DBNull.Value);

            var newId = (int)await cmd.ExecuteScalarAsync();
            entry.Id = newId;
            entry.TransferCode = transferCode;
            entry.Status = status;
            return newId;
        }

        // STEP 1 of 2 (Model B): Main Office confirms RECEIPT of a pending
        // Cutting transfer -- enters the ACTUAL quantity received (which
        // may differ from what was sent) and, if it's short, releases
        // just the shortfall back to the source pool's AvailableQuantity.
        // This method writes NO CuttingStockTransactions ledger row and
        // does NOT touch PhysicalQuantity -- per the critical rule, stock
        // only actually moves at ConfirmTransplantAsync. Quantity (what
        // was sent) is left untouched on the row.
        public async Task<(bool Success, string? Message)> ConfirmReceiptAsync(
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
                    "SELECT StockType, SourceCuttingStockId, Quantity, Status FROM dbo.InternalTransfers WITH (UPDLOCK, HOLDLOCK) WHERE Id = @Id",
                    conn, tx);
                lockCmd.Parameters.AddWithValue("@Id", id);
                using var reader = await lockCmd.ExecuteReaderAsync();
                if (!await reader.ReadAsync())
                {
                    reader.Close();
                    tx.Rollback();
                    return (false, "Internal Transfer record not found.");
                }
                var stockType = reader.GetString(reader.GetOrdinal("StockType"));
                var sourceCuttingStockId = reader.IsDBNull(reader.GetOrdinal("SourceCuttingStockId")) ? (int?)null : reader.GetInt32(reader.GetOrdinal("SourceCuttingStockId"));
                var sentQuantity = reader.GetDecimal(reader.GetOrdinal("Quantity"));
                var status = reader.GetString(reader.GetOrdinal("Status"));
                reader.Close();

                if (stockType != "Cutting" || !sourceCuttingStockId.HasValue)
                {
                    tx.Rollback();
                    return (false, "Only pending Cutting transfers can have their receipt confirmed.");
                }
                if (status != "PendingConfirmation")
                {
                    tx.Rollback();
                    return (false, $"This transfer is already '{status}' and cannot be confirmed again.");
                }
                if (confirmedQuantity > sentQuantity)
                {
                    tx.Rollback();
                    return (false, $"Confirmed quantity ({confirmedQuantity:N2}) cannot exceed the quantity actually sent ({sentQuantity:N2}).");
                }
                if (confirmedQuantity != sentQuantity && string.IsNullOrWhiteSpace(discrepancyReason))
                {
                    tx.Rollback();
                    return (false, $"Confirmed quantity ({confirmedQuantity:N2}) differs from sent quantity ({sentQuantity:N2}) -- a reason is required.");
                }

                // Release only the shortfall (sentQuantity - confirmedQuantity)
                // back to AvailableQuantity -- the confirmed portion stays
                // held (in transit, in spirit) against this transfer until
                // it is actually transplanted.
                var shortfall = sentQuantity - confirmedQuantity;
                if (shortfall > 0)
                {
                    var (releaseSuccess, releaseMessage) = await _cuttingStockRepo.ReleaseInTransitAsync(
                        conn, tx, sourceCuttingStockId.Value, shortfall);
                    if (!releaseSuccess)
                    {
                        tx.Rollback();
                        return (false, releaseMessage);
                    }
                }

                var updateCmd = new SqlCommand(@"
UPDATE dbo.InternalTransfers
SET Status = 'ConfirmedAwaitingTransplant',
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

        // STEP 2 of 2 (Model B): Main Office routes a 'ConfirmedAwaitingTransplant'
        // Cutting transfer to its Destination Polyhouse. This is the ONLY
        // point stock actually moves: the source CuttingStock's
        // PhysicalQuantity falls by ConfirmedQuantity and one 'Transplanted'
        // ledger row is written -- ONE-SIDED, by the critical rule. NOTHING
        // is credited to the destination Polyhouse; it never gets a
        // CuttingStock row touched on its behalf. Destination Polyhouse,
        // Destination Supervisor, and Transplant Date are all required.
        public async Task<(bool Success, string? Message)> ConfirmTransplantAsync(
            int id, int destinationPolyhouseAreaId, int destinationSupervisorId, DateTime transplantDate, string? remarks, int? userId, string? createdBy)
        {
            if (destinationPolyhouseAreaId <= 0)
                return (false, "Destination Polyhouse is required.");
            if (destinationSupervisorId <= 0)
                return (false, "Destination Polyhouse Supervisor is required.");

            using var conn = _dbHelper.GetConnection();
            await conn.OpenAsync();
            using var tx = conn.BeginTransaction();

            try
            {
                var lockCmd = new SqlCommand(
                    "SELECT StockType, SourceCuttingStockId, SourceAreaId, ConfirmedQuantity, Status FROM dbo.InternalTransfers WITH (UPDLOCK, HOLDLOCK) WHERE Id = @Id",
                    conn, tx);
                lockCmd.Parameters.AddWithValue("@Id", id);
                using var reader = await lockCmd.ExecuteReaderAsync();
                if (!await reader.ReadAsync())
                {
                    reader.Close();
                    tx.Rollback();
                    return (false, "Internal Transfer record not found.");
                }
                var stockType = reader.GetString(reader.GetOrdinal("StockType"));
                var sourceCuttingStockId = reader.IsDBNull(reader.GetOrdinal("SourceCuttingStockId")) ? (int?)null : reader.GetInt32(reader.GetOrdinal("SourceCuttingStockId"));
                var sourceAreaId = reader.GetInt32(reader.GetOrdinal("SourceAreaId"));
                var confirmedQuantity = reader.IsDBNull(reader.GetOrdinal("ConfirmedQuantity")) ? (decimal?)null : reader.GetDecimal(reader.GetOrdinal("ConfirmedQuantity"));
                var status = reader.GetString(reader.GetOrdinal("Status"));
                reader.Close();

                if (stockType != "Cutting" || !sourceCuttingStockId.HasValue || !confirmedQuantity.HasValue)
                {
                    tx.Rollback();
                    return (false, "Only a Cutting transfer with a confirmed receipt can be transplanted.");
                }
                if (status != "ConfirmedAwaitingTransplant")
                {
                    tx.Rollback();
                    return (false, $"This transfer is '{status}', not awaiting transplant.");
                }
                if (sourceAreaId == destinationPolyhouseAreaId)
                {
                    tx.Rollback();
                    return (false, "Destination Polyhouse must be different from the sending Area.");
                }

                var (transplantSuccess, transplantMessage) = await _cuttingStockRepo.RecordTransplantAsync(
                    conn, tx, sourceCuttingStockId.Value, confirmedQuantity.Value, "InternalTransfer", id, userId, remarks);
                if (!transplantSuccess)
                {
                    tx.Rollback();
                    return (false, transplantMessage);
                }

                const string insertTransplantSql = @"
INSERT INTO dbo.CuttingTransplants (InternalTransferId, DestinationSupervisorId, TransplantDate, Remarks, CreatedDate, CreatedBy)
VALUES (@InternalTransferId, @DestinationSupervisorId, @TransplantDate, @Remarks, SYSUTCDATETIME(), @CreatedBy);";
                var insertTransplantCmd = new SqlCommand(insertTransplantSql, conn, tx);
                insertTransplantCmd.Parameters.AddWithValue("@InternalTransferId", id);
                insertTransplantCmd.Parameters.AddWithValue("@DestinationSupervisorId", destinationSupervisorId);
                insertTransplantCmd.Parameters.AddWithValue("@TransplantDate", transplantDate.Date);
                insertTransplantCmd.Parameters.AddWithValue("@Remarks", (object?)remarks ?? DBNull.Value);
                insertTransplantCmd.Parameters.AddWithValue("@CreatedBy", (object?)createdBy ?? DBNull.Value);
                await insertTransplantCmd.ExecuteNonQueryAsync();

                var updateCmd = new SqlCommand(@"
UPDATE dbo.InternalTransfers
SET Status = 'Transplanted',
    DestinationAreaId = @DestinationAreaId,
    ModifiedDate = SYSUTCDATETIME(),
    ModifiedBy = @ModifiedBy
WHERE Id = @Id", conn, tx);
                updateCmd.Parameters.AddWithValue("@DestinationAreaId", destinationPolyhouseAreaId);
                updateCmd.Parameters.AddWithValue("@ModifiedBy", (object?)createdBy ?? DBNull.Value);
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

        // Phase 18 (Phase C): the Growing Partner Area confirms RECEIPT of
        // a pending 'MainOfficeIssue' transfer -- enters the ACTUAL
        // quantity received (which may differ from what was sent) and, in
        // ONE step (unlike Cutting's two-step confirm-then-transplant):
        // releases the FULL sent quantity from InTransit, decrements Main
        // Office's own PhysicalQuantity by only what was actually
        // confirmed (a shortfall is simply never taken out -- it never
        // left), and credits the destination Growing Partner Area's own
        // PottedPlantStock pool by that same confirmed quantity. A
        // MainOfficeIssue transfer has no separate "transplant" step
        // because its destination Area is already known and fixed at
        // creation, unlike Cutting's confirm-time routing.
        public async Task<(bool Success, string? Message)> ConfirmMainOfficeIssueAsync(
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
                    "SELECT StockType, SourcePottedPlantStockId, DestinationAreaId, Quantity, Status FROM dbo.InternalTransfers WITH (UPDLOCK, HOLDLOCK) WHERE Id = @Id",
                    conn, tx);
                lockCmd.Parameters.AddWithValue("@Id", id);
                using var reader = await lockCmd.ExecuteReaderAsync();
                if (!await reader.ReadAsync())
                {
                    reader.Close();
                    tx.Rollback();
                    return (false, "Internal Transfer record not found.");
                }
                var stockType = reader.GetString(reader.GetOrdinal("StockType"));
                var sourcePottedPlantStockId = reader.IsDBNull(reader.GetOrdinal("SourcePottedPlantStockId")) ? (int?)null : reader.GetInt32(reader.GetOrdinal("SourcePottedPlantStockId"));
                var destinationAreaId = reader.IsDBNull(reader.GetOrdinal("DestinationAreaId")) ? (int?)null : reader.GetInt32(reader.GetOrdinal("DestinationAreaId"));
                var sentQuantity = reader.GetDecimal(reader.GetOrdinal("Quantity"));
                var status = reader.GetString(reader.GetOrdinal("Status"));
                reader.Close();

                if (stockType != "MainOfficeIssue" || !sourcePottedPlantStockId.HasValue || !destinationAreaId.HasValue)
                {
                    tx.Rollback();
                    return (false, "Only a pending Main Office Issue transfer can have its receipt confirmed this way.");
                }
                if (status != "PendingConfirmation")
                {
                    tx.Rollback();
                    return (false, $"This transfer is already '{status}' and cannot be confirmed again.");
                }
                if (confirmedQuantity > sentQuantity)
                {
                    tx.Rollback();
                    return (false, $"Confirmed quantity ({confirmedQuantity:N2}) cannot exceed the quantity actually sent ({sentQuantity:N2}).");
                }
                if (confirmedQuantity != sentQuantity && string.IsNullOrWhiteSpace(discrepancyReason))
                {
                    tx.Rollback();
                    return (false, $"Confirmed quantity ({confirmedQuantity:N2}) differs from sent quantity ({sentQuantity:N2}) -- a reason is required.");
                }

                // Release the FULL sent quantity from InTransit -- unlike
                // Cutting (which keeps the confirmed portion held until a
                // separate Transplant step), a MainOfficeIssue transfer
                // moves both sides right here, so nothing stays "in
                // transit" once this commits.
                var (releaseSuccess, releaseMessage) = await _pottedPlantStockRepo.ReleaseInTransitAsync(
                    conn, tx, sourcePottedPlantStockId.Value, sentQuantity);
                if (!releaseSuccess)
                {
                    tx.Rollback();
                    return (false, releaseMessage);
                }

                if (confirmedQuantity > 0)
                {
                    var (srcSuccess, srcMessage) = await _pottedPlantStockRepo.RecordTransactionAsync(
                        conn, tx, sourcePottedPlantStockId.Value, -confirmedQuantity, "Transfer", "InternalTransfer", id, confirmedByUserId, discrepancyReason);
                    if (!srcSuccess)
                    {
                        tx.Rollback();
                        return (false, srcMessage);
                    }

                    var srcInfoCmd = new SqlCommand("SELECT SpeciesId, PotSize FROM dbo.PottedPlantStock WHERE Id = @Id", conn, tx);
                    srcInfoCmd.Parameters.AddWithValue("@Id", sourcePottedPlantStockId.Value);
                    using var srcInfoReader = await srcInfoCmd.ExecuteReaderAsync();
                    if (!await srcInfoReader.ReadAsync())
                    {
                        srcInfoReader.Close();
                        tx.Rollback();
                        return (false, "Source Potted Plant Stock pool no longer exists.");
                    }
                    var srcSpeciesId = srcInfoReader.GetInt32(srcInfoReader.GetOrdinal("SpeciesId"));
                    var srcPotSize = srcInfoReader.GetString(srcInfoReader.GetOrdinal("PotSize"));
                    srcInfoReader.Close();

                    // The destination Potted Plant Stock row still needs
                    // an EmptyPotInventoryId link (schema requirement, not
                    // a physical empty-pot movement) -- resolve or create
                    // a zero-quantity Empty Pot pool for this Pot Size in
                    // the Destination Area to link against, exactly as
                    // the immediate 'PottedPlant' transfer path already
                    // does in InsertAsync above.
                    var destEmptyPotId = await _emptyPotInventoryRepo.GetOrCreateLockedAsync(conn, tx, srcPotSize, destinationAreaId, modifiedBy);
                    var destId = await _pottedPlantStockRepo.GetOrCreateLockedAsync(conn, tx, srcSpeciesId, srcPotSize, destinationAreaId, destEmptyPotId, modifiedBy);
                    var (destSuccess, destMessage) = await _pottedPlantStockRepo.RecordTransactionAsync(
                        conn, tx, destId, confirmedQuantity, "Transfer", "InternalTransfer", id, confirmedByUserId, discrepancyReason);
                    if (!destSuccess)
                    {
                        tx.Rollback();
                        return (false, destMessage);
                    }
                }

                var updateCmd = new SqlCommand(@"
UPDATE dbo.InternalTransfers
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

        // Phase 20 (Phase F): the Outlet Area confirms RECEIPT of a pending
        // 'GrowingPartnerToOutlet' transfer -- byte-for-byte the same
        // single-step shape as ConfirmMainOfficeIssueAsync immediately
        // above (release full InTransit, decrement source by confirmed
        // quantity, credit destination by confirmed quantity, mark
        // Completed), since the underlying stock mechanics are identical
        // regardless of which Area type is the source and which is the
        // destination. Kept as its OWN method, deliberately not merged
        // into ConfirmMainOfficeIssueAsync, so that method's Phase C
        // behavior stays provably byte-for-byte unchanged (the same
        // "additive, don't touch the existing one" convention every prior
        // phase has followed for a new source/workflow). Enforces the
        // "sender cannot confirm their own transfer" rule (spec item 12)
        // indirectly: this method itself does no user-identity check (like
        // every other repository method, it has no ClaimsPrincipal), but
        // the calling page (ConfirmReceipt.cshtml.cs) requires
        // AreaAccessService.CanAccessArea(User, DestinationAreaId) --
        // a Growing Partner Supervisor's own UserRoles.AreaId is their
        // sending Area, never the Outlet's, so they fail that check unless
        // they also hold a full-access role, exactly like every other
        // sender/receiver split already enforced this way in this app.
        public async Task<(bool Success, string? Message)> ConfirmGrowingPartnerToOutletAsync(
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
                    "SELECT StockType, SourcePottedPlantStockId, DestinationAreaId, Quantity, Status FROM dbo.InternalTransfers WITH (UPDLOCK, HOLDLOCK) WHERE Id = @Id",
                    conn, tx);
                lockCmd.Parameters.AddWithValue("@Id", id);
                using var reader = await lockCmd.ExecuteReaderAsync();
                if (!await reader.ReadAsync())
                {
                    reader.Close();
                    tx.Rollback();
                    return (false, "Internal Transfer record not found.");
                }
                var stockType = reader.GetString(reader.GetOrdinal("StockType"));
                var sourcePottedPlantStockId = reader.IsDBNull(reader.GetOrdinal("SourcePottedPlantStockId")) ? (int?)null : reader.GetInt32(reader.GetOrdinal("SourcePottedPlantStockId"));
                var destinationAreaId = reader.IsDBNull(reader.GetOrdinal("DestinationAreaId")) ? (int?)null : reader.GetInt32(reader.GetOrdinal("DestinationAreaId"));
                var sentQuantity = reader.GetDecimal(reader.GetOrdinal("Quantity"));
                var status = reader.GetString(reader.GetOrdinal("Status"));
                reader.Close();

                if (stockType != "GrowingPartnerToOutlet" || !sourcePottedPlantStockId.HasValue || !destinationAreaId.HasValue)
                {
                    tx.Rollback();
                    return (false, "Only a pending Growing Partner to Outlet transfer can have its receipt confirmed this way.");
                }
                if (status != "PendingConfirmation")
                {
                    tx.Rollback();
                    return (false, $"This transfer is already '{status}' and cannot be confirmed again.");
                }
                if (confirmedQuantity > sentQuantity)
                {
                    tx.Rollback();
                    return (false, $"Confirmed quantity ({confirmedQuantity:N2}) cannot exceed the quantity actually sent ({sentQuantity:N2}).");
                }
                if (confirmedQuantity != sentQuantity && string.IsNullOrWhiteSpace(discrepancyReason))
                {
                    tx.Rollback();
                    return (false, $"Confirmed quantity ({confirmedQuantity:N2}) differs from sent quantity ({sentQuantity:N2}) -- a reason is required.");
                }

                // Release the FULL sent quantity from InTransit -- nothing
                // stays "in transit" once this commits, same reasoning as
                // ConfirmMainOfficeIssueAsync.
                var (releaseSuccess, releaseMessage) = await _pottedPlantStockRepo.ReleaseInTransitAsync(
                    conn, tx, sourcePottedPlantStockId.Value, sentQuantity);
                if (!releaseSuccess)
                {
                    tx.Rollback();
                    return (false, releaseMessage);
                }

                if (confirmedQuantity > 0)
                {
                    var (srcSuccess, srcMessage) = await _pottedPlantStockRepo.RecordTransactionAsync(
                        conn, tx, sourcePottedPlantStockId.Value, -confirmedQuantity, "Transfer", "InternalTransfer", id, confirmedByUserId, discrepancyReason);
                    if (!srcSuccess)
                    {
                        tx.Rollback();
                        return (false, srcMessage);
                    }

                    var srcInfoCmd = new SqlCommand("SELECT SpeciesId, PotSize FROM dbo.PottedPlantStock WHERE Id = @Id", conn, tx);
                    srcInfoCmd.Parameters.AddWithValue("@Id", sourcePottedPlantStockId.Value);
                    using var srcInfoReader = await srcInfoCmd.ExecuteReaderAsync();
                    if (!await srcInfoReader.ReadAsync())
                    {
                        srcInfoReader.Close();
                        tx.Rollback();
                        return (false, "Source Potted Plant Stock pool no longer exists.");
                    }
                    var srcSpeciesId = srcInfoReader.GetInt32(srcInfoReader.GetOrdinal("SpeciesId"));
                    var srcPotSize = srcInfoReader.GetString(srcInfoReader.GetOrdinal("PotSize"));
                    srcInfoReader.Close();

                    // The destination Potted Plant Stock row still needs an
                    // EmptyPotInventoryId link (schema requirement, not a
                    // physical empty-pot movement) -- resolve or create a
                    // zero-quantity Empty Pot pool for this Pot Size in the
                    // Outlet Area to link against, exactly as
                    // ConfirmMainOfficeIssueAsync already does for its own
                    // destination.
                    var destEmptyPotId = await _emptyPotInventoryRepo.GetOrCreateLockedAsync(conn, tx, srcPotSize, destinationAreaId, modifiedBy);
                    var destId = await _pottedPlantStockRepo.GetOrCreateLockedAsync(conn, tx, srcSpeciesId, srcPotSize, destinationAreaId, destEmptyPotId, modifiedBy);
                    var (destSuccess, destMessage) = await _pottedPlantStockRepo.RecordTransactionAsync(
                        conn, tx, destId, confirmedQuantity, "Transfer", "InternalTransfer", id, confirmedByUserId, discrepancyReason);
                    if (!destSuccess)
                    {
                        tx.Rollback();
                        return (false, destMessage);
                    }
                }

                var updateCmd = new SqlCommand(@"
UPDATE dbo.InternalTransfers
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

        // Main Office rejects a pending Cutting transfer outright (e.g.
        // nothing usable actually arrived). No PhysicalQuantity ever moved
        // for a pending transfer, so there is nothing to reverse there --
        // but the full sent Quantity IS currently held in
        // InTransitQuantity (reserved at InsertAsync), and that must be
        // released back to AvailableQuantity, since physically nothing
        // left the source.
        public async Task<(bool Success, string? Message)> RejectAsync(int id, string reason, string? modifiedBy)
        {
            if (string.IsNullOrWhiteSpace(reason))
                return (false, "A reason is required to reject a pending transfer.");

            using var conn = _dbHelper.GetConnection();
            await conn.OpenAsync();
            using var tx = conn.BeginTransaction();

            try
            {
                var lockCmd = new SqlCommand(
                    "SELECT StockType, SourceCuttingStockId, SourcePottedPlantStockId, Quantity, Status FROM dbo.InternalTransfers WITH (UPDLOCK, HOLDLOCK) WHERE Id = @Id",
                    conn, tx);
                lockCmd.Parameters.AddWithValue("@Id", id);
                using var reader = await lockCmd.ExecuteReaderAsync();
                if (!await reader.ReadAsync())
                {
                    reader.Close();
                    tx.Rollback();
                    return (false, "Internal Transfer record not found.");
                }
                var stockType = reader.GetString(reader.GetOrdinal("StockType"));
                var sourceCuttingStockId = reader.IsDBNull(reader.GetOrdinal("SourceCuttingStockId")) ? (int?)null : reader.GetInt32(reader.GetOrdinal("SourceCuttingStockId"));
                var sourcePottedPlantStockId = reader.IsDBNull(reader.GetOrdinal("SourcePottedPlantStockId")) ? (int?)null : reader.GetInt32(reader.GetOrdinal("SourcePottedPlantStockId"));
                var sentQuantity = reader.GetDecimal(reader.GetOrdinal("Quantity"));
                var status = reader.GetString(reader.GetOrdinal("Status"));
                reader.Close();

                if (status != "PendingConfirmation")
                {
                    tx.Rollback();
                    return (false, "Internal Transfer record not found, or it is no longer pending.");
                }

                if (stockType == "Cutting" && sourceCuttingStockId.HasValue)
                {
                    var (releaseSuccess, releaseMessage) = await _cuttingStockRepo.ReleaseInTransitAsync(
                        conn, tx, sourceCuttingStockId.Value, sentQuantity);
                    if (!releaseSuccess)
                    {
                        tx.Rollback();
                        return (false, releaseMessage);
                    }
                }
                else if ((stockType == "MainOfficeIssue" || stockType == "GrowingPartnerToOutlet") && sourcePottedPlantStockId.HasValue)
                {
                    // Same reasoning as the Cutting branch above: nothing
                    // has physically moved for a still-pending
                    // MainOfficeIssue or GrowingPartnerToOutlet transfer,
                    // so the full sent quantity -- currently held in
                    // InTransitQuantity -- must be released back to
                    // "available to send/issue". Both StockTypes share
                    // this branch since the release mechanics are
                    // identical regardless of which Area type is sending.
                    var (releaseSuccess, releaseMessage) = await _pottedPlantStockRepo.ReleaseInTransitAsync(
                        conn, tx, sourcePottedPlantStockId.Value, sentQuantity);
                    if (!releaseSuccess)
                    {
                        tx.Rollback();
                        return (false, releaseMessage);
                    }
                }

                var updateCmd = new SqlCommand(@"
UPDATE dbo.InternalTransfers
SET Status = 'Rejected', DiscrepancyReason = @Reason, ModifiedDate = SYSUTCDATETIME(), ModifiedBy = @ModifiedBy
WHERE Id = @Id AND Status = 'PendingConfirmation'", conn, tx);
                updateCmd.Parameters.AddWithValue("@Reason", reason);
                updateCmd.Parameters.AddWithValue("@ModifiedBy", (object?)modifiedBy ?? DBNull.Value);
                updateCmd.Parameters.AddWithValue("@Id", id);
                var rows = await updateCmd.ExecuteNonQueryAsync();
                if (rows == 0)
                {
                    tx.Rollback();
                    return (false, "Internal Transfer record not found, or it is no longer pending.");
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
        // SupervisorId, Remarks). StockType/source/destination/Quantity
        // are immutable after creation -- use CancelAsync to reverse a
        // transfer entirely.
        public async Task<(bool Success, string? Message)> UpdateDetailsAsync(InternalTransfer entry)
        {
            using var conn = _dbHelper.GetConnection();
            await conn.OpenAsync();

            try
            {
                const string updateSql = @"
UPDATE dbo.InternalTransfers
SET SupervisorId = @SupervisorId,
    Remarks = @Remarks,
    ModifiedDate = SYSUTCDATETIME(),
    ModifiedBy = @ModifiedBy
WHERE Id = @Id AND Status <> 'Cancelled'";

                using var cmd = new SqlCommand(updateSql, conn);
                cmd.Parameters.AddWithValue("@Id", entry.Id);
                cmd.Parameters.AddWithValue("@SupervisorId", (object?)entry.SupervisorId ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@Remarks", (object?)entry.Remarks ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@ModifiedBy", (object?)entry.ModifiedBy ?? DBNull.Value);

                var rows = await cmd.ExecuteNonQueryAsync();
                return rows > 0 ? (true, null) : (false, "Internal Transfer record not found, or it is already Cancelled.");
            }
            catch (Exception ex)
            {
                return (false, ex.Message);
            }
        }

        // Reverses a completed Internal Transfer: moves the quantity
        // back from the destination pool to the source pool with a
        // second, equal-and-opposite pair of 'Transfer' ledger entries
        // (rather than inventing a separate reversal-specific
        // TransactionType), then marks the record Cancelled. Refuses if
        // the destination pool no longer has enough physical quantity to
        // give back (e.g. it was already reserved/consumed/transferred
        // onward) -- RecordTransactionAsync's own checks enforce this
        // under lock.
        public async Task<(bool Success, string? Message)> CancelAsync(int id, string? modifiedBy, int? userId)
        {
            using var conn = _dbHelper.GetConnection();
            await conn.OpenAsync();
            using var tx = conn.BeginTransaction();

            try
            {
                var lockCmd = new SqlCommand(
                    "SELECT StockType, SourceEmptyPotInventoryId, SourcePottedPlantStockId, SourceCuttingStockId, SourceAreaId, DestinationAreaId, Quantity, ConfirmedQuantity, Status FROM dbo.InternalTransfers WITH (UPDLOCK, HOLDLOCK) WHERE Id = @Id",
                    conn, tx);
                lockCmd.Parameters.AddWithValue("@Id", id);
                using var reader = await lockCmd.ExecuteReaderAsync();
                if (!await reader.ReadAsync())
                {
                    reader.Close();
                    tx.Rollback();
                    return (false, "Internal Transfer record not found.");
                }
                var stockType = reader.GetString(reader.GetOrdinal("StockType"));
                var sourceEmptyPotInventoryId = reader.IsDBNull(reader.GetOrdinal("SourceEmptyPotInventoryId")) ? (int?)null : reader.GetInt32(reader.GetOrdinal("SourceEmptyPotInventoryId"));
                var sourcePottedPlantStockId = reader.IsDBNull(reader.GetOrdinal("SourcePottedPlantStockId")) ? (int?)null : reader.GetInt32(reader.GetOrdinal("SourcePottedPlantStockId"));
                var sourceCuttingStockId = reader.IsDBNull(reader.GetOrdinal("SourceCuttingStockId")) ? (int?)null : reader.GetInt32(reader.GetOrdinal("SourceCuttingStockId"));
                var destinationAreaId = reader.IsDBNull(reader.GetOrdinal("DestinationAreaId")) ? (int?)null : reader.GetInt32(reader.GetOrdinal("DestinationAreaId"));
                var quantity = reader.GetDecimal(reader.GetOrdinal("Quantity"));
                var confirmedQuantity = reader.IsDBNull(reader.GetOrdinal("ConfirmedQuantity")) ? (decimal?)null : reader.GetDecimal(reader.GetOrdinal("ConfirmedQuantity"));
                var status = reader.GetString(reader.GetOrdinal("Status"));
                reader.Close();

                if (status == "Cancelled")
                {
                    tx.Rollback();
                    return (false, "This Internal Transfer is already Cancelled.");
                }

                if (stockType == "Cutting")
                {
                    // Model B: PhysicalQuantity only ever moves at
                    // ConfirmTransplantAsync, and it moves ONE-SIDED (no
                    // destination CuttingStock row is ever touched), so
                    // reversal here only ever needs to credit the SOURCE
                    // back -- there is no destination pool to unwind.
                    if (status == "PendingConfirmation")
                    {
                        tx.Rollback();
                        return (false, "This Cutting transfer has not been confirmed yet -- use Reject instead of Cancel.");
                    }
                    if (status == "Rejected")
                    {
                        tx.Rollback();
                        return (false, "This Cutting transfer was rejected; no stock ever moved, so there is nothing to cancel.");
                    }
                    if (status == "ConfirmedAwaitingTransplant")
                    {
                        tx.Rollback();
                        return (false, "This Cutting transfer is awaiting transplant and has not moved any stock yet -- complete the transplant, or contact an administrator to withdraw it.");
                    }
                    if (status != "Transplanted")
                    {
                        tx.Rollback();
                        return (false, $"This Cutting transfer is '{status}' and cannot be cancelled automatically.");
                    }
                    if (!sourceCuttingStockId.HasValue || !confirmedQuantity.HasValue)
                    {
                        tx.Rollback();
                        return (false, "Internal Transfer record is malformed (missing confirmation details); cannot reverse automatically.");
                    }

                    if (confirmedQuantity.Value > 0)
                    {
                        var (srcSuccess, srcMessage) = await _cuttingStockRepo.RecordTransactionAsync(
                            conn, tx, sourceCuttingStockId.Value, confirmedQuantity.Value, "ReversalRemoval", "InternalTransfer", id, userId, "Reversal of cancelled Internal Transfer");
                        if (!srcSuccess)
                        {
                            tx.Rollback();
                            return (false, srcMessage);
                        }
                    }
                }
                else if (stockType == "MainOfficeIssue" || stockType == "GrowingPartnerToOutlet")
                {
                    // PhysicalQuantity only ever moves once the receiving
                    // side's receipt is actually confirmed
                    // (ConfirmMainOfficeIssueAsync/
                    // ConfirmGrowingPartnerToOutletAsync), so a still-
                    // pending or rejected transfer of either StockType has
                    // nothing to reverse here -- same reasoning as the
                    // Cutting branch above, but neither has a separate
                    // "awaiting transplant" state to also guard against.
                    // Both StockTypes share this branch: the reversal code
                    // below only ever reads sourcePottedPlantStockId/
                    // destinationAreaId/confirmedQuantity generically, with
                    // no StockType-specific field, so widening this
                    // condition changes nothing about how MainOfficeIssue
                    // itself is reversed.
                    if (status == "PendingConfirmation")
                    {
                        tx.Rollback();
                        return (false, "This transfer has not been confirmed yet -- use Reject instead of Cancel.");
                    }
                    if (status == "Rejected")
                    {
                        tx.Rollback();
                        return (false, "This transfer was rejected; no stock ever moved, so there is nothing to cancel.");
                    }
                    if (status != "Completed")
                    {
                        tx.Rollback();
                        return (false, $"This transfer is '{status}' and cannot be cancelled automatically.");
                    }
                    if (!sourcePottedPlantStockId.HasValue || !confirmedQuantity.HasValue || !destinationAreaId.HasValue)
                    {
                        tx.Rollback();
                        return (false, "Internal Transfer record is malformed (missing confirmation details); cannot reverse automatically.");
                    }

                    if (confirmedQuantity.Value > 0)
                    {
                        var srcInfoCmd = new SqlCommand("SELECT SpeciesId, PotSize FROM dbo.PottedPlantStock WHERE Id = @Id", conn, tx);
                        srcInfoCmd.Parameters.AddWithValue("@Id", sourcePottedPlantStockId.Value);
                        using var srcInfoReader = await srcInfoCmd.ExecuteReaderAsync();
                        if (!await srcInfoReader.ReadAsync())
                        {
                            srcInfoReader.Close();
                            tx.Rollback();
                            return (false, "Source Potted Plant Stock pool no longer exists; cannot reverse automatically.");
                        }
                        var srcSpeciesId = srcInfoReader.GetInt32(srcInfoReader.GetOrdinal("SpeciesId"));
                        var srcPotSize = srcInfoReader.GetString(srcInfoReader.GetOrdinal("PotSize"));
                        srcInfoReader.Close();

                        var destLookupCmd = new SqlCommand(
                            "SELECT Id FROM dbo.PottedPlantStock WHERE SpeciesId = @SpeciesId AND PotSize = @PotSize AND (AreaId = @AreaId OR (AreaId IS NULL AND @AreaId IS NULL))",
                            conn, tx);
                        destLookupCmd.Parameters.AddWithValue("@SpeciesId", srcSpeciesId);
                        destLookupCmd.Parameters.AddWithValue("@PotSize", srcPotSize);
                        destLookupCmd.Parameters.AddWithValue("@AreaId", (object?)destinationAreaId ?? DBNull.Value);
                        var destIdObj = await destLookupCmd.ExecuteScalarAsync();
                        if (destIdObj == null || destIdObj == DBNull.Value)
                        {
                            tx.Rollback();
                            return (false, "Destination Potted Plant Stock pool no longer exists; cannot reverse automatically.");
                        }
                        var destId = (int)destIdObj;

                        // Reverse BOTH sides with 'Transfer' entries (same
                        // TransactionType the original confirm used, sign
                        // flipped) -- keyed off ConfirmedQuantity (what
                        // actually moved), not Quantity (what was merely
                        // sent), same as the PottedPlant branch below but
                        // using the confirmed amount since sent and
                        // confirmed can legitimately differ here.
                        var (destSuccess, destMessage) = await _pottedPlantStockRepo.RecordTransactionAsync(
                            conn, tx, destId, -confirmedQuantity.Value, "Transfer", "InternalTransfer", id, userId, "Reversal of cancelled Internal Transfer");
                        if (!destSuccess)
                        {
                            tx.Rollback();
                            return (false, $"Cannot cancel: {destMessage}");
                        }

                        var (srcSuccess, srcMessage) = await _pottedPlantStockRepo.RecordTransactionAsync(
                            conn, tx, sourcePottedPlantStockId.Value, confirmedQuantity.Value, "Transfer", "InternalTransfer", id, userId, "Reversal of cancelled Internal Transfer");
                        if (!srcSuccess)
                        {
                            tx.Rollback();
                            return (false, srcMessage);
                        }
                    }
                }
                else if (stockType == "EmptyPot" && sourceEmptyPotInventoryId.HasValue)
                {
                    var potSize = await GetPotSizeForEmptyPotAsync(conn, tx, sourceEmptyPotInventoryId.Value);
                    if (potSize == null)
                    {
                        tx.Rollback();
                        return (false, "Source Empty Pot pool no longer exists; cannot reverse automatically.");
                    }

                    var destLookupCmd = new SqlCommand(
                        "SELECT Id FROM dbo.EmptyPotInventory WHERE PotSize = @PotSize AND (AreaId = @AreaId OR (AreaId IS NULL AND @AreaId IS NULL))",
                        conn, tx);
                    destLookupCmd.Parameters.AddWithValue("@PotSize", potSize);
                    destLookupCmd.Parameters.AddWithValue("@AreaId", (object?)destinationAreaId ?? DBNull.Value);
                    var destIdObj = await destLookupCmd.ExecuteScalarAsync();
                    if (destIdObj == null || destIdObj == DBNull.Value)
                    {
                        tx.Rollback();
                        return (false, "Destination Empty Pot pool no longer exists; cannot reverse automatically.");
                    }
                    var destId = (int)destIdObj;

                    var (destSuccess, destMessage) = await _emptyPotInventoryRepo.RecordTransactionAsync(
                        conn, tx, destId, -quantity, "Transfer", "InternalTransfer", id, userId, "Reversal of cancelled Internal Transfer");
                    if (!destSuccess)
                    {
                        tx.Rollback();
                        return (false, $"Cannot cancel: {destMessage}");
                    }

                    var (srcSuccess, srcMessage) = await _emptyPotInventoryRepo.RecordTransactionAsync(
                        conn, tx, sourceEmptyPotInventoryId.Value, quantity, "Transfer", "InternalTransfer", id, userId, "Reversal of cancelled Internal Transfer");
                    if (!srcSuccess)
                    {
                        tx.Rollback();
                        return (false, srcMessage);
                    }
                }
                else if (stockType == "PottedPlant" && sourcePottedPlantStockId.HasValue)
                {
                    var srcLookupCmd = new SqlCommand("SELECT SpeciesId, PotSize FROM dbo.PottedPlantStock WHERE Id = @Id", conn, tx);
                    srcLookupCmd.Parameters.AddWithValue("@Id", sourcePottedPlantStockId.Value);
                    using var srcLookupReader = await srcLookupCmd.ExecuteReaderAsync();
                    if (!await srcLookupReader.ReadAsync())
                    {
                        srcLookupReader.Close();
                        tx.Rollback();
                        return (false, "Source Potted Plant Stock pool no longer exists; cannot reverse automatically.");
                    }
                    var srcSpeciesId = srcLookupReader.GetInt32(srcLookupReader.GetOrdinal("SpeciesId"));
                    var srcPotSize = srcLookupReader.GetString(srcLookupReader.GetOrdinal("PotSize"));
                    srcLookupReader.Close();

                    var destLookupCmd = new SqlCommand(
                        "SELECT Id FROM dbo.PottedPlantStock WHERE SpeciesId = @SpeciesId AND PotSize = @PotSize AND (AreaId = @AreaId OR (AreaId IS NULL AND @AreaId IS NULL))",
                        conn, tx);
                    destLookupCmd.Parameters.AddWithValue("@SpeciesId", srcSpeciesId);
                    destLookupCmd.Parameters.AddWithValue("@PotSize", srcPotSize);
                    destLookupCmd.Parameters.AddWithValue("@AreaId", (object?)destinationAreaId ?? DBNull.Value);
                    var destIdObj = await destLookupCmd.ExecuteScalarAsync();
                    if (destIdObj == null || destIdObj == DBNull.Value)
                    {
                        tx.Rollback();
                        return (false, "Destination Potted Plant Stock pool no longer exists; cannot reverse automatically.");
                    }
                    var destId = (int)destIdObj;

                    var (destSuccess, destMessage) = await _pottedPlantStockRepo.RecordTransactionAsync(
                        conn, tx, destId, -quantity, "Transfer", "InternalTransfer", id, userId, "Reversal of cancelled Internal Transfer");
                    if (!destSuccess)
                    {
                        tx.Rollback();
                        return (false, $"Cannot cancel: {destMessage}");
                    }

                    var (srcSuccess, srcMessage) = await _pottedPlantStockRepo.RecordTransactionAsync(
                        conn, tx, sourcePottedPlantStockId.Value, quantity, "Transfer", "InternalTransfer", id, userId, "Reversal of cancelled Internal Transfer");
                    if (!srcSuccess)
                    {
                        tx.Rollback();
                        return (false, srcMessage);
                    }
                }
                else
                {
                    tx.Rollback();
                    return (false, "Internal Transfer record is malformed (source reference missing); cannot reverse automatically.");
                }

                var updateCmd = new SqlCommand(
                    "UPDATE dbo.InternalTransfers SET Status = 'Cancelled', ModifiedDate = SYSUTCDATETIME(), ModifiedBy = @ModifiedBy WHERE Id = @Id",
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

        private static async Task<string?> GetPotSizeForEmptyPotAsync(SqlConnection conn, SqlTransaction tx, int emptyPotInventoryId)
        {
            var cmd = new SqlCommand("SELECT PotSize FROM dbo.EmptyPotInventory WHERE Id = @Id", conn, tx);
            cmd.Parameters.AddWithValue("@Id", emptyPotInventoryId);
            var result = await cmd.ExecuteScalarAsync();
            return result == null || result == DBNull.Value ? null : (string)result;
        }

        private static InternalTransfer Map(SqlDataReader reader)
        {
            return new InternalTransfer
            {
                Id = reader.GetInt32(reader.GetOrdinal("Id")),
                TransferCode = reader.GetString(reader.GetOrdinal("TransferCode")),
                StockType = reader.GetString(reader.GetOrdinal("StockType")),
                SourceEmptyPotInventoryId = reader.IsDBNull(reader.GetOrdinal("SourceEmptyPotInventoryId")) ? null : reader.GetInt32(reader.GetOrdinal("SourceEmptyPotInventoryId")),
                SourcePottedPlantStockId = reader.IsDBNull(reader.GetOrdinal("SourcePottedPlantStockId")) ? null : reader.GetInt32(reader.GetOrdinal("SourcePottedPlantStockId")),
                SourceCuttingStockId = reader.IsDBNull(reader.GetOrdinal("SourceCuttingStockId")) ? null : reader.GetInt32(reader.GetOrdinal("SourceCuttingStockId")),
                SourceAreaId = reader.GetInt32(reader.GetOrdinal("SourceAreaId")),
                SourceAreaName = reader.IsDBNull(reader.GetOrdinal("SourceAreaName")) ? null : reader.GetString(reader.GetOrdinal("SourceAreaName")),
                SourceGrowingPartnerName = reader.IsDBNull(reader.GetOrdinal("SourceGrowingPartnerName")) ? null : reader.GetString(reader.GetOrdinal("SourceGrowingPartnerName")),
                DestinationAreaId = reader.IsDBNull(reader.GetOrdinal("DestinationAreaId")) ? null : reader.GetInt32(reader.GetOrdinal("DestinationAreaId")),
                DestinationAreaName = reader.IsDBNull(reader.GetOrdinal("DestinationAreaName")) ? null : reader.GetString(reader.GetOrdinal("DestinationAreaName")),
                PendingConfirmationAreaId = reader.IsDBNull(reader.GetOrdinal("PendingConfirmationAreaId")) ? null : reader.GetInt32(reader.GetOrdinal("PendingConfirmationAreaId")),
                PendingConfirmationAreaName = reader.IsDBNull(reader.GetOrdinal("PendingConfirmationAreaName")) ? null : reader.GetString(reader.GetOrdinal("PendingConfirmationAreaName")),
                Quantity = reader.GetDecimal(reader.GetOrdinal("Quantity")),
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
                PotSize = reader.IsDBNull(reader.GetOrdinal("PotSize")) ? null : reader.GetString(reader.GetOrdinal("PotSize")),
                SpeciesName = reader.IsDBNull(reader.GetOrdinal("SpeciesName")) ? null : reader.GetString(reader.GetOrdinal("SpeciesName")),
                PlantTypeName = reader.IsDBNull(reader.GetOrdinal("PlantTypeName")) ? null : reader.GetString(reader.GetOrdinal("PlantTypeName")),
                TransplantDestinationSupervisorId = reader.IsDBNull(reader.GetOrdinal("TransplantDestinationSupervisorId")) ? null : reader.GetInt32(reader.GetOrdinal("TransplantDestinationSupervisorId")),
                TransplantDestinationSupervisorName = reader.IsDBNull(reader.GetOrdinal("TransplantDestinationSupervisorName")) ? null : reader.GetString(reader.GetOrdinal("TransplantDestinationSupervisorName")),
                TransplantDate = reader.IsDBNull(reader.GetOrdinal("TransplantDate")) ? null : reader.GetDateTime(reader.GetOrdinal("TransplantDate")),
                TransplantRemarks = reader.IsDBNull(reader.GetOrdinal("TransplantRemarks")) ? null : reader.GetString(reader.GetOrdinal("TransplantRemarks"))
            };
        }
    }
}
