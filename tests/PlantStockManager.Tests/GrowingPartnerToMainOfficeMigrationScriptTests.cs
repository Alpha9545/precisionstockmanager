using System.Linq;

namespace PlantStockManager.Tests
{
    // Phase 32 (Phase 7, Potted Plant Distribution): checks the migration
    // script itself (Database/Phase32_GrowingPartnerToMainOffice.sql), not
    // a live database (none is reachable from here) -- that the widened
    // StockType/SourceMatchesStockType CHECK constraints are additive/
    // guarded, every pre-existing StockType value is still allowed
    // (widened, never narrowed), and no unrelated dbo.InternalTransfers/
    // dbo.PottedPlantStock/dbo.Area object is touched.
    public class GrowingPartnerToMainOfficeMigrationScriptTests
    {
        private static string RepoRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "PlantStockManager.csproj")))
                dir = dir.Parent;
            return dir?.FullName ?? throw new InvalidOperationException("PlantStockManager.csproj not found above the test output folder.");
        }

        private static string ScriptText()
            => File.ReadAllText(Path.Combine(RepoRoot(), "Database", "Phase32_GrowingPartnerToMainOffice.sql"));

        private static string ExecutableSql()
        {
            var noBlockComments = System.Text.RegularExpressions.Regex.Replace(
                ScriptText(), @"/\*.*?\*/", string.Empty, System.Text.RegularExpressions.RegexOptions.Singleline);
            return string.Join('\n', noBlockComments.Split('\n').Select(line =>
            {
                var idx = line.IndexOf("--", StringComparison.Ordinal);
                return idx >= 0 ? line[..idx] : line;
            }));
        }

        [Fact]
        public void Script_WidensStockTypeCheck_Guarded()
        {
            var sql = ScriptText();
            Assert.Contains("WHERE name = 'CK_InternalTransfers_StockType'", sql);
            Assert.Contains("'GrowingPartnerToMainOffice'", sql);
        }

        [Fact]
        public void Script_WidensStockTypeCheck_KeepsEveryPriorValue()
        {
            var sql = ScriptText();
            foreach (var existingType in new[] { "EmptyPot", "PottedPlant", "Cutting", "MainOfficeIssue", "GrowingPartnerToOutlet" })
                Assert.Contains($"'{existingType}'", sql);
        }

        [Fact]
        public void Script_WidensSourceMatchesStockTypeCheck_Guarded()
        {
            var sql = ScriptText();
            Assert.Contains("WHERE name = 'CK_InternalTransfers_SourceMatchesStockType'", sql);
            Assert.Contains("StockType = 'GrowingPartnerToMainOffice'", sql);
            Assert.Contains("SourcePottedPlantStockId  IS NOT NULL AND SourceEmptyPotInventoryId IS NULL AND SourceCuttingStockId IS NULL", sql);
        }

        [Fact]
        public void Script_NeverAddsNewTableOrColumn()
        {
            // Only "ADD CONSTRAINT" appears in this script (the two
            // widened CHECKs) -- no new table and no "ADD <column>
            // <type>" column definition anywhere.
            var sql = ExecutableSql();
            Assert.DoesNotContain("CREATE TABLE", sql, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("ADD COLUMN", sql, StringComparison.OrdinalIgnoreCase);
            foreach (var line in sql.Split('\n'))
            {
                var trimmed = line.TrimStart();
                if (trimmed.StartsWith("ADD ", StringComparison.OrdinalIgnoreCase))
                    Assert.StartsWith("ADD CONSTRAINT", trimmed, StringComparison.OrdinalIgnoreCase);
            }
        }

        [Fact]
        public void Script_NeverDeletesOrUpdatesExistingRows()
        {
            var sql = ExecutableSql();
            Assert.DoesNotContain("DELETE FROM", sql, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("UPDATE dbo.", sql, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("DROP TABLE", sql, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void Script_NeverTouchesAreaOrAreaTypeCheck()
        {
            // 'MainOffice' already exists as a valid AreaType (Phase 14) --
            // this script must not touch dbo.Area or its AreaType CHECK at
            // all.
            var sql = ExecutableSql();
            Assert.DoesNotContain("ALTER TABLE dbo.Area", sql);
            Assert.DoesNotContain("CK_Area_AreaType", sql);
        }

        [Fact]
        public void Script_NeverTouchesStatusCheck()
        {
            // No new Status value is needed -- PendingConfirmation/
            // Rejected/Completed/Cancelled already exist and are reused
            // as-is (single confirmation step, no Transplant-style second
            // stage for this StockType).
            Assert.DoesNotContain("CK_InternalTransfers_Status", ExecutableSql());
        }
    }
}
