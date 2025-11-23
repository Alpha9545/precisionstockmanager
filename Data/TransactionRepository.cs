using Microsoft.Data.SqlClient;
using System;
using System.Data;
using PlantStockManager.Models;


namespace PlantStockManager.Data
{
    public class TransactionRepository
    {
        private readonly DatabaseHelper _dbHelper;

        public TransactionRepository(DatabaseHelper dbHelper)
        {
            _dbHelper = dbHelper;
        }

        public void InsertTransaction(Transaction transaction)
        {
            using (var conn = _dbHelper.GetConnection())
            {
                conn.Open();
                using (SqlCommand cmd = new SqlCommand(
                    @"INSERT INTO Transactions 
                        (SeedEntryId, TransactionType, Quantity, TransactionDate, UpdatedBy, UpdatedOn) 
                      VALUES 
                        (@SeedEntryId, @TransactionType, @Quantity, GETDATE(), @UpdatedBy, GETDATE());", conn))
                {
                    cmd.Parameters.AddWithValue("@SeedEntryId", (object)transaction.SeedEntryId ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("@TransactionType", transaction.TransactionType);
                    cmd.Parameters.AddWithValue("@Quantity", transaction.Quantity);
                    cmd.Parameters.AddWithValue("@UpdatedBy", transaction.UpdatedBy);

                    cmd.ExecuteNonQuery();
                }
            }
        }

        public async Task InsertSowingTransactionAsync(
            SqlConnection conn, SqlTransaction tx,
            int seedEntryId, int quantity, string updatedBy)
        {
            var sql = @"
INSERT INTO dbo.Transactions
(SeedEntryId, TransactionType, Quantity, TransactionDate, UpdatedBy, UpdatedOn)
VALUES (@SeedEntryId, 'Sowing', @Quantity, SYSUTCDATETIME(), @UpdatedBy, SYSUTCDATETIME());";

            using var cmd = new SqlCommand(sql, conn, tx);
            cmd.Parameters.AddWithValue("@SeedEntryId", seedEntryId);
            cmd.Parameters.AddWithValue("@Quantity", quantity);
            cmd.Parameters.AddWithValue("@UpdatedBy", updatedBy);
            await cmd.ExecuteNonQueryAsync();
        }


        public async Task InsertSowingEditTransactionAsync(
       SqlConnection conn, SqlTransaction tx,
       int seedEntryId, int delta, string updatedBy)
        {
            const string sql = @"
INSERT INTO dbo.Transactions
(SeedEntryId, TransactionType, Quantity, TransactionDate, UpdatedBy, UpdatedOn)
VALUES (@SeedEntryId, 'SowingEdit', @Delta, SYSUTCDATETIME(), @UpdatedBy, SYSUTCDATETIME());";

            using var cmd = new SqlCommand(sql, conn, tx);
            cmd.Parameters.AddWithValue("@SeedEntryId", seedEntryId);
            cmd.Parameters.AddWithValue("@Delta", delta); // can be negative
            cmd.Parameters.AddWithValue("@UpdatedBy", updatedBy);
            await cmd.ExecuteNonQueryAsync();
        }
    }
}
