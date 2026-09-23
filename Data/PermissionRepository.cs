using Microsoft.Data.SqlClient;
using PlantStockManager.Models;

namespace PlantStockManager.Data
{
    public class PermissionRepository
    {
        private readonly DatabaseHelper _dbHelper;

        public PermissionRepository(DatabaseHelper dbHelper)
        {
            _dbHelper = dbHelper;
        }

        public async Task<List<Permission>> GetAllPermissionsAsync()
        {
            var permissions = new List<Permission>();
            using var conn = _dbHelper.GetConnection();
            await conn.OpenAsync();

            const string sql = "SELECT Id, Code, Description FROM dbo.Permissions ORDER BY Code";
            using var cmd = new SqlCommand(sql, conn);
            using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                permissions.Add(new Permission
                {
                    Id = reader.GetInt32(reader.GetOrdinal("Id")),
                    Code = reader.GetString(reader.GetOrdinal("Code")),
                    Description = reader.IsDBNull(reader.GetOrdinal("Description")) ? null : reader.GetString(reader.GetOrdinal("Description"))
                });
            }
            return permissions;
        }
    }
}
