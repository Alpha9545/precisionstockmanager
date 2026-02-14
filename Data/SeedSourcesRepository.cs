using PlantStockManager.Models;
using System.Collections.Generic;
using Microsoft.Data.SqlClient;
using System.Threading.Tasks;

namespace PlantStockManager.Data
{
    public class SeedSourcesRepository
    {
        private readonly DatabaseHelper _dbHelper;

        public SeedSourcesRepository(DatabaseHelper dbHelper)
        {
            _dbHelper = dbHelper;
        }

        public async Task<List<SeedSource>> GetAllSeedSources()
        {
            var seedSources = new List<SeedSource>();
            using (var conn = _dbHelper.GetConnection())
            {
                await conn.OpenAsync();
                var cmd = new SqlCommand("SELECT * FROM SeedSources WHERE IsActive = 1", conn);
                using (var reader = await cmd.ExecuteReaderAsync())
                {
                    while (await reader.ReadAsync())
                    {
                        seedSources.Add(new SeedSource
                        {
                            Id = reader.GetInt32(0),
                            Name = reader.GetString(1)
                        });
                    }
                }
            }
            return seedSources;
        }

        public async Task AddSeedSource(string name)
        {
            using (var conn = _dbHelper.GetConnection())
            {
                await conn.OpenAsync();
                var cmd = new SqlCommand("INSERT INTO SeedSources (Name) VALUES (@Name)", conn);
                cmd.Parameters.AddWithValue("@Name", name);
                await cmd.ExecuteNonQueryAsync();
            }
        }

        public async Task UpdateSeedSource(int id, string name)
        {
            using (var conn = _dbHelper.GetConnection())
            {
                await conn.OpenAsync();
                var cmd = new SqlCommand("UPDATE SeedSources SET Name = @Name WHERE Id = @Id", conn);
                cmd.Parameters.AddWithValue("@Name", name);
                cmd.Parameters.AddWithValue("@Id", id);
                await cmd.ExecuteNonQueryAsync();
            }
        }


        public async Task DeactivateSeedSource(int id)
        {
            using (var connection = _dbHelper.GetConnection())
            {
                string query = @"UPDATE SeedSources 
                                 SET IsActive = 0 
                                 WHERE Id = @Id";

                using (var command = new SqlCommand(query, connection))
                {
                    command.Parameters.AddWithValue("@Id", id);

                    await connection.OpenAsync();
                    await command.ExecuteNonQueryAsync();
                }
            }
        }
    }
}
