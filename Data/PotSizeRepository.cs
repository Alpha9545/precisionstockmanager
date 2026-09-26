using Microsoft.Data.SqlClient;
using PlantStockManager.Models;
using PlantStockManager.Services;

namespace PlantStockManager.Data
{
    // dbo.PotSizes -- the controlled pot-size master (Phase D).
    public class PotSizeRepository
    {
        private readonly DatabaseHelper _dbHelper;

        public PotSizeRepository(DatabaseHelper dbHelper)
        {
            _dbHelper = dbHelper;
        }

        public async Task<List<PotSize>> GetAllAsync(bool activeOnly = false)
        {
            var list = new List<PotSize>();
            using var conn = _dbHelper.GetConnection();
            await conn.OpenAsync();
            using var cmd = new SqlCommand(@"
SELECT Id, Name, SortOrder, IsActive, CreatedDate, CreatedBy
FROM dbo.PotSizes
WHERE (@ActiveOnly = 0 OR IsActive = 1)
ORDER BY SortOrder, Name", conn);
            cmd.Parameters.AddWithValue("@ActiveOnly", activeOnly);
            using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                list.Add(new PotSize
                {
                    Id = reader.GetInt32(0),
                    Name = reader.GetString(1),
                    SortOrder = reader.GetInt32(2),
                    IsActive = reader.GetBoolean(3),
                    CreatedDate = reader.GetDateTime(4),
                    CreatedBy = reader.IsDBNull(5) ? null : reader.GetString(5)
                });
            }
            return list;
        }

        public async Task<bool> IsActiveSizeAsync(string? name)
        {
            if (string.IsNullOrWhiteSpace(name))
                return false;
            using var conn = _dbHelper.GetConnection();
            await conn.OpenAsync();
            using var cmd = new SqlCommand("SELECT COUNT(*) FROM dbo.PotSizes WHERE Name = @Name AND IsActive = 1", conn);
            cmd.Parameters.AddWithValue("@Name", name);
            return (int)(await cmd.ExecuteScalarAsync())! > 0;
        }

        // The name is normalised ("5\"" -> "5 inch") and refused when the
        // same size already exists under any spelling.
        public async Task<(bool Success, string? Message)> InsertAsync(string? name, int sortOrder, string? createdBy)
        {
            var existing = (await GetAllAsync()).Select(p => p.Name).ToList();
            var (ok, normalized, error) = PotSizeRules.Validate(name, existing);
            if (!ok)
                return (false, error);

            using var conn = _dbHelper.GetConnection();
            await conn.OpenAsync();
            try
            {
                using var cmd = new SqlCommand(
                    "INSERT INTO dbo.PotSizes (Name, SortOrder, IsActive, CreatedBy) VALUES (@Name, @SortOrder, 1, @CreatedBy)", conn);
                cmd.Parameters.AddWithValue("@Name", normalized);
                cmd.Parameters.AddWithValue("@SortOrder", sortOrder);
                cmd.Parameters.AddWithValue("@CreatedBy", (object?)createdBy ?? DBNull.Value);
                await cmd.ExecuteNonQueryAsync();
                return (true, null);
            }
            catch (SqlException ex) when (ex.Number is 2601 or 2627)
            {
                return (false, $"Pot size '{normalized}' already exists.");
            }
        }

        // A size already used in stock cannot be renamed (the stock rows
        // reference it); it can be deactivated so it is no longer offered.
        public async Task<(bool Success, string? Message)> SetActiveAsync(int id, bool isActive)
        {
            using var conn = _dbHelper.GetConnection();
            await conn.OpenAsync();
            using var cmd = new SqlCommand("UPDATE dbo.PotSizes SET IsActive = @IsActive WHERE Id = @Id", conn);
            cmd.Parameters.AddWithValue("@IsActive", isActive);
            cmd.Parameters.AddWithValue("@Id", id);
            var rows = await cmd.ExecuteNonQueryAsync();
            return rows > 0 ? (true, null) : (false, "Pot size not found.");
        }

        public async Task<(bool Success, string? Message)> SetSortOrderAsync(int id, int sortOrder)
        {
            using var conn = _dbHelper.GetConnection();
            await conn.OpenAsync();
            using var cmd = new SqlCommand("UPDATE dbo.PotSizes SET SortOrder = @SortOrder WHERE Id = @Id", conn);
            cmd.Parameters.AddWithValue("@SortOrder", sortOrder);
            cmd.Parameters.AddWithValue("@Id", id);
            var rows = await cmd.ExecuteNonQueryAsync();
            return rows > 0 ? (true, null) : (false, "Pot size not found.");
        }
    }
}
