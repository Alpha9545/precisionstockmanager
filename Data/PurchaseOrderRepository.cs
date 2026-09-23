using Microsoft.Data.SqlClient;
using PlantStockManager.Models;

namespace PlantStockManager.Data
{
    // Phase 11: Purchase Orders with multi-category line items
    // ('Fertilizer' | 'EmptyPot' | 'Other'). Receiving a PO (InsertReceiptAsync)
    // is the only place stock actually moves:
    //   - 'Fertilizer' lines insert a new dbo.FertilizerStock batch row,
    //     in exactly the shape Pages/Fertilizer/Stock.cshtml.cs already
    //     writes by hand -- that table has no ledger of its own, each
    //     purchase/receipt is its own batch row.
    //   - 'EmptyPot' lines post a 'StockIn' entry to the existing
    //     dbo.EmptyPotInventory ledger via EmptyPotInventoryRepository,
    //     exactly like a manual "Add Stock" would.
    //   - 'Other' lines are paperwork only -- no stock table exists for
    //     miscellaneous supplies.
    // Per Decision 2, dbo.VendorPurchases/VendorPurchaseRepository are
    // never read or touched here.
    public class PurchaseOrderRepository
    {
        private readonly DatabaseHelper _dbHelper;
        private readonly BatchNumberRepository _batchNumberRepo;
        private readonly EmptyPotInventoryRepository _emptyPotInventoryRepo;

        public PurchaseOrderRepository(
            DatabaseHelper dbHelper,
            BatchNumberRepository batchNumberRepo,
            EmptyPotInventoryRepository emptyPotInventoryRepo)
        {
            _dbHelper = dbHelper;
            _batchNumberRepo = batchNumberRepo;
            _emptyPotInventoryRepo = emptyPotInventoryRepo;
        }

        private const string OrderBaseSelect = @"
SELECT po.Id, po.PurchaseOrderCode, po.VendorId, v.Name AS VendorName, po.OrderDate, po.ExpectedDeliveryDate,
       po.Status, po.Remarks, po.CreatedDate, po.CreatedBy, po.ModifiedDate, po.ModifiedBy
FROM dbo.PurchaseOrders po
INNER JOIN dbo.Vendors v ON po.VendorId = v.Id";

        private const string ItemBaseSelect = @"
SELECT poi.Id, poi.PurchaseOrderId, poi.ItemCategory,
       poi.FertilizerId, fm.FertilizerName, poi.FertilizerSourceId, fs.SourceName AS FertilizerSourceName,
       poi.FertilizerUnitId, um.UnitName AS FertilizerUnitName, poi.ExpiryDate,
       poi.PotSize, poi.AreaId, a.Name AS AreaName, poi.ItemName,
       poi.OrderedQuantity, poi.ReceivedQuantity, poi.UnitPrice, poi.Remarks,
       poi.CreatedDate, poi.CreatedBy, poi.ModifiedDate, poi.ModifiedBy
FROM dbo.PurchaseOrderItems poi
LEFT JOIN dbo.FertilizerMaster fm ON poi.FertilizerId = fm.FertilizerId
LEFT JOIN dbo.FertilizerSource fs ON poi.FertilizerSourceId = fs.SourceId
LEFT JOIN dbo.UnitMaster um ON poi.FertilizerUnitId = um.UnitId
LEFT JOIN dbo.Area a ON poi.AreaId = a.Id";

        public async Task<List<PurchaseOrder>> GetAllAsync(string? status = null)
        {
            var list = new List<PurchaseOrder>();
            using var conn = _dbHelper.GetConnection();
            await conn.OpenAsync();

            var sql = OrderBaseSelect + " WHERE (@Status IS NULL OR po.Status = @Status) ORDER BY po.CreatedDate DESC";
            using var cmd = new SqlCommand(sql, conn);
            cmd.Parameters.AddWithValue("@Status", (object?)status ?? DBNull.Value);
            using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                list.Add(MapOrder(reader));
            }
            return list;
        }

        public async Task<PurchaseOrder?> GetByIdAsync(int id)
        {
            using var conn = _dbHelper.GetConnection();
            await conn.OpenAsync();

            PurchaseOrder? order;
            var sql = OrderBaseSelect + " WHERE po.Id = @Id";
            using (var cmd = new SqlCommand(sql, conn))
            {
                cmd.Parameters.AddWithValue("@Id", id);
                using var reader = await cmd.ExecuteReaderAsync();
                if (!await reader.ReadAsync())
                    return null;
                order = MapOrder(reader);
            }

            var itemSql = ItemBaseSelect + " WHERE poi.PurchaseOrderId = @Id ORDER BY poi.Id";
            using (var itemCmd = new SqlCommand(itemSql, conn))
            {
                itemCmd.Parameters.AddWithValue("@Id", id);
                using var itemReader = await itemCmd.ExecuteReaderAsync();
                while (await itemReader.ReadAsync())
                {
                    order.Items.Add(MapItem(itemReader));
                }
            }

            return order;
        }

        public async Task<List<PurchaseReceipt>> GetReceiptsAsync(int purchaseOrderId)
        {
            var receipts = new List<PurchaseReceipt>();
            using var conn = _dbHelper.GetConnection();
            await conn.OpenAsync();

            const string headerSql = @"
SELECT r.Id, r.PurchaseOrderId, po.PurchaseOrderCode, r.ReceiptDate, r.ReceivedById, u.Name AS ReceivedByName,
       r.Remarks, r.CreatedDate, r.CreatedBy
FROM dbo.PurchaseReceipts r
INNER JOIN dbo.PurchaseOrders po ON r.PurchaseOrderId = po.Id
LEFT JOIN dbo.IMSUsers u ON r.ReceivedById = u.Id
WHERE r.PurchaseOrderId = @Id
ORDER BY r.ReceiptDate DESC";

            using (var cmd = new SqlCommand(headerSql, conn))
            {
                cmd.Parameters.AddWithValue("@Id", purchaseOrderId);
                using var reader = await cmd.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    receipts.Add(new PurchaseReceipt
                    {
                        Id = reader.GetInt32(reader.GetOrdinal("Id")),
                        PurchaseOrderId = reader.GetInt32(reader.GetOrdinal("PurchaseOrderId")),
                        PurchaseOrderCode = reader.GetString(reader.GetOrdinal("PurchaseOrderCode")),
                        ReceiptDate = reader.GetDateTime(reader.GetOrdinal("ReceiptDate")),
                        ReceivedById = reader.IsDBNull(reader.GetOrdinal("ReceivedById")) ? null : reader.GetInt32(reader.GetOrdinal("ReceivedById")),
                        ReceivedByName = reader.IsDBNull(reader.GetOrdinal("ReceivedByName")) ? null : reader.GetString(reader.GetOrdinal("ReceivedByName")),
                        Remarks = reader.IsDBNull(reader.GetOrdinal("Remarks")) ? null : reader.GetString(reader.GetOrdinal("Remarks")),
                        CreatedDate = reader.GetDateTime(reader.GetOrdinal("CreatedDate")),
                        CreatedBy = reader.IsDBNull(reader.GetOrdinal("CreatedBy")) ? null : reader.GetString(reader.GetOrdinal("CreatedBy"))
                    });
                }
            }

            const string lineSql = @"
SELECT ri.Id, ri.PurchaseReceiptId, ri.PurchaseOrderItemId, ri.ReceivedQuantity, ri.CreatedDate,
       poi.ItemCategory, poi.PotSize, poi.ItemName, fm.FertilizerName
FROM dbo.PurchaseReceiptItems ri
INNER JOIN dbo.PurchaseOrderItems poi ON ri.PurchaseOrderItemId = poi.Id
LEFT JOIN dbo.FertilizerMaster fm ON poi.FertilizerId = fm.FertilizerId
WHERE ri.PurchaseReceiptId = @ReceiptId
ORDER BY ri.Id";

            foreach (var receipt in receipts)
            {
                using var lineCmd = new SqlCommand(lineSql, conn);
                lineCmd.Parameters.AddWithValue("@ReceiptId", receipt.Id);
                using var lineReader = await lineCmd.ExecuteReaderAsync();
                while (await lineReader.ReadAsync())
                {
                    var category = lineReader.GetString(lineReader.GetOrdinal("ItemCategory"));
                    var displayName = category switch
                    {
                        "Fertilizer" => lineReader.IsDBNull(lineReader.GetOrdinal("FertilizerName")) ? "(Fertilizer)" : lineReader.GetString(lineReader.GetOrdinal("FertilizerName")),
                        "EmptyPot" => $"Empty Pot - {(lineReader.IsDBNull(lineReader.GetOrdinal("PotSize")) ? "" : lineReader.GetString(lineReader.GetOrdinal("PotSize")))}",
                        _ => lineReader.IsDBNull(lineReader.GetOrdinal("ItemName")) ? "(Other)" : lineReader.GetString(lineReader.GetOrdinal("ItemName"))
                    };

                    receipt.Items.Add(new PurchaseReceiptItem
                    {
                        Id = lineReader.GetInt32(lineReader.GetOrdinal("Id")),
                        PurchaseReceiptId = lineReader.GetInt32(lineReader.GetOrdinal("PurchaseReceiptId")),
                        PurchaseOrderItemId = lineReader.GetInt32(lineReader.GetOrdinal("PurchaseOrderItemId")),
                        ReceivedQuantity = lineReader.GetDecimal(lineReader.GetOrdinal("ReceivedQuantity")),
                        CreatedDate = lineReader.GetDateTime(lineReader.GetOrdinal("CreatedDate")),
                        ItemDisplayName = displayName
                    });
                }
            }

            return receipts;
        }

        public async Task<(bool Success, string? Message, int Id)> InsertAsync(PurchaseOrder order, int? userId)
        {
            if (order.VendorId <= 0)
                return (false, "Vendor is required.", 0);
            if (order.Items == null || order.Items.Count == 0)
                return (false, "At least one line item is required.", 0);

            foreach (var item in order.Items)
            {
                if (item.OrderedQuantity <= 0)
                    return (false, "Every line item must have an Ordered Quantity greater than zero.", 0);

                switch (item.ItemCategory)
                {
                    case "Fertilizer":
                        if (item.FertilizerId == null || item.FertilizerSourceId == null || item.FertilizerUnitId == null)
                            return (false, "Fertilizer lines require Fertilizer, Source and Unit.", 0);
                        item.PotSize = null; item.AreaId = null; item.ItemName = null;
                        break;
                    case "EmptyPot":
                        if (string.IsNullOrWhiteSpace(item.PotSize))
                            return (false, "Empty Pot lines require a Pot Size.", 0);
                        item.FertilizerId = null; item.FertilizerSourceId = null; item.FertilizerUnitId = null; item.ExpiryDate = null; item.ItemName = null;
                        break;
                    case "Other":
                        if (string.IsNullOrWhiteSpace(item.ItemName))
                            return (false, "Other lines require an Item Name.", 0);
                        item.FertilizerId = null; item.FertilizerSourceId = null; item.FertilizerUnitId = null; item.ExpiryDate = null; item.PotSize = null; item.AreaId = null;
                        break;
                    default:
                        return (false, $"Unknown item category '{item.ItemCategory}'.", 0);
                }
            }

            using var conn = _dbHelper.GetConnection();
            await conn.OpenAsync();
            using var tx = conn.BeginTransaction();

            try
            {
                var code = await _batchNumberRepo.GetNextBatchNumberAsync(conn, tx, "PO", order.OrderDate.Year);

                const string insertOrderSql = @"
INSERT INTO dbo.PurchaseOrders (PurchaseOrderCode, VendorId, OrderDate, ExpectedDeliveryDate, Status, Remarks, CreatedDate, CreatedBy)
VALUES (@Code, @VendorId, @OrderDate, @ExpectedDeliveryDate, 'Pending', @Remarks, SYSUTCDATETIME(), @CreatedBy);
SELECT CAST(SCOPE_IDENTITY() AS INT);";

                using var orderCmd = new SqlCommand(insertOrderSql, conn, tx);
                orderCmd.Parameters.AddWithValue("@Code", code);
                orderCmd.Parameters.AddWithValue("@VendorId", order.VendorId);
                orderCmd.Parameters.AddWithValue("@OrderDate", order.OrderDate);
                orderCmd.Parameters.AddWithValue("@ExpectedDeliveryDate", (object?)order.ExpectedDeliveryDate ?? DBNull.Value);
                orderCmd.Parameters.AddWithValue("@Remarks", (object?)order.Remarks ?? DBNull.Value);
                orderCmd.Parameters.AddWithValue("@CreatedBy", (object?)order.CreatedBy ?? DBNull.Value);

                var newOrderId = (int)await orderCmd.ExecuteScalarAsync();

                const string insertItemSql = @"
INSERT INTO dbo.PurchaseOrderItems
(PurchaseOrderId, ItemCategory, FertilizerId, FertilizerSourceId, FertilizerUnitId, ExpiryDate,
 PotSize, AreaId, ItemName, OrderedQuantity, ReceivedQuantity, UnitPrice, Remarks, CreatedDate, CreatedBy)
VALUES
(@PurchaseOrderId, @ItemCategory, @FertilizerId, @FertilizerSourceId, @FertilizerUnitId, @ExpiryDate,
 @PotSize, @AreaId, @ItemName, @OrderedQuantity, 0, @UnitPrice, @Remarks, SYSUTCDATETIME(), @CreatedBy);";

                foreach (var item in order.Items)
                {
                    using var itemCmd = new SqlCommand(insertItemSql, conn, tx);
                    itemCmd.Parameters.AddWithValue("@PurchaseOrderId", newOrderId);
                    itemCmd.Parameters.AddWithValue("@ItemCategory", item.ItemCategory);
                    itemCmd.Parameters.AddWithValue("@FertilizerId", (object?)item.FertilizerId ?? DBNull.Value);
                    itemCmd.Parameters.AddWithValue("@FertilizerSourceId", (object?)item.FertilizerSourceId ?? DBNull.Value);
                    itemCmd.Parameters.AddWithValue("@FertilizerUnitId", (object?)item.FertilizerUnitId ?? DBNull.Value);
                    itemCmd.Parameters.AddWithValue("@ExpiryDate", (object?)item.ExpiryDate ?? DBNull.Value);
                    itemCmd.Parameters.AddWithValue("@PotSize", (object?)item.PotSize ?? DBNull.Value);
                    itemCmd.Parameters.AddWithValue("@AreaId", (object?)item.AreaId ?? DBNull.Value);
                    itemCmd.Parameters.AddWithValue("@ItemName", (object?)item.ItemName ?? DBNull.Value);
                    itemCmd.Parameters.AddWithValue("@OrderedQuantity", item.OrderedQuantity);
                    itemCmd.Parameters.AddWithValue("@UnitPrice", (object?)item.UnitPrice ?? DBNull.Value);
                    itemCmd.Parameters.AddWithValue("@Remarks", (object?)item.Remarks ?? DBNull.Value);
                    itemCmd.Parameters.AddWithValue("@CreatedBy", (object?)item.CreatedBy ?? DBNull.Value);
                    await itemCmd.ExecuteNonQueryAsync();
                }

                tx.Commit();
                order.Id = newOrderId;
                order.PurchaseOrderCode = code;
                order.Status = "Pending";
                return (true, null, newOrderId);
            }
            catch (Exception ex)
            {
                tx.Rollback();
                return (false, ex.Message, 0);
            }
        }

        // Records one Purchase Receipt event covering one or more lines
        // of the given Purchase Order. Each line's quantity is capped at
        // that line's own PendingQuantity (never over-receive). Stock
        // integration per category happens here, inside the same
        // transaction as the receipt/item-total bookkeeping, so either
        // the whole receipt is recorded (paperwork + stock together) or
        // none of it is.
        public async Task<(bool Success, string? Message, int Id)> InsertReceiptAsync(
            int purchaseOrderId, List<(int PurchaseOrderItemId, decimal ReceivedQuantity)> lines,
            DateTime receiptDate, int? receivedById, string? remarks, string? createdBy, int? userId)
        {
            if (lines == null || lines.Count == 0)
                return (false, "At least one line must have a quantity to receive.", 0);

            using var conn = _dbHelper.GetConnection();
            await conn.OpenAsync();
            using var tx = conn.BeginTransaction();

            try
            {
                var orderLockCmd = new SqlCommand(
                    "SELECT PurchaseOrderCode, Status FROM dbo.PurchaseOrders WITH (UPDLOCK, HOLDLOCK) WHERE Id = @Id",
                    conn, tx);
                orderLockCmd.Parameters.AddWithValue("@Id", purchaseOrderId);
                using var orderReader = await orderLockCmd.ExecuteReaderAsync();
                if (!await orderReader.ReadAsync())
                {
                    orderReader.Close();
                    tx.Rollback();
                    return (false, "Purchase Order not found.", 0);
                }
                var poCode = orderReader.GetString(orderReader.GetOrdinal("PurchaseOrderCode"));
                var poStatus = orderReader.GetString(orderReader.GetOrdinal("Status"));
                orderReader.Close();

                if (poStatus == "Completed" || poStatus == "Cancelled")
                {
                    tx.Rollback();
                    return (false, $"This Purchase Order is '{poStatus}' and cannot receive any more stock.", 0);
                }

                // Insert the receipt header first so its Id is available
                // as ReferenceId on the EmptyPot ledger entries.
                const string insertReceiptSql = @"
INSERT INTO dbo.PurchaseReceipts (PurchaseOrderId, ReceiptDate, ReceivedById, Remarks, CreatedDate, CreatedBy)
VALUES (@PurchaseOrderId, @ReceiptDate, @ReceivedById, @Remarks, SYSUTCDATETIME(), @CreatedBy);
SELECT CAST(SCOPE_IDENTITY() AS INT);";

                using var receiptCmd = new SqlCommand(insertReceiptSql, conn, tx);
                receiptCmd.Parameters.AddWithValue("@PurchaseOrderId", purchaseOrderId);
                receiptCmd.Parameters.AddWithValue("@ReceiptDate", receiptDate);
                receiptCmd.Parameters.AddWithValue("@ReceivedById", (object?)receivedById ?? DBNull.Value);
                receiptCmd.Parameters.AddWithValue("@Remarks", (object?)remarks ?? DBNull.Value);
                receiptCmd.Parameters.AddWithValue("@CreatedBy", (object?)createdBy ?? DBNull.Value);
                var newReceiptId = (int)await receiptCmd.ExecuteScalarAsync();

                foreach (var (itemId, receivedQty) in lines)
                {
                    if (receivedQty <= 0)
                        continue;

                    var itemLockCmd = new SqlCommand(
                        @"SELECT ItemCategory, FertilizerId, FertilizerSourceId, FertilizerUnitId, ExpiryDate, PotSize, AreaId,
                                 OrderedQuantity, ReceivedQuantity
                          FROM dbo.PurchaseOrderItems WITH (UPDLOCK, HOLDLOCK)
                          WHERE Id = @Id AND PurchaseOrderId = @PurchaseOrderId",
                        conn, tx);
                    itemLockCmd.Parameters.AddWithValue("@Id", itemId);
                    itemLockCmd.Parameters.AddWithValue("@PurchaseOrderId", purchaseOrderId);
                    using var itemReader = await itemLockCmd.ExecuteReaderAsync();
                    if (!await itemReader.ReadAsync())
                    {
                        itemReader.Close();
                        tx.Rollback();
                        return (false, $"Line item {itemId} does not belong to this Purchase Order.", 0);
                    }

                    var category = itemReader.GetString(itemReader.GetOrdinal("ItemCategory"));
                    var fertilizerId = itemReader.IsDBNull(itemReader.GetOrdinal("FertilizerId")) ? (int?)null : itemReader.GetInt32(itemReader.GetOrdinal("FertilizerId"));
                    var fertilizerSourceId = itemReader.IsDBNull(itemReader.GetOrdinal("FertilizerSourceId")) ? (int?)null : itemReader.GetInt32(itemReader.GetOrdinal("FertilizerSourceId"));
                    var fertilizerUnitId = itemReader.IsDBNull(itemReader.GetOrdinal("FertilizerUnitId")) ? (int?)null : itemReader.GetInt32(itemReader.GetOrdinal("FertilizerUnitId"));
                    var expiryDate = itemReader.IsDBNull(itemReader.GetOrdinal("ExpiryDate")) ? (DateTime?)null : itemReader.GetDateTime(itemReader.GetOrdinal("ExpiryDate"));
                    var potSize = itemReader.IsDBNull(itemReader.GetOrdinal("PotSize")) ? null : itemReader.GetString(itemReader.GetOrdinal("PotSize"));
                    var areaId = itemReader.IsDBNull(itemReader.GetOrdinal("AreaId")) ? (int?)null : itemReader.GetInt32(itemReader.GetOrdinal("AreaId"));
                    var orderedQty = itemReader.GetDecimal(itemReader.GetOrdinal("OrderedQuantity"));
                    var alreadyReceived = itemReader.GetDecimal(itemReader.GetOrdinal("ReceivedQuantity"));
                    itemReader.Close();

                    var pending = orderedQty - alreadyReceived;
                    if (receivedQty > pending)
                    {
                        tx.Rollback();
                        return (false, $"Cannot receive {receivedQty:N2} for this line -- only {pending:N2} is still pending (ordered {orderedQty:N2}, already received {alreadyReceived:N2}).", 0);
                    }

                    const string insertReceiptItemSql = @"
INSERT INTO dbo.PurchaseReceiptItems (PurchaseReceiptId, PurchaseOrderItemId, ReceivedQuantity, CreatedDate)
VALUES (@PurchaseReceiptId, @PurchaseOrderItemId, @ReceivedQuantity, SYSUTCDATETIME());
SELECT CAST(SCOPE_IDENTITY() AS INT);";
                    using var receiptItemCmd = new SqlCommand(insertReceiptItemSql, conn, tx);
                    receiptItemCmd.Parameters.AddWithValue("@PurchaseReceiptId", newReceiptId);
                    receiptItemCmd.Parameters.AddWithValue("@PurchaseOrderItemId", itemId);
                    receiptItemCmd.Parameters.AddWithValue("@ReceivedQuantity", receivedQty);
                    var newReceiptItemId = (int)await receiptItemCmd.ExecuteScalarAsync();

                    var updateItemCmd = new SqlCommand(
                        "UPDATE dbo.PurchaseOrderItems SET ReceivedQuantity = ReceivedQuantity + @Delta, ModifiedDate = SYSUTCDATETIME(), ModifiedBy = @ModifiedBy WHERE Id = @Id",
                        conn, tx);
                    updateItemCmd.Parameters.AddWithValue("@Delta", receivedQty);
                    updateItemCmd.Parameters.AddWithValue("@ModifiedBy", (object?)createdBy ?? DBNull.Value);
                    updateItemCmd.Parameters.AddWithValue("@Id", itemId);
                    await updateItemCmd.ExecuteNonQueryAsync();

                    if (category == "Fertilizer")
                    {
                        // Mirrors Pages/Fertilizer/Stock.cshtml.cs's own
                        // INSERT exactly -- a new batch row, available
                        // quantity starts equal to the received quantity.
                        const string fertilizerStockSql = @"
INSERT INTO dbo.FertilizerStock
(FertilizerId, Quantity, LatestAvailableQuantity, UnitId, PurchaseDate, ExpiryDate, SourceId, BatchNumber, IsUtilized)
VALUES
(@FertilizerId, @Qty, @Qty, @UnitId, @PurchaseDate, @ExpiryDate, @SourceId, @BatchNumber, 0);";
                        using var fertCmd = new SqlCommand(fertilizerStockSql, conn, tx);
                        fertCmd.Parameters.AddWithValue("@FertilizerId", fertilizerId!.Value);
                        fertCmd.Parameters.AddWithValue("@Qty", receivedQty);
                        fertCmd.Parameters.AddWithValue("@UnitId", fertilizerUnitId!.Value);
                        fertCmd.Parameters.AddWithValue("@PurchaseDate", receiptDate);
                        fertCmd.Parameters.AddWithValue("@ExpiryDate", (object?)expiryDate ?? DBNull.Value);
                        fertCmd.Parameters.AddWithValue("@SourceId", fertilizerSourceId!.Value);
                        fertCmd.Parameters.AddWithValue("@BatchNumber", poCode);
                        await fertCmd.ExecuteNonQueryAsync();
                    }
                    else if (category == "EmptyPot")
                    {
                        var emptyPotInventoryId = await _emptyPotInventoryRepo.GetOrCreateLockedAsync(conn, tx, potSize!, areaId, createdBy);
                        var (stockInSuccess, stockInMessage) = await _emptyPotInventoryRepo.RecordTransactionAsync(
                            conn, tx, emptyPotInventoryId, receivedQty, "StockIn", "PurchaseReceipt", newReceiptItemId, userId, remarks);
                        if (!stockInSuccess)
                        {
                            tx.Rollback();
                            return (false, stockInMessage, 0);
                        }
                    }
                    // 'Other' -- paperwork only, no stock table to post to.
                }

                // Recompute the PO's own Status from the fresh totals.
                var totalsCmd = new SqlCommand(
                    "SELECT SUM(OrderedQuantity) AS TotalOrdered, SUM(ReceivedQuantity) AS TotalReceived FROM dbo.PurchaseOrderItems WHERE PurchaseOrderId = @Id",
                    conn, tx);
                totalsCmd.Parameters.AddWithValue("@Id", purchaseOrderId);
                using var totalsReader = await totalsCmd.ExecuteReaderAsync();
                await totalsReader.ReadAsync();
                var totalOrdered = totalsReader.GetDecimal(totalsReader.GetOrdinal("TotalOrdered"));
                var totalReceived = totalsReader.GetDecimal(totalsReader.GetOrdinal("TotalReceived"));
                totalsReader.Close();

                var newStatus = totalReceived >= totalOrdered ? "Completed" : (totalReceived > 0 ? "PartiallyReceived" : "Pending");
                var updateOrderCmd = new SqlCommand(
                    "UPDATE dbo.PurchaseOrders SET Status = @Status, ModifiedDate = SYSUTCDATETIME(), ModifiedBy = @ModifiedBy WHERE Id = @Id",
                    conn, tx);
                updateOrderCmd.Parameters.AddWithValue("@Status", newStatus);
                updateOrderCmd.Parameters.AddWithValue("@ModifiedBy", (object?)createdBy ?? DBNull.Value);
                updateOrderCmd.Parameters.AddWithValue("@Id", purchaseOrderId);
                await updateOrderCmd.ExecuteNonQueryAsync();

                tx.Commit();
                return (true, null, newReceiptId);
            }
            catch (Exception ex)
            {
                tx.Rollback();
                return (false, ex.Message, 0);
            }
        }

        // Only while still 'Pending' -- once any receipt has posted
        // stock (Status moves to 'PartiallyReceived'/'Completed'),
        // reversing it is out of this phase's scope (see the SQL
        // migration's header comment).
        public async Task<(bool Success, string? Message)> CancelAsync(int id, string? modifiedBy)
        {
            using var conn = _dbHelper.GetConnection();
            await conn.OpenAsync();
            using var tx = conn.BeginTransaction();

            try
            {
                var lockCmd = new SqlCommand("SELECT Status FROM dbo.PurchaseOrders WITH (UPDLOCK, HOLDLOCK) WHERE Id = @Id", conn, tx);
                lockCmd.Parameters.AddWithValue("@Id", id);
                var statusObj = await lockCmd.ExecuteScalarAsync();
                if (statusObj == null)
                {
                    tx.Rollback();
                    return (false, "Purchase Order not found.");
                }
                var status = (string)statusObj;
                if (status != "Pending")
                {
                    tx.Rollback();
                    return (false, $"This Purchase Order is '{status}' and can no longer be cancelled (stock has already been received against it).");
                }

                var updateCmd = new SqlCommand(
                    "UPDATE dbo.PurchaseOrders SET Status = 'Cancelled', ModifiedDate = SYSUTCDATETIME(), ModifiedBy = @ModifiedBy WHERE Id = @Id",
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

        private static PurchaseOrder MapOrder(SqlDataReader reader)
        {
            return new PurchaseOrder
            {
                Id = reader.GetInt32(reader.GetOrdinal("Id")),
                PurchaseOrderCode = reader.GetString(reader.GetOrdinal("PurchaseOrderCode")),
                VendorId = reader.GetInt32(reader.GetOrdinal("VendorId")),
                VendorName = reader.GetString(reader.GetOrdinal("VendorName")),
                OrderDate = reader.GetDateTime(reader.GetOrdinal("OrderDate")),
                ExpectedDeliveryDate = reader.IsDBNull(reader.GetOrdinal("ExpectedDeliveryDate")) ? null : reader.GetDateTime(reader.GetOrdinal("ExpectedDeliveryDate")),
                Status = reader.GetString(reader.GetOrdinal("Status")),
                Remarks = reader.IsDBNull(reader.GetOrdinal("Remarks")) ? null : reader.GetString(reader.GetOrdinal("Remarks")),
                CreatedDate = reader.GetDateTime(reader.GetOrdinal("CreatedDate")),
                CreatedBy = reader.IsDBNull(reader.GetOrdinal("CreatedBy")) ? null : reader.GetString(reader.GetOrdinal("CreatedBy")),
                ModifiedDate = reader.IsDBNull(reader.GetOrdinal("ModifiedDate")) ? null : reader.GetDateTime(reader.GetOrdinal("ModifiedDate")),
                ModifiedBy = reader.IsDBNull(reader.GetOrdinal("ModifiedBy")) ? null : reader.GetString(reader.GetOrdinal("ModifiedBy"))
            };
        }

        private static PurchaseOrderItem MapItem(SqlDataReader reader)
        {
            return new PurchaseOrderItem
            {
                Id = reader.GetInt32(reader.GetOrdinal("Id")),
                PurchaseOrderId = reader.GetInt32(reader.GetOrdinal("PurchaseOrderId")),
                ItemCategory = reader.GetString(reader.GetOrdinal("ItemCategory")),
                FertilizerId = reader.IsDBNull(reader.GetOrdinal("FertilizerId")) ? null : reader.GetInt32(reader.GetOrdinal("FertilizerId")),
                FertilizerName = reader.IsDBNull(reader.GetOrdinal("FertilizerName")) ? null : reader.GetString(reader.GetOrdinal("FertilizerName")),
                FertilizerSourceId = reader.IsDBNull(reader.GetOrdinal("FertilizerSourceId")) ? null : reader.GetInt32(reader.GetOrdinal("FertilizerSourceId")),
                FertilizerSourceName = reader.IsDBNull(reader.GetOrdinal("FertilizerSourceName")) ? null : reader.GetString(reader.GetOrdinal("FertilizerSourceName")),
                FertilizerUnitId = reader.IsDBNull(reader.GetOrdinal("FertilizerUnitId")) ? null : reader.GetInt32(reader.GetOrdinal("FertilizerUnitId")),
                FertilizerUnitName = reader.IsDBNull(reader.GetOrdinal("FertilizerUnitName")) ? null : reader.GetString(reader.GetOrdinal("FertilizerUnitName")),
                ExpiryDate = reader.IsDBNull(reader.GetOrdinal("ExpiryDate")) ? null : reader.GetDateTime(reader.GetOrdinal("ExpiryDate")),
                PotSize = reader.IsDBNull(reader.GetOrdinal("PotSize")) ? null : reader.GetString(reader.GetOrdinal("PotSize")),
                AreaId = reader.IsDBNull(reader.GetOrdinal("AreaId")) ? null : reader.GetInt32(reader.GetOrdinal("AreaId")),
                AreaName = reader.IsDBNull(reader.GetOrdinal("AreaName")) ? null : reader.GetString(reader.GetOrdinal("AreaName")),
                ItemName = reader.IsDBNull(reader.GetOrdinal("ItemName")) ? null : reader.GetString(reader.GetOrdinal("ItemName")),
                OrderedQuantity = reader.GetDecimal(reader.GetOrdinal("OrderedQuantity")),
                ReceivedQuantity = reader.GetDecimal(reader.GetOrdinal("ReceivedQuantity")),
                UnitPrice = reader.IsDBNull(reader.GetOrdinal("UnitPrice")) ? null : reader.GetDecimal(reader.GetOrdinal("UnitPrice")),
                Remarks = reader.IsDBNull(reader.GetOrdinal("Remarks")) ? null : reader.GetString(reader.GetOrdinal("Remarks")),
                CreatedDate = reader.GetDateTime(reader.GetOrdinal("CreatedDate")),
                CreatedBy = reader.IsDBNull(reader.GetOrdinal("CreatedBy")) ? null : reader.GetString(reader.GetOrdinal("CreatedBy")),
                ModifiedDate = reader.IsDBNull(reader.GetOrdinal("ModifiedDate")) ? null : reader.GetDateTime(reader.GetOrdinal("ModifiedDate")),
                ModifiedBy = reader.IsDBNull(reader.GetOrdinal("ModifiedBy")) ? null : reader.GetString(reader.GetOrdinal("ModifiedBy"))
            };
        }
    }
}
