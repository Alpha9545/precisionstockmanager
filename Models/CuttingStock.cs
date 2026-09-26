namespace PlantStockManager.Models
{
    // Phase 15: a location-scoped balance of raw (unpotted) cuttings --
    // fills the gap between "cuttings were harvested" (ActualCuttings,
    // Phase 4 -- a production-stage record with no Area) and "cuttings
    // are physically sitting somewhere" (needed for the Mother
    // Plant/Kunjir -> Main Office -> destination workflow). One row per
    // (SpeciesId, AreaId) pair; PhysicalQuantity and InTransitQuantity are
    // never written directly outside of CuttingStockRepository's own
    // locked, transactional methods (RecordTransactionAsync,
    // ReserveInTransitAsync, ReleaseInTransitAsync, RecordTransplantAsync).
    public class CuttingStock
    {
        public int Id { get; set; }

        public int SpeciesId { get; set; }
        public string? SpeciesName { get; set; }
        public string? PlantTypeName { get; set; }
        public string? SpeciesColor { get; set; }

        public int AreaId { get; set; }
        public string? AreaName { get; set; }
        public string? AreaType { get; set; }

        // Phase 19: display-only, via Area.GrowingPartnerId -- lets
        // Pot Production's "From Cutting Stock" source picker show which
        // Growing Partner a pool belongs to (NULL for an internally-run
        // Area).
        public string? GrowingPartnerName { get; set; }

        public decimal PhysicalQuantity { get; set; }

        // Phase 16: portion of PhysicalQuantity currently held against an
        // active (PendingConfirmation or ConfirmedAwaitingTransplant)
        // outbound Cutting transfer sourced from this row -- prevents the
        // same physical cuttings from being sent twice while a transfer
        // is in flight.
        public decimal InTransitQuantity { get; set; }

        // Phase 16: DB-computed (PhysicalQuantity - InTransitQuantity).
        // What a NEW outbound transfer is allowed to draw against.
        public decimal AvailableQuantity { get; set; }

        public DateTime CreatedDate { get; set; } = DateTime.UtcNow;
        public string? CreatedBy { get; set; }
        public DateTime? ModifiedDate { get; set; }
        public string? ModifiedBy { get; set; }
    }
}
