namespace PlantStockManager.Services
{
    // Sending Ready Stock (seedling trays) to Main Office or an Outlet
    // (dbo.InternalTransfers, StockType 'ReadyStock'). A Ready Stock row is
    // one whole sowing batch (UQ_ReadyStock_SeedSowing: one row per
    // SeedSowingId), so it moves as a whole batch -- never split -- and
    // only before anything has been reserved for a booking or dispatched
    // to a customer from it.
    public static class ReadyStockTransferRules
    {
        public static (bool Ok, string? Error) ValidateTransfer(
            decimal quantity, decimal batchQuantity, decimal reserved, decimal dispatched, int sourceAreaId, int? destinationAreaId)
        {
            if (reserved > 0 || dispatched > 0)
                return (false, $"This batch has {reserved:N0} trays reserved for bookings and {dispatched:N0} dispatched; it cannot be moved. Release the reservations first.");
            if (quantity <= 0 || !DirectSowingRules.IsWholeNumber(quantity))
                return (false, "Quantity must be a whole number greater than zero.");
            if (quantity != batchQuantity)
                return (false, $"The whole batch ({batchQuantity:N0} trays) must move together -- it cannot be split across two Areas.");
            if (!destinationAreaId.HasValue)
                return (false, "Choose where the trays are going.");
            if (destinationAreaId.Value == sourceAreaId)
                return (false, "The trays are already in that Area.");
            return (true, null);
        }
    }
}
