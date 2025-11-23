using Microsoft.Data.SqlClient;
using PlantStockManager.Models;


namespace PlantStockManager.Data
{
    public class InventoryTransactionRepository
    {
        private readonly DatabaseHelper _dbHelper;

        public InventoryTransactionRepository(DatabaseHelper dbHelper)
        {
            _dbHelper = dbHelper;
        }
        public async Task<List<InventoryTransaction>> GetTransactionsByBookingId(int bookingId)
        {
            var list = new List<InventoryTransaction>();
            using var conn = _dbHelper.GetConnection();
            await conn.OpenAsync();

            var query = @"SELECT it.Id, it.InventoryId, it.QuantityUtilized, it.TransactionType,
                     pt.Name AS PlantTypeName, ps.Name AS SpeciesName
                 FROM InventoryTransactions it
                 JOIN Inventory i ON i.Id = it.InventoryId
                 JOIN PlantTypes pt ON pt.Id = i.PlantId
                 JOIN PlantSpecies ps ON ps.Id = i.SpeciesId
                 WHERE it.BookingId = @BookingId AND it.TransactionType = 'Allocation'";

            using var cmd = new SqlCommand(query, conn);
            cmd.Parameters.AddWithValue("@BookingId", bookingId);

            using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                list.Add(new InventoryTransaction
                {
                    Id = reader.GetInt32(0),
                    InventoryId = reader.GetInt32(1),
                    QuantityUtilized = reader.GetInt32(2),
                    TransactionType = reader["TransactionType"].ToString(),
                    PlantTypeName = reader["PlantTypeName"].ToString(),
                    SpeciesName = reader["SpeciesName"].ToString()
                });
            }
            return list;
        }

        public async Task MarkTransactionAsAdjustment(int transactionId)
        {
            using var conn = _dbHelper.GetConnection();
            await conn.OpenAsync();

            var query = @"
        UPDATE InventoryTransactions
        SET TransactionType = 'Adjustment',
            UpdatedOn = GETDATE(),
            Notes = ISNULL(Notes, '') + ' (Auto-adjusted on Booking revert)'
        WHERE Id = @Id";

            using var cmd = new SqlCommand(query, conn);
            cmd.Parameters.AddWithValue("@Id", transactionId);

            await cmd.ExecuteNonQueryAsync();
        }


    }
}
