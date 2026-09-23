namespace PlantStockManager.Models
{
    // Phase 25 (Phase K): the confirmed-ready stock pool. Grain is ONE
    // ROW PER SeedSowingId (UNIQUE) -- not pooled across Sowings by
    // Species+Area+Lot the way dbo.SeedStock/dbo.PottedPlantStock/
    // dbo.CuttingStock are. The Phase K spec explicitly requires Ready
    // Stock to "retain traceability back to the originating Seed
    // Sowing" and forbids losing original batch identity; pooling
    // multiple Sowings' confirmed quantity into one row would destroy
    // exactly that. See Database/Phase25_ReadyConfirmation.sql and
    // Decision 22 in PROJECT_DOCUMENTATION.md for the full reasoning.
    //
    // Created (via ReadyStockRepository.GetOrCreateLockedAsync) only
    // the FIRST time a Sowing is actually Ready-Confirmed -- never
    // eagerly at Sowing time, per the explicit "no automatic Ready
    // Stock creation" rule.
    public class ReadyStock
    {
        public int Id { get; set; }

        public int SeedSowingId { get; set; }

        // Denormalized from the owning Sowing at the moment this row
        // is first created -- never re-derived later, never trusted
        // from a caller.
        public int SpeciesId { get; set; }
        public int AreaId { get; set; }
        public string BatchNo { get; set; } = string.Empty;
        public string CavityType { get; set; } = string.Empty;
        public DateTime SowingDate { get; set; }

        // Running balance of confirmed-ready stock not yet consumed by
        // any later phase (nothing in this app consumes it yet -- this
        // phase only ever adds via 'Confirmed'/removes via
        // 'ReversalRemoval'). Never written directly outside of
        // ReadyStockRepository.RecordTransactionAsync.
        public decimal Quantity { get; set; }

        public DateTime? FirstConfirmationDate { get; set; }

        public DateTime CreatedDate { get; set; } = DateTime.UtcNow;
        public string? CreatedBy { get; set; }
        public DateTime? ModifiedDate { get; set; }
        public string? ModifiedBy { get; set; }

        // Display-only, populated by joins in ReadyStockRepository.
        public string? SowingCode { get; set; }
        public string? SpeciesName { get; set; }
        public string? PlantTypeName { get; set; }
        public string? AreaName { get; set; }
        public string? PolyhouseName { get; set; }
    }
}
