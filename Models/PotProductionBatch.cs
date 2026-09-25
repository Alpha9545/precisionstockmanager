namespace PlantStockManager.Models
{
    // Phase 31: the batch header wrapping repeatable daily
    // dbo.PotProduction entries -- see Database/Phase31_PotProductionBatch.sql
    // for the full design rationale (header + repeatable-child-event
    // pattern, same as SeedSowing/ReadyConfirmation and
    // PottedPlantBookings/Dispatches).
    public class PotProductionBatch
    {
        public int Id { get; set; }
        public string BatchCode { get; set; } = string.Empty;

        public int SourceCuttingStockId { get; set; }

        // Denormalized from the locked source Cutting Stock row at
        // Insert time -- never independently chosen.
        public int SpeciesId { get; set; }
        public int AreaId { get; set; }

        public string PotSize { get; set; } = string.Empty;
        public int EmptyPotInventoryId { get; set; }

        public DateTime ExpectedReadyDate { get; set; }

        // Planned/allocated cutting quantity (rule 1) -- the budget
        // daily entries are checked against (rule 4), in addition to
        // (not instead of) the underlying Cutting Stock pool's own
        // physical-availability check.
        public decimal PlannedCuttingQuantity { get; set; }

        // Running totals, maintained ONLY by
        // PotProductionRepository.InsertFromCuttingStockAsync under this
        // row's own lock, one daily entry at a time.
        public decimal CuttingQuantityConsumedTotal { get; set; }
        public decimal QuantityProducedTotal { get; set; }

        // 'InProduction' -> 'Ready' (rule 6, Supervisor confirms) or
        // 'Cancelled' (only while nothing has been produced yet).
        public string Status { get; set; } = "InProduction";

        public int? SupervisorId { get; set; }
        public string? Remarks { get; set; }

        public DateTime CreatedDate { get; set; } = DateTime.UtcNow;
        public string? CreatedBy { get; set; }
        public int? CreatedById { get; set; }
        public DateTime? ModifiedDate { get; set; }
        public string? ModifiedBy { get; set; }

        // Display-only, populated by joins in PotProductionBatchRepository.
        public string? SpeciesName { get; set; }
        public string? PlantTypeName { get; set; }
        public string? AreaName { get; set; }
        public string? SupervisorName { get; set; }
        public string? GrowingPartnerName { get; set; }

        // Convenience for views/logic -- how much of the plan remains
        // to be consumed, and how much potting loss has accrued so far.
        // Deliberately NOT stored columns -- fully derived from the
        // running totals above (the "no redundant quantity columns" rule).
        public decimal RemainingPlannedQuantity => PlannedCuttingQuantity - CuttingQuantityConsumedTotal;
        public decimal Loss => CuttingQuantityConsumedTotal - QuantityProducedTotal;
    }
}
