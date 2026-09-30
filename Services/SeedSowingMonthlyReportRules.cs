using PlantStockManager.Models;

namespace PlantStockManager.Services
{
    // Phase H (2026-09-30): the "Monthly Wise Sowing" replacement -- pure
    // (database-free) pivot builder, so the day-grid math is unit-testable
    // without a DB, same reasoning as DirectSowingRules. The repository's
    // GetMonthlyPivotAsync does the grouping/filtering/Area-security in SQL
    // and hands back flat (Area, Plant Type, Species, Day) rows; this class
    // only reshapes those into one row per Area/Plant Type/Species with a
    // Day 1..N array and a Month Total. Cancelled Sowings are never in the
    // input (the repository's own WHERE Status <> 'Cancelled' excludes them
    // before this ever runs), so this function has no Status concept at all.
    public static class SeedSowingMonthlyReportRules
    {
        public static List<SeedSowingMonthlyPivotGridRow> BuildPivot(
            IEnumerable<SeedSowingMonthlyPivotRow> flatRows, int daysInMonth)
        {
            if (daysInMonth < 28 || daysInMonth > 31)
                throw new ArgumentOutOfRangeException(nameof(daysInMonth), "A calendar month has 28 to 31 days.");

            var grid = new List<SeedSowingMonthlyPivotGridRow>();
            var byKey = flatRows
                .GroupBy(r => (r.AreaId, r.PlantTypeId, r.SpeciesId))
                .OrderBy(g => g.First().AreaName, StringComparer.Ordinal)
                .ThenBy(g => g.First().PlantTypeName, StringComparer.Ordinal)
                .ThenBy(g => g.First().SpeciesName, StringComparer.Ordinal);

            foreach (var group in byKey)
            {
                var first = group.First();
                var daily = new decimal[daysInMonth];
                foreach (var row in group)
                {
                    // Defensive: a Day outside the actual month length (e.g. a
                    // caller passing the wrong daysInMonth) is dropped rather
                    // than throwing or silently wrapping into another day.
                    if (row.Day >= 1 && row.Day <= daysInMonth)
                        daily[row.Day - 1] += row.Quantity;
                }
                grid.Add(new SeedSowingMonthlyPivotGridRow
                {
                    AreaId = first.AreaId,
                    AreaName = first.AreaName,
                    PlantTypeId = first.PlantTypeId,
                    PlantTypeName = first.PlantTypeName,
                    SpeciesId = first.SpeciesId,
                    SpeciesName = first.SpeciesName,
                    SpeciesColor = first.SpeciesColor,
                    DailyQuantities = daily,
                    MonthTotal = daily.Sum()
                });
            }
            return grid;
        }
    }
}
