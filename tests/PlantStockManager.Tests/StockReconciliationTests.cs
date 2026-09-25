using System.IO;
using System.Linq;
using PlantStockManager.Models;

namespace PlantStockManager.Tests
{
    // Phase 8 (Stock History and Wastage Integration): "Opening + IN -
    // OUT - Wastage = Closing" for every stock pool. Opening is always 0
    // (StockReconciliationRow.LedgerComputedClosing), so these tests
    // cover (a) the pure arithmetic, and (b) -- since the actual
    // reconciliation SQL runs against a live database this sandbox
    // cannot reach -- that Data/StockLedgerRepository.cs's own SQL text
    // correctly excludes the two documented exceptions (PottedPlantStock's
    // Reservation/ReservationRelease track ReservedQuantity, not
    // PhysicalQuantity; ReadyStock's Closing column only moves via
    // Confirmed/ReversalRemoval, not Dispatch) so a regression that
    // silently widens either filter is caught here rather than only in
    // production data.
    public class StockReconciliationTests
    {
        private static string RepoRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "PlantStockManager.csproj")))
                dir = dir.Parent;
            return dir?.FullName ?? throw new InvalidOperationException("PlantStockManager.csproj not found above the test output folder.");
        }

        private static string RepositoryText()
            => File.ReadAllText(Path.Combine(RepoRoot(), "Data", "StockLedgerRepository.cs"));

        [Fact]
        public void Balanced_WhenActualClosingMatchesLedger()
        {
            var row = new StockReconciliationRow { TotalIn = 1300, TotalOut = -300, TotalWastage = -50, ActualClosing = 950 };
            Assert.Equal(950m, row.LedgerComputedClosing);
            Assert.True(row.IsBalanced);
            Assert.Equal(0m, row.Discrepancy);
        }

        [Fact]
        public void Unbalanced_WhenActualClosingDrifts()
        {
            var row = new StockReconciliationRow { TotalIn = 1300, TotalOut = -300, TotalWastage = -50, ActualClosing = 900 };
            Assert.Equal(950m, row.LedgerComputedClosing);
            Assert.False(row.IsBalanced);
            Assert.Equal(-50m, row.Discrepancy);
        }

        [Fact]
        public void DayByDayExample_ReconcilesToTheWorkedTotal()
        {
            // The business's own worked example reused across this
            // engagement: Day 1 = 600, Day 2 = 500, Day 3 = 200 -> 1,300.
            var row = new StockReconciliationRow { TotalIn = 600 + 500 + 200, TotalOut = 0, TotalWastage = 0, ActualClosing = 1300 };
            Assert.True(row.IsBalanced);
        }

        [Fact]
        public void Repository_ExcludesReservationTypes_FromPottedPlantReconciliation()
        {
            var sql = RepositoryText();
            // The PottedPlant reconciliation block must explicitly exclude
            // both Reservation types from both its IN and OUT sums.
            Assert.Contains("t.TransactionType NOT IN ('Reservation', 'ReservationRelease') AND t.Quantity > 0", sql);
            Assert.Contains("t.TransactionType NOT IN ('Reservation', 'ReservationRelease') AND t.Quantity < 0", sql);
        }

        [Fact]
        public void Repository_RestrictsReadyStockReconciliation_ToConfirmedAndReversalRemovalOnly()
        {
            var sql = RepositoryText();
            Assert.Contains("t.TransactionType IN ('Confirmed', 'ReversalRemoval') AND t.Quantity > 0", sql);
            Assert.Contains("t.TransactionType IN ('Confirmed', 'ReversalRemoval') AND t.Quantity < 0", sql);
            // 'Dispatch' must NOT appear in the ReadyStock reconciliation
            // sums -- it moves DispatchedQuantity, a separate column, not
            // the Quantity column this check reconciles against.
            var readyStockBlock = sql.Substring(sql.IndexOf("'ReadyStock', s.Id"));
            Assert.DoesNotContain("'Dispatch'", readyStockBlock.Split("ORDER BY")[0]);
        }

        [Fact]
        public void Repository_SeparatesWastage_ForPottedPlantOnly()
        {
            var sql = RepositoryText();
            Assert.Contains("TransactionType = 'Wastage' THEN t.Quantity ELSE 0", sql);
        }
    }
}
