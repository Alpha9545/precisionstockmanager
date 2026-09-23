namespace PlantStockManager.Models
{
    // Phase 25 (Phase K): the auditable confirmation EVENT -- one row
    // per explicit "this batch is physically ready" action, however
    // many are eventually recorded against the same Sowing (partial
    // confirmations). Mirrors Dispatch's own role against
    // PottedPlantBookings: a header/event table that both (a)
    // increments a dedicated stock pool (ReadyStock, via its own
    // ledger) and (b) maintains a running total on its parent
    // (SeedSowings.ConfirmedReadyQuantity, mirroring
    // PottedPlantBookings.DispatchedQuantity from Phase 21/Phase G) --
    // see Decision 22 in PROJECT_DOCUMENTATION.md for the full
    // reasoning, including why no new SeedSowings.Status value was
    // added.
    public class ReadyConfirmation
    {
        public int Id { get; set; }
        public string ConfirmationCode { get; set; } = string.Empty;

        public int SeedSowingId { get; set; }
        public int ReadyStockId { get; set; }

        // The caller-supplied (possibly partial) quantity confirmed
        // ready NOW -- validated against the parent Sowing's own
        // remaining quantity, under lock, in
        // ReadyConfirmationRepository.ConfirmAsync.
        public decimal ConfirmedQuantity { get; set; }

        public DateTime ConfirmationDate { get; set; } = DateTime.UtcNow;

        // 'Confirmed' | 'Cancelled'. Independent of SeedSowings.Status,
        // exactly like Dispatch.Status is independent of
        // PottedPlantBookings.Status.
        public string Status { get; set; } = "Confirmed";

        public int? ResponsiblePersonId { get; set; }
        public int? SupervisorId { get; set; }
        public string? Remarks { get; set; }

        public DateTime CreatedDate { get; set; } = DateTime.UtcNow;
        public string? CreatedBy { get; set; }
        public DateTime? ModifiedDate { get; set; }
        public string? ModifiedBy { get; set; }

        // Display-only, populated by joins in ReadyConfirmationRepository
        // (all denormalized from the parent Sowing at READ time -- this
        // is a display convenience only, never a stored/persisted copy,
        // so it always reflects the Sowing's current historical values,
        // e.g. the true ExpectedReadyDate/ReadyStockDays).
        public string? SowingCode { get; set; }
        public string? SpeciesName { get; set; }
        public string? PlantTypeName { get; set; }
        public string? AreaName { get; set; }
        public string? PolyhouseName { get; set; }
        public string? BatchNo { get; set; }
        public string? CavityType { get; set; }
        public DateTime? SowingDate { get; set; }
        public DateTime? ExpectedReadyDate { get; set; }
        public int? ReadyStockDays { get; set; }
        public decimal? QuantitySown { get; set; }
        public decimal? ConfirmedReadyQuantity { get; set; }
        public string? ResponsiblePersonName { get; set; }
        public string? SupervisorName { get; set; }
    }
}
