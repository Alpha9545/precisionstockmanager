using Microsoft.Data.SqlClient;
using PlantStockManager.Services;

namespace PlantStockManager.Data
{
    // Counts the rows that still reference one parent row (for the
    // dependency-aware Delete). The relationships checked are the known
    // list in DeletionRules plus every foreign key the database itself
    // declares against the parent table, so a relationship added later is
    // never silently missed. Only identifiers from those two sources that
    // pass DeletionRules.IsSafeIdentifier are placed in SQL text; the row
    // Id is always a parameter. Read-only.
    public static class DependencyChecker
    {
        public static async Task<List<DependencyCount>> CountAsync(
            SqlConnection conn, SqlTransaction? tx, string parentTable, IReadOnlyList<DependencyDefinition> known, int id)
        {
            var discovered = await DiscoverForeignKeysAsync(conn, tx, parentTable);
            var definitions = DeletionRules.Merge(known, discovered);

            var result = new List<DependencyCount>();
            foreach (var d in definitions)
            {
                if (!DeletionRules.IsSafeIdentifier(d.Schema) || !DeletionRules.IsSafeIdentifier(d.Table) || !DeletionRules.IsSafeIdentifier(d.Column))
                    continue;

                var qualified = $"[{d.Schema}].[{d.Table}]";
                using (var exists = new SqlCommand(
                    "SELECT CASE WHEN OBJECT_ID(@Table, N'U') IS NOT NULL AND COL_LENGTH(@Table, @Column) IS NOT NULL THEN 1 ELSE 0 END", conn, tx))
                {
                    exists.Parameters.AddWithValue("@Table", qualified);
                    exists.Parameters.AddWithValue("@Column", d.Column);
                    if ((int)(await exists.ExecuteScalarAsync())! == 0)
                        continue;   // table/column not present in this database
                }

                using var count = new SqlCommand($"SELECT COUNT(*) FROM {qualified} WHERE [{d.Column}] = @Id", conn, tx);
                count.Parameters.AddWithValue("@Id", id);
                result.Add(new DependencyCount(d.Label, d.Table, d.Column, (int)(await count.ExecuteScalarAsync())!));
            }
            return result;
        }

        // How many rows of each stock table still hold current stock for this
        // Area (DeletionRules.AreaStockChecks). A check whose table or columns
        // do not exist in this database is skipped. The conditions are fixed
        // in code; the Area Id is a parameter. Read-only.
        public static async Task<List<DependencyCount>> CountStockAsync(
            SqlConnection conn, SqlTransaction? tx, IReadOnlyList<StockCheckDefinition> checks, int id)
        {
            var result = new List<DependencyCount>();
            foreach (var check in checks)
            {
                if (!DeletionRules.IsSafeIdentifier(check.Table) || !check.Columns.All(DeletionRules.IsSafeIdentifier))
                    continue;

                var qualified = $"[dbo].[{check.Table}]";
                var present = true;
                foreach (var column in check.Columns)
                {
                    using var exists = new SqlCommand(
                        "SELECT CASE WHEN OBJECT_ID(@Table, N'U') IS NOT NULL AND COL_LENGTH(@Table, @Column) IS NOT NULL THEN 1 ELSE 0 END", conn, tx);
                    exists.Parameters.AddWithValue("@Table", qualified);
                    exists.Parameters.AddWithValue("@Column", column);
                    if ((int)(await exists.ExecuteScalarAsync())! == 0)
                    {
                        present = false;
                        break;
                    }
                }
                if (!present)
                    continue;

                using var count = new SqlCommand($"SELECT COUNT(*) FROM {qualified} WHERE {check.Condition}", conn, tx);
                count.Parameters.AddWithValue("@Id", id);
                result.Add(new DependencyCount(check.Label, check.Table, string.Join(",", check.Columns), (int)(await count.ExecuteScalarAsync())!));
            }
            return result;
        }

        // Single-column foreign keys to the parent's Id, plus the Id part of
        // composite keys such as (MotherPlantId, SpeciesId).
        private static async Task<List<(string Schema, string Table, string Column)>> DiscoverForeignKeysAsync(
            SqlConnection conn, SqlTransaction? tx, string parentTable)
        {
            const string sql = @"
SELECT OBJECT_SCHEMA_NAME(fkc.parent_object_id), OBJECT_NAME(fkc.parent_object_id), COL_NAME(fkc.parent_object_id, fkc.parent_column_id)
FROM sys.foreign_key_columns fkc
WHERE fkc.referenced_object_id = OBJECT_ID(@Parent)
  AND COL_NAME(fkc.referenced_object_id, fkc.referenced_column_id) = N'Id'
  AND fkc.parent_object_id <> fkc.referenced_object_id";

            var list = new List<(string, string, string)>();
            using var cmd = new SqlCommand(sql, conn, tx);
            cmd.Parameters.AddWithValue("@Parent", parentTable);
            using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                if (reader.IsDBNull(0) || reader.IsDBNull(1) || reader.IsDBNull(2))
                    continue;
                list.Add((reader.GetString(0), reader.GetString(1), reader.GetString(2)));
            }
            return list;
        }
    }
}
