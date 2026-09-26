namespace PlantStockManager.Services
{
    // Phase E: the Outlet module. An Outlet is an Area with AreaType =
    // 'Outlet' -- these rules never invent a second stock system; they
    // validate the same PottedPlantStock/ReadyStock quantities every other
    // workflow already uses, just for a customer transaction that can cover
    // several potted and/or tray items at once.
    public static class OutletRules
    {
        public const string AreaType = "Outlet";

        public static bool IsActiveOutlet(string? areaType, bool areaIsActive)
            => areaIsActive && string.Equals(areaType, AreaType, StringComparison.Ordinal);

        public static (bool Ok, string? Error) ValidatePurchase(decimal quantity, string? supplierName)
        {
            if (quantity <= 0 || !DirectSowingRules.IsWholeNumber(quantity))
                return (false, "Quantity must be a whole number greater than zero.");
            if (string.IsNullOrWhiteSpace(supplierName))
                return (false, "Supplier name is required.");
            return (true, null);
        }

        // One line of a multi-item Direct Sale, Booking or Wastage record --
        // pots for 'Potted', WHOLE TRAYS for 'Tray'. Never a converted
        // seedling count: that conversion only happens where the ReadyStock
        // ledger itself is touched, using the cavity from the locked row.
        public static (bool Ok, string? Error) ValidateItemQuantity(decimal quantity)
        {
            if (quantity <= 0 || !DirectSowingRules.IsWholeNumber(quantity))
                return (false, "Quantity must be a whole number greater than zero.");
            return (true, null);
        }

        public static (bool Ok, string? Error) ValidateCustomer(string? customerName)
            => string.IsNullOrWhiteSpace(customerName) ? (false, "Customer name is required.") : (true, null);

        // A Direct Sale needs at least one item, and the WHOLE sale fails
        // together if any single item cannot be fulfilled (no partial
        // deduction) -- checked against each item's own locked, current
        // available quantity by the caller, one at a time, all in the same
        // transaction; the first failure rolls everything back.
        public static (bool Ok, string? Error) ValidateSaleHasItems(int itemCount)
            => itemCount > 0 ? (true, null) : (false, "Add at least one item.");

        public static (bool Ok, string? Error) ValidateSaleItemAvailable(decimal quantity, decimal available)
            => quantity > available
                ? (false, $"Only {available:N0} available (requested {quantity:N0}).")
                : (true, null);
    }

    // A Direct Sale/booking/wastage item's "which Outlet is this stock
    // actually held at" check -- can never use another Outlet's stock, even
    // if the item's stock id is otherwise valid.
    public static class OutletStockOwnershipRules
    {
        public static (bool Ok, string? Error) ValidateSameArea(int stockAreaId, int outletAreaId)
            => stockAreaId == outletAreaId
                ? (true, null)
                : (false, "That stock does not belong to this Outlet.");
    }

    // Customer booking: reserve now, collect (fully or partially) later,
    // cancel releases the reservation -- the same Physical/Reserved/
    // Available shape every other stock in this app already uses, for both
    // potted plants and trays.
    public static class OutletBookingRules
    {
        public const string Pending = "Pending";
        public const string PartiallyCollected = "PartiallyCollected";
        public const string Completed = "Completed";
        public const string Cancelled = "Cancelled";

        public static bool IsClosed(string status) => status == Completed || status == Cancelled;

        public static (bool Ok, string? Error) ValidateCollect(string bookingStatus, decimal requested, decimal openQuantity)
        {
            if (IsClosed(bookingStatus))
                return (false, "This booking is closed.");
            if (requested <= 0 || !DirectSowingRules.IsWholeNumber(requested))
                return (false, "Quantity must be a whole number greater than zero.");
            if (requested > openQuantity)
                return (false, $"Only {openQuantity:N0} of this item is still open.");
            return (true, null);
        }

        public static (bool Ok, string? Error) ValidateCancel(string bookingStatus)
            => IsClosed(bookingStatus) ? (false, "This booking is already closed.") : (true, null);

        // The header's own status, derived from where every item's
        // collection stands -- recomputed after every collect.
        public static string StatusAfterCollect(decimal totalQuantity, decimal totalCollected)
        {
            if (totalCollected <= 0) return Pending;
            return totalCollected >= totalQuantity ? Completed : PartiallyCollected;
        }
    }

    // The Outlet's own record of losing stock -- a DIFFERENT concept from
    // nursery production wastage (never an automatic percentage).
    public static class OutletWastageRules
    {
        public static readonly IReadOnlyList<string> Reasons =
            new[] { "Damaged", "Died / Wilted", "Pest or Disease", "Breakage", "Other" };

        public static bool IsValidReason(string? reason)
            => reason != null && Reasons.Contains(reason, StringComparer.Ordinal);

        public static (bool Ok, string? Error) Validate(decimal quantity, decimal available, string? reason)
        {
            if (quantity <= 0 || !DirectSowingRules.IsWholeNumber(quantity))
                return (false, "Quantity must be a whole number greater than zero.");
            if (!IsValidReason(reason))
                return (false, $"Choose a reason: {string.Join(", ", Reasons)}.");
            if (quantity > available)
                return (false, $"Only {available:N0} available -- wastage cannot exceed what is physically there.");
            return (true, null);
        }
    }
}
