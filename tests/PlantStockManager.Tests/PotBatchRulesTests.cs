using PlantStockManager.Services;

namespace PlantStockManager.Tests
{
    // Pot production batch: cuttings allocated -> daily production (using
    // the Area's own empty pots) -> READY by the assigned supervisor.
    public class PotBatchRulesTests
    {
        private static readonly DateTime Start = new(2026, 9, 1);
        private const int Creator = 22, Supervisor = 40, OtherSupervisor = 41;
        private static readonly int[] AreaSupervisors = { Creator, Supervisor, OtherSupervisor };

        // ---- starting a batch ------------------------------------------

        [Fact]
        public void Create_Valid() => Assert.True(PotBatchRules.ValidateCreate(8000, 10000, Start, Start.AddDays(60), Supervisor, Creator, AreaSupervisors).Ok);

        [Theory]
        [InlineData(0)]
        [InlineData(-1)]
        [InlineData(100.5)]
        public void Create_InvalidAllocation_IsRefused(decimal allocated)
            => Assert.False(PotBatchRules.ValidateCreate(allocated, 10000, Start, Start.AddDays(60), Supervisor, Creator, AreaSupervisors).Ok);

        [Fact]
        public void Create_MoreThanCuttingStock_IsRefused()
            => Assert.False(PotBatchRules.ValidateCreate(10001, 10000, Start, Start.AddDays(60), Supervisor, Creator, AreaSupervisors).Ok);

        [Fact]
        public void Create_SelfAsSupervisor_IsRefused()
        {
            var (ok, error) = PotBatchRules.ValidateCreate(8000, 10000, Start, Start.AddDays(60), Creator, Creator, AreaSupervisors);
            Assert.False(ok);
            Assert.Contains("another supervisor", error);
        }

        [Fact]
        public void Create_SupervisorFromAnotherArea_IsRefused()
            => Assert.False(PotBatchRules.ValidateCreate(8000, 10000, Start, Start.AddDays(60), 99, Creator, AreaSupervisors).Ok);

        [Fact]
        public void Create_ReadyDateBeforeStart_IsRefused()
            => Assert.False(PotBatchRules.ValidateCreate(8000, 10000, Start, Start.AddDays(-1), Supervisor, Creator, AreaSupervisors).Ok);

        // ---- daily production: 600 + 500 + 200 = 1,300 ----------------

        [Fact]
        public void DailyProduction_AddsUpToOneBatchTotal()
        {
            decimal produced = 0, pots = 5000;
            foreach (var day in new[] { 600m, 500m, 200m })
            {
                var (ok, error) = PotBatchRules.ValidateEntry(PotBatchRules.InProduction, day, produced, 8000, pots, Start.AddDays(1), Start, Start.AddDays(5));
                Assert.True(ok, error);
                produced += day;
                pots -= day;
            }
            Assert.Equal(1300, produced);
        }

        [Fact]
        public void DailyProduction_AboveAllocatedCuttings_IsRefused()
        {
            var (ok, error) = PotBatchRules.ValidateEntry(PotBatchRules.InProduction, 101, 7900, 8000, 5000, Start, Start, Start);
            Assert.False(ok);
            Assert.Contains("Only 100 cuttings are left", error);
        }

        [Fact]
        public void DailyProduction_AboveAreaEmptyPots_IsRefused()
        {
            var (ok, error) = PotBatchRules.ValidateEntry(PotBatchRules.InProduction, 600, 0, 8000, 500, Start, Start, Start);
            Assert.False(ok);
            Assert.Contains("empty pots", error);
        }

        [Theory]
        [InlineData(0)]
        [InlineData(-5)]
        [InlineData(10.5)]
        public void DailyProduction_InvalidQuantity_IsRefused(decimal q)
            => Assert.False(PotBatchRules.ValidateEntry(PotBatchRules.InProduction, q, 0, 8000, 5000, Start, Start, Start).Ok);

        [Fact]
        public void DailyProduction_FutureOrBeforeStart_IsRefused()
        {
            Assert.False(PotBatchRules.ValidateEntry(PotBatchRules.InProduction, 10, 0, 8000, 5000, Start.AddDays(3), Start, Start.AddDays(2)).Ok);
            Assert.False(PotBatchRules.ValidateEntry(PotBatchRules.InProduction, 10, 0, 8000, 5000, Start.AddDays(-1), Start, Start.AddDays(2)).Ok);
        }

        [Theory]
        [InlineData(PotBatchRules.Ready)]
        [InlineData(PotBatchRules.Cancelled)]
        public void DailyProduction_OnClosedBatch_IsRefused(string status)
            => Assert.False(PotBatchRules.ValidateEntry(status, 10, 0, 8000, 5000, Start, Start, Start).Ok);

        // ---- READY ----------------------------------------------------

        [Fact]
        public void Ready_AllPotsReady_UnusedCuttingsNeedAnAction()
        {
            // 8000 allocated, 1300 potted, 1300 ready.
            Assert.False(PotBatchRules.ValidateReady(PotBatchRules.InProduction, 1300, 1300, 8000, null, null).Ok);
            var (ok, wastage, unused, _) = PotBatchRules.ValidateReady(PotBatchRules.InProduction, 1300, 1300, 8000, null, PotBatchRules.ReturnedToStock);
            Assert.True(ok);
            Assert.Equal(0, wastage);
            Assert.Equal(6700, unused);
        }

        [Fact]
        public void Ready_LostPots_AreWastage_WithReason()
        {
            Assert.False(PotBatchRules.ValidateReady(PotBatchRules.InProduction, 1250, 1300, 1300, null, null).Ok);
            var (ok, wastage, unused, _) = PotBatchRules.ValidateReady(PotBatchRules.InProduction, 1250, 1300, 1300, "Disease", null);
            Assert.True(ok);
            Assert.Equal(50, wastage);
            Assert.Equal(0, unused);
        }

        [Theory]
        [InlineData(1301)]   // more than produced
        [InlineData(-1)]
        [InlineData(100.5)]
        public void Ready_InvalidQuantity_IsRefused(decimal ready)
            => Assert.False(PotBatchRules.ValidateReady(PotBatchRules.InProduction, ready, 1300, 1300, "Disease", null).Ok);

        // ---- complete loss ------------------------------------------------

        [Fact]
        public void CompleteLoss_ZeroReady_NeedsAReason()
        {
            var (ok, _, _, error) = PotBatchRules.ValidateReady(PotBatchRules.InProduction, 0, 1300, 1300, null, null);
            Assert.False(ok);
            Assert.Contains("complete loss", error);
        }

        [Fact]
        public void CompleteLoss_ZeroReady_WithReason_ClosesAsLost_AllProducedIsWastage()
        {
            var (ok, wastage, unused, _) = PotBatchRules.ValidateReady(PotBatchRules.InProduction, 0, 1300, 1300, "Disease", null);
            Assert.True(ok);
            Assert.Equal(1300, wastage);
            Assert.Equal(0, unused);
            Assert.Equal(PotBatchRules.Lost, PotBatchRules.StatusFor(0));
            Assert.Equal(PotBatchRules.Ready, PotBatchRules.StatusFor(1));
        }

        [Fact]
        public void CompleteLoss_UnusedCuttings_StillNeedAnAction()
        {
            Assert.False(PotBatchRules.ValidateReady(PotBatchRules.InProduction, 0, 500, 800, "Disease", null).Ok);
            var (ok, wastage, unused, _) = PotBatchRules.ValidateReady(PotBatchRules.InProduction, 0, 500, 800, "Disease", PotBatchRules.UnusedAsWastage);
            Assert.True(ok);
            Assert.Equal(500, wastage);
            Assert.Equal(300, unused);
        }

        [Fact]
        public void CompleteLoss_BeforeAnyPotting_CuttingsDied()
        {
            // nothing potted, every cutting lost: closed as Lost, cuttings recorded as wastage
            var (ok, wastage, unused, _) = PotBatchRules.ValidateReady(PotBatchRules.InProduction, 0, 0, 800, "Disease", PotBatchRules.UnusedAsWastage);
            Assert.True(ok);
            Assert.Equal(0, wastage);
            Assert.Equal(800, unused);
        }

        [Fact]
        public void LostBatch_IsClosed()
        {
            Assert.True(PotBatchRules.IsClosed(PotBatchRules.Lost));
            Assert.False(PotBatchRules.ValidateReady(PotBatchRules.Lost, 0, 10, 10, "Disease", null).Ok);
            Assert.Equal("Complete loss", PotBatchRules.ReadinessStatus(PotBatchRules.Lost, Start, Start));
        }

        [Fact]
        public void OneCuttingMakesOnePot_ByDefault() => Assert.Equal(1, PotBatchRules.CuttingsPerPot);

        [Fact]
        public void Ready_Twice_IsRefused()
            => Assert.False(PotBatchRules.ValidateReady(PotBatchRules.Ready, 1300, 1300, 1300, null, null).Ok);

        [Fact]
        public void Ready_WithoutProduction_IsRefused()
            => Assert.False(PotBatchRules.ValidateReady(PotBatchRules.InProduction, 1, 0, 8000, null, PotBatchRules.ReturnedToStock).Ok);

        // ---- expected ready date alerts --------------------------------

        [Theory]
        [InlineData(30, PotBatchRules.DueUpcoming)]
        [InlineData(7, PotBatchRules.DueSoon)]
        [InlineData(1, PotBatchRules.DueSoon)]
        [InlineData(0, PotBatchRules.DueToday)]
        [InlineData(-1, PotBatchRules.DueOverdue)]
        public void Readiness_FollowsTheExpectedReadyDate(int daysAhead, string expected)
            => Assert.Equal(expected, PotBatchRules.ReadinessStatus(PotBatchRules.InProduction, Start.AddDays(daysAhead), Start));

        [Fact]
        public void Readiness_ReadyAndCancelled()
        {
            Assert.Equal("Ready", PotBatchRules.ReadinessStatus(PotBatchRules.Ready, Start.AddDays(-10), Start));
            Assert.Equal("Cancelled", PotBatchRules.ReadinessStatus(PotBatchRules.Cancelled, Start, Start));
        }
    }
}
