namespace PlantStockManager.Services
{
    // Cocopeat / Pitmos transfers between Polyhouses (2026-10-23; Database/Migrations/2026-10-25_MaterialStockTransfers.sql).
    // Pure rules -- MaterialTransferRepository applies them to values re-read from the database under lock.
    //
    // A transfer is ONE atomic move (unlike empty trays, which wait in transit for a receiver): in one transaction the
    // source balance loses the quantity (TRANSFER_OUT) and the destination balance gains it (TRANSFER_IN), both tagged with
    // the dbo.MaterialTransfers row. Quantity is in the material's own stock unit (kg, the usable kg the stock page shows);
    // the Add Material conversion factor is NEVER applied to a transfer. A wrong transfer is corrected by a transfer back.
    //
    // Locations: a Polyhouse of an active Main Office or Mother Plant Area (never an Outlet), as for tray transfers. Material
    // is USED only by Main Office sowings, so stock at a Mother Plant Polyhouse can only be held or transferred again.
    public static class MaterialTransferRules
    {
        public const string TransferOut = "TRANSFER_OUT";
        public const string TransferIn = "TRANSFER_IN";
        public const string ReferenceType = "MaterialTransfer";
        public const string CodePrefix = "MT";
        public const string Permission = "MaterialStock.Transfer";
        public const int MaxRemarksLength = MaterialStockRules.MaxRemarksLength;

        public static readonly IReadOnlyList<string> AreaTypes = new[] { MaterialStockRules.MainOfficeAreaType, TrayTransferRules.MotherPlantAreaType };

        public const string NotAuthorizedSourceMessage = "You are not authorized to transfer material from the selected Area.";
        public const string NoViewerMessage = "No one who can see material stock is assigned to the destination Area. Choose another destination (an administrator can make exceptional transfers).";
        public const string ExpiredFormMessage = "This form has expired. Reload the page and enter the transfer again.";
        public const string SamePolyhouseMessage = "The source and destination must be different Polyhouses.";

        public static bool IsEligibleAreaType(string? areaType) => areaType != null && AreaTypes.Contains(areaType);

        // One side of a transfer, as re-read from the database (never as posted).
        public record Location(int AreaId, string? AreaType, bool AreaActive, int PolyhouseId, int? PolyhouseAreaId);

        public static string? ValidateLocations(Location? source, Location? destination)
        {
            if (source == null || destination == null)
                return "The selected Polyhouse does not exist.";
            foreach (var (loc, side) in new[] { (source, "source"), (destination, "destination") })
            {
                if (loc.PolyhouseAreaId != loc.AreaId)
                    return $"The {side} Polyhouse does not belong to the {side} Area.";
                if (!loc.AreaActive || !IsEligibleAreaType(loc.AreaType))
                    return $"The {side} must be a Polyhouse of an active Main Office or Mother Plant Area.";
            }
            if (source.PolyhouseId == destination.PolyhouseId)
                return SamePolyhouseMessage;
            return null;
        }

        // The form's own values, before anything is read: quantity (stock unit, at most 3 decimals), remark, date.
        public static string? ValidateRequest(decimal quantity, string? remarks, DateTime transferDate, DateTime todayIndia)
        {
            var qtyError = MaterialStockRules.ValidateQuantity(quantity);
            if (qtyError != null)
                return qtyError;
            if (MaterialStockRules.Trimmed(remarks)?.Length > MaxRemarksLength)
                return $"The remark is too long ({MaxRemarksLength} characters at most).";
            if (transferDate == default)
                return "Date is required.";
            if (transferDate.Date > todayIndia.Date)
                return LedgerDates.FutureDateMessage;
            return null;
        }

        // Ledger remarks of the two sides.
        public static string OutRemark(string code, string areaName, string polyhouseName) => $"Transfer {code} to {areaName} / {polyhouseName}";
        public static string InRemark(string code, string areaName, string polyhouseName) => $"Transfer {code} from {areaName} / {polyhouseName}";

        // Lock order of two balances: always the same for the same pair, whichever side is the source -- so a transfer
        // A -> B and a simultaneous B -> A take their locks in the same order and never deadlock.
        public static bool LockSourceFirst(int sourceAreaId, int sourcePolyhouseId, int destinationAreaId, int destinationPolyhouseId)
            => (sourceAreaId, sourcePolyhouseId).CompareTo((destinationAreaId, destinationPolyhouseId)) <= 0;
    }
}
