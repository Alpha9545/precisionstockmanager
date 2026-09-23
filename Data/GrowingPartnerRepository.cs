using Microsoft.Data.SqlClient;
using PlantStockManager.Models;

namespace PlantStockManager.Data
{
    // Simple master-data CRUD for dbo.GrowingPartners (Phase 17), mirroring
    // VendorRepository's pattern. Not related to VendorRepository/dbo.Vendors
    // -- GrowingPartners is a distinct concept (see GrowingPartner.cs).
    public class GrowingPartnerRepository
    {
        private readonly DatabaseHelper _dbHelper;

        public GrowingPartnerRepository(DatabaseHelper dbHelper)
        {
            _dbHelper = dbHelper;
        }

        private const string BaseSelect = @"
SELECT Id, Name, ContactPerson, Phone, Email, Address, IsActive,
       CreatedDate, CreatedBy, ModifiedDate, ModifiedBy
FROM dbo.GrowingPartners";

        public async Task<List<GrowingPartner>> GetAllAsync(bool activeOnly = false)
        {
            var list = new List<GrowingPartner>();
            using var conn = _dbHelper.GetConnection();
            await conn.OpenAsync();

            var sql = BaseSelect + (activeOnly ? " WHERE IsActive = 1" : "") + " ORDER BY Name";
            using var cmd = new SqlCommand(sql, conn);
            using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                list.Add(Map(reader));
            }
            return list;
        }

        public async Task<GrowingPartner?> GetByIdAsync(int id)
        {
            using var conn = _dbHelper.GetConnection();
            await conn.OpenAsync();

            var sql = BaseSelect + " WHERE Id = @Id";
            using var cmd = new SqlCommand(sql, conn);
            cmd.Parameters.AddWithValue("@Id", id);
            using var reader = await cmd.ExecuteReaderAsync();
            if (await reader.ReadAsync())
            {
                return Map(reader);
            }
            return null;
        }

        public async Task<(bool Success, string? Message, int Id)> InsertAsync(GrowingPartner entry)
        {
            if (string.IsNullOrWhiteSpace(entry.Name))
                return (false, "Growing Partner Name is required.", 0);

            using var conn = _dbHelper.GetConnection();
            await conn.OpenAsync();

            try
            {
                const string insertSql = @"
INSERT INTO dbo.GrowingPartners (Name, ContactPerson, Phone, Email, Address, IsActive, CreatedDate, CreatedBy)
VALUES (@Name, @ContactPerson, @Phone, @Email, @Address, @IsActive, SYSUTCDATETIME(), @CreatedBy);
SELECT CAST(SCOPE_IDENTITY() AS INT);";

                using var cmd = new SqlCommand(insertSql, conn);
                cmd.Parameters.AddWithValue("@Name", entry.Name);
                cmd.Parameters.AddWithValue("@ContactPerson", (object?)entry.ContactPerson ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@Phone", (object?)entry.Phone ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@Email", (object?)entry.Email ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@Address", (object?)entry.Address ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@IsActive", entry.IsActive);
                cmd.Parameters.AddWithValue("@CreatedBy", (object?)entry.CreatedBy ?? DBNull.Value);

                var newId = (int)await cmd.ExecuteScalarAsync();
                entry.Id = newId;
                return (true, null, newId);
            }
            catch (Exception ex)
            {
                return (false, ex.Message, 0);
            }
        }

        public async Task<(bool Success, string? Message)> UpdateAsync(GrowingPartner entry)
        {
            if (string.IsNullOrWhiteSpace(entry.Name))
                return (false, "Growing Partner Name is required.");

            using var conn = _dbHelper.GetConnection();
            await conn.OpenAsync();

            try
            {
                const string updateSql = @"
UPDATE dbo.GrowingPartners
SET Name = @Name, ContactPerson = @ContactPerson, Phone = @Phone, Email = @Email,
    Address = @Address, IsActive = @IsActive,
    ModifiedDate = SYSUTCDATETIME(), ModifiedBy = @ModifiedBy
WHERE Id = @Id";

                using var cmd = new SqlCommand(updateSql, conn);
                cmd.Parameters.AddWithValue("@Id", entry.Id);
                cmd.Parameters.AddWithValue("@Name", entry.Name);
                cmd.Parameters.AddWithValue("@ContactPerson", (object?)entry.ContactPerson ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@Phone", (object?)entry.Phone ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@Email", (object?)entry.Email ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@Address", (object?)entry.Address ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@IsActive", entry.IsActive);
                cmd.Parameters.AddWithValue("@ModifiedBy", (object?)entry.ModifiedBy ?? DBNull.Value);

                var rows = await cmd.ExecuteNonQueryAsync();
                return rows > 0 ? (true, null) : (false, "Growing Partner not found.");
            }
            catch (Exception ex)
            {
                return (false, ex.Message);
            }
        }

        private static GrowingPartner Map(SqlDataReader reader)
        {
            return new GrowingPartner
            {
                Id = reader.GetInt32(reader.GetOrdinal("Id")),
                Name = reader.GetString(reader.GetOrdinal("Name")),
                ContactPerson = reader.IsDBNull(reader.GetOrdinal("ContactPerson")) ? null : reader.GetString(reader.GetOrdinal("ContactPerson")),
                Phone = reader.IsDBNull(reader.GetOrdinal("Phone")) ? null : reader.GetString(reader.GetOrdinal("Phone")),
                Email = reader.IsDBNull(reader.GetOrdinal("Email")) ? null : reader.GetString(reader.GetOrdinal("Email")),
                Address = reader.IsDBNull(reader.GetOrdinal("Address")) ? null : reader.GetString(reader.GetOrdinal("Address")),
                IsActive = reader.GetBoolean(reader.GetOrdinal("IsActive")),
                CreatedDate = reader.GetDateTime(reader.GetOrdinal("CreatedDate")),
                CreatedBy = reader.IsDBNull(reader.GetOrdinal("CreatedBy")) ? null : reader.GetString(reader.GetOrdinal("CreatedBy")),
                ModifiedDate = reader.IsDBNull(reader.GetOrdinal("ModifiedDate")) ? null : reader.GetDateTime(reader.GetOrdinal("ModifiedDate")),
                ModifiedBy = reader.IsDBNull(reader.GetOrdinal("ModifiedBy")) ? null : reader.GetString(reader.GetOrdinal("ModifiedBy"))
            };
        }
    }
}
