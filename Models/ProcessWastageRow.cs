namespace PlantStockManager.Models
{
    // Phase 8: one Seed Sowing or Cutting Sowing batch with recorded
    // wastage -- read directly from SeedSowings.WastageQuantity /
    // CuttingSowings.WastageQuantity (already-existing business columns,
    // approved at Ready Confirmation time), never a new ledger row.
    public class ProcessWastageRow
    {
        public string SourceType { get; set; } = string.Empty; // 'SeedSowing' | 'CuttingSowing'
        public string BatchCode { get; set; } = string.Empty;
        public string SpeciesName { get; set; } = string.Empty;
        public string AreaName { get; set; } = string.Empty;
        public DateTime SowingDate { get; set; }
        public decimal QuantitySown { get; set; }
        public decimal WastageQuantity { get; set; }

        public decimal WastagePercent => QuantitySown > 0
            ? Math.Round(WastageQuantity / QuantitySown * 100m, 2, MidpointRounding.AwayFromZero)
            : 0m;
    }
}
