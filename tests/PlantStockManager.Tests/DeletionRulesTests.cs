using System.Text.RegularExpressions;
using PlantStockManager.Services;

namespace PlantStockManager.Tests
{
    // Dependency-aware Delete: the decision rules and the relationship lists.
    public class DeletionRulesTests
    {
        private static DependencyCount C(string label, int count) => new(label, label.Replace(" ", ""), "AreaId", count);

        [Fact]
        public void CanDelete_WhenNothingUsesTheRecord()
        {
            var deps = new[] { C("Mother Plants", 0), C("Seed Sowings", 0), C("Ready Stock", 0) };
            Assert.True(DeletionRules.CanDelete(deps));
            Assert.Empty(DeletionRules.Blocking(deps));
        }

        [Theory]
        [InlineData("Seed Sowings")]
        [InlineData("Mother Plants")]
        [InlineData("Ready Stock")]
        [InlineData("Cutting Productions")]
        [InlineData("Cutting Plans")]
        [InlineData("Actual Cuttings")]
        public void CannotDelete_WhenAnyRelationshipHasRecords(string blockingLabel)
        {
            var deps = new[] { C("Polyhouses", 0), C(blockingLabel, 1), C("User Role assignments", 0) };
            Assert.False(DeletionRules.CanDelete(deps));
            var blocking = Assert.Single(DeletionRules.Blocking(deps));
            Assert.Equal(blockingLabel, blocking.Label);
        }

        [Fact]
        public void BlockedMessage_NamesEveryBlockingRelationshipAndTheWayForward()
        {
            var deps = new[] { C("Mother Plants", 1), C("Seed Sowings", 1), C("Polyhouses", 0), C("Ready Stock", 1) };
            var message = DeletionRules.BlockedMessage("Area", deps);

            Assert.StartsWith("This Area cannot be deleted because it is being used by existing records", message);
            Assert.Contains("Mother Plants: 1", message);
            Assert.Contains("Seed Sowings: 1", message);
            Assert.Contains("Ready Stock: 1", message);
            Assert.DoesNotContain("Polyhouses", message);
            Assert.Contains("Remove/archive the dependent records first, then delete this Area", message);
            Assert.Contains("deactivate", message);
        }

        [Fact]
        public void BlockedMessage_StillExplainsWhenOnlyTheDatabaseRefused()
        {
            var message = DeletionRules.BlockedMessage("Mother Plant batch", Array.Empty<DependencyCount>());
            Assert.Contains("a related record in the database", message);
        }

        [Fact]
        public void ForDisplay_ListsBlockingRelationshipsFirst()
        {
            var deps = new[] { C("Polyhouses", 0), C("Seed Sowings", 2), C("User Role assignments", 0), C("Mother Plants", 1) };
            var ordered = DeletionRules.ForDisplay(deps).Select(d => d.Label).ToList();
            Assert.Equal(new[] { "Seed Sowings", "Mother Plants", "Polyhouses", "User Role assignments" }, ordered);
        }

        [Theory]
        [InlineData("SeedSowings", "AreaId")]
        [InlineData("MotherPlants", "AreaId")]
        [InlineData("ReadyStock", "AreaId")]
        [InlineData("Polyhouses", "AreaId")]
        [InlineData("UserRoles", "AreaId")]
        [InlineData("InternalTransfers", "SourceAreaId")]
        [InlineData("InternalTransfers", "DestinationAreaId")]
        [InlineData("OutletWastages", "OutletAreaId")]
        public void AreaDependencies_IncludeTheKnownRelationships(string table, string column)
            => Assert.Contains(DeletionRules.AreaDependencies, d => d.Table == table && d.Column == column);

        [Fact]
        public void MotherPlantDependencies_AreExactlyTheSixForeignKeys()
        {
            var expected = new[] { "PotProduction", "CuttingPlans", "ActualCuttings", "CuttingDeliveries", "PropagationBatches", "CuttingProductions" };
            Assert.Equal(expected.OrderBy(x => x), DeletionRules.MotherPlantDependencies.Select(d => d.Table).OrderBy(x => x));
            Assert.All(DeletionRules.MotherPlantDependencies, d => Assert.Equal("MotherPlantId", d.Column));
        }

        // Guard: every foreign key to dbo.Area / dbo.MotherPlants declared in
        // the migration scripts is in the known list. (Keys that exist only in
        // a live database are still found at run time via sys.foreign_keys.)
        [Theory]
        [InlineData("Area")]
        [InlineData("MotherPlants")]
        public void EveryForeignKeyInTheMigrationScripts_IsAKnownDependency(string parent)
        {
            var known = parent == "Area" ? DeletionRules.AreaDependencies : DeletionRules.MotherPlantDependencies;
            var found = ForeignKeysInScripts(parent);

            Assert.NotEmpty(found);
            var missing = found.Where(f => !known.Any(k => k.Table.Equals(f.Table, StringComparison.OrdinalIgnoreCase)
                                                           && k.Column.Equals(f.Column, StringComparison.OrdinalIgnoreCase)))
                               .Select(f => $"{f.Table}.{f.Column}")
                               .ToList();
            Assert.True(missing.Count == 0, "Not in the dependency list: " + string.Join(", ", missing));
        }

        [Fact]
        public void Merge_AddsForeignKeysFoundInTheDatabase_WithoutDuplicates()
        {
            var merged = DeletionRules.Merge(DeletionRules.AreaDependencies, new[]
            {
                ("dbo", "SeedSowings", "AreaId"),          // already known
                ("dbo", "SomeNewTable", "AreaId"),         // new
            });
            Assert.Equal(DeletionRules.AreaDependencies.Count + 1, merged.Count);
            Assert.Contains(merged, d => d.Table == "SomeNewTable" && d.Label == "SomeNewTable (AreaId)");
        }

        [Theory]
        [InlineData("SeedSowings", true)]
        [InlineData("Outlet_Wastages2", true)]
        [InlineData("", false)]
        [InlineData(null, false)]
        [InlineData("Area]; DROP TABLE x;--", false)]
        [InlineData("Area Id", false)]
        [InlineData("dbo.Area", false)]
        public void OnlyPlainIdentifiers_AreEverPutInSql(string? name, bool safe)
            => Assert.Equal(safe, DeletionRules.IsSafeIdentifier(name));

        // ---- Area deactivation: current stock only --------------------------

        [Theory]
        [InlineData("SeedStock")]
        [InlineData("SeedSowings")]
        [InlineData("ReadyStock")]
        [InlineData("CuttingStock")]
        [InlineData("PottedPlantStock")]
        [InlineData("EmptyPotInventory")]
        [InlineData("PotProductionBatches")]
        [InlineData("InternalTransfers")]
        public void AreaStockChecks_CoverEveryCurrentStockTable(string table)
            => Assert.Contains(DeletionRules.AreaStockChecks, c => c.Table == table);

        [Fact]
        public void AreaStockChecks_AreSafeAndScopedToTheArea()
        {
            Assert.All(DeletionRules.AreaStockChecks, c =>
            {
                Assert.True(DeletionRules.IsSafeIdentifier(c.Table));
                Assert.All(c.Columns, col => Assert.True(DeletionRules.IsSafeIdentifier(col)));
                Assert.Contains("@Id", c.Condition);
                // every stock table is also a delete dependency
                Assert.Contains(DeletionRules.AreaDependencies, d => d.Table == c.Table);
            });
        }

        [Fact]
        public void AreaStockChecks_LookAtCurrentQuantitiesAndOpenWorkOnly()
        {
            string Cond(string table) => DeletionRules.AreaStockChecks.Single(c => c.Table == table).Condition;
            Assert.Contains("PhysicalQuantity > 0", Cond("SeedStock"));
            Assert.Contains("Status = N'Sown'", Cond("SeedSowings"));
            Assert.Contains("Quantity - DispatchedQuantity > 0", Cond("ReadyStock"));
            Assert.Contains("PhysicalQuantity > 0", Cond("CuttingStock"));
            Assert.Contains("PhysicalQuantity > 0", Cond("PottedPlantStock"));
            Assert.Contains("PhysicalQuantity > 0", Cond("EmptyPotInventory"));
            Assert.Contains("Status = N'InProduction'", Cond("PotProductionBatches"));
            Assert.Contains("Status = N'PendingConfirmation'", Cond("InternalTransfers"));
        }

        [Fact]
        public void Area_CanBeDeactivated_OnlyWithoutCurrentStock()
        {
            Assert.True(DeletionRules.CanDeactivateArea(new[] { C("Seed Stock", 0), C("Ready Stock", 0) }));
            Assert.True(DeletionRules.CanDeactivateArea(Array.Empty<DependencyCount>()));
            Assert.False(DeletionRules.CanDeactivateArea(new[] { C("Seed Stock", 0), C("Potted Plant Stock", 2) }));
        }

        [Fact]
        public void AreaDeactivationBlockedMessage_IsClearAndListsTheStock()
        {
            var message = DeletionRules.AreaDeactivationBlockedMessage(new[] { C("Potted Plant Stock", 2), C("Seed Stock", 0), C("Ready Stock", 1) });
            Assert.Equal(
                "This Area cannot be deactivated because it currently contains stock (Potted Plant Stock: 2, Ready Stock: 1). " +
                "Move or clear the stock before deactivating this Area.",
                message);
        }

        [Fact]
        public void MotherPlant_DeactivatedStatus_IsTheExistingRemovedStatus()
            => Assert.Equal("Removed", DeletionRules.MotherPlantDeactivatedStatus);

        [Theory]
        [InlineData("Active", true)]
        [InlineData("Completed", true)]
        [InlineData("Removed", false)]
        [InlineData(" removed ", false)]
        public void MotherPlant_CanBeDeactivatedUnlessAlreadyRemoved(string status, bool expected)
            => Assert.Equal(expected, DeletionRules.CanDeactivateMotherPlant(status));

        private static List<(string Table, string Column)> ForeignKeysInScripts(string parent)
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "PlantStockManager.csproj")))
                dir = dir.Parent;
            Assert.NotNull(dir);

            var tableLine = new Regex(@"\b(?:CREATE|ALTER)\s+TABLE\s+(?:dbo\.)?(\w+)", RegexOptions.IgnoreCase);
            var fkLine = new Regex(@"FOREIGN\s+KEY\s*\(([^)]*)\)\s*REFERENCES\s+(?:dbo\.)?" + parent + @"\s*\(([^)]*)\)", RegexOptions.IgnoreCase);
            var result = new List<(string, string)>();

            foreach (var file in Directory.GetFiles(Path.Combine(dir!.FullName, "Database"), "*.sql"))
            {
                string? current = null;
                foreach (var line in File.ReadLines(file))
                {
                    var t = tableLine.Match(line);
                    if (t.Success)
                        current = t.Groups[1].Value;
                    var fk = fkLine.Match(line);
                    if (!fk.Success || current == null || current.Equals(parent, StringComparison.OrdinalIgnoreCase))
                        continue;
                    var cols = fk.Groups[1].Value.Split(',').Select(c => c.Trim()).ToList();
                    var refs = fk.Groups[2].Value.Split(',').Select(c => c.Trim()).ToList();
                    var idIndex = refs.FindIndex(r => r.Equals("Id", StringComparison.OrdinalIgnoreCase));
                    if (idIndex >= 0 && idIndex < cols.Count)
                        result.Add((current, cols[idIndex]));
                }
            }
            return result.Distinct().ToList();
        }
    }
}
