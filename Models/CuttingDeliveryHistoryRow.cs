namespace PlantStockManager.Models
{
    // Correction #7 -- one line of the Cutting Delivery History (read-only; nothing here is stored separately).
    //
    // A row is either
    //   Kind 'E'  a CUTTING ENTRY (dbo.CuttingProductions) with its delivery (dbo.InternalTransfers, linked by
    //             SourceCuttingProductionId) when the entry was sent to Main Office; an entry kept "for Pot Production", or made
    //             before destinations existed, has NO delivery and is still listed so nothing is assumed to have been delivered;
    //   Kind 'D'  an OLDER DELIVERY made before deliveries were linked to entries (no Cutting Entry, so the Mother Plant,
    //             supervisor and Polyhouse it came from were never recorded and cannot be derived from the shared stock pool).
    //
    // The people are kept SEPARATE, each from its own column:
    //   Cutting Supervisor  CuttingProductions.SupervisorId  (the Mother Plant Supervisor of the entry)
    //   Entered By          InternalTransfers.CreatedBy / CuttingProductions.CreatedBy  (username of who typed it in)
    //   Received By         InternalTransfers.ConfirmedBy + ConfirmedDate  (Main Office confirmed receipt)
    //   Rejected By         InternalTransfers.ModifiedBy + ModifiedDate when Status = Rejected (only the username is stored)
    // The Destination Polyhouse is NOT stored anywhere for a cutting delivery (Confirm Receipt records a quantity, not a
    // place inside Main Office, and a Main Office Area has several Polyhouses), so it is always "Not recorded".
    public class CuttingDeliveryHistoryRow
    {
        public string Kind { get; set; } = "E";
        public bool HasEntry => Kind == "E";
        public bool HasDelivery => TransferId.HasValue;

        public DateTime RecordDate { get; set; }

        public int? ProductionId { get; set; }
        public string? ProductionCode { get; set; }
        public DateTime? ProductionDate { get; set; }

        public int? TransferId { get; set; }
        public string? TransferCode { get; set; }
        public string? TransferStatus { get; set; }           // as stored: PendingConfirmation / Completed / Rejected ...
        public string StatusKey { get; set; } = "None";       // PendingConfirmation / Completed / CompletedShortfall / Rejected / None

        public int SpeciesId { get; set; }
        public string? SpeciesName { get; set; }
        public string? Color { get; set; }
        public string? PlantTypeName { get; set; }

        public decimal Quantity { get; set; }                  // sent (or, with no delivery, cut)
        public decimal? ConfirmedQuantity { get; set; }
        public decimal? TransitLoss => TransferStatus == "Completed" && ConfirmedQuantity.HasValue ? Quantity - ConfirmedQuantity.Value : null;

        public int SourceAreaId { get; set; }
        public string? SourceAreaName { get; set; }
        public int? MotherPlantId { get; set; }
        public string? MotherPlantCode { get; set; }
        public int? SourcePolyhouseId { get; set; }
        public string? SourcePolyhouseName { get; set; }
        public int? SupervisorId { get; set; }
        public string? SupervisorName { get; set; }

        public string DestinationType { get; set; } = "NotRecorded";   // MainOffice / PotProduction / NotRecorded
        public int? DestinationAreaId { get; set; }
        public string? DestinationAreaName { get; set; }
        public int? PendingAreaId { get; set; }                        // the Main Office Area an unconfirmed delivery is waiting at

        public string? EnteredBy { get; set; }
        public string? EnteredByName { get; set; }
        public DateTime? DeliveryDate { get; set; }                    // when the delivery was made (sent)
        public int? ConfirmedById { get; set; }
        public string? ConfirmedByName { get; set; }
        public DateTime? ConfirmedDate { get; set; }
        public string? RejectedBy { get; set; }
        public DateTime? RejectedDate { get; set; }
        public string? Reason { get; set; }                            // rejection reason / shortfall reason (InternalTransfers.DiscrepancyReason)

        public string? EntryRemarks { get; set; }
        public string? DeliveryRemarks { get; set; }
    }
}
