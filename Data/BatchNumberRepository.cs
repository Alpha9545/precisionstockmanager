using System.Globalization;
using Microsoft.Data.SqlClient;

namespace PlantStockManager.Data
{
    // Generates sequential batch numbers such as "MP-2026-00001" against
    // dbo.BatchNumberSequences. Deliberately generic (prefix + year) so
    // later phases can reuse the same table/class for CUT-, CD-, PROP-,
    // POT-, BK- and DIS- batch numbers instead of one sequence table per
    // module.
    //
    // Phase B: also generates the Direct Sowing batch number
    // "YYYY-MM-DD-L-NNN" (e.g. 2026-01-20-A-001) from the SAME table -- the
    // per-day sequence is simply another (Prefix, Year) counter row, keyed
    // "SB" + MMDD (e.g. "SB0120", 2026). No second batch-number system.
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
            var nextNumber = await GetNextSequenceAsync(conn, tx, prefix, year);
            return $"{prefix}-{year}-{nextNumber.ToString().PadLeft(padding, '0')}";
        }

        // The atomic counter itself (MERGE ... WITH (HOLDLOCK) serialises
        // concurrent callers for the same Prefix/Year).
        public async Task<int> GetNextSequenceAsync(SqlConnection conn, SqlTransaction tx, string prefix, int year)
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

            return (int)(await cmd.ExecuteScalarAsync())!;
        }

        // ---- Direct Sowing batch number (Phase B) --------------------------

        public const string SowingBatchPrefix = "SB";

        // January = A ... December = L.
        public static char MonthLetter(int month)
        {
            if (month < 1 || month > 12)
                throw new ArgumentOutOfRangeException(nameof(month));
            return (char)('A' + month - 1);
        }

        // Counter key for the per-day sequence: "SB" + MMDD (fits the
        // NVARCHAR(10) Prefix column); the Year column holds the year.
        public static string SowingBatchCounterPrefix(DateTime sowingDate)
            => SowingBatchPrefix + sowingDate.ToString("MMdd", CultureInfo.InvariantCulture);

        // "YYYY-MM-DD-L-NNN" -- the sequence is zero-padded to 3 digits and
        // simply grows beyond 999 (still unique, still <= 20 characters).
        public static string FormatSowingBatchNumber(DateTime sowingDate, int sequence)
        {
            if (sequence < 1)
                throw new ArgumentOutOfRangeException(nameof(sequence));
            return $"{sowingDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}-{MonthLetter(sowingDate.Month)}-{sequence.ToString("000", CultureInfo.InvariantCulture)}";
        }

        public async Task<string> GetNextSowingBatchNumberAsync(SqlConnection conn, SqlTransaction tx, DateTime sowingDate)
        {
            var sequence = await GetNextSequenceAsync(conn, tx, SowingBatchCounterPrefix(sowingDate), sowingDate.Year);
            return FormatSowingBatchNumber(sowingDate, sequence);
        }
    }
}
