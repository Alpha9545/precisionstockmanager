namespace PlantStockManager.Services
{
    // Main Area Ready Stock -> Mother Plant Area (dbo.InternalTransfers, StockType
    // 'MotherPlantTransfer'). Pure rules -- the repository applies them to values it
    // re-reads under lock; nothing here trusts the browser.
    //
    //   create   PendingConfirmation; the plants are HELD at the source (InTransitQuantity):
    //            no longer available there, not yet at the destination
    //   receive  Completed; the held plants leave the source ('TransferOut', and
    //            'TransferLoss' for any shortfall) and a NEW batch appears at the destination
    //            ('TransferIn') -- exactly once per transfer
    //   reject   Rejected by the receiving Mother Plant Area; the hold is released
    //   cancel   Cancelled by the sender while still pending; the hold is released
    //
    // Units: plants (ReadyStock quantities). A Main Area batch only ever holds whole trays, so
    // a transfer sends whole trays of the batch's cavity.
    public static class MotherPlantTransferRules
    {
        public const string StockType = "MotherPlantTransfer";
        public const string MotherPlantAreaType = "MotherPlant";
        public const string ReferenceType = "InternalTransfer";
        public const string UseOwnScreenMessage = "This is a Main Area to Mother Plant transfer: send, receive, reject or cancel it from Ready Stock > Mother Plant Transfers.";

        // 2026-10-14: why the plants are sent (InternalTransfers.TransferPurpose, 2026-10-14 migration).
        //   PotProduction   kept as Ready Stock in the Mother Plant Area for Pot Production -- never offered
        //                   for, and refused by, Transplant into Trays.
        //   TrayTransplant  received, then Transplant into Trays (a different cavity) -> a tray batch in the
        //                   existing Sowing Approvals.
        // Older transfers have none recorded (null): their received batches keep today's behaviour.
        public const string PurposePotProduction = "PotProduction";
        public const string PurposeTrayTransplant = "TrayTransplant";
        public static readonly IReadOnlyList<string> Purposes = new[] { PurposePotProduction, PurposeTrayTransplant };

        public static string PurposeLabel(string? purpose) => purpose switch
        {
            PurposePotProduction => "Pot Production",
            PurposeTrayTransplant => "Transplant into a different cavity tray",
            _ => "Not recorded"
        };

        public const string PotProductionNotTransplantedMessage =
            "This batch was sent to the Mother Plant Area for Pot Production. It is not transplanted into trays -- only stock sent for \"Transplant into a different cavity tray\" is.";

        // Transplant into Trays of a batch received through a transfer of this purpose (null = not a received
        // batch, or an older transfer without a recorded purpose -- unchanged).
        public static (bool Ok, string? Error) ValidateTransplantPurpose(string? receivedTransferPurpose)
            => receivedTransferPurpose == PurposePotProduction ? (false, PotProductionNotTransplantedMessage) : (true, null);

        public const string StatusPending = "PendingConfirmation";
        public const string StatusCompleted = "Completed";
        public const string StatusRejected = "Rejected";
        public const string StatusCancelled = "Cancelled";

        public static string StatusLabel(string status) => status switch
        {
            StatusPending => "In transit",
            StatusCompleted => "Received",
            StatusRejected => "Rejected",
            StatusCancelled => "Cancelled",
            _ => status
        };

        public record SourceBatch(string OriginType, string? AreaType, bool AreaActive, int AreaId, string CavityType, decimal Available);
        public record Destination(int? AreaId, string? AreaType, bool AreaActive, int? PolyhouseId, int? PolyhouseAreaId);

        // Everything about a NEW transfer. quantity = plants.
        // areaOnly (2026-10-14 migration applied): the destination is the Mother Plant AREA -- no Polyhouse is
        // chosen (one named anyway must belong to it) -- and the purpose is required. Without the migration:
        // the earlier rule (destination Polyhouse required, no purpose).
        public static (bool Ok, decimal Trays, string? Error) ValidateCreate(
            SourceBatch source, Destination destination, decimal quantity, DateTime transferDate, DateTime todayIndia, DateTime sowingDate,
            bool areaOnly = false, string? purpose = null)
        {
            if (source.OriginType != "Sowing" || !DirectSowingRules.IsMainOfficeSeedLocation(source.AreaType, source.AreaActive))
                return (false, 0, "Only Ready Stock of a Main Area sowing batch can be sent to a Mother Plant Area.");
            var cavity = DirectSowingRules.CavityCount(source.CavityType);
            if (cavity is null or <= 0)
                return (false, 0, "This batch's cavity is not recognised.");
            if (quantity <= 0 || !DirectSowingRules.IsWholeNumber(quantity))
                return (false, 0, "Quantity to transfer must be a whole number of plants greater than zero.");
            if (quantity % cavity.Value != 0)
                return (false, 0, $"Main Area stock moves in whole trays: the quantity must be a multiple of {cavity.Value} ({source.CavityType}).");
            if (quantity > source.Available)
                return (false, 0, $"Only {QuantityFormat.Qty(source.Available)} plants of this batch are available (not reserved, dispatched or already in transit).");
            if (destination.AreaId is null)
                return (false, 0, "Choose the destination Mother Plant Area.");
            if (destination.AreaType != MotherPlantAreaType || !destination.AreaActive)
                return (false, 0, "The destination must be an active Mother Plant Area.");
            if (destination.AreaId == source.AreaId)
                return (false, 0, "The destination must be a different Area.");
            if (areaOnly && !Purposes.Contains(purpose))
                return (false, 0, "Choose the purpose of the transfer: Pot Production, or Transplant into a different cavity tray.");
            if (destination.PolyhouseId is null && !areaOnly)
                return (false, 0, "Choose the destination Polyhouse.");
            if (destination.PolyhouseId is not null && destination.PolyhouseAreaId != destination.AreaId)
                return (false, 0, "The Polyhouse does not belong to the destination Mother Plant Area.");
            if (transferDate.Date > todayIndia.Date)
                return (false, 0, "The transfer date cannot be in the future.");
            if (transferDate.Date < sowingDate.Date)
                return (false, 0, "The transfer date cannot be before the batch was sown.");
            return (true, quantity / cavity.Value, null);
        }

        // Receiving: received <= sent; a shortfall is recorded as loss in transit and needs a reason.
        public static (bool Ok, decimal Loss, string? Error) ValidateReceive(string status, decimal sent, decimal received, string? reason)
        {
            if (status != StatusPending)
                return (false, 0, $"This transfer is already {StatusLabel(status).ToLowerInvariant()}.");
            if (received <= 0 || !DirectSowingRules.IsWholeNumber(received))
                return (false, 0, "Received quantity must be a whole number greater than zero (use Reject if nothing usable arrived).");
            if (received > sent)
                return (false, 0, $"Received quantity cannot be more than the {QuantityFormat.Qty(sent)} plants sent.");
            var loss = sent - received;
            if (loss > 0 && string.IsNullOrWhiteSpace(reason))
                return (false, 0, $"{QuantityFormat.Qty(loss)} plants are short: enter the reason.");
            return (true, loss, null);
        }

        // 2026-10-11: the business date the plants physically arrived (India date). Defaults to today;
        // never in the future; never before the transfer was sent; an earlier date (received earlier,
        // recorded later) needs a reason. The recorded time (ConfirmedDate) is kept separately.
        public const int MaxReceiptReasonLength = 500;

        public static (bool Ok, string? Error) ValidateReceiptDate(DateTime receivedDate, DateTime transferDate, DateTime todayIndia, string? backdateReason)
        {
            if (receivedDate.Date > todayIndia.Date)
                return (false, "The receipt date cannot be in the future.");
            if (receivedDate.Date < transferDate.Date)
                return (false, $"The receipt date cannot be before the transfer was sent ({transferDate:dd/MM/yyyy}).");
            if (receivedDate.Date < todayIndia.Date && string.IsNullOrWhiteSpace(backdateReason))
                return (false, "The receipt date is earlier than today: enter why it is recorded late.");
            if (backdateReason?.Trim().Length > MaxReceiptReasonLength)
                return (false, $"The reason is too long ({MaxReceiptReasonLength} characters at most).");
            return (true, null);
        }

        public static (bool Ok, string? Error) ValidateClose(string status, string? reason, string action)
        {
            if (status != StatusPending)
                return (false, $"This transfer is already {StatusLabel(status).ToLowerInvariant()} and cannot be {action}.");
            if (string.IsNullOrWhiteSpace(reason))
                return (false, $"Enter the reason it is {action}.");
            if (reason.Trim().Length > 500)
                return (false, "The reason is too long (500 characters at most).");
            return (true, null);
        }
    }
}
