namespace PlantStockManager.Models
{
    // Phase H (2026-09-30): the "Monthly Wise Sowing" replacement -- one flat
    // (Area, Plant Type, Species, Day) grouped row from
    // SeedSowingRepository.GetMonthlyPivotAsync. The grid itself (Day 1..N +
    // Month Total) is built from a list of these by the pure, unit-testable
    // Services/SeedSowingMonthlyReportRules.BuildPivot.
    public class SeedSowingMonthlyPivotRow
    {
        public int AreaId { get; set; }
        public string AreaName { get; set; } = string.Empty;
        public int PlantTypeId { get; set; }
        public string PlantTypeName { get; set; } = string.Empty;
        public int SpeciesId { get; set; }
        public string SpeciesName { get; set; } = string.Empty;
        public string? SpeciesColor { get; set; }
        public int Day { get; set; }
        public decimal Quantity { get; set; }
    }

    // One display row of the pivoted grid: one Area/Plant Type/Species combo,
    // its Quantity Sown per day of the selected month, and the Month Total.
    public class SeedSowingMonthlyPivotGridRow
    {
        public int AreaId { get; set; }
        public string AreaName { get; set; } = string.Empty;
        public int PlantTypeId { get; set; }
        public string PlantTypeName { get; set; } = string.Empty;
        public int SpeciesId { get; set; }
        public string SpeciesName { get; set; } = string.Empty;
        public string? SpeciesColor { get; set; }

        // Index 0 = Day 1. Length = the selected month's actual day count
        // (28/29/30/31), never hardcoded to 31.
        public decimal[] DailyQuantities { get; set; } = System.Array.Empty<decimal>();
        public decimal MonthTotal { get; set; }
    }
}
