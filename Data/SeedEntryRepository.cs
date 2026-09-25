using Microsoft.Data.SqlClient;
using Microsoft.IdentityModel.Tokens;
using PlantStockManager.Models;
using System.Data;

namespace PlantStockManager.Data
{
    public class SeedEntryRepository
    {
        private readonly DatabaseHelper _dbHelper;

        public SeedEntryRepository(DatabaseHelper dbHelper)
        {
            _dbHelper = dbHelper;
        }

        private static async Task<(int PlantId, int SpeciesId, int SeedsPlanted)> GetCurrentAsync(SqlConnection conn, SqlTransaction tx, int id)
        {
            const string sql = @"
SELECT PlantId, SpeciesId, SeedsPlanted
FROM dbo.SeedEntries WITH (UPDLOCK, ROWLOCK)
WHERE Id = @Id;";

            using var cmd = new SqlCommand(sql, conn, tx);
            cmd.Parameters.AddWithValue("@Id", id);
            using var r = await cmd.ExecuteReaderAsync();
            if (!await r.ReadAsync())
                throw new InvalidOperationException("Seed entry not found.");
            return (r.GetInt32(0), r.GetInt32(1), r.GetInt32(2));
        }


        public async Task<List<SeedEntries>> GetSowingRecords(int? polyhouseId, int? plantTypeId, int? speciesId, DateTime? fromDate, DateTime? toDate)
        {
            var sowingRecords = new List<SeedEntries>();

            // Default date logic: if both are null, set fromDate to 15 days ago, toDate to today
            if (!fromDate.HasValue && !toDate.HasValue)
            {
                fromDate = DateTime.Today.AddDays(-15);
                toDate = DateTime.Today;
            }

            using (var conn = _dbHelper.GetConnection())
            {
                await conn.OpenAsync();
                var query = @"
SELECT 
    s.Id, 
    s.PolyhouseId, 
    p.Name AS Polyhouse, 
    s.PlantId, 
    pt.Name AS PlantType, 
    s.SpeciesId, 
    ps.Name AS Species, 
    s.SeedingDate, 
    s.SeedsPlanted,
    DATEDIFF(DAY, s.SeedingDate, GETDATE()) AS DaysPassed,
    s.TraysAlive,
    s.HardeningAlive,
    s.AliveCount,
    s.SeedSourceId,
    s.OtherSeedSource,
    s.TraysDate,
    s.HardeningDate,
    s.InventoryDate,
    s.location,
    s.locationdesc,
    se.Name,
    e.Name
FROM SeedEntries s
INNER JOIN Polyhouses p ON s.PolyhouseId = p.Id
INNER JOIN PlantSpecies ps ON s.SpeciesId = ps.Id
INNER JOIN PlantTypes pt ON s.PlantId = pt.Id
Inner JOIN IMSUsers e ON s.SupervisorId = e.Id
LEFT OUTER JOIN SeedSources se ON s.SeedSourceId = se.Id
WHERE (@PolyhouseId IS NULL OR s.PolyhouseId = @PolyhouseId)
AND (@PlantTypeId IS NULL OR s.PlantId = @PlantTypeId)
AND (@SpeciesId IS NULL OR s.SpeciesId = @SpeciesId)
AND (@FromDate IS NULL OR s.SeedingDate >= @FromDate)
AND (@ToDate IS NULL OR s.SeedingDate <= @ToDate)
ORDER BY s.SeedingDate";

                using (var cmd = new SqlCommand(query, conn))
                {
                    cmd.Parameters.AddWithValue("@PolyhouseId", (object)polyhouseId ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("@PlantTypeId", (object)plantTypeId ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("@SpeciesId", (object)speciesId ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("@FromDate", (object)fromDate ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("@ToDate", (object)toDate ?? DBNull.Value);

                    using (var reader = await cmd.ExecuteReaderAsync())
                    {
                        while (await reader.ReadAsync())
                        {
                            sowingRecords.Add(new SeedEntries
                            {
                                Id = reader.GetInt32(0),
                                PolyhouseId = reader.GetInt32(1),
                                PolyhouseName = reader.GetString(2),
                                PlantId = reader.GetInt32(3),
                                PlantTypeName = reader.GetString(4),
                                SpeciesId = reader.GetInt32(5),
                                SpeciesName = reader.GetString(6),
                                SeedingDate = reader.GetDateTime(7),
                                SeedsPlanted = reader.GetInt32(8),
                                DaysPassed = reader.GetInt32(9),
                                TraysAlive = reader.IsDBNull(10) ? null : reader.GetInt32(10),
                                HardeningAlive = reader.IsDBNull(11) ? null : reader.GetInt32(11),
                                AliveCount = reader.IsDBNull(12) ? null : reader.GetInt32(12),
                                //SeedSourceId = reader.GetInt32(13),
                                //OtherSeedSource = reader.IsDBNull(14) ? null : reader.GetString(14),
                                TraysDate = reader.IsDBNull(15) ? null : reader.GetDateTime(15),
                                HardeningDate = reader.IsDBNull(16) ? null : reader.GetDateTime(16),
                                InventoryDate = reader.IsDBNull(17) ? null : reader.GetDateTime(17),
                                Location = reader.IsDBNull(18) ? null : reader.GetString(18),
                                locationDesc = reader.IsDBNull(19) ? null : reader.GetString(19),
                                SeedSourceName = reader.IsDBNull(20) ? null : reader.GetString(20),
                                Supervisor = reader.IsDBNull(21) ? null : reader.GetString(21)
                            });
                        }
                    }
                }
            }

            return sowingRecords;
        }

        public async Task<List<SeedEntries>> GetSowingRecordsByMonth(int? polyhouseId, int? plantTypeId, int? speciesId, int SelectedMonth, int SelectedYear)
        {
            var sowingRecords = new List<SeedEntries>();

            // Default date logic: if both are null, set fromDate to 15 days ago, toDate to today
           

            using (var conn = _dbHelper.GetConnection())
            {
                await conn.OpenAsync();
                var query = @"
SELECT 
    s.Id, 
    s.PolyhouseId, 
    p.Name AS Polyhouse, 
    s.PlantId, 
    pt.Name AS PlantType, 
    s.SpeciesId, 
    ps.Name AS Species, 
    s.SeedingDate, 
    s.SeedsPlanted,
    DATEDIFF(DAY, s.SeedingDate, GETDATE()) AS DaysPassed,
    s.TraysAlive,
    s.HardeningAlive,
    s.AliveCount,
    s.SeedSourceId,
    s.OtherSeedSource,
    s.TraysDate,
    s.HardeningDate,
    s.InventoryDate,
    s.location,
    s.locationdesc,
    se.Name,
    e.Name
FROM SeedEntries s
INNER JOIN Polyhouses p ON s.PolyhouseId = p.Id
INNER JOIN PlantSpecies ps ON s.SpeciesId = ps.Id
INNER JOIN PlantTypes pt ON s.PlantId = pt.Id
Inner JOIN Employee e ON s.SupervisorId = e.Id
LEFT OUTER JOIN SeedSources se ON s.SeedSourceId = se.Id
WHERE (@PolyhouseId IS NULL OR s.PolyhouseId = @PolyhouseId)
AND (@PlantTypeId IS NULL OR s.PlantId = @PlantTypeId)
AND (@SpeciesId IS NULL OR s.SpeciesId = @SpeciesId)
AND MONTH(s.SeedingDate) = @Month AND YEAR(s.SeedingDate) = @Year
ORDER BY s.SeedingDate";

                using (var cmd = new SqlCommand(query, conn))
                {
                    cmd.Parameters.AddWithValue("@PolyhouseId", (object)polyhouseId ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("@PlantTypeId", (object)plantTypeId ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("@SpeciesId", (object)speciesId ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("@Month", SelectedMonth);
                    cmd.Parameters.AddWithValue("@Year", SelectedYear);

                    using (var reader = await cmd.ExecuteReaderAsync())
                    {
                        while (await reader.ReadAsync())
                        {
                            sowingRecords.Add(new SeedEntries
                            {
                                Id = reader.GetInt32(0),
                                PolyhouseId = reader.GetInt32(1),
                                PolyhouseName = reader.GetString(2),
                                PlantId = reader.GetInt32(3),
                                PlantTypeName = reader.GetString(4),
                                SpeciesId = reader.GetInt32(5),
                                SpeciesName = reader.GetString(6),
                                SeedingDate = reader.GetDateTime(7),
                                SeedsPlanted = reader.GetInt32(8),
                                DaysPassed = reader.GetInt32(9),
                                TraysAlive = reader.IsDBNull(10) ? null : reader.GetInt32(10),
                                HardeningAlive = reader.IsDBNull(11) ? null : reader.GetInt32(11),
                                AliveCount = reader.IsDBNull(12) ? null : reader.GetInt32(12),
                                SeedSourceId = reader.GetInt32(13),
                                OtherSeedSource = reader.IsDBNull(14) ? null : reader.GetString(14),
                                TraysDate = reader.IsDBNull(15) ? null : reader.GetDateTime(15),
                                HardeningDate = reader.IsDBNull(16) ? null : reader.GetDateTime(16),
                                InventoryDate = reader.IsDBNull(17) ? null : reader.GetDateTime(17),
                                Location = reader.IsDBNull(18) ? null : reader.GetString(18),
                                locationDesc = reader.IsDBNull(19) ? null : reader.GetString(19),
                                SeedSourceName = reader.IsDBNull(20) ? null : reader.GetString(20),
                                Supervisor = reader.IsDBNull(21) ? null : reader.GetString(21)
                            });
                        }
                    }
                }
            }

            return sowingRecords;
        }




    }
}
