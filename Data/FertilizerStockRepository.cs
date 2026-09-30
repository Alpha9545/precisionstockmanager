using Microsoft.Data.SqlClient;
using PlantStockManager.Models;
using PlantStockManager.Services;

namespace PlantStockManager.Data
{
    public sealed class FertilizerStockRow
    {
        public int StockId { get; set; }
        public string FertilizerName { get; set; } = "";
        public decimal Quantity { get; set; }
        public decimal LatestAvailableQuantity { get; set; }
        public string UnitName { get; set; } = "";
        public DateTime PurchaseDate { get; set; }
        public DateTime? ExpiryDate { get; set; }
        public string SourceName { get; set; } = "";
        public string? BatchNumber { get; set; }
        public bool IsUtilized { get; set; }
    }

    public sealed record FertilizerStockSaveResult(bool Success, string? Message, int StockId = 0);

    // Correction I3 -- Fertilizer Stock (dbo.FertilizerStock, a purchase batch). Moved out of the raw SqlCommand code
    // that used to live directly in Pages/Fertilizer/Stock.cshtml.cs: Insert/Update/Delete are now here, each under a
    // database row lock, and each re-applies its own rule at the moment it writes -- never trusting what the page
    // showed when the form was opened. No schema change; the existing table, its CHECK constraints (Quantity > 0,
    // LatestAvailableQuantity >= 0) and the existing FK_FertilizerUsage_Stock are all unchanged and are exactly what
    // this repository defends against being violated in an untranslated way.
    public class FertilizerStockRepository
    {
        private readonly DatabaseHelper _db;
        public FertilizerStockRepository(DatabaseHelper db) => _db = db;

        public async Task<List<FertilizerStockRow>> GetAllAsync()
        {
            var list = new List<FertilizerStockRow>();
            using var conn = _db.GetConnection();
            await conn.OpenAsync();
            using var cmd = new SqlCommand(@"
SELECT fs.StockId, fm.FertilizerName, fs.Quantity, fs.LatestAvailableQuantity, um.UnitName,
       fs.PurchaseDate, fs.ExpiryDate, src.SourceName, fs.BatchNumber, fs.IsUtilized
FROM dbo.FertilizerStock fs
JOIN dbo.FertilizerMaster fm ON fs.FertilizerId = fm.FertilizerId
JOIN dbo.UnitMaster um ON fs.UnitId = um.UnitId
JOIN dbo.FertilizerSource src ON fs.SourceId = src.SourceId
WHERE fs.IsUtilized = 0
ORDER BY fs.StockId", conn);
            using var r = await cmd.ExecuteReaderAsync();
            while (await r.ReadAsync())
            {
                list.Add(new FertilizerStockRow
                {
                    StockId = r.GetInt32(0),
                    FertilizerName = r.GetString(1).Trim(),
                    Quantity = r.GetDecimal(2),
                    LatestAvailableQuantity = r.GetDecimal(3),
                    UnitName = r.GetString(4).Trim(),
                    PurchaseDate = r.GetDateTime(5),
                    ExpiryDate = r.IsDBNull(6) ? null : r.GetDateTime(6),
                    SourceName = r.GetString(7).Trim(),
                    BatchNumber = r.IsDBNull(8) ? null : r.GetString(8),
                    IsUtilized = r.GetBoolean(9)
                });
            }
            return list;
        }

        public async Task<FertilizerStock?> GetByIdAsync(int id)
        {
            using var conn = _db.GetConnection();
            await conn.OpenAsync();
            using var cmd = new SqlCommand(
                "SELECT StockId, FertilizerId, Quantity, LatestAvailableQuantity, UnitId, PurchaseDate, ExpiryDate, SourceId, BatchNumber, IsUtilized " +
                "FROM dbo.FertilizerStock WHERE StockId = @Id", conn);
            cmd.Parameters.AddWithValue("@Id", id);
            using var r = await cmd.ExecuteReaderAsync();
            if (!await r.ReadAsync()) return null;
            return new FertilizerStock
            {
                StockId = r.GetInt32(0),
                FertilizerId = r.GetInt32(1),
                Quantity = r.GetDecimal(2),
                LatestAvailableQuantity = r.GetDecimal(3),
                UnitId = r.GetInt32(4),
                PurchaseDate = r.GetDateTime(5),
                ExpiryDate = r.IsDBNull(6) ? null : r.GetDateTime(6),
                SourceId = r.GetInt32(7),
                BatchNumber = r.IsDBNull(8) ? null : r.GetString(8),
                IsUtilized = r.GetBoolean(9)
            };
        }

        // Values a database lookup is needed for (existence of the chosen Fertilizer/Unit/Source), checked once and
        // shared by Insert and Update.
        private async Task<string?> ValidateForeignKeysAsync(SqlConnection conn, SqlTransaction? tx, int fertilizerId, int unitId, int sourceId)
        {
            async Task<bool> Exists(string table, string idColumn, int id)
            {
                using var cmd = new SqlCommand($"SELECT 1 FROM dbo.{table} WHERE {idColumn} = @Id", conn, tx);
                cmd.Parameters.AddWithValue("@Id", id);
                return await cmd.ExecuteScalarAsync() != null;
            }
            if (!await Exists("FertilizerMaster", "FertilizerId", fertilizerId)) return FertilizerStockRules.FertilizerNotFoundMessage;
            if (!await Exists("UnitMaster", "UnitId", unitId)) return FertilizerStockRules.UnitNotFoundMessage;
            if (!await Exists("FertilizerSource", "SourceId", sourceId)) return FertilizerStockRules.SourceNotFoundMessage;
            return null;
        }

        // A new purchase batch. LatestAvailableQuantity starts equal to Quantity (nothing issued yet); IsUtilized 0.
        public async Task<FertilizerStockSaveResult> InsertAsync(FertilizerStock stock)
        {
            var quantityError = FertilizerStockRules.ValidateQuantity(stock.Quantity) ?? FertilizerStockRules.ValidateDate(stock.PurchaseDate);
            if (quantityError != null) return new FertilizerStockSaveResult(false, quantityError);

            using var conn = _db.GetConnection();
            await conn.OpenAsync();
            using var tx = conn.BeginTransaction();
            try
            {
                var fkError = await ValidateForeignKeysAsync(conn, tx, stock.FertilizerId, stock.UnitId, stock.SourceId);
                if (fkError != null)
                {
                    tx.Rollback();
                    return new FertilizerStockSaveResult(false, fkError);
                }

                using var cmd = new SqlCommand(@"
INSERT INTO dbo.FertilizerStock (FertilizerId, Quantity, LatestAvailableQuantity, UnitId, PurchaseDate, ExpiryDate, SourceId, BatchNumber, IsUtilized)
OUTPUT INSERTED.StockId
VALUES (@f, @pq, @pq, @u, @p, @e, @s, @b, 0)", conn, tx);
                AddCommonParameters(cmd, stock);
                var newId = (int)(await cmd.ExecuteScalarAsync())!;
                tx.Commit();
                return new FertilizerStockSaveResult(true, null, newId);
            }
            catch (SqlException ex) when (ex.Number is 547 or 2601 or 2627)
            {
                try { tx.Rollback(); } catch { /* already rolled back */ }
                return new FertilizerStockSaveResult(false, "The database refused this stock entry (a value does not satisfy a stock rule). Nothing was saved.");
            }
            catch
            {
                try { tx.Rollback(); } catch { /* already rolled back */ }
                throw;
            }
        }

        // Update: allowed ONLY when, re-checked HERE under a row lock (never trusting when the edit form was
        // opened), nothing has been issued from this batch yet (FertilizerStockRules.EditAllowed). If an Issue
        // happened after the form was opened and before this save, the lock makes the two mutually exclusive: this
        // update sees the now-reduced LatestAvailableQuantity and is refused; nothing is changed. When the edit is
        // allowed, LatestAvailableQuantity is kept equal to the (possibly changed) Quantity -- both were equal
        // before the edit and nothing was used, so they stay equal after.
        public async Task<FertilizerStockSaveResult> UpdateAsync(FertilizerStock stock)
        {
            var quantityError = FertilizerStockRules.ValidateQuantity(stock.Quantity) ?? FertilizerStockRules.ValidateDate(stock.PurchaseDate);
            if (quantityError != null) return new FertilizerStockSaveResult(false, quantityError);

            using var conn = _db.GetConnection();
            await conn.OpenAsync();
            using var tx = conn.BeginTransaction();
            try
            {
                decimal currentQuantity, currentAvailable;
                using (var lockCmd = new SqlCommand(
                    "SELECT Quantity, LatestAvailableQuantity FROM dbo.FertilizerStock WITH (UPDLOCK, HOLDLOCK) WHERE StockId = @Id", conn, tx))
                {
                    lockCmd.Parameters.AddWithValue("@Id", stock.StockId);
                    using var r = await lockCmd.ExecuteReaderAsync();
                    if (!await r.ReadAsync())
                    {
                        r.Close();
                        tx.Rollback();
                        return new FertilizerStockSaveResult(false, FertilizerStockRules.NotFoundMessage);
                    }
                    currentQuantity = r.GetDecimal(0);
                    currentAvailable = r.GetDecimal(1);
                }
                if (!FertilizerStockRules.EditAllowed(currentQuantity, currentAvailable))
                {
                    tx.Rollback();
                    return new FertilizerStockSaveResult(false, FertilizerStockRules.ChangedSinceOpenedMessage);
                }

                var fkError = await ValidateForeignKeysAsync(conn, tx, stock.FertilizerId, stock.UnitId, stock.SourceId);
                if (fkError != null)
                {
                    tx.Rollback();
                    return new FertilizerStockSaveResult(false, fkError);
                }

                using var cmd = new SqlCommand(@"
UPDATE dbo.FertilizerStock
SET FertilizerId = @f, Quantity = @pq, LatestAvailableQuantity = @pq, UnitId = @u,
    PurchaseDate = @p, ExpiryDate = @e, SourceId = @s, BatchNumber = @b
WHERE StockId = @id", conn, tx);
                AddCommonParameters(cmd, stock);
                cmd.Parameters.AddWithValue("@id", stock.StockId);
                await cmd.ExecuteNonQueryAsync();
                tx.Commit();
                return new FertilizerStockSaveResult(true, null, stock.StockId);
            }
            catch (SqlException ex) when (ex.Number is 547 or 2601 or 2627)
            {
                try { tx.Rollback(); } catch { /* already rolled back */ }
                return new FertilizerStockSaveResult(false, "The database refused this update (a value does not satisfy a stock rule). Nothing was changed.");
            }
            catch
            {
                try { tx.Rollback(); } catch { /* already rolled back */ }
                throw;
            }
        }

        private static void AddCommonParameters(SqlCommand cmd, FertilizerStock stock)
        {
            cmd.Parameters.AddWithValue("@f", stock.FertilizerId);
            cmd.Parameters.AddWithValue("@pq", stock.Quantity);
            cmd.Parameters.AddWithValue("@u", stock.UnitId);
            cmd.Parameters.AddWithValue("@p", stock.PurchaseDate);
            cmd.Parameters.AddWithValue("@e", (object?)stock.ExpiryDate ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@s", stock.SourceId);
            cmd.Parameters.AddWithValue("@b", (object?)stock.BatchNumber ?? DBNull.Value);
        }

        // Delete: locked check-then-delete, the same pattern as MotherPlantRepository.DeleteAsync /
        // AreaRepository.DeleteAsync -- allowed only when DependencyChecker finds no FertilizerUsage row pointing at
        // this batch (DeletionRules.FertilizerStockDependencies). The row lock (taken before the dependency count)
        // makes this mutually exclusive with a concurrent Issue on the same batch: whichever gets the lock first
        // wins, and the other sees the now-current state (an Issue that lands first makes the delete see a
        // dependency and refuse cleanly; a delete that lands first removes the row before any Issue can start,
        // because the Issue's own lock on the same StockId then finds no row). A same-moment FK violation from any
        // other race is still caught and reported the same way, never surfaced as a raw SQL error.
        public async Task<DeleteResult> DeleteAsync(int id)
        {
            using var conn = _db.GetConnection();
            await conn.OpenAsync();
            using var tx = conn.BeginTransaction();
            try
            {
                int? found;
                using (var lockCmd = new SqlCommand("SELECT StockId FROM dbo.FertilizerStock WITH (UPDLOCK, HOLDLOCK) WHERE StockId = @Id", conn, tx))
                {
                    lockCmd.Parameters.AddWithValue("@Id", id);
                    found = (int?)await lockCmd.ExecuteScalarAsync();
                }
                if (found == null)
                {
                    tx.Rollback();
                    return new DeleteResult(DeleteOutcome.NotFound, FertilizerStockRules.NotFoundMessage, Array.Empty<DependencyCount>());
                }

                var dependencies = await DependencyChecker.CountAsync(conn, tx, DeletionRules.FertilizerStockTable, DeletionRules.FertilizerStockDependencies, id);
                if (!DeletionRules.CanDelete(dependencies))
                {
                    tx.Rollback();
                    return new DeleteResult(DeleteOutcome.Blocked, DeletionRules.BlockedMessage("Fertilizer Stock batch", dependencies), dependencies);
                }

                const string savepoint = "BeforeFertilizerStockDelete";
                tx.Save(savepoint);
                try
                {
                    using var delete = new SqlCommand("DELETE FROM dbo.FertilizerStock WHERE StockId = @Id", conn, tx);
                    delete.Parameters.AddWithValue("@Id", id);
                    await delete.ExecuteNonQueryAsync();
                }
                catch (SqlException ex) when (ex.Number == 547)
                {
                    tx.Rollback(savepoint);
                    var again = await DependencyChecker.CountAsync(conn, tx, DeletionRules.FertilizerStockTable, DeletionRules.FertilizerStockDependencies, id);
                    tx.Rollback();
                    return new DeleteResult(DeleteOutcome.Blocked, DeletionRules.BlockedMessage("Fertilizer Stock batch", again), again);
                }

                tx.Commit();
                return new DeleteResult(DeleteOutcome.Deleted, DeletionRules.DeletedMessage("Fertilizer Stock batch", $"#{id}"), dependencies);
            }
            catch
            {
                try { tx.Rollback(); } catch { /* already rolled back */ }
                throw;
            }
        }

        // Dropdown sources (unchanged data, just moved out of the page).
        public async Task<List<DropdownItem>> GetFertilizersAsync() => await LoadDropdownAsync("SELECT FertilizerId, FertilizerName FROM dbo.FertilizerMaster ORDER BY FertilizerName");
        public async Task<List<DropdownItem>> GetUnitsAsync() => await LoadDropdownAsync("SELECT UnitId, UnitName FROM dbo.UnitMaster ORDER BY UnitName");
        public async Task<List<DropdownItem>> GetSourcesAsync() => await LoadDropdownAsync("SELECT SourceId, SourceName FROM dbo.FertilizerSource ORDER BY SourceName");

        private async Task<List<DropdownItem>> LoadDropdownAsync(string sql)
        {
            var list = new List<DropdownItem>();
            using var conn = _db.GetConnection();
            await conn.OpenAsync();
            using var cmd = new SqlCommand(sql, conn);
            using var r = await cmd.ExecuteReaderAsync();
            while (await r.ReadAsync())
                list.Add(new DropdownItem { Id = r.GetInt32(0), Name = r.GetString(1).Trim() });
            return list;
        }
    }
}
