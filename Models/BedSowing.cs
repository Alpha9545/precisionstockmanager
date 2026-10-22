namespace PlantStockManager.Models
{
    // 2026-10-12 (Database/Migrations/2026-10-12_BedSowing.sql): seed sown into a BED at the
    // Bed Sowing Polyhouse (dbo.BedSowingLocations). No cavity, no trays, no Ready Stock at this
    // stage -- the seed is deducted from a Main Office seed lot. The Bed Sowing Transplanter later
    // moves seedlings into cavity trays (BedSowingOperation, Transplant); each transplant becomes a
    // growing tray batch (dbo.SeedSowings, SourceType 'BedSowing') that goes through the existing
    // Sowing Approval -> Ready Stock. Conservation: Quantity = Remaining + Transplanted + Lost.
    public class BedSowing
    {
        public const string StatusPending = "PendingTransplant";
        public const string StatusPartial = "PartiallyTransplanted";
        public const string StatusCompleted = "Completed";
        public const string StatusCancelled = "Cancelled";

        public int Id { get; set; }
        public string BedSowingCode { get; set; } = string.Empty;
        public int SpeciesId { get; set; }
        public string SpeciesName { get; set; } = string.Empty;
        public string? PlantTypeName { get; set; }
        public int SeedStockId { get; set; }
        public string SeedLotNo { get; set; } = string.Empty;
        public int? SeedSourceId { get; set; }
        public string? SeedSourceName { get; set; }
        public int AreaId { get; set; }
        public string? AreaName { get; set; }
        public int PolyhouseId { get; set; }
        public string? PolyhouseName { get; set; }
        public DateTime SowingDate { get; set; }
        public decimal Quantity { get; set; }
        // 2026-10-13: a bed has NO Ready Date -- readiness starts when seedlings are transplanted into
        // trays (Ready Date = transplant date + Ready Days, on each tray batch). These two are only
        // set on beds recorded before 2026-10-13_TraysToSowingApprovals.sql (legacy, never shown).
        public int? ReadyStockDays { get; set; }
        public DateTime? ExpectedReadyDate { get; set; }
        public decimal TransplantedQuantity { get; set; }
        public decimal LostQuantity { get; set; }
        public decimal RemainingQuantity => Quantity - TransplantedQuantity - LostQuantity;
        public string Status { get; set; } = StatusPending;
        public string? Remarks { get; set; }
        public Guid? SubmissionToken { get; set; }
        public bool IsDuplicateSubmission { get; set; }
        public DateTime CreatedDate { get; set; }
        public string? CreatedBy { get; set; }
        public int? CreatedById { get; set; }
        public DateTime? CancelledDate { get; set; }
        public string? CancelledByName { get; set; }
        public string? CancelReason { get; set; }

        public bool IsOpen => Status is StatusPending or StatusPartial;
        public string StatusLabel => Services.BedSowingRules.StatusLabel(Status);
    }

    // One immutable transplant or bed-loss row. A mistake is reversed (Status Reversed with
    // who / when / why), never edited or deleted (TR_BedSowingOperations_Immutable).
    public class BedSowingOperation
    {
        public const string TypeTransplant = "Transplant";
        public const string TypeLoss = "Loss";
        public const string StatusActive = "Active";
        public const string StatusReversed = "Reversed";

        public int Id { get; set; }
        public string OperationCode { get; set; } = string.Empty;
        public int BedSowingId { get; set; }
        public string? BedSowingCode { get; set; }
        public string OperationType { get; set; } = TypeTransplant;
        public DateTime OperationDate { get; set; }
        public decimal Quantity { get; set; }
        public string? CavityType { get; set; }
        public int? OutputTrays { get; set; }
        public int? TargetAreaId { get; set; }
        public string? TargetAreaName { get; set; }
        public int? TargetPolyhouseId { get; set; }
        public string? TargetPolyhouseName { get; set; }
        // The growing tray batch this transplant created (dbo.SeedSowings).
        public int? SeedSowingId { get; set; }
        public string? SowingCode { get; set; }
        public string? SowingStatus { get; set; }
        public decimal SowingApprovedQuantity { get; set; }
        public decimal SowingWastageQuantity { get; set; }
        public string? SupervisorName { get; set; }
        public DateTime? SowingReadyDate { get; set; }   // the tray batch's Ready Date (transplant date + Ready Days)
        public string? GrowingMaterialName { get; set; }
        public string? Reason { get; set; }
        public string? Remarks { get; set; }
        public string Status { get; set; } = StatusActive;
        public DateTime CreatedDate { get; set; }
        public string? CreatedBy { get; set; }
        public int? CreatedById { get; set; }
        public DateTime? ReversedDate { get; set; }
        public string? ReversedBy { get; set; }
        public string? ReversalReason { get; set; }
        public bool IsTransplant => OperationType == TypeTransplant;
        public bool IsActive => Status == StatusActive;
    }

    // The Bed Sowing Polyhouse (dbo.BedSowingLocations), with its Area read from the database; also
    // used for the transplant TARGETS (that Polyhouse, or a Polyhouse of an active Mother Plant Area).
    public class BedSowingLocation
    {
        public int PolyhouseId { get; set; }
        public string PolyhouseName { get; set; } = string.Empty;
        public int AreaId { get; set; }
        public string AreaName { get; set; } = string.Empty;
        public string? AreaType { get; set; }
    }

    // Input of a transplant (Bed Sowing Transplanter form).
    public class BedTransplantRequest
    {
        public int BedSowingId { get; set; }
        public int TargetPolyhouseId { get; set; }
        public string? CavityType { get; set; }
        public decimal Quantity { get; set; }
        // Shown on the form; the server derives it from Quantity / cavity and refuses a mismatch.
        public int? OutputTrays { get; set; }
        public DateTime TransplantDate { get; set; }
        public int? GrowingMaterialTypeId { get; set; }
        public string? Remarks { get; set; }
        public Guid? SubmissionToken { get; set; }
        // 2026-10-14: the Transplanter's explicit confirmation that ALL the remaining seedlings are dead /
        // unusable -- the whole remaining quantity recorded as waste with WasteReason, no tray batch, bed closed.
        public bool CloseAsWaste { get; set; }
        public string? WasteReason { get; set; }
    }
}
