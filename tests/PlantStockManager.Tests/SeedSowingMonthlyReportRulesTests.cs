using PlantStockManager.Models;
using PlantStockManager.Services;

namespace PlantStockManager.Tests
{
    // "Monthly Wise Sowing" replacement -- the pure pivot builder
    // (Services/SeedSowingMonthlyReportRules.cs). The repository's own SQL
    // (Data/SeedSowingRepository.GetMonthlyPivotAsync) is what actually
    // excludes Cancelled sowings and enforces Area security; verified
    // separately by manual query against PlantsIMS2_Test (no DB access here).
    public class SeedSowingMonthlyReportRulesTests
    {
        private static SeedSowingMonthlyPivotRow Row(int areaId, string area, int ptId, string pt, int spId, string species, int day, decimal qty)
            => new()
            {
                AreaId = areaId, AreaName = area, PlantTypeId = ptId, PlantTypeName = pt,
                SpeciesId = spId, SpeciesName = species, Day = day, Quantity = qty
            };

        [Fact]
        public void EmptyInput_ProducesNoRows()
        {
            var grid = SeedSowingMonthlyReportRules.BuildPivot(new List<SeedSowingMonthlyPivotRow>(), 30);
            Assert.Empty(grid);
        }

        [Fact]
        public void SingleRow_PlacedAtCorrectDayIndex_RestOfMonthZero()
        {
            var grid = SeedSowingMonthlyReportRules.BuildPivot(new[] { Row(1, "Main Office", 1, "Vegetable", 10, "Tomato", 5, 500) }, 30);
            var g = Assert.Single(grid);
            Assert.Equal(30, g.DailyQuantities.Length);
            Assert.Equal(500, g.DailyQuantities[4]); // Day 5 -> index 4
            Assert.All(Enumerable.Range(0, 30).Where(i => i != 4), i => Assert.Equal(0, g.DailyQuantities[i]));
            Assert.Equal(500, g.MonthTotal);
        }

        [Fact]
        public void MultipleDaysForSameCombo_SumIntoMonthTotal()
        {
            var rows = new[]
            {
                Row(1, "Main Office", 1, "Vegetable", 10, "Tomato", 1, 100),
                Row(1, "Main Office", 1, "Vegetable", 10, "Tomato", 15, 200),
                Row(1, "Main Office", 1, "Vegetable", 10, "Tomato", 31, 50),
            };
            var g = Assert.Single(SeedSowingMonthlyReportRules.BuildPivot(rows, 31));
            Assert.Equal(100, g.DailyQuantities[0]);
            Assert.Equal(200, g.DailyQuantities[14]);
            Assert.Equal(50, g.DailyQuantities[30]);
            Assert.Equal(350, g.MonthTotal);
        }

        [Fact]
        public void MultipleSameDayRowsForSameCombo_AreSummed()
        {
            // e.g. two separate Sowing batches of the same Area/Type/Species
            // on the same day -- the repository's own GROUP BY already sums
            // these in SQL, but the pivot must not silently overwrite either.
            var rows = new[]
            {
                Row(1, "Main Office", 1, "Vegetable", 10, "Tomato", 5, 300),
                Row(1, "Main Office", 1, "Vegetable", 10, "Tomato", 5, 200),
            };
            var g = Assert.Single(SeedSowingMonthlyReportRules.BuildPivot(rows, 30));
            Assert.Equal(500, g.DailyQuantities[4]);
        }

        [Fact]
        public void DifferentAreaPlantTypeOrSpecies_ProduceSeparateRows()
        {
            var rows = new[]
            {
                Row(1, "Main Office", 1, "Vegetable", 10, "Tomato", 1, 100),
                Row(2, "Outlet A", 1, "Vegetable", 10, "Tomato", 1, 200),      // different Area
                Row(1, "Main Office", 2, "Flower", 20, "Marigold", 1, 300),   // different Plant Type + Species
            };
            var grid = SeedSowingMonthlyReportRules.BuildPivot(rows, 30);
            Assert.Equal(3, grid.Count);
        }

        [Fact]
        public void Rows_AreOrderedByArea_ThenPlantType_ThenSpecies()
        {
            var rows = new[]
            {
                Row(2, "Outlet A", 2, "Flower", 20, "Marigold", 1, 1),
                Row(1, "Main Office", 1, "Vegetable", 10, "Tomato", 1, 1),
                Row(1, "Main Office", 1, "Vegetable", 5, "Brinjal", 1, 1),
            };
            var grid = SeedSowingMonthlyReportRules.BuildPivot(rows, 30);
            Assert.Equal(new[] { "Brinjal", "Tomato", "Marigold" }, grid.Select(g => g.SpeciesName));
        }

        // ---- Month length: 28/29/30/31 days handled correctly -----------

        [Theory]
        [InlineData(2027, 2, 28)]  // non-leap February
        [InlineData(2028, 2, 29)]  // leap February
        [InlineData(2026, 4, 30)]  // 30-day month
        [InlineData(2026, 1, 31)]  // 31-day month
        public void DaysInMonth_MatchesCalendar_ArrayIsExactlyThatLength(int year, int month, int expectedDays)
        {
            Assert.Equal(expectedDays, DateTime.DaysInMonth(year, month));
            var grid = SeedSowingMonthlyReportRules.BuildPivot(
                new[] { Row(1, "Main Office", 1, "Vegetable", 10, "Tomato", expectedDays, 42) }, expectedDays);
            var g = Assert.Single(grid);
            Assert.Equal(expectedDays, g.DailyQuantities.Length);
            Assert.Equal(42, g.DailyQuantities[expectedDays - 1]); // last day of month, not overflowed
        }

        [Fact]
        public void ADayOutsideTheGivenMonthLength_IsDroppedNotThrown()
        {
            // Defensive only -- the repository's own DAY(SowingDate) can
            // never actually produce this, since it is always consistent
            // with the same SowingDate's YEAR/MONTH.
            var grid = SeedSowingMonthlyReportRules.BuildPivot(
                new[] { Row(1, "Main Office", 1, "Vegetable", 10, "Tomato", 30, 99) }, 28);
            var g = Assert.Single(grid);
            Assert.Equal(28, g.DailyQuantities.Length);
            Assert.Equal(0, g.MonthTotal);
        }

        [Theory]
        [InlineData(27)]
        [InlineData(32)]
        public void InvalidDaysInMonth_Throws(int badDaysInMonth)
            => Assert.Throws<ArgumentOutOfRangeException>(() => SeedSowingMonthlyReportRules.BuildPivot(new List<SeedSowingMonthlyPivotRow>(), badDaysInMonth));
    }
}
