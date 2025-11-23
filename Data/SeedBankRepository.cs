using Microsoft.Data.SqlClient;

namespace PlantStockManager.Data
{
    public class SeedBankRepository
    {
        private readonly DatabaseHelper _db;
        public SeedBankRepository(DatabaseHelper db) => _db = db;

        public DatabaseHelper Db => _db;

        public async Task<int> GetAvailableAsync(int plantId, int speciesId)
        {
            using var conn = _db.GetConnection();
            await conn.OpenAsync();
            var sql = @"
SELECT ISNULL(SUM(Available),0)
FROM dbo.SeedCuttingBank
WHERE PlantId = @p AND SpeciesId = @s ;";
            using var cmd = new SqlCommand(sql, conn);
            cmd.Parameters.AddWithValue("@p", plantId);
            cmd.Parameters.AddWithValue("@s", speciesId);
            var v = await cmd.ExecuteScalarAsync();
            return Convert.ToInt32(v);
        }

        public async Task ConsumeAsync(
            SqlConnection conn, SqlTransaction tx,
            int plantId, int speciesId, 
            int utilizeQty, int seedEntryId, string userName, string? remarks = null)
        {
            using var cmd = new SqlCommand("dbo.SeedCutting_Consume", conn, tx);
            cmd.CommandType = System.Data.CommandType.StoredProcedure;
            cmd.Parameters.AddWithValue("@PlantId", plantId);
            cmd.Parameters.AddWithValue("@SpeciesId", speciesId);
            cmd.Parameters.AddWithValue("@UtilizeQty", utilizeQty);
            cmd.Parameters.AddWithValue("@SeedEntryId", seedEntryId);
            cmd.Parameters.AddWithValue("@UserName", userName);
            cmd.Parameters.AddWithValue("@Remarks", (object?)remarks ?? DBNull.Value);
            await cmd.ExecuteNonQueryAsync();
        }


        public async Task UnconsumeAsync(SqlConnection conn, SqlTransaction tx,
       int seedEntryId, int returnQty, string userName, string? remarks = null)
        {
            using var cmd = new SqlCommand("dbo.SeedCutting_Unconsume", conn, tx)
            {
                CommandType = System.Data.CommandType.StoredProcedure
            };
            cmd.Parameters.AddWithValue("@SeedEntryId", seedEntryId);
            cmd.Parameters.AddWithValue("@ReturnQty", returnQty);
            cmd.Parameters.AddWithValue("@UserName", userName);
            cmd.Parameters.AddWithValue("@Remarks", (object?)remarks ?? DBNull.Value);
            await cmd.ExecuteNonQueryAsync();
        }
    }
}
