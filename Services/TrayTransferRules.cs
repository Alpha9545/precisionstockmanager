namespace PlantStockManager.Services
{
    // Empty-tray corrections on the existing Tray Stock pools (dbo.TrayStock + dbo.TrayStockTransactions),
    // 2026-10-10. Pure rules -- the repositories apply them to values re-read under lock.
    //
    // ADJUSTMENT (TrayStock.Adjust): a compensating ledger row of the existing type 'Adjustment'
    //   (ReferenceType 'TrayAdjustment'), +/- whole trays with a mandatory reason. Never edits or
    //   deletes a ledger row; never below zero (also CK_TrayStock_NonNegative).
    // TRANSFER (dbo.TrayTransfers, 2026-10-10_TrayTransfers.sql), owner decision: trays leave at sending.
    //   send     Pending    source -q  'TransferOut'   (in transit: in neither pool)
    //   receive  Received   destination +q 'TransferIn'
    //   reject / cancel     source +q 'TransferReturn'
    public static class TrayTransferRules
    {
        public const string AdjustReferenceType = "TrayAdjustment";
        public const string TransferReferenceType = "TrayTransfer";

        public const string StatusPending = "Pending";
        public const string StatusReceived = "Received";
        public const string StatusRejected = "Rejected";
        public const string StatusCancelled = "Cancelled";

        public const string AdjustPermission = "TrayStock.Adjust";
        public const string TransferPermission = "TrayStock.Transfer";
        public const string ReceivePermission = "TrayStock.ReceiveTransfer";
        // 2026-10-10 (owner decision): receiving is LOCATION-AWARE -- the permission that may receive
        // or reject trays depends on the destination Area's type, and only in Areas the user is
        // assigned to (TrayAreaScope.IsReceivableBy). Full-access users receive anywhere.
        //   Mother Plant Area -> TrayStock.ReceiveTransfer          (Mother Plant Supervisor)
        //   Main Office Area  -> TrayStock.ReceiveTransferMainArea  (Sowing Supervisor -- the Main Area tray role)
        //   any other type    -> nobody but full-access users
        public const string ReceiveMainAreaPermission = "TrayStock.ReceiveTransferMainArea";
        public const string MotherPlantAreaType = "MotherPlant";

        public static string? ReceivePermissionFor(string? areaType) => areaType switch
        {
            MotherPlantAreaType => ReceivePermission,
            DirectSowingRules.MainOfficeAreaType => ReceiveMainAreaPermission,
            _ => null
        };

        public const int MaxReasonLength = 500;

        public static string StatusLabel(string status) => status switch
        {
            StatusPending => "In transit",
            StatusReceived => "Received",
            StatusRejected => "Rejected",
            StatusCancelled => "Cancelled",
            _ => status
        };

        // Adjustment: whole trays, not zero, a reason, never below zero.
        public static (bool Ok, string? Error) ValidateAdjustment(decimal delta, decimal currentBalance, string? reason, string? traySize)
        {
            if (!DirectSowingRules.IsValidCavityType(traySize))
                return (false, $"Cavity must be one of: {string.Join(", ", DirectSowingRules.CavityTypes)}.");
            if (delta == 0 || !DirectSowingRules.IsWholeNumber(delta))
                return (false, "Enter a whole number of trays to add (+) or remove (-); zero changes nothing.");
            if (string.IsNullOrWhiteSpace(reason))
                return (false, "A reason is required for every adjustment.");
            if (reason.Trim().Length > MaxReasonLength)
                return (false, $"The reason is too long ({MaxReasonLength} characters at most).");
            if (currentBalance + delta < 0)
                return (false, $"Only {QuantityFormat.Qty(currentBalance)} trays are recorded here; the balance cannot go below zero.");
            return (true, null);
        }

        public record Location(int AreaId, string? AreaType, bool AreaActive, int PolyhouseId, int? PolyhouseAreaId);

        // A new transfer. Locations are as re-read from the database (never as posted).
        public static (bool Ok, string? Error) ValidateTransfer(Location source, Location destination, string? traySize,
            decimal quantity, decimal sourceBalance, DateTime transferDate, DateTime todayIndia)
        {
            if (!DirectSowingRules.IsValidCavityType(traySize))
                return (false, $"Cavity must be one of: {string.Join(", ", DirectSowingRules.CavityTypes)}.");
            if (quantity <= 0 || !DirectSowingRules.IsWholeNumber(quantity))
                return (false, "Enter a whole number of trays greater than zero.");
            foreach (var (loc, side) in new[] { (source, "source"), (destination, "destination") })
            {
                if (!loc.AreaActive || loc.AreaType == OutletRules.AreaType)
                    return (false, $"The {side} must be an active, non-Outlet Area.");
                if (loc.PolyhouseAreaId != loc.AreaId)
                    return (false, $"The {side} Polyhouse does not belong to the {side} Area.");
            }
            if (source.PolyhouseId == destination.PolyhouseId)
                return (false, "The source and destination must be different Polyhouses.");
            if (quantity > sourceBalance)
                return (false, $"Only {QuantityFormat.Qty(sourceBalance)} empty {traySize} trays are in the source Polyhouse.");
            if (transferDate.Date > todayIndia.Date)
                return (false, LedgerDates.FutureDateMessage);
            return (true, null);
        }

        public static (bool Ok, string? Error) ValidateClose(string status, string? reason, string action)
        {
            if (status != StatusPending)
                return (false, $"This transfer is already {StatusLabel(status).ToLowerInvariant()} and cannot be {action}.");
            if (string.IsNullOrWhiteSpace(reason))
                return (false, $"Enter the reason it is {action}.");
            if (reason.Trim().Length > MaxReasonLength)
                return (false, $"The reason is too long ({MaxReasonLength} characters at most).");
            return (true, null);
        }
    }
}
