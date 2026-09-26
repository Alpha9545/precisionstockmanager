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

        // Needed by Production/MotherPlant/Edit to pre-select the Plant Type
        // dropdown for an existing batch, since MotherPlants only stores
        // SpeciesId (the cascading Plant Type -> Species pattern used
        // elsewhere in the app needs the parent id to redisplay correctly).
        public async Task<int> GetPlantTypeIdBySpeciesId(int speciesId)
        {
            using (var conn = _dbHelper.GetConnection())
            {
                await conn.OpenAsync();
                var cmd = new SqlCommand("SELECT PlantTypeId FROM PlantSpecies WHERE Id = @Id", conn);
                cmd.Parameters.AddWithValue("@Id", speciesId);
                var result = await cmd.ExecuteScalarAsync();
                return result != null ? Convert.ToInt32(result) : 0;
            }
        }

        // Phase 26 (Phase L): the Management Dashboard's Plant/Variety
        // filter dropdown needs every Species regardless of Plant Type,
        // which no existing method provided (GetSpeciesByPlantType
        // requires a PlantTypeId; GetByIdAsync is single-row). Purely
        // additive read-only SELECT, mirrors GetSpeciesByPlantType's own
        // shape exactly -- no existing caller or method is touched.
        public async Task<List<PlantSpecies>> GetAllAsync()
        {
            var speciesList = new List<PlantSpecies>();
            using (var conn = _dbHelper.GetConnection())
            {
                await conn.OpenAsync();
                var cmd = new SqlCommand("SELECT Id, PlantTypeId, Name, ScientificName, ReadyStockDays, Color FROM PlantSpecies ORDER BY Name", conn);
                using (var reader = await cmd.ExecuteReaderAsync())
                {
                    while (await reader.ReadAsync())
                    {
                        speciesList.Add(new PlantSpecies
                        {
                            Id = reader.GetInt32(0),
                            PlantTypeId = reader.GetInt32(1),
                            Name = reader.GetString(2),
                            ScientificName = reader.IsDBNull(3) ? null : reader.GetString(3),
                            ReadyStockDays = reader.IsDBNull(4) ? (int?)null : reader.GetInt32(4),
                            Color = reader.IsDBNull(5) ? null : reader.GetString(5)
                        });
                    }
                }
            }
            return speciesList;
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
                    // Phase 24: read ReadyStockDays by name (not a fixed
                    // ordinal) so this SELECT * keeps working regardless
                    // of where the new column landed. Every existing
                    // caller of this method (there are many, across the
                    // legacy SeedEntry/Bookings/Data pages) only ever
                    // reads Id/PlantTypeId/Name/ScientificName, so adding
                    // this field is purely additive and safe.
                    var readyStockDaysOrdinal = reader.GetOrdinal("ReadyStockDays");
                    var colorOrdinal = reader.GetOrdinal("Color");
                    while (await reader.ReadAsync())
                    {
                        speciesList.Add(new PlantSpecies
                        {
                            Id = reader.GetInt32(0),
                            PlantTypeId = reader.GetInt32(1),
                            Name = reader.GetString(2),
                            ScientificName = reader.IsDBNull(3) ? null : reader.GetString(3),
                            ReadyStockDays = reader.IsDBNull(readyStockDaysOrdinal) ? (int?)null : reader.GetInt32(readyStockDaysOrdinal),
                            Color = reader.IsDBNull(colorOrdinal) ? null : reader.GetString(colorOrdinal)
                        });
                    }
                }
            }
            return speciesList;
        }

        // Phase 24 (Phase J): needed by SeedSowingRepository.InsertAsync
        // to look up a single species' ReadyStockDays at the moment of
        // sowing (SowingDate + ReadyStockDays = ExpectedReadyDate, using
        // the value applicable AT THAT TIME -- never recomputed later).
        // Called the same way AreaRepository.GetAreaById already is from
        // that same method: a plain read-only master lookup on its own
        // connection, outside the SeedStock transaction.
        public async Task<PlantSpecies?> GetByIdAsync(int id)
        {
            using var conn = _dbHelper.GetConnection();
            await conn.OpenAsync();
            var cmd = new SqlCommand("SELECT Id, PlantTypeId, Name, ScientificName, ReadyStockDays, Color FROM PlantSpecies WHERE Id = @Id", conn);
            cmd.Parameters.AddWithValue("@Id", id);
            using var reader = await cmd.ExecuteReaderAsync();
            if (await reader.ReadAsync())
            {
                return new PlantSpecies
                {
                    Id = reader.GetInt32(0),
                    PlantTypeId = reader.GetInt32(1),
                    Name = reader.GetString(2),
                    ScientificName = reader.IsDBNull(3) ? null : reader.GetString(3),
                    ReadyStockDays = reader.IsDBNull(4) ? (int?)null : reader.GetInt32(4),
                    Color = reader.IsDBNull(5) ? null : reader.GetString(5)
                };
            }
            return null;
        }


        // Phase 24: added an optional readyStockDays parameter, defaulted
        // to null. AddPlantSpecies/UpdatePlantSpecies have exactly one
        // caller in the whole codebase -- Pages/Admin/Plant.cshtml.cs --
        // confirmed by grep, so widening the signature here is safe and
        // that page has been updated to pass the new field through.
        public async Task AddPlantSpecies(int plantTypeId, string name, string scientificName, int? readyStockDays = null, string? color = null)
        {
            using (var conn = _dbHelper.GetConnection())
            {
                await conn.OpenAsync();
                var cmd = new SqlCommand("INSERT INTO PlantSpecies (PlantTypeId, Name, ScientificName, ReadyStockDays, Color) VALUES (@PlantTypeId, @Name, @ScientificName, @ReadyStockDays, @Color)", conn);
                cmd.Parameters.AddWithValue("@PlantTypeId", plantTypeId);
                cmd.Parameters.AddWithValue("@Name", name.Trim());
                cmd.Parameters.AddWithValue("@ScientificName", string.IsNullOrWhiteSpace(scientificName) ? (object)DBNull.Value : scientificName);
                cmd.Parameters.AddWithValue("@ReadyStockDays", readyStockDays.HasValue ? (object)readyStockDays.Value : DBNull.Value);
                cmd.Parameters.AddWithValue("@Color", string.IsNullOrWhiteSpace(color) ? (object)DBNull.Value : color.Trim());
                await cmd.ExecuteNonQueryAsync();
            }
        }

        public async Task UpdatePlantSpecies(int id, string name, string scientificName, int? readyStockDays = null, string? color = null)
        {
            using (var conn = _dbHelper.GetConnection())
            {
                await conn.OpenAsync();
                var cmd = new SqlCommand("UPDATE PlantSpecies SET Name = @Name, ScientificName = @ScientificName, ReadyStockDays = @ReadyStockDays, Color = @Color WHERE Id = @Id", conn);
                cmd.Parameters.AddWithValue("@Name", name);
                cmd.Parameters.AddWithValue("@ScientificName", string.IsNullOrWhiteSpace(scientificName) ? (object)DBNull.Value : scientificName);
                cmd.Parameters.AddWithValue("@ReadyStockDays", readyStockDays.HasValue ? (object)readyStockDays.Value : DBNull.Value);
                cmd.Parameters.AddWithValue("@Color", string.IsNullOrWhiteSpace(color) ? (object)DBNull.Value : color.Trim());
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

                // 🔹 Check if exists in MotherPlants (Production module)
                string checkMotherPlants = "SELECT COUNT(*) FROM dbo.MotherPlants WHERE SpeciesId = @Id";

                using (var cmd = new SqlCommand(checkMotherPlants, connection))
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
