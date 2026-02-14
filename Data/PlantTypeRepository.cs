using PlantStockManager.Models;
using System.Collections.Generic;
using Microsoft.Data.SqlClient;
using System.Threading.Tasks;

namespace PlantStockManager.Data
{
    public class PlantTypeRepository
    {
        private readonly DatabaseHelper _dbHelper;

        public PlantTypeRepository(DatabaseHelper dbHelper)
        {
            _dbHelper = dbHelper;
        }

        public async Task<List<PlantType>> GetAllPlantTypes()
        {
            var plantTypes = new List<PlantType>();
            using (var conn = _dbHelper.GetConnection())
            {
                await conn.OpenAsync();
                var cmd = new SqlCommand("SELECT * FROM PlantTypes", conn);
                using (var reader = await cmd.ExecuteReaderAsync())
                {
                    while (await reader.ReadAsync())
                    {
                        plantTypes.Add(new PlantType
                        {
                            Id = reader.GetInt32(0),
                            Name = reader.GetString(1)
                        });
                    }
                }
            }
            return plantTypes;
        }


        public async Task AddPlantType(string name)
        {
            using (var conn = _dbHelper.GetConnection())
            {
                await conn.OpenAsync();
                var cmd = new SqlCommand("INSERT INTO PlantTypes (Name) VALUES (@Name)", conn);
                cmd.Parameters.AddWithValue("@Name", name);
                await cmd.ExecuteNonQueryAsync();
            }
        }

        public async Task UpdatePlantType(int id, string name)
        {
            using (var conn = _dbHelper.GetConnection())
            {
                await conn.OpenAsync();
                var cmd = new SqlCommand("UPDATE PlantTypes SET Name = @Name WHERE Id = @Id", conn);
                cmd.Parameters.AddWithValue("@Name", name);
                cmd.Parameters.AddWithValue("@Id", id);
                await cmd.ExecuteNonQueryAsync();
            }
        }

        public async Task<bool> DeletePlantType(int id)
        {
            using (var connection = _dbHelper.GetConnection())
            {
                await connection.OpenAsync();

                // 🔹 Check if exists in SeedEntries
                string checkQuery = "SELECT COUNT(*) FROM SeedEntries WHERE PlantId = @Id";

                using (var checkCmd = new SqlCommand(checkQuery, connection))
                {
                    checkCmd.Parameters.AddWithValue("@Id", id);
                    int count = (int)await checkCmd.ExecuteScalarAsync();

                    if (count > 0)
                        return false; // Cannot delete
                }

                string checkSCB = "SELECT COUNT(*) FROM SeedCuttingBank WHERE PlantId = @Id";

                using (var cmd = new SqlCommand(checkSCB, connection))
                {
                    cmd.Parameters.AddWithValue("@Id", id);
                    int count = (int)await cmd.ExecuteScalarAsync();
                    if (count > 0)
                        return false;
                }

                // 🔹 Safe to delete
                string deleteQuery = "DELETE FROM PlantTypes WHERE Id = @Id";

                using (var deleteCmd = new SqlCommand(deleteQuery, connection))
                {
                    deleteCmd.Parameters.AddWithValue("@Id", id);
                    await deleteCmd.ExecuteNonQueryAsync();
                }

                return true;
            }
        }

    }
}
