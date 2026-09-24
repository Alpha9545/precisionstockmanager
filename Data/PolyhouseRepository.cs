using PlantStockManager.Models;
using System.Collections.Generic;
using Microsoft.Data.SqlClient;
using System.Threading.Tasks;

namespace PlantStockManager.Data
{
    // Phase B: dbo.Polyhouses.AreaId (added by Database/PhaseB_DirectSowing.sql)
    // records the business hierarchy Area (site) -> Polyhouse (production unit
    // inside it). The legacy dbo.Area.PolyhouseId link (Area located inside a
    // Polyhouse) is untouched and still used by the older modules.
    public class PolyhouseRepository
    {
        private readonly DatabaseHelper _dbHelper;

        private const string BaseSelect = @"
SELECT p.Id, p.Name, p.AreaId, a.Name AS AreaName, a.IsActive AS AreaIsActive
FROM dbo.Polyhouses p
LEFT JOIN dbo.Area a ON a.Id = p.AreaId";

        public PolyhouseRepository(DatabaseHelper dbHelper)
        {
            _dbHelper = dbHelper;
        }

        public async Task<List<Polyhouse>> GetAllPolyhouses()
        {
            using var conn = _dbHelper.GetConnection();
            await conn.OpenAsync();
            using var cmd = new SqlCommand(BaseSelect + " ORDER BY p.Name", conn);
            return await ReadListAsync(cmd);
        }

        // Polyhouses that belong to the given Area (Phase B hierarchy).
        public async Task<List<Polyhouse>> GetByAreaIdAsync(int areaId)
        {
            using var conn = _dbHelper.GetConnection();
            await conn.OpenAsync();
            using var cmd = new SqlCommand(BaseSelect + " WHERE p.AreaId = @AreaId ORDER BY p.Name", conn);
            cmd.Parameters.AddWithValue("@AreaId", areaId);
            return await ReadListAsync(cmd);
        }

        public async Task<Polyhouse?> GetByIdAsync(int id)
        {
            using var conn = _dbHelper.GetConnection();
            await conn.OpenAsync();
            using var cmd = new SqlCommand(BaseSelect + " WHERE p.Id = @Id", conn);
            cmd.Parameters.AddWithValue("@Id", id);
            var list = await ReadListAsync(cmd);
            return list.FirstOrDefault();
        }

        public async Task AddPolyhouse(string name, int? areaId = null)
        {
            using var conn = _dbHelper.GetConnection();
            await conn.OpenAsync();
            using var cmd = new SqlCommand("INSERT INTO dbo.Polyhouses (Name, AreaId) VALUES (@Name, @AreaId)", conn);
            cmd.Parameters.AddWithValue("@Name", name);
            cmd.Parameters.AddWithValue("@AreaId", (object?)areaId ?? DBNull.Value);
            await cmd.ExecuteNonQueryAsync();
        }

        public async Task UpdatePolyhouse(int id, string name, int? areaId = null)
        {
            using var conn = _dbHelper.GetConnection();
            await conn.OpenAsync();
            using var cmd = new SqlCommand("UPDATE dbo.Polyhouses SET Name = @Name, AreaId = @AreaId WHERE Id = @Id", conn);
            cmd.Parameters.AddWithValue("@Name", name);
            cmd.Parameters.AddWithValue("@AreaId", (object?)areaId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@Id", id);
            await cmd.ExecuteNonQueryAsync();
        }

        private static async Task<List<Polyhouse>> ReadListAsync(SqlCommand cmd)
        {
            var polyhouses = new List<Polyhouse>();
            using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                polyhouses.Add(new Polyhouse
                {
                    Id = reader.GetInt32(0),
                    Name = reader.GetString(1),
                    AreaId = reader.IsDBNull(2) ? null : reader.GetInt32(2),
                    AreaName = reader.IsDBNull(3) ? null : reader.GetString(3),
                    AreaIsActive = !reader.IsDBNull(4) && reader.GetBoolean(4)
                });
            }
            return polyhouses;
        }
    }
}
