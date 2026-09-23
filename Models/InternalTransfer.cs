namespace PlantStockManager.Models
{
    public class InternalTransfer
    {
        public int Id { get; set; }
        public string TransferCode { get; set; } = string.Empty;

        // 'EmptyPot' | 'PottedPlant' | 'Cutting' | 'MainOfficeIssue' (Phase 18/
        // Phase C -- Main Office issuing Potted Plant starter material to a
        // Growing Partner Area; distinct from the immediate, no-confirmation
        // 'PottedPlant' transfer other Areas already use) |
        // 'GrowingPartnerToOutlet' (Phase 20/Phase F -- a Growing Partner
        // Area sending Potted Plant stock to an Outlet Area; the mirror
        // image of 'MainOfficeIssue' -- same single-step send/confirm-with-
        // discrepancy shape, but source must be Growing-Partner-linked and
        // destination must be AreaType='Outlet', the opposite of
        // MainOfficeIssue's own source/destination rule).
        public string StockType { get; set; } = string.Empty;

        // Exactly one of these three is populated, matching StockType.
        public int? SourceEmptyPotInventoryId { get; set; }
        public int? SourcePottedPlantStockId { get; set; }
        public int? SourceCuttingStockId { get; set; }
        public int SourceAreaId { get; set; }

        // Nullable as of Phase 15: for StockType = 'Cutting', the
        // destination isn't known at creation time -- Main Office
        // decides it when confirming (see ConfirmedQuantity/ConfirmedBy
        // below). Still required at creation for EmptyPot/PottedPlant,
        // exactly as before (CK_InternalTransfers_DestinationRequired).
        public int? DestinationAreaId { get; set; }

        // Phase 15: for a pending Cutting transfer, which Main Office
        // Area it is awaiting confirmation at (distinct from
        // DestinationAreaId, which is the eventual REAL destination
        // chosen at confirmation time).
        public int? PendingConfirmationAreaId { get; set; }

        // Quantity: what was SENT. Never altered after creation, even
        // when the confirmed amount differs -- "must not silently change
        // 500 to 480."
        public decimal Quantity { get; set; }

        // 'Completed' | 'Cancelled' | 'PendingConfirmation' | 'Rejected' |
        // 'ConfirmedAwaitingTransplant' | 'Transplanted' (the last two are
        // Cutting-only -- Phase 16, Model B). A 'MainOfficeIssue' or
        // 'GrowingPartnerToOutlet' row goes PendingConfirmation ->
        // Completed (or Rejected) directly -- neither has a Transplant-
        // style second step, since the destination is already known at
        // creation for both.
        public string Status { get; set; } = "Completed";

        // Phase 15 confirmation fields -- populated once a
        // 'PendingConfirmation' Cutting, MainOfficeIssue, or
        // GrowingPartnerToOutlet transfer's receipt is confirmed
        // (ConfirmReceiptAsync/ConfirmMainOfficeIssueAsync/
        // ConfirmGrowingPartnerToOutletAsync) or rejected (RejectAsync).
        public decimal? ConfirmedQuantity { get; set; }
        public int? ConfirmedBy { get; set; }
        public DateTime? ConfirmedDate { get; set; }
        // Required by the application whenever ConfirmedQuantity differs
        // from Quantity, or when the transfer is Rejected outright.
        public string? DiscrepancyReason { get; set; }

        // Phase 16 (Model B): populated only once a Cutting transfer
        // reaches 'Transplanted' -- sourced from the 1:1
        // dbo.CuttingTransplants row via a join, never written directly
        // on this model/table. DestinationAreaId above doubles as the
        // Destination Polyhouse once Transplanted.
        public int? TransplantDestinationSupervisorId { get; set; }
        public string? TransplantDestinationSupervisorName { get; set; }
        public DateTime? TransplantDate { get; set; }
        public string? TransplantRemarks { get; set; }

        public int? ResponsiblePersonId { get; set; }
        public int? SupervisorId { get; set; }
        public string? Remarks { get; set; }

        public DateTime CreatedDate { get; set; } = DateTime.UtcNow;
        public string? CreatedBy { get; set; }
        public DateTime? ModifiedDate { get; set; }
        public string? ModifiedBy { get; set; }

        // Display-only, populated by joins in InternalTransferRepository.
        public string? PotSize { get; set; }
        public string? SpeciesName { get; set; }
        public string? PlantTypeName { get; set; }
        public string? SourceAreaName { get; set; }

        // Phase 20/Phase F: the Growing Partner that owns the SOURCE Area,
        // when it has one (Area.GrowingPartnerId -> GrowingPartners.Name).
        // Null for a MainOffice/internally-run source, e.g. a
        // MainOfficeIssue row. Lets the Outlet Supervisor's confirmation
        // screen show "Growing Partner: Kiran Partner" without duplicating
        // that value onto this row.
        public string? SourceGrowingPartnerName { get; set; }
        public string? DestinationAreaName { get; set; }
        public string? PendingConfirmationAreaName { get; set; }
        public string? ResponsiblePersonName { get; set; }
        public string? SupervisorName { get; set; }
        public string? ConfirmedByName { get; set; }
    }
}
