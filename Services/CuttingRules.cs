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
    public static class CuttingRules
    {
        // Main Office Cutting Stock feeds cutting sowing and pot production
        // for every production Area, so any user allowed to sow / pot may use
        // it; cuttings held in another Area are only for that Area's users.
        public static bool CanUseAsSource(bool isMainOfficeStock, bool canAccessStockArea)
            => isMainOfficeStock || canAccessStockArea;

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
                return (false, $"Only {available:N0} cuttings are available to send.");
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
                return (false, 0, $"Received quantity ({received:N0}) cannot be more than the {sent:N0} cuttings sent.");
            var loss = sent - received;
            if (loss > 0 && string.IsNullOrWhiteSpace(shortfallReason))
                return (false, loss, $"{loss:N0} cuttings are missing -- enter the reason.");
            return (true, loss, null);
        }
    }
}
