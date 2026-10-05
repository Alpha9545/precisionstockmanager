using PlantStockManager.Models;

namespace PlantStockManager.Services
{
    // Add Empty Pot Stock (replaces the retired Main Office Purchase -> Issue
    // workflow): the Mother Plant Supervisor adds empty pots directly to one
    // of their own Areas. Stock is kept per (Pot Size, Area) in
    // dbo.EmptyPotInventory, with every movement in
    // dbo.EmptyPotInventoryTransactions ('StockIn', ReferenceType
    // 'AreaStockEntry', TransactionDate = the date entered on the form).
    public static class EmptyPotStockRules
    {
        public const string AreaStockEntryReferenceType = "AreaStockEntry";

        public const string AreaNotAllowedMessage =
            "Choose one of your own active production Areas. Empty pots cannot be added to a Main Office Area.";

        public static (bool Ok, string? Error) ValidateAddStock(string? potSize, decimal quantity, DateTime stockDate, DateTime today)
        {
            if (string.IsNullOrWhiteSpace(potSize))
                return (false, "Pot Size is required.");
            if (quantity <= 0 || !DirectSowingRules.IsWholeNumber(quantity))
                return (false, "Quantity must be a whole number greater than zero.");
            if (stockDate == default)
                return (false, "Date is required.");
            if (stockDate.Date > today.Date)
                return (false, "Date cannot be in the future.");
            return (true, null);
        }

        // An Area may receive empty pots when it exists, is active and is not a
        // Main Office Area (the Office store is no longer stocked directly).
        public static bool IsAllowedStockArea(bool exists, string? areaType, bool isActive)
            => exists && isActive && !string.Equals(areaType, DirectSowingRules.MainOfficeAreaType, StringComparison.Ordinal);

        // The Area dropdown: active non-Main-Office Areas, limited to the
        // user's assigned Areas unless the user has full Area access.
        public static List<Area> SelectableAreas(IEnumerable<Area> allAreas, bool hasFullAreaAccess, IReadOnlyCollection<int> assignedAreaIds)
            => allAreas
                .Where(a => IsAllowedStockArea(true, a.AreaType, a.IsActive))
                .Where(a => hasFullAreaAccess || assignedAreaIds.Contains(a.Id))
                .OrderBy(a => a.Name)
                .ToList();
    }
}
