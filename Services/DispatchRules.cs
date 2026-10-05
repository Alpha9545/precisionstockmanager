namespace PlantStockManager.Services
{
    // Direct customer sale of Potted Plant Stock (DispatchRepository.DirectSaleAsync).
    // Not tied to any particular Area type -- an authorized user (checked by the
    // caller's own permission/Area-access policy) may sell READY stock from any
    // active Area they can reach. Only readiness (available quantity) and the
    // Area being active are business rules here.
    public static class DispatchRules
    {
        public static (bool Ok, string? Error) ValidateDirectSale(
            decimal quantity, string? customerName, bool areaExists, bool areaActive, decimal available)
        {
            if (quantity <= 0 || !DirectSowingRules.IsWholeNumber(quantity))
                return (false, "Quantity must be a whole number greater than zero.");
            if (string.IsNullOrWhiteSpace(customerName))
                return (false, "Customer name is required.");
            if (!areaExists || !areaActive)
                return (false, "This stock does not belong to an active Area.");
            if (quantity > available)
                return (false, $"Only {QuantityFormat.Qty(available)} plants are available (not reserved or in transit).");
            return (true, null);
        }

        // ---- Potted Plant Booking -> Dispatch (fulfilment of a booking) --------------------------------
        // A booking reserves stock when it is made (Reserved goes up, Physical does not move). Each
        // dispatch (partial or full) hands over plants: Reserved AND Physical both go down by the
        // dispatched quantity. Statuses: Pending -> PartiallyDispatched -> Dispatched (Cancelled aside).
        //   Booked = Quantity; Fulfilled = DispatchedQuantity; Remaining = Booked - Fulfilled.
        public const string StatusPending = "Pending";
        public const string StatusPartiallyDispatched = "PartiallyDispatched";
        public const string StatusDispatched = "Dispatched";

        public static bool CanDispatchStatus(string? status)
            => status == StatusPending || status == StatusPartiallyDispatched;

        // The status a booking has once `dispatchedTotal` of `booked` has been handed over.
        public static string StatusAfterDispatch(decimal booked, decimal dispatchedTotal)
            => dispatchedTotal >= booked ? StatusDispatched : StatusPartiallyDispatched;

        // Every quantity rule of one dispatch, applied by the repository under the booking's lock (the
        // page uses the same function for its messages): plants are whole units, greater than zero,
        // only an open (Pending / PartiallyDispatched) booking, and never more than what is still
        // remaining on the booking.
        public static (bool Ok, decimal Remaining, string? Error) ValidateBookingDispatch(
            string? status, decimal booked, decimal dispatchedSoFar, decimal quantity)
        {
            var remaining = booked - dispatchedSoFar;
            if (quantity <= 0 || !DirectSowingRules.IsWholeNumber(quantity))
                return (false, remaining, "Dispatch quantity must be a whole number greater than zero.");
            if (!CanDispatchStatus(status))
                return (false, remaining, $"This Booking is '{status}' and cannot be dispatched (only Pending or PartiallyDispatched bookings can be dispatched).");
            if (quantity > remaining)
                return (false, remaining, $"Dispatch quantity ({QuantityFormat.Qty(quantity)}) exceeds this Booking's remaining quantity ({QuantityFormat.Qty(remaining)}).");
            return (true, remaining, null);
        }

        // Double-submit / stale-form guard. The dispatch form remembers how much of the booking had been
        // dispatched when it was opened; if that changed since (the same form submitted twice, or another
        // user dispatched meanwhile) the dispatch is refused instead of being applied a second time.
        public static (bool Ok, string? Error) CheckFormIsCurrent(decimal? expectedDispatched, decimal dispatchedSoFar)
            => !expectedDispatched.HasValue || expectedDispatched.Value == dispatchedSoFar
                ? (true, null)
                : (false, $"This Booking has changed since you opened the form ({QuantityFormat.Qty(dispatchedSoFar)} already dispatched, not {QuantityFormat.Qty(expectedDispatched.Value)}). "
                          + "Nothing was dispatched: reload the page, check the remaining quantity and dispatch again.");
    }
}
