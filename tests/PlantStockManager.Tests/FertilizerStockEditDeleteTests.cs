using PlantStockManager.Services;

namespace PlantStockManager.Tests
{
    // CORRECTION I3 -- Fertilizer Stock edit/delete hole. Pure rules and wiring here; the real database behaviour
    // (row lock, race, delete dependency check) is FertilizerStockEditDeleteE2ETests, which runs against a scratch
    // copy of the database.
    public class FertilizerStockEditDeleteTests
    {
        // ---- EditAllowed -------------------------------------------------------------------------------------

        [Fact]
        public void EditAllowed_WhenNothingHasBeenIssued()
            => Assert.True(FertilizerStockRules.EditAllowed(quantity: 10, latestAvailableQuantity: 10));

        [Theory]
        [InlineData(10, 9)]
        [InlineData(10, 0)]
        [InlineData(10, 0.001)]
        public void EditNotAllowed_OnceAnythingHasBeenIssued(double quantity, double available)
            => Assert.False(FertilizerStockRules.EditAllowed((decimal)quantity, (decimal)available));

        [Fact]
        public void EditAllowed_IsExactEquality_NotJustClose()
            => Assert.False(FertilizerStockRules.EditAllowed(10.000m, 9.999m));

        // ---- input validation ----------------------------------------------------------------------------------

        [Theory]
        [InlineData(0)] [InlineData(-1)] [InlineData(-0.001)]
        public void Quantity_MustBeGreaterThanZero(double quantity)
            => Assert.NotNull(FertilizerStockRules.ValidateQuantity((decimal)quantity));

        [Theory]
        [InlineData(0.001)] [InlineData(1)] [InlineData(100000)]
        public void Quantity_AboveZero_IsAccepted(double quantity)
            => Assert.Null(FertilizerStockRules.ValidateQuantity((decimal)quantity));

        [Fact]
        public void PurchaseDate_Default_IsRejected()
            => Assert.NotNull(FertilizerStockRules.ValidateDate(default));

        [Fact]
        public void PurchaseDate_ARealDate_IsAccepted()
            => Assert.Null(FertilizerStockRules.ValidateDate(new DateTime(2026, 1, 1)));

        // ---- DeletionRules wiring -------------------------------------------------------------------------------

        [Fact]
        public void FertilizerStockDependencies_IsExactlyFertilizerUsageOnStockId()
        {
            var dep = Assert.Single(DeletionRules.FertilizerStockDependencies);
            Assert.Equal(("FertilizerUsage", "StockId"), (dep.Table, dep.Column));
        }

        [Fact]
        public void FertilizerStockTable_IsTheQualifiedTableName()
            => Assert.Equal("dbo.FertilizerStock", DeletionRules.FertilizerStockTable);

        [Fact]
        public void CanDelete_TrueOnlyWhenTheDependencyCountIsZero()
        {
            Assert.True(DeletionRules.CanDelete(new[] { new DependencyCount("Fertilizer Usage", "FertilizerUsage", "StockId", 0) }));
            Assert.False(DeletionRules.CanDelete(new[] { new DependencyCount("Fertilizer Usage", "FertilizerUsage", "StockId", 1) }));
        }

        // ---- wiring (source scans): the page no longer writes SQL itself ----------------------------------------

        private static string Repo(params string[] parts)
        {
            var dir = AppContext.BaseDirectory;
            while (dir != null && !Directory.GetFiles(dir, "*.sln").Any()) dir = Path.GetDirectoryName(dir);
            Assert.NotNull(dir);
            return File.ReadAllText(Path.Combine(new[] { dir! }.Concat(parts).ToArray()));
        }

        [Fact]
        public void ThePage_HasNoRawSql_AndCallsTheRepository()
        {
            var page = Repo("Pages", "Fertilizer", "Stock.cshtml.cs");
            Assert.DoesNotContain("SqlCommand", page);
            Assert.DoesNotContain("INSERT INTO", page, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("UPDATE FertilizerStock", page, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("DELETE FROM", page, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("_repo.InsertAsync(Stock)", page);
            Assert.Contains("_repo.UpdateAsync(Stock)", page);
            Assert.Contains("_repo.DeleteAsync(id)", page);
        }

        [Fact]
        public void TheRepository_LocksTheRowBeforeCheckingOrWriting()
        {
            var repo = Repo("Data", "FertilizerStockRepository.cs");
            // Update: the lock is taken, then EditAllowed is checked, before any UPDATE is issued.
            var updateStart = repo.IndexOf("public async Task<FertilizerStockSaveResult> UpdateAsync", StringComparison.Ordinal);
            var updateBody = repo.Substring(updateStart);
            var lockIdx = updateBody.IndexOf("WITH (UPDLOCK, HOLDLOCK) WHERE StockId = @Id", StringComparison.Ordinal);
            var editAllowedIdx = updateBody.IndexOf("FertilizerStockRules.EditAllowed(", StringComparison.Ordinal);
            var updateSqlIdx = updateBody.IndexOf("UPDATE dbo.FertilizerStock", StringComparison.Ordinal);
            Assert.True(lockIdx >= 0 && lockIdx < editAllowedIdx && editAllowedIdx < updateSqlIdx);

            // Delete: the lock, then the dependency check, before any DELETE is issued.
            var deleteStart = repo.IndexOf("public async Task<DeleteResult> DeleteAsync", StringComparison.Ordinal);
            var deleteBody = repo.Substring(deleteStart);
            var dLockIdx = deleteBody.IndexOf("WITH (UPDLOCK, HOLDLOCK) WHERE StockId = @Id", StringComparison.Ordinal);
            var depIdx = deleteBody.IndexOf("DependencyChecker.CountAsync(", StringComparison.Ordinal);
            var deleteSqlIdx = deleteBody.IndexOf("DELETE FROM dbo.FertilizerStock", StringComparison.Ordinal);
            Assert.True(dLockIdx >= 0 && dLockIdx < depIdx && depIdx < deleteSqlIdx);
        }

        [Fact]
        public void TheRepository_KeepsLatestAvailableQuantityEqualToQuantityOnUpdate()
        {
            var repo = Repo("Data", "FertilizerStockRepository.cs");
            var updateStart = repo.IndexOf("public async Task<FertilizerStockSaveResult> UpdateAsync", StringComparison.Ordinal);
            var updateSql = repo.Substring(repo.IndexOf("UPDATE dbo.FertilizerStock", updateStart, StringComparison.Ordinal));
            Assert.Contains("Quantity = @pq, LatestAvailableQuantity = @pq", updateSql);
        }

        [Fact]
        public void InsertAndUpdate_ValidateForeignKeys_BeforeWriting()
        {
            var repo = Repo("Data", "FertilizerStockRepository.cs");
            Assert.Contains("ValidateForeignKeysAsync(conn, tx, stock.FertilizerId, stock.UnitId, stock.SourceId)", repo);
            // both callers check the error before the INSERT / UPDATE statement runs
            Assert.True(repo.IndexOf("fkError = await ValidateForeignKeysAsync", StringComparison.Ordinal)
                < repo.IndexOf("INSERT INTO dbo.FertilizerStock", StringComparison.Ordinal));
        }

        [Fact]
        public void Delete_UsesDeletionRulesMessage_NotAnAdHocOne()
        {
            var repo = Repo("Data", "FertilizerStockRepository.cs");
            Assert.Contains("DeletionRules.BlockedMessage(\"Fertilizer Stock batch\", dependencies)", repo);
            Assert.Contains("DeletionRules.DeletedMessage(\"Fertilizer Stock batch\"", repo);
        }

        [Fact]
        public void PagePermission_IsUnchanged()
        {
            var rule = PlantStockManager.Authorization.FeatureAuthorizationConventions.GetRule("/Fertilizer/Stock");
            Assert.Equal("Fertilizer.View", rule.Read);
            Assert.Equal("Fertilizer.Enter", rule.Write);
        }
    }
}
