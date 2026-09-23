namespace PlantStockManager.Models
{
    public class PottedPlantStock
    {
        public int Id { get; set; }
        public int SpeciesId { get; set; }
        public string PotSize { get; set; } = string.Empty;

        // Which EmptyPotInventory pool (PotSize + Area) this stock's
        // pots were produced from -- a proper surrogate-key FK, since
        // PotSize alone no longer uniquely identifies a pool once
        // multiple Areas can each hold the same Pot Size.
        public int EmptyPotInventoryId { get; set; }

        // Which Area physically holds this potted stock. Nullable for
        // legacy/"unassigned location" rows created before Phase 8;
        // every NEW row going forward always has a real AreaId. The
        // business key is (SpeciesId, PotSize, AreaId).
        public int? AreaId { get; set; }

        public decimal PhysicalQuantity { get; set; }
        public decimal ReservedQuantity { get; set; }
        public decimal SoldDispatchedQuantity { get; set; }
        public decimal WastedQuantity { get; set; }

        // Phase 18 (Phase C): independent of ReservedQuantity/
        // AvailableQuantity below -- mirrors dbo.CuttingStock's own
        // InTransitQuantity (Phase 16). Raised while a 'MainOfficeIssue'
        // Internal Transfer is out for confirmation, so the same physical
        // stock can't be issued twice; never touched by Booking/
        // Reservation (Phase 9/10), which only ever reads/writes
        // ReservedQuantity.
        public decimal InTransitQuantity { get; set; }

        // Available = Physical - Reserved (mirrors the DB's persisted
        // computed column). Deliberately NOT redefined to also subtract
        // InTransitQuantity -- Booking/Dispatch/Reservation must see
        // exactly the same AvailableQuantity as before Phase 18. "Available
        // to send on a new Main Office Issue" (Physical - Reserved -
        // InTransit) is computed separately, only inside
        // PottedPlantStockRepository.ReserveInTransitAsync.
        public decimal AvailableQuantity => PhysicalQuantity - ReservedQuantity;

        public DateTime CreatedDate { get; set; } = DateTime.UtcNow;
        public string? CreatedBy { get; set; }
        public DateTime? ModifiedDate { get; set; }
        public string? ModifiedBy { get; set; }

        // Display-only, populated by joins in PottedPlantStockRepository.
        public string? SpeciesName { get; set; }
        public string? PlantTypeName { get; set; }
        public string? AreaName { get; set; }

        // Phase E (Growing Partner Pot/Tray Production & Stock) display-only
        // additions -- all derived at read time via joins, no new columns.
        // GrowingPartnerName: AreaId -> Area.GrowingPartnerId -> GrowingPartners.Name
        // (null for internally-run Areas / unassigned stock, exactly like
        // PotProduction.GrowingPartnerName from Phase 19).
        public string? GrowingPartnerName { get; set; }

        // Traceability (Phase E spec item 9): the most recent 'Production'
        // ledger entry's PotProduction row, so a Growing Partner can trace
        // PottedPlantStock -> PotProduction -> SourceCuttingStock/
        // PropagationBatch -> Area -> GrowingPartner without this row
        // duplicating any of those fields itself.
        public int? LastProductionId { get; set; }
        public string? LastProductionCode { get; set; }
        public string? LastProductionSource { get; set; } // "Cutting Stock" | "Propagation Batch" | null

        // Most recent transaction of ANY type against this stock row
        // (Production, Reservation, Dispatch, Wastage, Transfer, ...), for
        // the "Last transaction/date" dashboard column (spec item 13).
        public DateTime? LastTransactionDate { get; set; }
    }

    public class PottedPlantStockTransaction
    {
        public int Id { get; set; }
        public int PottedPlantStockId { get; set; }
        public DateTime TransactionDate { get; set; } = DateTime.UtcNow;

        // 'Production' is the only type Phase 7 writes. The others are
        // reserved for Phases 8-10 to reuse this same ledger.
        public string TransactionType { get; set; } = string.Empty;
        public string? ReferenceType { get; set; }
        public int? ReferenceId { get; set; }

        // Signed delta applied to PhysicalQuantity.
        public decimal Quantity { get; set; }
        public decimal BeforeQuantity { get; set; }
        public decimal AfterQuantity => BeforeQuantity + Quantity;

        public int? UserId { get; set; }
        public string? Remarks { get; set; }
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

        // Display-only, populated by joins in PottedPlantStockRepository.
        public string? SpeciesName { get; set; }
        public string? PotSize { get; set; }
        public string? UserName { get; set; }
    }
}
