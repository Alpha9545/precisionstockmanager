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
        // dbo.Polyhouses has no IsActive column of its own -- "active" here
        // means the parent Area is active, same gate already applied to the
        // Areas dropdown itself (Create/EditModel.LoadDropdownsAsync).
        public async Task<List<Polyhouse>> GetByAreaIdAsync(int areaId)
        {
            using var conn = _dbHelper.GetConnection();
            await conn.OpenAsync();
            using var cmd = new SqlCommand(BaseSelect + " WHERE p.AreaId = @AreaId AND a.IsActive = 1 ORDER BY p.Name", conn);
            cmd.Parameters.AddWithValue("@AreaId", areaId);
            return await ReadListAsync(cmd);
        }

        // Cutting Tray Sowing destination choices: real, actual Polyhouses
        // (named physical facilities) a cutting may be sown into. Excludes:
        //   - Outlet (OutletRules.AreaType) -- customer-facing, never a
        //     growing destination;
        //   - a Polyhouse with no Area assigned at all -- choosing one would
        //     leave DirectSowingRules.ResolveGrowingLocation nothing to
        //     resolve to except the stock's own (Main Office) Area, silently
        //     reproducing the exact bug this list exists to prevent;
        //   - any inactive Area.
        // 2026-10-02 correction: Main Office (DirectSowingRules.
        // MainOfficeAreaType) is NOT excluded here. The earlier blanket
        // exclusion conflated two different things: the Main Office Cutting
        // Stock DEPOT (a stock pool, never itself a sowing destination -- a
        // sowing is never silently defaulted to it because PolyhouseId is
        // mandatory with no blank/fallback option) versus the ACTUAL, named
        // Polyhouses (e.g. "Facility-5") that are organisationally filed
        // under a Main Office-type Area and are real growing sites like any
        // other -- those must be selectable. Every other AreaType (including
        // the documented-but-currently-unused 'Kunjir'/'Kiran', and a
        // legacy/plain Area with AreaType NULL) is likewise a legitimate
        // growing destination -- this is an EXCLUDE list (Outlet + inactive +
        // unassigned only), not an allow-list of specific types.
        public async Task<List<Polyhouse>> GetGrowingDestinationsAsync()
        {
            using var conn = _dbHelper.GetConnection();
            await conn.OpenAsync();
            using var cmd = new SqlCommand(BaseSelect + @"
WHERE p.AreaId IS NOT NULL AND a.IsActive = 1
  AND (a.AreaType IS NULL OR a.AreaType <> @Outlet)
ORDER BY a.Name, p.Name", conn);
            cmd.Parameters.AddWithValue("@Outlet", Services.OutletRules.AreaType);
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
