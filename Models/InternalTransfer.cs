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
        // MainOfficeIssue's own source/destination rule) |
        // 'GrowingPartnerToMainOffice' (Phase 32/Phase 7 -- Destination #1
        // of Ready Potted Plant Stock's three allowed destinations; the
        // mirror image of GrowingPartnerToOutlet -- same shape, but
        // destination must be AreaType='MainOffice' instead of 'Outlet').
        // See Services/PottedPlantDistributionRules.cs for the shared
        // Area-type predicates GrowingPartnerToOutlet/GrowingPartnerToMainOffice
        // and PottedPlantBookingRepository all reuse.
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
        // Cutting-only -- Phase 16, Model B). A 'MainOfficeIssue',
        // 'GrowingPartnerToOutlet', or 'GrowingPartnerToMainOffice' row goes
        // PendingConfirmation -> Completed (or Rejected) directly --
        // neither has a Transplant-style second step, since the
        // destination is already known at creation for all three.
        public string Status { get; set; } = "Completed";

        // Phase 15 confirmation fields -- populated once a
        // 'PendingConfirmation' Cutting, MainOfficeIssue,
        // GrowingPartnerToOutlet, or GrowingPartnerToMainOffice transfer's
        // receipt is confirmed (ConfirmReceiptAsync/
        // ConfirmMainOfficeIssueAsync/ConfirmGrowingPartnerToOutletAsync/
        // ConfirmGrowingPartnerToMainOfficeAsync) or rejected (RejectAsync).
        public decimal? ConfirmedQuantity { get; set; }
        public int? ConfirmedBy { get; set; }
        public DateTime? ConfirmedDate { get; set; }
        // Required by the application whenever ConfirmedQuantity differs
        // from Quantity, or when the transfer is Rejected outright. For a
        // Cutting transfer this is set automatically to WastageReason
        // below (kept for the Reject path and for any existing display
        // that already reads it -- see Transplant.cshtml).
        public string? DiscrepancyReason { get; set; }

        // Phase 4 (Cutting Delivery to Main Office) -- Cutting transfers
        // only, mirroring the Direct Sowing/Ready Confirmation tray
        // architecture (Services/DirectSowingRules.cs) instead of a
        // freely-typed Quantity/ConfirmedQuantity pair:
        //   CavityType/NumberOfTrays  -- chosen and computed when the
        //     Mother Plant Supervisor sends it (GiveToMainOffice); Quantity
        //     above becomes NumberOfTrays x cavity size (cuttings used in
        //     COMPLETE trays only -- CuttingQuantityEntered is what was
        //     actually counted; the rest simply stays in the source pool,
        //     exactly like Direct Sowing's remaining seeds, with no
        //     separate "remaining" concept surfaced anywhere);
        //   ActualReadyTrays/WastageQuantity/WastageReason -- entered and
        //     computed when Main Office confirms receipt (ConfirmReceipt):
        //     ConfirmedQuantity = ActualReadyTrays x cavity size,
        //     WastageQuantity = Quantity - ConfirmedQuantity.
        public string? CavityType { get; set; }
        public decimal? CuttingQuantityEntered { get; set; }
        public int? NumberOfTrays { get; set; }
        public decimal? ActualReadyTrays { get; set; }
        public decimal? WastageQuantity { get; set; }
        public string? WastageReason { get; set; }

        // Display-only: how much of what was actually counted at the
        // source Area never left as a complete tray (Direct Sowing's
        // "remaining seeds" equivalent -- deliberately NOT called
        // "Remaining Seedling Quantity", and never surfaced on the
        // confirmation screen; it simply stays in the source Cutting
        // Stock pool, available for the next delivery).
        public decimal RemainingCuttings => CuttingQuantityEntered.HasValue ? CuttingQuantityEntered.Value - Quantity : 0;

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
