using PlantStockManager.Services;

namespace PlantStockManager.Tests
{
    // Phase 4 (Cutting Delivery to Main Office): checks the migration
    // script itself (Database/Phase29_CuttingDeliveryTrayCavity.sql), not a
    // live database (none is reachable from here) -- that every new column
    // is additive/guarded, no existing data is touched, and its closed
    // cavity/wastage-reason lists match DirectSowingRules exactly (the
    // reuse this phase is built on, so the two can never silently drift
    // apart).
    public class CuttingDeliveryMigrationScriptTests
    {
        private static string RepoRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "PlantStockManager.csproj")))
                dir = dir.Parent;
            return dir?.FullName ?? throw new InvalidOperationException("PlantStockManager.csproj not found above the test output folder.");
        }

        private static string ScriptText()
            => File.ReadAllText(Path.Combine(RepoRoot(), "Database", "Phase29_CuttingDeliveryTrayCavity.sql"));

        [Theory]
        [InlineData("CavityType")]
        [InlineData("CuttingQuantityEntered")]
        [InlineData("NumberOfTrays")]
        [InlineData("ActualReadyTrays")]
        [InlineData("WastageQuantity")]
        [InlineData("WastageReason")]
        public void Script_AddsEachColumn_Guarded(string column)
        {
            var sql = ScriptText();
            Assert.Contains($"AND name = '{column}'", sql);
            Assert.Contains($"ADD {column}", sql);
        }

        [Fact]
        public void Script_EveryNewColumnIsNullable()
        {
            var sql = ScriptText();
            foreach (var line in new[]
            {
                "ADD CavityType NVARCHAR(20) NULL",
                "ADD CuttingQuantityEntered DECIMAL(18,2) NULL",
                "ADD NumberOfTrays INT NULL",
                "ADD ActualReadyTrays DECIMAL(18,2) NULL",
                "ADD WastageQuantity DECIMAL(18,2) NULL",
                "ADD WastageReason NVARCHAR(50) NULL",
            })
            {
                Assert.Contains(line, sql);
            }
        }

        [Fact]
        public void Script_CavityConstraint_MatchesDirectSowingRulesExactly()
        {
            var sql = ScriptText();
            foreach (var cavity in DirectSowingRules.CavityTypes)
                Assert.Contains($"'{cavity}'", sql);
        }

        [Fact]
        public void Script_WastageReasonConstraint_MatchesDirectSowingRulesExactly()
        {
            var sql = ScriptText();
            foreach (var reason in DirectSowingRules.WastageReasons)
                Assert.Contains($"'{reason}'", sql);
        }

        [Fact]
        public void Script_NeverTouchesExistingDataOrObjects()
        {
            var sql = ScriptText();
            Assert.DoesNotContain("DELETE", sql, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("UPDATE ", sql, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("DROP", sql, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("CREATE TABLE", sql, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("TRUNCATE", sql, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void Script_TargetsOnlyInternalTransfers()
        {
            var sql = ScriptText();
            Assert.DoesNotContain("ALTER TABLE dbo.SeedSowings", sql);
            Assert.DoesNotContain("ALTER TABLE dbo.ReadyConfirmations", sql);
            Assert.DoesNotContain("ALTER TABLE dbo.CuttingStock", sql);
        }
    }
}
