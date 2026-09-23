namespace PlantStockManager.Models
{
    // Phase 23 (Phase I): a single-actor production event that consumes
    // dbo.SeedStock (Phase 22/H). Mirrors PotProduction's shape -- one
    // header row per sowing action, no confirm/reject workflow, an
    // explicit Cancel to reverse it. Deliberately named "SeedSowing"
    // (never bare "Sowing") to stay clear of the pre-existing, untouched
    // legacy Models/SowingRecord.cs / Pages/SeedEntry/Sowing.cshtml.
    public class SeedSowing
    {
        public int Id { get; set; }
        public string SowingCode { get; set; } = string.Empty;

        public int SourceSeedStockId { get; set; }

        // Denormalized, server-derived traceability fields -- never
        // trusted from the caller (see SeedSowingRepository.InsertAsync).
        public int SpeciesId { get; set; }
        public string SpeciesName { get; set; } = string.Empty;
        public string PlantTypeName { get; set; } = string.Empty;

        public int AreaId { get; set; }
        public string AreaName { get; set; } = string.Empty;
        public string? GrowingPartnerName { get; set; }

        // Phase 24 (Phase J): display-only, populated by a join to
        // dbo.Polyhouses via the source Area's own PolyhouseId -- the
        // same "AreaType = operational role, PolyhouseId = physical
        // location" split Decision 19/Phase H already established.
        // Never a second Polyhouse relationship. Not persisted here.
        public string? PolyhouseName { get; set; }

        public string BatchNo { get; set; } = string.Empty;
        public int? SeedSourceId { get; set; }
        public string? SeedSourceName { get; set; }

        public string CavityType { get; set; } = string.Empty;
        public int? NumberOfTrays { get; set; }
        public decimal QuantitySown { get; set; }

        public DateTime SowingDate { get; set; } = DateTime.Today;

        // Phase 24 (Phase J) CORRECTION to Phase I: the ReadyStockDays
        // value actually applied when THIS Sowing was inserted --
        // denormalized from PlantSpecies.ReadyStockDays at that moment,
        // exactly like SpeciesId/AreaId/BatchNo already are. Preserved
        // here (never re-read from the species master later) so that
        // editing a variety's ReadyStockDays afterwards can never change
        // any existing Sowing's historical figures. NULL means either
        // the species had no ReadyStockDays configured at sowing time,
        // or this is a pre-Phase-24 row.
        public int? ReadyStockDays { get; set; }

        // Phase I: originally a purely manual, optional estimate.
        // Phase 24 CORRECTION: SeedSowingRepository.InsertAsync now
        // computes this as SowingDate + ReadyStockDays whenever the
        // sown species has a configured ReadyStockDays value, and only
        // falls back to a manually-entered value when it does not --
        // see Database/Phase24_ReadyAlerts.sql and Decision 21 in
        // PROJECT_DOCUMENTATION.md for the full reasoning. Once stored,
        // this value is fixed for this row; it is never recomputed on
        // read, so a later master-data change cannot silently rewrite
        // a historical Sowing's Expected Ready Date.
        public DateTime? ExpectedReadyDate { get; set; }

        public string Status { get; set; } = "Sown";

        public int? ResponsiblePersonId { get; set; }
        public string? ResponsiblePersonName { get; set; }
        public int? SupervisorId { get; set; }
        public string? SupervisorName { get; set; }

        public string? Remarks { get; set; }

        public DateTime CreatedDate { get; set; }
        public string? CreatedBy { get; set; }
        public DateTime? ModifiedDate { get; set; }
        public string? ModifiedBy { get; set; }

        // Display-only, populated by a join to the source SeedStock row.
        // Not persisted on this table.
        public decimal? SourceAvailableQuantity { get; set; }

        // Phase 25 (Phase K): running total of quantity already moved
        // into Ready Stock via one or more explicit Ready Confirmations
        // (ReadyConfirmationRepository.ConfirmAsync/CancelAsync are the
        // ONLY code paths that ever change this). Mirrors
        // PottedPlantBookings.DispatchedQuantity (Phase 21/Phase G)
        // exactly -- deliberately NOT a new Status value; see Decision
        // 22 in PROJECT_DOCUMENTATION.md. Zero for every Sowing until
        // its first Ready Confirmation.
        public decimal ConfirmedReadyQuantity { get; set; }

        // How much of this Sowing's QuantitySown is still eligible for
        // a further (partial) Ready Confirmation right now. The
        // precise, lock-protected check still happens in
        // ReadyConfirmationRepository.ConfirmAsync against a freshly
        // re-read value -- this is for display/pre-validation only.
        public decimal RemainingReadyQuantity => QuantitySown - ConfirmedReadyQuantity;
    }
}
