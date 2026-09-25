namespace PlantStockManager.Models
{
    // Phase 5: Cutting Stock -> Cutting Sowing -> Tray/Cavity -> Complete
    // Trays -> Ready Seedlings -> Wastage -> Ready Stock. The exact
    // mirror of SeedSowing (Phase 23/PhaseB), except the source is
    // dbo.CuttingStock instead of dbo.SeedStock -- a deliberately
    // SEPARATE table/pipeline, per "do not mix seed stock and cutting
    // stock". Unlike Direct Sowing, there is no separate "growing Area"
    // choice: a Cutting Sowing consumes its source Cutting Stock pool IN
    // PLACE, at that pool's own Area (same pattern as
    // PotProduction/CreateFromCutting.cshtml.cs), so AreaId below is
    // always the source pool's Area -- Area is preserved automatically,
    // never re-chosen.
    //
    // Feeds the SAME dbo.ReadyStock / dbo.ReadyConfirmations tables Direct
    // Sowing does (a "Ready Stock" seedling is fungible for booking/
    // dispatch regardless of production origin) via a parallel nullable
    // CuttingSowingId FK, exactly like PotProduction's own dual source
    // (PropagationBatchId / SourceCuttingStockId, Phase 19). Every
    // downstream Ready Stock/Booking/Dispatch rule (assigned-supervisor-
    // only approval, at-most-one-Confirmed-approval-per-sowing, whole-
    // tray Ready Stock quantities) applies identically; see
    // Database/Phase30_CuttingSowing.sql for the schema and
    // Data/CuttingSowingRepository.cs / Data/ReadyConfirmationRepository.cs
    // (ConfirmCuttingSowingAsync) for the application-layer enforcement
    // (no new database trigger -- see that script's header comment for why).
    public class CuttingSowing
    {
        public int Id { get; set; }
        public string SowingCode { get; set; } = string.Empty;

        public int SourceCuttingStockId { get; set; }

        // Denormalized from the locked source CuttingStock row at Insert
        // time -- never trusted from the caller, same discipline as
        // every other production/transfer header in this app.
        public int SpeciesId { get; set; }
        public string SpeciesName { get; set; } = string.Empty;
        public string PlantTypeName { get; set; } = string.Empty;

        // Always the source Cutting Stock pool's own Area -- a Cutting
        // Sowing is never moved to a different Area.
        public int AreaId { get; set; }
        public string AreaName { get; set; } = string.Empty;
        public string? GrowingPartnerName { get; set; }
        public string? PolyhouseName { get; set; }

        public string CavityType { get; set; } = string.Empty;
        public int? NumberOfTrays { get; set; }
        // Cuttings used in complete trays (QuantitySown = NumberOfTrays x
        // cavity size) -- deducted from Cutting Stock. Mirrors
        // SeedSowing.QuantitySown exactly.
        public decimal QuantitySown { get; set; }
        // The raw cutting quantity entered on the form. Mirrors
        // SeedSowing.SeedQuantity. Only the cuttings filling complete
        // trays are sown; the rest stay in the source Cutting Stock pool
        // -- there is no separate "remaining" field/concept surfaced
        // anywhere (same rule as Phase 4's Cutting Delivery).
        public decimal CuttingQuantityEntered { get; set; }
        public decimal RemainingCuttings => CuttingQuantityEntered > 0 ? CuttingQuantityEntered - QuantitySown : 0;

        public decimal WastageQuantity { get; set; }
        public decimal WastagePercent => PlantStockManager.Services.DirectSowingRules.WastagePercent(WastageQuantity, QuantitySown);

        public DateTime SowingDate { get; set; } = DateTime.Today;

        // Denormalized from PlantSpecies.ReadyStockDays at the moment
        // this row was inserted -- never re-read from the species master
        // later, exactly like SeedSowing.ReadyStockDays/ExpectedReadyDate.
        public int? ReadyStockDays { get; set; }
        public DateTime? ExpectedReadyDate { get; set; }

        public string Status { get; set; } = "Sown";

        public int? SupervisorId { get; set; }
        public string? SupervisorName { get; set; }
        public string? Remarks { get; set; }

        public DateTime CreatedDate { get; set; }
        public string? CreatedBy { get; set; }
        public int? CreatedById { get; set; }
        public DateTime? ModifiedDate { get; set; }
        public string? ModifiedBy { get; set; }

        // Display-only, populated by a join to the source CuttingStock row.
        public decimal? SourceAvailableQuantity { get; set; }

        // Running total moved into Ready Stock via its (at most one)
        // Ready Confirmation. Mirrors SeedSowing.ConfirmedReadyQuantity.
        public decimal ConfirmedReadyQuantity { get; set; }
        public decimal RemainingReadyQuantity => QuantitySown - ConfirmedReadyQuantity - WastageQuantity;
    }
}
