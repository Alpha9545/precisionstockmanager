using PlantStockManager.Models;
using System.Collections.Generic;
using Microsoft.Data.SqlClient;
using System.Threading.Tasks;

namespace PlantStockManager.Data
{
    public class PlantSpeciesRepository
    {
        private readonly DatabaseHelper _dbHelper;

        public PlantSpeciesRepository(DatabaseHelper dbHelper)
        {
            _dbHelper = dbHelper;
        }

        public async Task<List<PlantSpecies>> GetSpeciesByPlantType(int plantTypeId)
        {
            var speciesList = new List<PlantSpecies>();
            using (var conn = _dbHelper.GetConnection())
            {
                await conn.OpenAsync();
                var cmd = new SqlCommand("SELECT * FROM PlantSpecies WHERE PlantTypeId = @PlantTypeId ORDER BY Name", conn);
                cmd.Parameters.AddWithValue("@PlantTypeId", plantTypeId);

                using (var reader = await cmd.ExecuteReaderAsync())
                {
                    while (await reader.ReadAsync())
                    {
                        speciesList.Add(new PlantSpecies
                        {
                            Id = reader.GetInt32(0),
                            PlantTypeId = reader.GetInt32(1),
                            Name = reader.GetString(2),
                            ScientificName = reader.IsDBNull(3) ? null : reader.GetString(3)
                        });
                    }
                }
            }
            return speciesList;
        }


        public async Task AddPlantSpecies(int plantTypeId, string name, string scientificName)
        {
            using (var conn = _dbHelper.GetConnection())
            {
                await conn.OpenAsync();
                var cmd = new SqlCommand("INSERT INTO PlantSpecies (PlantTypeId, Name, ScientificName) VALUES (@PlantTypeId, @Name, @ScientificName)", conn);
                cmd.Parameters.AddWithValue("@PlantTypeId", plantTypeId);
                cmd.Parameters.AddWithValue("@Name", name);
                cmd.Parameters.AddWithValue("@ScientificName", string.IsNullOrWhiteSpace(scientificName) ? (object)DBNull.Value : scientificName);
                await cmd.ExecuteNonQueryAsync();
            }
        }

        public async Task UpdatePlantSpecies(int id, string name, string scientificName)
        {
            using (var conn = _dbHelper.GetConnection())
            {
                await conn.OpenAsync();
                var cmd = new SqlCommand("UPDATE PlantSpecies SET Name = @Name, ScientificName = @ScientificName WHERE Id = @Id", conn);
                cmd.Parameters.AddWithValue("@Name", name);
                cmd.Parameters.AddWithValue("@ScientificName", string.IsNullOrWhiteSpace(scientificName) ? (object)DBNull.Value : scientificName);
                cmd.Parameters.AddWithValue("@Id", id);
                await cmd.ExecuteNonQueryAsync();
            }
        }

        public async Task<bool> DeletePlantSpecies(int id)
        {
            using (var connection = _dbHelper.GetConnection())
            {
                await connection.OpenAsync();

                // 🔹 Check if exists in SeedEntries
                string checkQuery = "SELECT COUNT(*) FROM SeedEntries WHERE SpeciesId = @Id";

                using (var checkCmd = new SqlCommand(checkQuery, connection))
                {
                    checkCmd.Parameters.AddWithValue("@Id", id);
                    int count = (int)await checkCmd.ExecuteScalarAsync();

                    if (count > 0)
                        return false; // Cannot delete
                }

                string checkSCB = "SELECT COUNT(*) FROM SeedCuttingBank WHERE SpeciesId = @Id";

                using (var cmd = new SqlCommand(checkSCB, connection))
                {
                    cmd.Parameters.AddWithValue("@Id", id);
                    int count = (int)await cmd.ExecuteScalarAsync();
                    if (count > 0)
                        return false;
                }

                // 🔹 Safe to delete
                string deleteQuery = "DELETE FROM PlantSpecies WHERE Id = @Id";

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
