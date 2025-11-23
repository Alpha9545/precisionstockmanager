using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Data.SqlClient;
using PlantStockManager.Models;
using PlantStockManager.Data;
using Microsoft.AspNetCore.Authorization;

namespace PlantStockManager.Pages.SeedEntry
{
    [Authorize]
    public class SowingToInventoryModel : PageModel
    {
          private readonly DatabaseHelper _dbHelper;
        public List<SeedEntries> SeedEntries { get; set; }

        public SowingToInventoryModel(DatabaseHelper dbHelper)
        {
            _dbHelper = dbHelper;
        }

        public async Task<IActionResult> OnGetAsync()
        {
            SeedEntries = await GetSowingRecords();
            return Page();
        }

        public async Task<List<SeedEntries>> GetSowingRecords()
        {
            var records = new List<SeedEntries>();
            using (var conn = _dbHelper.GetConnection())
            {
                await conn.OpenAsync();
                var cmd = new SqlCommand(@"
                SELECT s.Id, p.Name AS PolyhouseName, pt.Name AS PlantTypeName, ps.Name AS SpeciesName, 
                       s.SeedingDate, s.SeedsPlanted, DATEDIFF(DAY, s.SeedingDate, GETDATE()) AS DaysPassed
                FROM SeedEntries s
                JOIN Polyhouses p ON s.PolyhouseId = p.Id
                JOIN PlantSpecies ps ON s.SpeciesId = ps.Id
                JOIN PlantTypes pt ON ps.PlantTypeId = pt.Id
                WHERE s.ReadyForInventory = 0  ORDER BY s.SeedingDate;", conn);

                using (var reader = await cmd.ExecuteReaderAsync())
                {
                    while (await reader.ReadAsync())
                    {
                        records.Add(new SeedEntries
                        {
                            Id = reader.GetInt32(0),
                            PolyhouseName = reader.GetString(1),
                            PlantTypeName = reader.GetString(2),
                            SpeciesName = reader.GetString(3),
                            SeedingDate = reader.GetDateTime(4),
                            SeedsPlanted = reader.GetInt32(5),
                            DaysPassed = reader.GetInt32(6)
                        });
                    }
                }
            }
            return records;
        }
        public async Task<IActionResult> OnPostAddToInventoryAsync(int SeedEntryId, int AliveCount)
        {
            try
            {
                if (SeedEntryId <= 0 || AliveCount <= 0)
                {
                    return BadRequest(new { success = false, message = "Invalid request data" });
                }

                bool success = await AddSeedEntryToInventory(SeedEntryId, AliveCount);
                return new JsonResult(new { success });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { success = false, message = ex.Message });
            }
        }


        private async Task<bool> AddSeedEntryToInventory(int seedEntryId, int aliveCount)
        {
            using (var conn = _dbHelper.GetConnection())
            {
                await conn.OpenAsync();
                using (var transaction = conn.BeginTransaction())
                {
                    try
                    {
                        // ✅ Update SeedEntries Table
                        var updateSeedCmd = new SqlCommand(@"
                UPDATE SeedEntries 
                SET ReadyForInventory = 1, AliveCount = @AliveCount 
                WHERE Id = @SeedEntryId", conn, transaction);
                        updateSeedCmd.Parameters.AddWithValue("@AliveCount", aliveCount);
                        updateSeedCmd.Parameters.AddWithValue("@SeedEntryId", seedEntryId);
                        await updateSeedCmd.ExecuteNonQueryAsync();

                        // ✅ Insert into Inventory and Retrieve InventoryId
                        var insertInventoryCmd = new SqlCommand(@"
                INSERT INTO Inventory (PlantId, SpeciesId, PolyhouseId, SeedEntryId, Quantity, RemainingQuantity, LastUpdated)
                OUTPUT INSERTED.Id 
                SELECT s.PlantId, s.SpeciesId, s.PolyhouseId, s.Id, @AliveCount, @AliveCount, GETDATE()
                FROM SeedEntries s
                WHERE s.Id = @SeedEntryId;", conn, transaction);
                        insertInventoryCmd.Parameters.AddWithValue("@AliveCount", aliveCount);
                        insertInventoryCmd.Parameters.AddWithValue("@SeedEntryId", seedEntryId);

                        int inventoryId = (int)await insertInventoryCmd.ExecuteScalarAsync();

                        // ✅ Insert into Transactions Table
                        var insertTransactionCmd = new SqlCommand(@"
                INSERT INTO Transactions (InventoryId, TransactionType, Quantity, TransactionDate, UpdatedBy, UpdatedOn)
                VALUES (@InventoryId, 'Inventory', @Quantity, GETDATE(), @UpdatedBy, GETDATE())", conn, transaction);
                        insertTransactionCmd.Parameters.AddWithValue("@InventoryId", inventoryId);
                        insertTransactionCmd.Parameters.AddWithValue("@Quantity", aliveCount);
                        insertTransactionCmd.Parameters.AddWithValue("@UpdatedBy", "Lalit"); // Replace with actual user if needed

                        await insertTransactionCmd.ExecuteNonQueryAsync();

                        // ✅ Commit transaction if all operations succeed
                        transaction.Commit();
                        return true;
                    }
                    catch
                    {
                        // ❌ Rollback transaction on error
                        transaction.Rollback();
                        return false;
                    }
                }
            }
        }

    }
}
