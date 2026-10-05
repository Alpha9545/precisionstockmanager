namespace PlantStockManager.Services
{
    // Mother Plant -> Cutting Production -> Delivery -> Main Office
    // Confirmation -> Cutting Stock. Pure rules; the repositories apply them
    // again under their row locks.
    //
    // Traceability of one delivery:
    //   Delivered (sent)   = InternalTransfers.Quantity
    //   Received           = InternalTransfers.ConfirmedQuantity  -> Main Office Cutting Stock
    //   Transit loss       = Delivered - Received                 -> Wastage ('TransitLoss')
    // Where the cuttings of one Cutting Entry go (dbo.CuttingProductions.DestinationType).
    //   MainOffice     the harvest is recorded in the Mother Plant's Area Cutting Stock
    //                  AND a Cutting delivery to Main Office is created in the same
    //                  transaction (PendingConfirmation, quantity held In-Transit); the
    //                  cuttings become Main Office stock only when Main Office confirms
    //                  what it received (existing ConfirmReceipt), any shortfall being
    //                  booked as transit loss.
    //   PotProduction  the harvest stays in the Mother Plant's own Area Cutting Stock,
    //                  which that Area's Pot Production / Cutting Tray Sowing already draws
    //                  from; no transfer, nothing appears as Main Office stock.
    // Rows recorded before destinations existed have NULL (unknown), never a guess.
    public static class CuttingDestination
    {
        public const string MainOffice = "MainOffice";
        public const string PotProduction = "PotProduction";

        public static bool IsValid(string? destinationType)
            => destinationType == MainOffice || destinationType == PotProduction;

        public static string Label(string? destinationType) => destinationType switch
        {
            MainOffice => "Main Office",
            PotProduction => "Use for Pot Production",
            _ => "-"
        };
    }

    public static class CuttingRules
    {
        // The destination is mandatory. Returns the Area the cuttings are destined for:
        //   PotProduction -> the Mother Plant's own Area (a posted Main Office Area is ignored)
        //   MainOffice    -> an ACTIVE Main Office Area other than the source Area; when
        //                    exactly one exists it is chosen automatically, otherwise the
        //                    caller must have picked one of them.
        // Re-applied by CuttingProductionRepository under its transaction: never trust the form.
        public static (bool Ok, int? DestinationAreaId, string? Error) ResolveDestination(
            string? destinationType, int? requestedMainOfficeAreaId, int sourceAreaId, IReadOnlyCollection<int> activeMainOfficeAreaIds)
        {
            if (!CuttingDestination.IsValid(destinationType))
                return (false, null, "Choose where the cuttings go: Main Office or Use for Pot Production.");
            if (destinationType == CuttingDestination.PotProduction)
                return (true, sourceAreaId, null);

            var candidates = activeMainOfficeAreaIds.Where(id => id != sourceAreaId).Distinct().ToList();
            if (candidates.Count == 0)
                return (false, null, "There is no active Main Office Area to send the cuttings to.");
            if (requestedMainOfficeAreaId.HasValue && requestedMainOfficeAreaId.Value > 0)
                return candidates.Contains(requestedMainOfficeAreaId.Value)
                    ? (true, requestedMainOfficeAreaId.Value, null)
                    : (false, null, "The selected destination is not an active Main Office Area.");
            return candidates.Count == 1
                ? (true, candidates[0], null)
                : (false, null, "Choose the Main Office Area the cuttings are going to.");
        }

        // AREA ISOLATION of Cutting Stock (Correction #3). A Cutting Stock pool
        // belongs to ONE Area (dbo.CuttingStock.AreaId) and may only be used --
        // listed as the source of a Cutting Tray Sowing or a Pot Batch, or drawn
        // on for the extra cuttings of a Supervisor Approval -- by a user who may
        // access THAT Area under the ordinary Area rules (AreaAccessService: the
        // user's UserRoles.AreaId assignments, or full Area access for
        // Admin / Management / MainOfficeOfficer / System Administrator).
        // Main Office stock is simply the Main Office Area's stock: users of the
        // Main Office Area and full-access users use it; a production Area's
        // users (e.g. Green Bless Nursery) do NOT, and never see it as their own.
        // (Before Correction #3 Main Office pools were usable by every Area's
        // users; records made under that rule are history and stay valid.)
        public static bool CanUseAsSource(bool canAccessStockArea) => canAccessStockArea;

        public static (bool Ok, string? Error) ValidateProductionQuantity(decimal quantity)
        {
            if (quantity <= 0)
                return (false, "Cutting quantity must be greater than zero.");
            if (!DirectSowingRules.IsWholeNumber(quantity))
                return (false, "Cutting quantity must be a whole number.");
            return (true, null);
        }

        public static (bool Ok, string? Error) ValidateDelivery(decimal quantity, decimal available)
        {
            var (ok, error) = ValidateProductionQuantity(quantity);
            if (!ok)
                return (false, error!.Replace("Cutting quantity", "Delivery quantity"));
            if (quantity > available)
                return (false, $"Only {QuantityFormat.Qty(available)} cuttings are available to send.");
            return (true, null);
        }

        // Main Office confirms what actually arrived. The shortfall is recorded
        // as transit loss (wastage) -- it left the sending Area and never
        // reached Main Office.
        public static (bool Ok, decimal TransitLoss, string? Error) ConfirmDelivery(decimal sent, decimal received, string? shortfallReason)
        {
            if (received < 0)
                return (false, 0, "Received quantity cannot be negative.");
            if (!DirectSowingRules.IsWholeNumber(received))
                return (false, 0, "Received quantity must be a whole number.");
            if (received > sent)
                return (false, 0, $"Received quantity ({QuantityFormat.Qty(received)}) cannot be more than the {QuantityFormat.Qty(sent)} cuttings sent.");
            var loss = sent - received;
            if (loss > 0 && string.IsNullOrWhiteSpace(shortfallReason))
                return (false, loss, $"{QuantityFormat.Qty(loss)} cuttings are missing -- enter the reason.");
            return (true, loss, null);
        }
    }
}
