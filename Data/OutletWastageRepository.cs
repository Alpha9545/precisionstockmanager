using Microsoft.Data.SqlClient;
using PlantStockManager.Models;
using PlantStockManager.Services;

namespace PlantStockManager.Data
{
    // Phase E: the Outlet's own record of losing some of its potted-plant or
    // ready-tray stock (breakage, wilting, pests...) -- reuses the EXISTING
    // stock ledgers (PottedPlantStockTransactions / ReadyStockTransactions,
    // 'Wastage' transaction type on both), never a new inventory system. A
    // different concept from nursery production wastage: no automatic
    // percentage is ever applied here.
    public class OutletWastageRepository
    {
        private readonly DatabaseHelper _dbHelper;
        private readonly BatchNumberRepository _batchNumberRepo;
        private readonly PottedPlantStockRepository _pottedPlantStockRepo;
        private readonly ReadyStockRepository _readyStockRepo;

        public OutletWastageRepository(DatabaseHelper dbHelper, BatchNumberRepository batchNumberRepo,
            PottedPlantStockRepository pottedPlantStockRepo, ReadyStockRepository readyStockRepo)
        {
            _dbHelper = dbHelper;
            _batchNumberRepo = batchNumberRepo;
            _pottedPlantStockRepo = pottedPlantStockRepo;
            _readyStockRepo = readyStockRepo;
        }

        private const string BaseSelect = @"
SELECT w.Id, w.WastageCode, w.WastageDate, w.OutletAreaId, a.Name AS OutletAreaName, w.StockType,
       w.PottedPlantStockId, w.ReadyStockId, w.SpeciesId, ps.Name AS SpeciesName, pt.Name AS PlantTypeName, ps.Color AS SpeciesColor,
       w.PotSize, w.CavityType, w.Quantity, w.Reason, w.Remarks, w.CreatedById, w.CreatedBy, w.CreatedDate
FROM dbo.OutletWastages w
INNER JOIN dbo.Area a ON a.Id = w.OutletAreaId
INNER JOIN dbo.PlantSpecies ps ON ps.Id = w.SpeciesId
INNER JOIN dbo.PlantTypes pt ON pt.Id = ps.PlantTypeId";

        public async Task<List<OutletWastage>> GetAllAsync(int? outletAreaId = null)
        {
            var list = new List<OutletWastage>();
            using var conn = _dbHelper.GetConnection();
            await conn.OpenAsync();
            using var cmd = new SqlCommand(BaseSelect + " WHERE (@AreaId IS NULL OR w.OutletAreaId = @AreaId) ORDER BY w.WastageDate DESC, w.Id DESC", conn);
            cmd.Parameters.AddWithValue("@AreaId", (object?)outletAreaId ?? DBNull.Value);
            using var r = await cmd.ExecuteReaderAsync();
            while (await r.ReadAsync())
                list.Add(Map(r));
            return list;
        }

        // stockId = PottedPlantStockId when stockType="Potted", or
        // ReadyStockId when stockType="Tray". quantity = pots or WHOLE TRAYS.
        public async Task<(bool Success, string? Message, int Id)> InsertAsync(
            string stockType, int stockId, decimal quantity, string reason, string? remarks,
            DateTime wastageDate, int outletAreaId, int? userId, string? createdBy)
        {
            if (wastageDate == default || wastageDate.Date > DateTime.Today)
                return (false, "Enter a wastage date that is not in the future.", 0);

            using var conn = _dbHelper.GetConnection();
            await conn.OpenAsync();
            using var tx = conn.BeginTransaction();
            try
            {
                if (stockType == OutletStockType.Potted)
                    return await WastePottedAsync(conn, tx, stockId, quantity, reason, remarks, wastageDate, outletAreaId, userId, createdBy);
                if (stockType == OutletStockType.Tray)
                    return await WasteTrayAsync(conn, tx, stockId, quantity, reason, remarks, wastageDate, outletAreaId, userId, createdBy);
                tx.Rollback();
                return (false, "Choose Potted Plant or Ready Tray.", 0);
            }
            catch (Exception ex)
            {
                try { tx.Rollback(); } catch { }
                return (false, ex.Message, 0);
            }
        }

        private async Task<(bool, string?, int)> WastePottedAsync(
            SqlConnection conn, SqlTransaction tx, int stockId, decimal quantity, string reason, string? remarks,
            DateTime wastageDate, int outletAreaId, int? userId, string? createdBy)
        {
            var lockCmd = new SqlCommand(
                "SELECT SpeciesId, PotSize, AreaId, PhysicalQuantity, ReservedQuantity FROM dbo.PottedPlantStock WITH (UPDLOCK, HOLDLOCK) WHERE Id = @Id", conn, tx);
            lockCmd.Parameters.AddWithValue("@Id", stockId);
            int speciesId; string potSize; int? areaId; decimal available;
            using (var r = await lockCmd.ExecuteReaderAsync())
            {
                if (!await r.ReadAsync()) { r.Close(); tx.Rollback(); return (false, "Stock not found.", 0); }
                speciesId = r.GetInt32(0);
                potSize = r.GetString(1);
                areaId = r.IsDBNull(2) ? null : r.GetInt32(2);
                available = r.GetDecimal(3) - r.GetDecimal(4);
            }
            var (ownOk, ownError) = OutletStockOwnershipRules.ValidateSameArea(areaId ?? -1, outletAreaId);
            if (!ownOk) { tx.Rollback(); return (false, ownError, 0); }
            var (ok, error) = OutletWastageRules.Validate(quantity, available, reason);
            if (!ok) { tx.Rollback(); return (false, error, 0); }

            var code = await _batchNumberRepo.GetNextBatchNumberAsync(conn, tx, "OWS", wastageDate.Year);
            var insert = new SqlCommand(@"
INSERT INTO dbo.OutletWastages (WastageCode, WastageDate, OutletAreaId, StockType, PottedPlantStockId, SpeciesId, PotSize, Quantity, Reason, Remarks, CreatedById, CreatedBy)
VALUES (@Code, @Date, @AreaId, N'Potted', @StockId, @SpeciesId, @PotSize, @Quantity, @Reason, @Remarks, @CreatedById, @CreatedBy);
SELECT CAST(SCOPE_IDENTITY() AS INT);", conn, tx);
            insert.Parameters.AddWithValue("@Code", code);
            insert.Parameters.AddWithValue("@Date", wastageDate.Date);
            insert.Parameters.AddWithValue("@AreaId", outletAreaId);
            insert.Parameters.AddWithValue("@StockId", stockId);
            insert.Parameters.AddWithValue("@SpeciesId", speciesId);
            insert.Parameters.AddWithValue("@PotSize", potSize);
            insert.Parameters.AddWithValue("@Quantity", quantity);
            insert.Parameters.AddWithValue("@Reason", reason);
            insert.Parameters.AddWithValue("@Remarks", (object?)remarks ?? DBNull.Value);
            insert.Parameters.AddWithValue("@CreatedById", (object?)userId ?? DBNull.Value);
            insert.Parameters.AddWithValue("@CreatedBy", (object?)createdBy ?? DBNull.Value);
            var newId = (int)(await insert.ExecuteScalarAsync())!;

            var (dedOk, dedMessage) = await _pottedPlantStockRepo.RecordTransactionAsync(
                conn, tx, stockId, -quantity, "Wastage", "OutletWastage", newId, userId, $"Outlet wastage {code}: {reason}" + (string.IsNullOrWhiteSpace(remarks) ? "" : $" - {remarks}"));
            if (!dedOk) { tx.Rollback(); return (false, dedMessage, 0); }
            var wastedCmd = new SqlCommand("UPDATE dbo.PottedPlantStock SET WastedQuantity = WastedQuantity + @Q WHERE Id = @Id", conn, tx);
            wastedCmd.Parameters.AddWithValue("@Q", quantity);
            wastedCmd.Parameters.AddWithValue("@Id", stockId);
            await wastedCmd.ExecuteNonQueryAsync();

            tx.Commit();
            return (true, null, newId);
        }

        private async Task<(bool, string?, int)> WasteTrayAsync(
            SqlConnection conn, SqlTransaction tx, int stockId, decimal quantity, string reason, string? remarks,
            DateTime wastageDate, int outletAreaId, int? userId, string? createdBy)
        {
            var lockCmd = new SqlCommand(
                "SELECT SpeciesId, CavityType, AreaId, Quantity, ReservedQuantity, DispatchedQuantity FROM dbo.ReadyStock WITH (UPDLOCK, HOLDLOCK) WHERE Id = @Id", conn, tx);
            lockCmd.Parameters.AddWithValue("@Id", stockId);
            int speciesId; string cavityType; int? areaId; decimal readyQty, reserved, dispatched;
            using (var r = await lockCmd.ExecuteReaderAsync())
            {
                if (!await r.ReadAsync()) { r.Close(); tx.Rollback(); return (false, "Ready Stock batch not found.", 0); }
                speciesId = r.GetInt32(0);
                cavityType = r.GetString(1);
                areaId = r.IsDBNull(2) ? null : r.GetInt32(2);
                readyQty = r.GetDecimal(3);
                reserved = r.GetDecimal(4);
                dispatched = r.GetDecimal(5);
            }
            var (ownOk, ownError) = OutletStockOwnershipRules.ValidateSameArea(areaId ?? -1, outletAreaId);
            if (!ownOk) { tx.Rollback(); return (false, ownError, 0); }
            var cavitySize = DirectSowingRules.CavityCount(cavityType);
            if (cavitySize is null or <= 0) { tx.Rollback(); return (false, "This batch's cavity is not recognised.", 0); }
            var availableTrays = (readyQty - reserved - dispatched) / cavitySize.Value;
            var (ok, error) = OutletWastageRules.Validate(quantity, availableTrays, reason);
            if (!ok) { tx.Rollback(); return (false, error, 0); }

            var code = await _batchNumberRepo.GetNextBatchNumberAsync(conn, tx, "OWS", wastageDate.Year);
            var insert = new SqlCommand(@"
INSERT INTO dbo.OutletWastages (WastageCode, WastageDate, OutletAreaId, StockType, ReadyStockId, SpeciesId, CavityType, Quantity, Reason, Remarks, CreatedById, CreatedBy)
VALUES (@Code, @Date, @AreaId, N'Tray', @StockId, @SpeciesId, @CavityType, @Quantity, @Reason, @Remarks, @CreatedById, @CreatedBy);
SELECT CAST(SCOPE_IDENTITY() AS INT);", conn, tx);
            insert.Parameters.AddWithValue("@Code", code);
            insert.Parameters.AddWithValue("@Date", wastageDate.Date);
            insert.Parameters.AddWithValue("@AreaId", outletAreaId);
            insert.Parameters.AddWithValue("@StockId", stockId);
            insert.Parameters.AddWithValue("@SpeciesId", speciesId);
            insert.Parameters.AddWithValue("@CavityType", cavityType);
            insert.Parameters.AddWithValue("@Quantity", quantity);
            insert.Parameters.AddWithValue("@Reason", reason);
            insert.Parameters.AddWithValue("@Remarks", (object?)remarks ?? DBNull.Value);
            insert.Parameters.AddWithValue("@CreatedById", (object?)userId ?? DBNull.Value);
            insert.Parameters.AddWithValue("@CreatedBy", (object?)createdBy ?? DBNull.Value);
            var newId = (int)(await insert.ExecuteScalarAsync())!;

            var seedlings = quantity * cavitySize.Value;
            var (resOk, resMessage) = await _readyStockRepo.RecordReservationAsync(
                conn, tx, stockId, seedlings, "OutletWastage", newId, userId, $"Outlet wastage {code}: {reason}");
            if (!resOk) { tx.Rollback(); return (false, resMessage, 0); }
            var (wasteOk, wasteMessage) = await _readyStockRepo.RecordDispatchAsync(
                conn, tx, stockId, seedlings, "OutletWastage", newId, userId,
                $"Outlet wastage {code}: {reason}" + (string.IsNullOrWhiteSpace(remarks) ? "" : $" - {remarks}"), "Wastage");
            if (!wasteOk) { tx.Rollback(); return (false, wasteMessage, 0); }

            tx.Commit();
            return (true, null, newId);
        }

        private static OutletWastage Map(SqlDataReader r) => new()
        {
            Id = r.GetInt32(0),
            WastageCode = r.GetString(1),
            WastageDate = r.GetDateTime(2),
            OutletAreaId = r.GetInt32(3),
            OutletAreaName = r.GetString(4),
            StockType = r.GetString(5),
            PottedPlantStockId = r.IsDBNull(6) ? null : r.GetInt32(6),
            ReadyStockId = r.IsDBNull(7) ? null : r.GetInt32(7),
            SpeciesId = r.GetInt32(8),
            SpeciesName = r.GetString(9),
            PlantTypeName = r.GetString(10),
            SpeciesColor = r.IsDBNull(11) ? null : r.GetString(11),
            PotSize = r.IsDBNull(12) ? null : r.GetString(12),
            CavityType = r.IsDBNull(13) ? null : r.GetString(13),
            Quantity = r.GetDecimal(14),
            Reason = r.GetString(15),
            Remarks = r.IsDBNull(16) ? null : r.GetString(16),
            CreatedById = r.IsDBNull(17) ? null : r.GetInt32(17),
            CreatedBy = r.IsDBNull(18) ? null : r.GetString(18),
            CreatedDate = r.GetDateTime(19)
        };
    }
}
