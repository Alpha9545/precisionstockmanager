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
        public string? AreaType { get; set; }   // 'MainOffice' = a Main Area, 'MotherPlant', 'Outlet', ...
        public bool InMainArea => AreaType == "MainOffice";
        public bool InMotherPlantArea => AreaType == "MotherPlant";

        // Phase C: Quantity keeps its Phase B meaning (supervisor-approved Ready
        // quantity). Reservations and dispatches are tracked separately so the
        // approved history of the batch is never overwritten.
        public decimal ReservedQuantity { get; set; }
        public decimal DispatchedQuantity { get; set; }
        // 2026-10-09: plants sent to a Mother Plant Area and not yet received there --
        // still part of Quantity, but neither physically here nor available.
        public decimal InTransitQuantity { get; set; }
        public decimal PhysicalQuantity => Quantity - DispatchedQuantity - InTransitQuantity;

        // 2026-10-22 Overgrowth (2026-10-24_ReadyStockOvergrowth.sql; Services/OvergrowthRules.cs): a deliberate, audited mark on
        // THIS batch row. All false / null without the migration.
        public bool IsOvergrowth { get; set; }
        public DateTime? OvergrowthMarkedAt { get; set; }      // UTC
        public string? OvergrowthMarkedByName { get; set; }
        public string? OvergrowthReason { get; set; }
        public decimal AvailableQuantity => Quantity - ReservedQuantity - DispatchedQuantity - InTransitQuantity;

        // 2026-10-09 lineage (Database/Migrations/2026-10-09_ReadyStockTransplanting.sql):
        //   'Sowing'     the batch of a sowing (one per sowing; every batch before this date)
        //   'Transfer'   received in a Mother Plant Area from ParentReadyStockId (OriginReferenceId = InternalTransfers.Id)
        //   'Transplant' output of a transplanting of ParentReadyStockId (OriginReferenceId = ReadyStockTransplants.Id)
        // SeedSowingId / BatchNo / SowingDate always stay those of the ORIGINAL sowing.
        public string OriginType { get; set; } = OriginSowing;
        public int? ParentReadyStockId { get; set; }
        public int? OriginReferenceId { get; set; }
        public const string OriginSowing = "Sowing";
        public const string OriginTransfer = "Transfer";
        public const string OriginTransplant = "Transplant";
        public bool IsSowingBatch => OriginType == OriginSowing;
        public string OriginLabel => OriginType switch
        {
            OriginTransfer => "Received from Main Area",
            OriginTransplant => "Transplanted",
            _ => "Sowing"
        };
        // Physical trays of this batch: CEILING(plants / cavity) -- a received or transplanted
        // batch may end in a partial tray; a sowing batch is always whole trays.
        public decimal? PhysicalTrays => CavitySize is > 0 ? Math.Ceiling(PhysicalQuantity / CavitySize.Value) : null;

        // Tray relationship. CavityType is this batch's own cavity: the sowing's for
        // 'Sowing'/'Transfer' batches (FK_ReadyStock_SowingCavity), the target cavity
        // for a 'Transplant' output.
        public int? CavitySize => PlantStockManager.Services.DirectSowingRules.CavityCount(CavityType);
        // 2026-10-31: a sowing batch holding fewer plants than one tray (a partial-tray cutting sowing) is ONE tray of its
        // whole quantity and moves only as a whole; TraySize = plants per tray wherever the batch is counted in trays.
        public bool IsPartialTrayBatch => PlantStockManager.Services.PartialTrayRules.IsPartialTrayBatch(OriginType, Quantity, CavityType);
        public int? TraySize => PlantStockManager.Services.PartialTrayRules.TraySize(OriginType, Quantity, CavityType);
        public int? SowingTrays { get; set; }
        // Actual Ready Trays from the tray-based approval (NULL for Ready Stock
        // approved before the tray rule, e.g. by seedling count).
        public int? ReadyTrays { get; set; }
        public decimal WastagePercent => PlantStockManager.Services.DirectSowingRules.WastagePercent(WastageQuantity, QuantitySown);
        public string Status =>
            Quantity <= 0 ? "Empty"
            : DispatchedQuantity >= Quantity ? "Fully dispatched"
            : InTransitQuantity > 0 && AvailableQuantity <= 0 ? "In transit"
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
