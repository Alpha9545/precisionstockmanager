using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Data.SqlClient;
using PlantStockManager.Data;
using PlantStockManager.Models;
using System.Data;

namespace PlantStockManager.Pages.SeedEntry
{
    [Authorize]
    public class tempModel : PageModel
    {

        private readonly DatabaseHelper _dbHelper;
        public List<SeedEntries> SeedEntries { get; set; }

        public tempModel(DatabaseHelper dbHelper)
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
            // Get the current user’s ID from claims
            var userIdClaim = User.FindFirst("UserId")?.Value;

            // If no user ID is found, return empty
            if (string.IsNullOrWhiteSpace(userIdClaim) || !int.TryParse(userIdClaim, out int userId))
                return new List<SeedEntries>();

            var records = new List<SeedEntries>();

            using (var conn = _dbHelper.GetConnection())
            {
                await conn.OpenAsync();

                var sql = @"
            SELECT s.Id, 
                   p.Name AS PolyhouseName, 
                   pt.Name AS PlantTypeName, 
                   ps.Name AS SpeciesName, 
                   s.SeedingDate, 
                   s.SeedsPlanted, 
                   DATEDIFF(DAY, s.SeedingDate, GETDATE()) AS DaysPassed,
                   s.TraysAlive, 
                   s.HardeningAlive, 
                   s.AliveCount, 
                   s.CurrentStage,
                   s.TraysDate, 
                   s.HardeningDate, 
                   s.InventoryDate, 
                   s.locationDesc, 
                   e.Name
            FROM SeedEntries s
            JOIN Polyhouses p ON s.PolyhouseId = p.Id
            JOIN PlantSpecies ps ON s.SpeciesId = ps.Id
            JOIN PlantTypes pt ON ps.PlantTypeId = pt.Id
            JOIN IMSUsers e ON s.SupervisorId = e.Id
            WHERE s.ReadyForInventory = 0 AND (@userId = 5 OR s.SupervisorId = @userId) 
            ORDER BY s.SeedingDate;";

                using (var cmd = new SqlCommand(sql, conn))
                {
                    // ✅ Use correct type
                    cmd.Parameters.Add("@userId", System.Data.SqlDbType.Int).Value = userId;

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
                                TraysAlive = reader.IsDBNull(7) ? null : reader.GetInt32(7),
                                HardeningAlive = reader.IsDBNull(8) ? null : reader.GetInt32(8),
                                AliveCount = reader.IsDBNull(9) ? null : reader.GetInt32(9),
                                CurrentStage = reader.GetString(10),
                                TraysDate = reader.IsDBNull(11) ? null : reader.GetDateTime(11),
                                HardeningDate = reader.IsDBNull(12) ? null : reader.GetDateTime(12),
                                InventoryDate = reader.IsDBNull(13) ? null : reader.GetDateTime(13),
                                locationDesc = reader.IsDBNull(14) ? null : reader.GetString(14),
                                Supervisor = reader.IsDBNull(15) ? null : reader.GetString(15)
                            });
                        }
                    }
                }
            }

            return records;
        }


        public async Task<IActionResult> OnPostUpdateStageAsync(int SeedEntryId, int AliveCount, string NextStage, DateTime stageDate, string location)
        {
            try
            {
                if (SeedEntryId <= 0 || AliveCount <= 0 || string.IsNullOrEmpty(NextStage))
                    return BadRequest(new { success = false, message = "Invalid request data" });

                bool success = await AddSeedEntryToInventory(SeedEntryId, AliveCount, NextStage, stageDate, location);
                return new JsonResult(new { success });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { success = false, message = ex.Message });
            }
        }

        private async Task<bool> AddSeedEntryToInventory(int seedEntryId, int aliveCount, string nextStage, DateTime stageDate, string location)
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
                            "SELECT CurrentStage, SeedsPlanted, TraysAlive, HardeningAlive, SeedingDate, TraysDate, HardeningDate FROM SeedEntries WHERE Id = @Id",
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
                            DateTime seedingDate = reader.GetDateTime(4);
                            DateTime? traysDate = reader.IsDBNull(5) ? null : reader.GetDateTime(5);
                            DateTime? hardeningDate = reader.IsDBNull(6) ? null : reader.GetDateTime(6);

                            // Validate progression
                            DateTime previousDate = currentStage switch
                            {
                                "Sowing" => seedingDate,
                                "Trays" => traysDate ?? seedingDate,
                                "Hardening" => hardeningDate ?? traysDate ?? seedingDate,
                                _ => throw new ArgumentException("Invalid current stage")
                            };

                            if (stageDate < previousDate)
                            {
                                transaction.Rollback();
                                return false;
                            }

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
                        string dateColumn = nextStage switch
                        {
                            "Trays" => "TraysDate",
                            "Hardening" => "HardeningDate",
                            "Inventory" => "InventoryDate",
                            _ => throw new ArgumentException("Invalid next stage")
                        };

                        var cmdUpdate = new SqlCommand(
                            $@"UPDATE SeedEntries 
                       SET {updateColumn} = @AliveCount,
                            {dateColumn} = @StageDate,
                           CurrentStage = @NextStage
                           {(nextStage == "Inventory" ? ", ReadyForInventory = 1" : "")}
                           {(nextStage == "Inventory" ? ", locationDesc = @locationDesc" : "")} 
                       WHERE Id = @Id",
                            conn, transaction
                        );
                        cmdUpdate.Parameters.AddWithValue("@AliveCount", aliveCount);
                        cmdUpdate.Parameters.AddWithValue("@NextStage", nextStage);
                        cmdUpdate.Parameters.AddWithValue("@Id", seedEntryId);
                        cmdUpdate.Parameters.AddWithValue("@StageDate", stageDate);
                        if (nextStage == "Inventory")
                        {
                            cmdUpdate.Parameters.AddWithValue("@locationDesc",
                                string.IsNullOrWhiteSpace(location) ? (object)DBNull.Value : location);
                        }

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
        public string GetPreviousDate(SeedEntries entry)
        {
            return entry.CurrentStage switch
            {
                "Sowing" => entry.SeedingDate.ToString("yyyy-MM-dd"),
                "Trays" => entry.TraysDate?.ToString("yyyy-MM-dd") ?? entry.SeedingDate.ToString("yyyy-MM-dd"),
                "Hardening" => entry.HardeningDate?.ToString("yyyy-MM-dd") ?? entry.TraysDate?.ToString("yyyy-MM-dd") ?? entry.SeedingDate.ToString("yyyy-MM-dd"),
                _ => DateTime.Today.ToString("yyyy-MM-dd")
            };
        }
        public string GetStageBadgeClass(string currentStage, string targetStage)
        {
            return currentStage == targetStage ? "badge bg-primary" : "badge bg-secondary";
        }
    }
}
