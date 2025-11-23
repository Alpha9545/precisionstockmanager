using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Data.SqlClient;
using PlantStockManager.Data;
using PlantStockManager.Models;

namespace PlantStockManager.Pages.SeedEntry
{
    
    public class SampleModel : PageModel
    {

        private readonly DatabaseHelper _dbHelper;
        public List<SeedEntries> SeedEntries { get; set; }

        public SampleModel(DatabaseHelper dbHelper)
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
                       s.SeedingDate, s.SeedsPlanted, DATEDIFF(DAY, s.SeedingDate, GETDATE()) AS DaysPassed,
                       s.TraysAlive, s.HardeningAlive, s.AliveCount, s.CurrentStage
                FROM SeedEntries s
                JOIN Polyhouses p ON s.PolyhouseId = p.Id
                JOIN PlantSpecies ps ON s.SpeciesId = ps.Id
                JOIN PlantTypes pt ON ps.PlantTypeId = pt.Id
                WHERE s.SeedingDate >= DATEADD(MONTH, -2, GETDATE())
                 ORDER BY s.SeedingDate;", conn);

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
                            DaysPassed = reader.GetInt32(6),
                            TraysAlive = reader.IsDBNull(7) ? null : (int?)reader.GetInt32(7),
                            HardeningAlive = reader.IsDBNull(8) ? null : (int?)reader.GetInt32(8),
                            AliveCount = reader.IsDBNull(9) ? null : (int?)reader.GetInt32(9),
                            CurrentStage = reader.GetString(10)
                        });
                    }
                }
            }
            return records;
        }

        public async Task<IActionResult> OnPostUpdateStageAsync(int SeedEntryId, int AliveCount, string NextStage)
        {
            try
            {
                if (SeedEntryId <= 0 || AliveCount <= 0 || string.IsNullOrEmpty(NextStage))
                    return BadRequest(new { success = false, message = "Invalid request data" });

                bool success = await AddSeedEntryToInventory(SeedEntryId, AliveCount, NextStage);
                return new JsonResult(new { success });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { success = false, message = ex.Message });
            }
        }

        private async Task<bool> AddSeedEntryToInventory(int seedEntryId, int aliveCount, string nextStage)
        {
            using (var conn = _dbHelper.GetConnection())
            {
                await conn.OpenAsync();
                using (var transaction = conn.BeginTransaction())
                {
                    try
                    {
                        // 1. Get current stage and validate
                        var cmdGet = new SqlCommand(
                            "SELECT CurrentStage, SeedsPlanted, TraysAlive, HardeningAlive FROM SeedEntries WHERE Id = @Id",
                            conn, transaction
                        );
                        cmdGet.Parameters.AddWithValue("@Id", seedEntryId);

                        using (var reader = await cmdGet.ExecuteReaderAsync())
                        {
                            if (!await reader.ReadAsync()) return false;
                            string currentStage = reader.GetString(0);
                            int seedsPlanted = reader.GetInt32(1);
                            int? traysAlive = reader.IsDBNull(2) ? null : (int?)reader.GetInt32(2);
                            int? hardeningAlive = reader.IsDBNull(3) ? null : (int?)reader.GetInt32(3);

                            // Validate progression
                            if ((currentStage == "Sowing" && nextStage != "Trays") ||
                                (currentStage == "Trays" && nextStage != "Hardening") ||
                                (currentStage == "Hardening" && nextStage != "Inventory"))
                            {
                                transaction.Rollback();
                                return false;
                            }



                            // Validate alive count
                            if ((nextStage == "Trays" && aliveCount > seedsPlanted) ||
                                (nextStage == "Hardening" && (traysAlive == null || aliveCount > traysAlive)) ||
                                (nextStage == "Inventory" && (hardeningAlive == null || aliveCount > hardeningAlive)))
                            {
                                transaction.Rollback();
                                return false;
                            }
                        }

                        // 2. Update SeedEntries
                        string updateColumn = nextStage switch
                        {
                            "Trays" => "TraysAlive",
                            "Hardening" => "HardeningAlive",
                            "Inventory" => "AliveCount",
                            _ => throw new ArgumentException("Invalid stage")
                        };

                        var cmdUpdate = new SqlCommand(
                            $@"UPDATE SeedEntries 
                       SET {updateColumn} = @AliveCount, 
                           CurrentStage = @NextStage
                           {(nextStage == "Inventory" ? ", ReadyForInventory = 1" : "")}
                       WHERE Id = @Id",
                            conn, transaction
                        );
                        cmdUpdate.Parameters.AddWithValue("@AliveCount", aliveCount);
                        cmdUpdate.Parameters.AddWithValue("@NextStage", nextStage);
                        cmdUpdate.Parameters.AddWithValue("@Id", seedEntryId);
                        await cmdUpdate.ExecuteNonQueryAsync();

                        // 3. Add to Inventory if final stage
                        if (nextStage == "Inventory")
                        {
                            var cmdInventory = new SqlCommand(@"
                                INSERT INTO Inventory (PlantId, SpeciesId, PolyhouseId, SeedEntryId, Quantity,RemainingQuantity, LastUpdated)
                                OUTPUT INSERTED.Id 
                                SELECT s.PlantId, s.SpeciesId, s.PolyhouseId, s.Id, @AliveCount,@aliveCount, GETDATE()
                                FROM SeedEntries s
                                WHERE s.Id = @SeedEntryId;", conn, transaction
                            );
                            cmdInventory.Parameters.AddWithValue("@AliveCount", aliveCount);
                            cmdInventory.Parameters.AddWithValue("@SeedEntryId", seedEntryId);
                            int inventoryId = Convert.ToInt32(await cmdInventory.ExecuteScalarAsync());

                            var cmdTransaction = new SqlCommand(@"
                                INSERT INTO Transactions (InventoryId, TransactionType, Quantity, TransactionDate, UpdatedBy, UpdatedOn)
                                VALUES (@InventoryId, 'Inventory', @Quantity, GETDATE(), @UpdatedBy, GETDATE())", conn, transaction);
                            cmdTransaction.Parameters.AddWithValue("@InventoryId", inventoryId);
                            cmdTransaction.Parameters.AddWithValue("@Quantity", aliveCount);
                            cmdTransaction.Parameters.AddWithValue("@UpdatedBy", "Lalit");

                            await cmdTransaction.ExecuteNonQueryAsync();
                        }

                        transaction.Commit();
                        return true;
                    }
                    catch
                    {
                        transaction.Rollback();
                        throw;
                    }
                }
            }
        }


        // Add to the PageModel class
        public int GetCurrentCount(SeedEntries entry)
        {
            return entry.CurrentStage switch
            {
                "Sowing" => entry.SeedsPlanted,
                "Trays" => entry.TraysAlive ?? 0,
                "Hardening" => entry.HardeningAlive ?? 0,
                "Inventory" => entry.AliveCount ?? 0,
                _ => 0
            };
        }

        // Add these helper methods to your PageModel
        public string GetStageBadgeClass(string currentStage, string targetStage)
        {
            return currentStage == targetStage ? "badge bg-primary" : "badge bg-secondary";
        }

        public string GetProgressWidth(SeedEntries entry)
        {
            return entry.CurrentStage switch
            {
                "Sowing" => "25%",
                "trays" => "50%",
                "hardening" => "75%",
                "inventory" => "100%",
                _ => "0%"
            };
        }
    }
}
