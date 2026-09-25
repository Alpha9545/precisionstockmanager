using System.Linq;

namespace PlantStockManager.Tests
{
    // Phase 31 (Phase 6, Cutting to Potted Plant Production): checks the
    // migration script itself (Database/Phase31_PotProductionBatch.sql),
    // not a live database (none is reachable from here) -- that the new
    // header table and its widening of dbo.PotProduction are additive/
    // guarded, and -- critically -- that no existing PotProduction/
    // CuttingStock/EmptyPotInventory object or row is altered or dropped.
    public class PotProductionBatchMigrationScriptTests
    {
        private static string RepoRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "PlantStockManager.csproj")))
                dir = dir.Parent;
            return dir?.FullName ?? throw new InvalidOperationException("PlantStockManager.csproj not found above the test output folder.");
        }

        private static string ScriptText()
            => File.ReadAllText(Path.Combine(RepoRoot(), "Database", "Phase31_PotProductionBatch.sql"));

        // Strips '/* ... */' block comments and '--' line comments -- this
        // script explains, in its own header comment, exactly which
        // existing objects/rules it deliberately reuses or leaves alone;
        // those explanatory mentions must not trip a "never touches X" check.
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
        public void Script_CreatesPotProductionBatchesTable_Guarded()
        {
            var sql = ScriptText();
            Assert.Contains("CREATE TABLE dbo.PotProductionBatches", sql);
            Assert.Contains("WHERE name = 'PotProductionBatches' AND schema_id = SCHEMA_ID('dbo')", sql);
        }

        [Fact]
        public void Script_NeverAddsColorColumn()
        {
            // D-4 (previously approved, this engagement): color is never a
            // separate DB column -- the Variety/Species name already
            // carries it. Checked against the executable SQL only (the
            // script's own header comment explains this decision in
            // English prose, which must not trip the check).
            Assert.DoesNotContain("Color", ExecutableSql());
        }

        [Fact]
        public void Script_NeverAddsResponsiblePersonOrApprovalColumn()
        {
            // Rule 8: no unnecessary approval or employee-responsibility
            // fields. Ready confirmation reuses the existing
            // assigned-supervisor rule via the single SupervisorId column,
            // never a second approver/responsible-person column.
            var sql = ExecutableSql();
            Assert.DoesNotContain("ResponsiblePerson", sql);
            Assert.DoesNotContain("ApprovedBy", sql);
        }

        [Fact]
        public void Script_PlannedQuantityAndRunningTotals_HaveTheirOwnCapConstraints()
        {
            var sql = ScriptText();
            Assert.Contains("CK_PotProductionBatches_PlannedCuttingQuantity", sql);
            Assert.Contains("CK_PotProductionBatches_ConsumedTotal", sql);
            Assert.Contains("CuttingQuantityConsumedTotal <= PlannedCuttingQuantity", sql);
            Assert.Contains("CK_PotProductionBatches_ProducedTotal", sql);
            Assert.Contains("QuantityProducedTotal <= CuttingQuantityConsumedTotal", sql);
        }

        [Fact]
        public void Script_StatusCheck_IsExactlyThreeClosedValues()
        {
            var sql = ScriptText();
            Assert.Contains("CK_PotProductionBatches_Status", sql);
            Assert.Contains("'InProduction'", sql);
            Assert.Contains("'Ready'", sql);
            Assert.Contains("'Cancelled'", sql);
        }

        [Fact]
        public void Script_WidensPotProduction_WithGuardedNullableColumn()
        {
            var sql = ScriptText();
            Assert.Contains("COL_LENGTH('dbo.PotProduction', 'PotProductionBatchId') IS NULL", sql);
            Assert.Contains("ALTER TABLE dbo.PotProduction ADD PotProductionBatchId INT NULL", sql);
        }

        [Fact]
        public void Script_AddsForeignKeyAndIndex_Guarded()
        {
            var sql = ScriptText();
            Assert.Contains("FK_PotProduction_PotProductionBatch", sql);
            Assert.Contains("WHERE name = 'FK_PotProduction_PotProductionBatch'", sql);
            Assert.Contains("IX_PotProduction_PotProductionBatchId", sql);
        }

        // The critical safety check: this phase must never touch any
        // existing PotProduction/CuttingStock/EmptyPotInventory column,
        // constraint, or row -- it only ADDS a new table and one new
        // nullable column/FK/index onto dbo.PotProduction.
        [Fact]
        public void Script_NeverDeletesOrUpdatesExistingRows()
        {
            var sql = ExecutableSql();
            Assert.DoesNotContain("DELETE FROM", sql, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("UPDATE dbo.", sql, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("DROP TABLE", sql, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("DROP COLUMN", sql, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void Script_NeverAltersExistingPotProductionConstraints()
        {
            // Word-boundary match: "PK_PotProduction" is deliberately also
            // a PREFIX of this script's own new "PK_PotProductionBatches"
            // constraint, so a plain substring check would false-positive
            // on the new table itself. \b anchors to the real, separate
            // dbo.PotProduction constraint names only.
            var sql = ExecutableSql();
            foreach (var existingConstraint in new[]
            {
                "PK_PotProduction", "UQ_PotProduction_Code", "FK_PotProduction_PropagationBatch",
                "FK_PotProduction_MotherPlant", "FK_PotProduction_Species", "FK_PotProduction_PotSize",
                "FK_PotProduction_Responsible", "FK_PotProduction_Supervisor",
                "CK_PotProduction_Status", "CK_PotProduction_Quantity"
            })
            {
                Assert.DoesNotMatch($@"\b{System.Text.RegularExpressions.Regex.Escape(existingConstraint)}\b", sql);
            }
        }
    }
}
