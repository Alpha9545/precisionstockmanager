using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Data.SqlClient;
using PlantStockManager.Data;
using PlantStockManager.Models;

namespace PlantStockManager.Pages.Data
{
   
    public class MonthWiseSowingModel : PageModel
    {
        private readonly DatabaseHelper _dbHelper;

        public MonthWiseSowingModel(DatabaseHelper dbHelper)
        {
            _dbHelper = dbHelper;
        }


        public List<Polyhouse> Polyhouses { get; set; }
        public List<PlantType> PlantTypes { get; set; }
        public List<SowingReport> SowingData { get; set; }

        public int SelectedPolyhouseId { get; set; }
        public int SelectedPlantTypeId { get; set; }
        public int SelectedMonth { get; set; }
        public int SelectedYear { get; set; }
        public int DaysInMonth { get; set; }

        public async Task OnGetAsync(int? polyhouseId, int? plantTypeId, int? month, int? year)
        {
            SelectedPolyhouseId = polyhouseId ?? 0;  // Default to 0 (All)
            SelectedPlantTypeId = plantTypeId ?? 0;  // Default to 0 (All)
            SelectedMonth = month ?? DateTime.Now.Month;
            SelectedYear = year ?? DateTime.Now.Year;
            DaysInMonth = DateTime.DaysInMonth(SelectedYear, SelectedMonth);

            Polyhouses = await GetPolyhouses();
            PlantTypes = await GetPlantTypes();
            SowingData = await GetSowingReport(polyhouseId, plantTypeId);
        }

        private async Task<List<Polyhouse>> GetPolyhouses()
        {
            var list = new List<Polyhouse>();
            using (var conn = _dbHelper.GetConnection())
            {
                await conn.OpenAsync();
                var cmd = new SqlCommand("SELECT Id, Name FROM Polyhouses", conn);
                using (var reader = await cmd.ExecuteReaderAsync())
                {
                    while (await reader.ReadAsync())
                    {
                        list.Add(new Polyhouse { Id = reader.GetInt32(0), Name = reader.GetString(1) });
                    }
                }
            }
            return list;
        }

        private async Task<List<PlantType>> GetPlantTypes()
        {
            var list = new List<PlantType>();
            using (var conn = _dbHelper.GetConnection())
            {
                await conn.OpenAsync();
                var cmd = new SqlCommand("SELECT Id, Name FROM PlantTypes", conn);
                using (var reader = await cmd.ExecuteReaderAsync())
                {
                    while (await reader.ReadAsync())
                    {
                        list.Add(new PlantType { Id = reader.GetInt32(0), Name = reader.GetString(1) });
                    }
                }
            }
            return list;
        }

        private async Task<List<SowingReport>> GetSowingReport(int? polyhouseId, int? plantTypeId)
        {
            var data = new List<SowingReport>();
            using (var conn = _dbHelper.GetConnection())
            {
                await conn.OpenAsync();

                // Dynamically build SQL query
                string query = @"
        SELECT " + (polyhouseId == 0 ? "'All Polyhouses'" : "ph.Name") + @" AS Polyhouse, 
               pt.Name AS PlantType, ps.Name AS Species, 
               DAY(se.SeedingDate) AS Day, 
               SUM(se.SeedsPlanted) AS Count
        FROM SeedEntries se
        JOIN Polyhouses ph ON se.PolyhouseId = ph.Id
        JOIN PlantSpecies ps ON se.SpeciesId = ps.Id
        JOIN PlantTypes pt ON ps.PlantTypeId = pt.Id
        WHERE MONTH(se.SeedingDate) = @Month AND YEAR(se.SeedingDate) = @Year
        " + (polyhouseId > 0 ? " AND se.PolyhouseId = @PolyhouseId" : "") +
                    (plantTypeId > 0 ? " AND pt.Id = @PlantTypeId" : "") + @"
        GROUP BY " + (polyhouseId == 0 ? "" : "ph.Name, ") + "pt.Name, ps.Name, DAY(se.SeedingDate)";

                var cmd = new SqlCommand(query, conn);
                cmd.Parameters.AddWithValue("@Month", SelectedMonth);
                cmd.Parameters.AddWithValue("@Year", SelectedYear);
                if (polyhouseId > 0) cmd.Parameters.AddWithValue("@PolyhouseId", polyhouseId);
                if (plantTypeId > 0) cmd.Parameters.AddWithValue("@PlantTypeId", plantTypeId);

                using (var reader = await cmd.ExecuteReaderAsync())
                {
                    while (await reader.ReadAsync())
                    {
                        int day = reader.GetInt32(3);
                        int count = reader.GetInt32(4);

                        var record = data.Find(r => r.PolyhouseName == reader.GetString(0) &&
                                                    r.PlantTypeName == reader.GetString(1) &&
                                                    r.SpeciesName == reader.GetString(2));

                        if (record == null)
                        {
                            record = new SowingReport
                            {
                                PolyhouseName = reader.GetString(0),
                                PlantTypeName = reader.GetString(1),
                                SpeciesName = reader.GetString(2),
                                DailyCounts = new int[DaysInMonth]
                            };
                            data.Add(record);
                        }

                        record.DailyCounts[day - 1] = count;
                        record.MonthTotal += count;
                    }
                }
            }
            return data;
        }
    }

    public class SowingReport { public string PolyhouseName, PlantTypeName, SpeciesName; public int[] DailyCounts; public int MonthTotal; }

}
