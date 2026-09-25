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

        // Phase C: Quantity keeps its Phase B meaning (supervisor-approved Ready
        // quantity). Reservations and dispatches are tracked separately so the
        // approved history of the batch is never overwritten.
        public decimal ReservedQuantity { get; set; }
        public decimal DispatchedQuantity { get; set; }
        public decimal PhysicalQuantity => Quantity - DispatchedQuantity;
        public decimal AvailableQuantity => Quantity - ReservedQuantity - DispatchedQuantity;

        // Tray relationship, inherited from the sowing (CavityType is the
        // sowing's; FK_ReadyStock_SowingCavity keeps both equal).
        public int? CavitySize => PlantStockManager.Services.DirectSowingRules.CavityCount(CavityType);
        public int? SowingTrays { get; set; }
        // Actual Ready Trays from the tray-based approval (NULL for Ready Stock
        // approved before the tray rule, e.g. by seedling count).
        public int? ReadyTrays { get; set; }
        public decimal WastagePercent => PlantStockManager.Services.DirectSowingRules.WastagePercent(WastageQuantity, QuantitySown);
        public string Status =>
            Quantity <= 0 ? "Empty"
            : DispatchedQuantity >= Quantity ? "Fully dispatched"
            : AvailableQuantity <= 0 ? "Fully reserved"
            : ReservedQuantity > 0 || DispatchedQuantity > 0 ? "Partly reserved/dispatched"
            : "Available";

        // Phase B: location + traceability of an approved batch.
        public int? PolyhouseId { get; set; }
        public decimal QuantitySown { get; set; }
        public decimal ApprovedReadyQuantity { get; set; }
        public decimal WastageQuantity { get; set; }
        public string? SowingStatus { get; set; }
        public string? ApprovedByName { get; set; }
        public DateTime? ApprovalDate { get; set; }
        public string? PolyhouseName { get; set; }
    }
}
