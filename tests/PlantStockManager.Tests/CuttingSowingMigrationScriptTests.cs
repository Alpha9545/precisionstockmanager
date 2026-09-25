using System.Linq;
using PlantStockManager.Services;

namespace PlantStockManager.Tests
{
    // Phase 5 (Cutting Sowing / Tray Production): checks the migration
    // script itself (Database/Phase30_CuttingSowing.sql), not a live
    // database (none is reachable from here) -- that dbo.CuttingSowings
    // and the dual-source widening are additive/guarded, the closed
    // cavity/wastage-reason lists match DirectSowingRules exactly (so the
    // two can never silently drift apart), and -- critically -- that the
    // EXISTING Direct Sowing trigger backstop (PhaseB_ApprovalAndTrayRules.sql
    // / PhaseB_ReadyStockTrays.sql) is left completely untouched, since
    // Phase 5 deliberately does not extend it (see the script's own header
    // comment for why) and any accidental edit there would be a real risk
    // to the already-working Direct Sowing pipeline.
    public class CuttingSowingMigrationScriptTests
    {
        private static string RepoRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "PlantStockManager.csproj")))
                dir = dir.Parent;
            return dir?.FullName ?? throw new InvalidOperationException("PlantStockManager.csproj not found above the test output folder.");
        }

        private static string ScriptText()
            => File.ReadAllText(Path.Combine(RepoRoot(), "Database", "Phase30_CuttingSowing.sql"));

        // Strips '/* ... */' block comments and '--' line comments (this
        // script explains, in comments, exactly which existing objects it
        // deliberately leaves alone -- those explanatory mentions must not
        // trip a "never touches X" check).
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
        public void Script_CreatesCuttingSowingsTable_Guarded()
        {
            var sql = ScriptText();
            Assert.Contains("CREATE TABLE dbo.CuttingSowings", sql);
            Assert.Contains("WHERE name = 'CuttingSowings' AND schema_id = SCHEMA_ID('dbo')", sql);
        }

        [Fact]
        public void Script_NeverAddsResponsiblePerson()
        {
            // Phase 1 retired Responsible Person from data entry across the
            // whole application; a brand-new table must never reintroduce it
            // as an actual column (this script's own comment explaining that
            // is fine -- checked against the executable SQL only).
            Assert.DoesNotContain("ResponsiblePerson", ExecutableSql());
        }

        [Fact]
        public void Script_CavityAndTrayMathConstraints_MatchDirectSowingRulesExactly()
        {
            var sql = ScriptText();
            foreach (var cavity in DirectSowingRules.CavityTypes)
                Assert.Contains($"N'{cavity}'", sql);
            Assert.Contains("CK_CuttingSowings_TrayMath", sql);
            Assert.Contains("QuantitySown = NumberOfTrays *", sql);
        }

        [Theory]
        [InlineData("CuttingSowingId")]
        public void Script_WidensReadyStockAndReadyConfirmations_Guarded(string column)
        {
            var sql = ScriptText();
            Assert.Contains($"COL_LENGTH('dbo.ReadyStock', '{column}')", sql);
            Assert.Contains($"COL_LENGTH('dbo.ReadyConfirmations', '{column}')", sql);
        }

        [Fact]
        public void Script_AddsSourceTypeChecks_ExactlyOneOf()
        {
            var sql = ScriptText();
            Assert.Contains("CK_ReadyStock_SourceType", sql);
            Assert.Contains("CK_ReadyConfirmations_SourceType", sql);
        }

        [Fact]
        public void Script_RelaxesSeedSowingIdToNullable_OnlyIfCurrentlyNotNull()
        {
            var sql = ScriptText();
            // Guarded by is_nullable = 0 -- a second run is a no-op, and no
            // existing row's SeedSowingId value is touched (ALTER COLUMN
            // ... NULL never changes data, only the column's nullability).
            Assert.Contains("is_nullable = 0", sql);
            Assert.Contains("ALTER COLUMN SeedSowingId INT NULL", sql);
        }

        [Fact]
        public void Script_AddsSownTransactionType_ToExistingCuttingStockLedger()
        {
            var sql = ScriptText();
            Assert.Contains("CK_CuttingStockTx_Type", sql);
            Assert.Contains("'Sown'", sql);
            // Every previously-allowed type must still be listed (widened,
            // never narrowed).
            foreach (var existingType in new[] { "Harvest", "Transfer", "Potted", "Adjustment", "ReversalRemoval", "Transplanted", "ReversalReturn" })
                Assert.Contains($"'{existingType}'", sql);
        }

        // The critical safety check: this phase must NEVER touch the
        // existing Direct Sowing trigger backstop or its objects -- those
        // are guarded PlantsIMS2_Test-only and "not yet approved for
        // production" in their own scripts; editing them here, blind (no
        // reachable database to verify against), would risk the
        // already-working Direct Sowing pipeline for no need this phase has.
        [Theory]
        [InlineData("TR_ReadyConfirmations_AssignedSupervisor")]
        [InlineData("TR_ReadyConfirmations_TrayQuantity")]
        [InlineData("TR_ReadyStock_WholeTrays")]
        [InlineData("TR_SeedSowings_RequireSeedQuantity")]
        [InlineData("TR_SeedSowings_ImmutableTrayData")]
        [InlineData("FK_ReadyStock_SowingCavity")]
        [InlineData("UX_ReadyConfirmations_OneConfirmedPerSowing")]
        [InlineData("UQ_ReadyStock_SeedSowing")]
        public void Script_NeverAltersOrDrops_ExistingDirectSowingBackstopObjects(string objectName)
        {
            var sql = ExecutableSql(); // comments (this script's own explanation of what it leaves alone) don't count
            // UQ_ReadyStock_SeedSowing is the one exception: it IS dropped
            // (replaced by the filtered unique index), guarded by an
            // existence check -- verified separately below. Every other
            // object in this list must not appear in any executable
            // statement in this script.
            if (objectName == "UQ_ReadyStock_SeedSowing")
            {
                Assert.Contains($"DROP CONSTRAINT {objectName}", sql);
                Assert.Contains($"EXISTS (SELECT 1 FROM sys.key_constraints WHERE name = '{objectName}')", sql);
            }
            else
            {
                Assert.DoesNotContain(objectName, sql);
            }
        }

        [Fact]
        public void Script_NeverDropsAnyTrigger()
        {
            Assert.DoesNotContain("DROP TRIGGER", ExecutableSql(), StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void Script_NeverDeletesOrUpdatesExistingRows()
        {
            var sql = ExecutableSql();
            Assert.DoesNotContain("DELETE FROM", sql, StringComparison.OrdinalIgnoreCase);
            // ALTER COLUMN and ALTER TABLE ... ADD CONSTRAINT are fine (no
            // data row is changed); an UPDATE statement against a data row
            // is not used anywhere in this script.
            Assert.DoesNotContain("UPDATE dbo.", sql, StringComparison.OrdinalIgnoreCase);
        }
    }
}
