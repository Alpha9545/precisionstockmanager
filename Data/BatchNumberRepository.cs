using Microsoft.Data.SqlClient;

namespace PlantStockManager.Data
{
    // Generates sequential batch numbers such as "MP-2026-00001" against
    // dbo.BatchNumberSequences. Deliberately generic (prefix + year) so
    // later phases can reuse the same table/class for CUT-, CD-, PROP-,
    // POT-, BK- and DIS- batch numbers instead of one sequence table per
    // module.
    public class BatchNumberRepository
    {
        private readonly DatabaseHelper _dbHelper;

        public BatchNumberRepository(DatabaseHelper dbHelper)
        {
            _dbHelper = dbHelper;
        }

        // Must be called on the SAME connection/transaction as the insert
        // that consumes the returned number, so that if the insert fails
        // and the transaction rolls back, the sequence increment rolls
        // back with it (no gaps left behind by failed saves).
        public async Task<string> GetNextBatchNumberAsync(SqlConnection conn, SqlTransaction tx, string prefix, int year, int padding = 5)
        {
            const string mergeSql = @"
MERGE dbo.BatchNumberSequences WITH (HOLDLOCK) AS target
USING (SELECT @Prefix AS Prefix, @Year AS [Year]) AS src
    ON target.Prefix = src.Prefix AND target.[Year] = src.[Year]
WHEN MATCHED THEN
    UPDATE SET LastNumber = target.LastNumber + 1
WHEN NOT MATCHED THEN
    INSERT (Prefix, [Year], LastNumber) VALUES (src.Prefix, src.[Year], 1)
OUTPUT INSERTED.LastNumber;";

            using var cmd = new SqlCommand(mergeSql, conn, tx);
            cmd.Parameters.AddWithValue("@Prefix", prefix);
            cmd.Parameters.AddWithValue("@Year", year);

            var nextNumber = (int)await cmd.ExecuteScalarAsync();
            return $"{prefix}-{year}-{nextNumber.ToString().PadLeft(padding, '0')}";
        }
    }
}
