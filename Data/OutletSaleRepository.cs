using Microsoft.Data.SqlClient;
using PlantStockManager.Models;
using PlantStockManager.Services;

namespace PlantStockManager.Data
{
    // Phase E: one customer, several potted-plant and/or ready-tray items,
    // one atomic transaction -- each item deducts the EXISTING stock ledger
    // it belongs to (PottedPlantStock 'Dispatch', or ReadyStock reserve-then-
    // dispatch), under that row's own lock, exactly like a single-item
    // direct sale already does. If ANY item cannot be fulfilled, the whole
    // sale is rolled back -- no partial deduction of either stock type.
    public class OutletSaleRepository
    {
        private readonly DatabaseHelper _dbHelper;
        private readonly BatchNumberRepository _batchNumberRepo;
        private readonly PottedPlantStockRepository _pottedPlantStockRepo;
        private readonly ReadyStockRepository _readyStockRepo;

        public OutletSaleRepository(DatabaseHelper dbHelper, BatchNumberRepository batchNumberRepo,
            PottedPlantStockRepository pottedPlantStockRepo, ReadyStockRepository readyStockRepo)
        {
            _dbHelper = dbHelper;
            _batchNumberRepo = batchNumberRepo;
            _pottedPlantStockRepo = pottedPlantStockRepo;
            _readyStockRepo = readyStockRepo;
        }

        // StockId = PottedPlantStockId when StockType="Potted", or
        // ReadyStockId when StockType="Tray". Quantity = pots or WHOLE TRAYS.
        public record SaleItemInput(string StockType, int StockId, decimal Quantity);

        private const string HeaderSelect = @"
SELECT s.Id, s.SaleCode, s.OutletAreaId, a.Name AS OutletAreaName, s.CustomerName, s.CustomerContact, s.SaleDate,
       s.Remarks, s.CreatedById, s.CreatedBy, s.CreatedDate
FROM dbo.OutletSales s
INNER JOIN dbo.Area a ON a.Id = s.OutletAreaId";

        private const string ItemSelect = @"
SELECT i.Id, i.SaleId, i.OutletAreaId, i.StockType, i.PottedPlantStockId, i.ReadyStockId, i.SpeciesId,
       ps.Name AS SpeciesName, pt.Name AS PlantTypeName, ps.Color AS SpeciesColor, i.PotSize, i.CavityType, i.Quantity, i.CreatedDate
FROM dbo.OutletSaleItems i
INNER JOIN dbo.PlantSpecies ps ON ps.Id = i.SpeciesId
INNER JOIN dbo.PlantTypes pt ON pt.Id = ps.PlantTypeId";

        public async Task<List<OutletSale>> GetAllAsync(int? outletAreaId = null)
        {
            var list = new List<OutletSale>();
            using var conn = _dbHelper.GetConnection();
            await conn.OpenAsync();
            using (var cmd = new SqlCommand(HeaderSelect + " WHERE (@AreaId IS NULL OR s.OutletAreaId = @AreaId) ORDER BY s.SaleDate DESC, s.Id DESC", conn))
            {
                cmd.Parameters.AddWithValue("@AreaId", (object?)outletAreaId ?? DBNull.Value);
                using var r = await cmd.ExecuteReaderAsync();
                while (await r.ReadAsync())
                    list.Add(MapHeader(r));
            }
            if (list.Count == 0) return list;

            using (var cmd = new SqlCommand(ItemSelect + " WHERE i.SaleId IN (SELECT Id FROM dbo.OutletSales WHERE OutletAreaId = @AreaId OR @AreaId IS NULL)", conn))
            {
                cmd.Parameters.AddWithValue("@AreaId", (object?)outletAreaId ?? DBNull.Value);
                using var r = await cmd.ExecuteReaderAsync();
                var bySale = list.ToDictionary(s => s.Id);
                while (await r.ReadAsync())
                {
                    var item = MapItem(r);
                    if (bySale.TryGetValue(item.SaleId, out var sale))
                        sale.Items.Add(item);
                }
            }
            return list;
        }

        public async Task<OutletSale?> GetByIdAsync(int id)
        {
            using var conn = _dbHelper.GetConnection();
            await conn.OpenAsync();
            OutletSale? sale;
            using (var cmd = new SqlCommand(HeaderSelect + " WHERE s.Id = @Id", conn))
            {
                cmd.Parameters.AddWithValue("@Id", id);
                using var r = await cmd.ExecuteReaderAsync();
                if (!await r.ReadAsync()) return null;
                sale = MapHeader(r);
            }
            using (var cmd = new SqlCommand(ItemSelect + " WHERE i.SaleId = @Id", conn))
            {
                cmd.Parameters.AddWithValue("@Id", id);
                using var r = await cmd.ExecuteReaderAsync();
                while (await r.ReadAsync())
                    sale.Items.Add(MapItem(r));
            }
            return sale;
        }

        // Every item is checked (and, if all pass, deducted) inside ONE
        // transaction under each stock row's own lock -- the first item
        // that cannot be fulfilled rolls back everything already done in
        // this call, for BOTH stock types together.
        public async Task<(bool Success, string? Message, int Id)> InsertAsync(
            OutletSale header, IReadOnlyList<SaleItemInput> items, int outletAreaId, int? userId)
        {
            var (custOk, custError) = OutletRules.ValidateCustomer(header.CustomerName);
            if (!custOk)
                return (false, custError, 0);
            var (itemsOk, itemsError) = OutletRules.ValidateSaleHasItems(items.Count);
            if (!itemsOk)
                return (false, itemsError, 0);
            foreach (var line in items)
            {
                var (ok, error) = OutletRules.ValidateItemQuantity(line.Quantity);
                if (!ok)
                    return (false, error, 0);
            }

            using var conn = _dbHelper.GetConnection();
            await conn.OpenAsync();
            using var tx = conn.BeginTransaction();
            try
            {
                var code = await _batchNumberRepo.GetNextBatchNumberAsync(conn, tx, "OSL", header.SaleDate.Year);
                var headerCmd = new SqlCommand(@"
INSERT INTO dbo.OutletSales (SaleCode, OutletAreaId, CustomerName, CustomerContact, SaleDate, Remarks, CreatedById, CreatedBy)
VALUES (@Code, @AreaId, @Customer, @Contact, @Date, @Remarks, @CreatedById, @CreatedBy);
SELECT CAST(SCOPE_IDENTITY() AS INT);", conn, tx);
                headerCmd.Parameters.AddWithValue("@Code", code);
                headerCmd.Parameters.AddWithValue("@AreaId", outletAreaId);
                headerCmd.Parameters.AddWithValue("@Customer", header.CustomerName.Trim());
                headerCmd.Parameters.AddWithValue("@Contact", string.IsNullOrWhiteSpace(header.CustomerContact) ? DBNull.Value : header.CustomerContact.Trim());
                headerCmd.Parameters.AddWithValue("@Date", header.SaleDate.Date);
                headerCmd.Parameters.AddWithValue("@Remarks", (object?)header.Remarks ?? DBNull.Value);
                headerCmd.Parameters.AddWithValue("@CreatedById", (object?)userId ?? DBNull.Value);
                headerCmd.Parameters.AddWithValue("@CreatedBy", (object?)header.CreatedBy ?? DBNull.Value);
                var saleId = (int)(await headerCmd.ExecuteScalarAsync())!;

                foreach (var line in items)
                {
                    if (line.StockType == OutletStockType.Potted)
                    {
                        var (ok, message) = await SellPottedAsync(conn, tx, saleId, outletAreaId, line, userId, code, header.CustomerName);
                        if (!ok) { tx.Rollback(); return (false, message, 0); }
                    }
                    else if (line.StockType == OutletStockType.Tray)
                    {
                        var (ok, message) = await SellTrayAsync(conn, tx, saleId, outletAreaId, line, userId, code, header.CustomerName);
                        if (!ok) { tx.Rollback(); return (false, message, 0); }
                    }
                    else
                    {
                        tx.Rollback();
                        return (false, "Each item must be either a Potted Plant or a Ready Tray.", 0);
                    }
                }

                tx.Commit();
                header.Id = saleId;
                header.SaleCode = code;
                return (true, null, saleId);
            }
            catch (Exception ex)
            {
                try { tx.Rollback(); } catch { }
                return (false, ex.Message, 0);
            }
        }

        private async Task<(bool Ok, string? Message)> SellPottedAsync(
            SqlConnection conn, SqlTransaction tx, int saleId, int outletAreaId, SaleItemInput line, int? userId, string code, string customerName)
        {
            var lockCmd = new SqlCommand(
                "SELECT SpeciesId, PotSize, AreaId, PhysicalQuantity, ReservedQuantity, InTransitQuantity FROM dbo.PottedPlantStock WITH (UPDLOCK, HOLDLOCK) WHERE Id = @Id",
                conn, tx);
            lockCmd.Parameters.AddWithValue("@Id", line.StockId);
            int speciesId; string potSize; int? areaId; decimal available;
            using (var r = await lockCmd.ExecuteReaderAsync())
            {
                if (!await r.ReadAsync()) { r.Close(); return (false, "One of the items' stock was not found."); }
                speciesId = r.GetInt32(0);
                potSize = r.GetString(1);
                areaId = r.IsDBNull(2) ? null : r.GetInt32(2);
                available = r.GetDecimal(3) - r.GetDecimal(4) - r.GetDecimal(5);
            }
            var (ownOk, ownError) = OutletStockOwnershipRules.ValidateSameArea(areaId ?? -1, outletAreaId);
            if (!ownOk) return (false, ownError);
            var (availOk, availError) = OutletRules.ValidateSaleItemAvailable(line.Quantity, available);
            if (!availOk) return (false, availError);

            var itemCmd = new SqlCommand(@"
INSERT INTO dbo.OutletSaleItems (SaleId, OutletAreaId, StockType, PottedPlantStockId, SpeciesId, PotSize, Quantity)
VALUES (@SaleId, @AreaId, N'Potted', @StockId, @SpeciesId, @PotSize, @Quantity);
SELECT CAST(SCOPE_IDENTITY() AS INT);", conn, tx);
            itemCmd.Parameters.AddWithValue("@SaleId", saleId);
            itemCmd.Parameters.AddWithValue("@AreaId", outletAreaId);
            itemCmd.Parameters.AddWithValue("@StockId", line.StockId);
            itemCmd.Parameters.AddWithValue("@SpeciesId", speciesId);
            itemCmd.Parameters.AddWithValue("@PotSize", potSize);
            itemCmd.Parameters.AddWithValue("@Quantity", line.Quantity);
            var itemId = (int)(await itemCmd.ExecuteScalarAsync())!;

            var (dedOk, dedMessage) = await _pottedPlantStockRepo.RecordTransactionAsync(
                conn, tx, line.StockId, -line.Quantity, "Dispatch", "OutletSaleItem", itemId, userId, $"Outlet sale {code} to {customerName.Trim()}");
            if (!dedOk) return (false, dedMessage);
            var soldCmd = new SqlCommand("UPDATE dbo.PottedPlantStock SET SoldDispatchedQuantity = SoldDispatchedQuantity + @Q WHERE Id = @Id", conn, tx);
            soldCmd.Parameters.AddWithValue("@Q", line.Quantity);
            soldCmd.Parameters.AddWithValue("@Id", line.StockId);
            await soldCmd.ExecuteNonQueryAsync();
            return (true, null);
        }

        private async Task<(bool Ok, string? Message)> SellTrayAsync(
            SqlConnection conn, SqlTransaction tx, int saleId, int outletAreaId, SaleItemInput line, int? userId, string code, string customerName)
        {
            var lockCmd = new SqlCommand(
                "SELECT SpeciesId, CavityType, AreaId, Quantity, ReservedQuantity, DispatchedQuantity FROM dbo.ReadyStock WITH (UPDLOCK, HOLDLOCK) WHERE Id = @Id",
                conn, tx);
            lockCmd.Parameters.AddWithValue("@Id", line.StockId);
            int speciesId; string cavityType; int? areaId; decimal readyQty, reserved, dispatched;
            using (var r = await lockCmd.ExecuteReaderAsync())
            {
                if (!await r.ReadAsync()) { r.Close(); return (false, "One of the items' Ready Stock batches was not found."); }
                speciesId = r.GetInt32(0);
                cavityType = r.GetString(1);
                areaId = r.IsDBNull(2) ? null : r.GetInt32(2);
                readyQty = r.GetDecimal(3);
                reserved = r.GetDecimal(4);
                dispatched = r.GetDecimal(5);
            }
            var (ownOk, ownError) = OutletStockOwnershipRules.ValidateSameArea(areaId ?? -1, outletAreaId);
            if (!ownOk) return (false, ownError);

            var cavitySize = DirectSowingRules.CavityCount(cavityType);
            if (cavitySize is null or <= 0)
                return (false, "This batch's cavity is not recognised.");
            var availableTrays = (readyQty - reserved - dispatched) / cavitySize.Value;
            var (availOk, availError) = OutletRules.ValidateSaleItemAvailable(line.Quantity, availableTrays);
            if (!availOk) return (false, availError);

            var itemCmd = new SqlCommand(@"
INSERT INTO dbo.OutletSaleItems (SaleId, OutletAreaId, StockType, ReadyStockId, SpeciesId, CavityType, Quantity)
VALUES (@SaleId, @AreaId, N'Tray', @StockId, @SpeciesId, @CavityType, @Quantity);
SELECT CAST(SCOPE_IDENTITY() AS INT);", conn, tx);
            itemCmd.Parameters.AddWithValue("@SaleId", saleId);
            itemCmd.Parameters.AddWithValue("@AreaId", outletAreaId);
            itemCmd.Parameters.AddWithValue("@StockId", line.StockId);
            itemCmd.Parameters.AddWithValue("@SpeciesId", speciesId);
            itemCmd.Parameters.AddWithValue("@CavityType", cavityType);
            itemCmd.Parameters.AddWithValue("@Quantity", line.Quantity);
            var itemId = (int)(await itemCmd.ExecuteScalarAsync())!;

            var seedlings = line.Quantity * cavitySize.Value;
            var (resOk, resMessage) = await _readyStockRepo.RecordReservationAsync(
                conn, tx, line.StockId, seedlings, "OutletSaleItem", itemId, userId, $"Outlet sale {code}");
            if (!resOk) return (false, resMessage);
            var (dispOk, dispMessage) = await _readyStockRepo.RecordDispatchAsync(
                conn, tx, line.StockId, seedlings, "OutletSaleItem", itemId, userId, $"Outlet sale {code} to {customerName.Trim()}", "Dispatch");
            if (!dispOk) return (false, dispMessage);
            return (true, null);
        }

        private static OutletSale MapHeader(SqlDataReader r) => new()
        {
            Id = r.GetInt32(0),
            SaleCode = r.GetString(1),
            OutletAreaId = r.GetInt32(2),
            OutletAreaName = r.GetString(3),
            CustomerName = r.GetString(4),
            CustomerContact = r.IsDBNull(5) ? null : r.GetString(5),
            SaleDate = r.GetDateTime(6),
            Remarks = r.IsDBNull(7) ? null : r.GetString(7),
            CreatedById = r.IsDBNull(8) ? null : r.GetInt32(8),
            CreatedBy = r.IsDBNull(9) ? null : r.GetString(9),
            CreatedDate = r.GetDateTime(10)
        };

        private static OutletSaleItem MapItem(SqlDataReader r) => new()
        {
            Id = r.GetInt32(0),
            SaleId = r.GetInt32(1),
            OutletAreaId = r.GetInt32(2),
            StockType = r.GetString(3),
            PottedPlantStockId = r.IsDBNull(4) ? null : r.GetInt32(4),
            ReadyStockId = r.IsDBNull(5) ? null : r.GetInt32(5),
            SpeciesId = r.GetInt32(6),
            SpeciesName = r.GetString(7),
            PlantTypeName = r.GetString(8),
            SpeciesColor = r.IsDBNull(9) ? null : r.GetString(9),
            PotSize = r.IsDBNull(10) ? null : r.GetString(10),
            CavityType = r.IsDBNull(11) ? null : r.GetString(11),
            Quantity = r.GetDecimal(12),
            CreatedDate = r.GetDateTime(13)
        };
    }
}
