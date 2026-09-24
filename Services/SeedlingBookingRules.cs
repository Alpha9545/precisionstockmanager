using PlantStockManager.Models;

namespace PlantStockManager.Services
{
    // Phase C: the pure (database-free) rules of
    //   Booking -> Reserve Ready Stock -> Batch Allocation -> Substitution -> Dispatch.
    // Data/SeedlingFulfilmentRepository.cs applies them inside its locked SQL
    // transactions; the pages use the same functions for their messages.
    // Everything here is unit-tested (no second copy of any rule).
    public static class SeedlingBookingRules
    {
        public const string SourceLegacy = "Legacy";
        public const string SourceReadyStock = "ReadyStock";
        public const string InsufficientStockMessage = "Insufficient available Ready Stock.";

        // Plants are whole units.
        public static bool IsWholePositive(decimal quantity)
            => quantity > 0 && quantity == decimal.Truncate(quantity);

        // Default reservation: the booked variety only, oldest batch first
        // (sowing date, then approval date, then batch id), only what is
        // actually available. All-or-nothing: if the batches cannot cover the
        // requested quantity nothing is planned.
        public static (bool Ok, List<(int ReadyStockId, decimal Quantity)> Plan, string? Error) PlanFifo(
            IEnumerable<ReadyBatchOption> batches, int speciesId, decimal requested)
        {
            var plan = new List<(int, decimal)>();
            if (!IsWholePositive(requested))
                return (false, plan, "Quantity to reserve must be a whole number greater than zero.");

            var candidates = batches
                .Where(b => b.SpeciesId == speciesId && b.AvailableQuantity > 0)
                .OrderBy(b => b.SowingDate)
                .ThenBy(b => b.FirstConfirmationDate ?? DateTime.MaxValue)
                .ThenBy(b => b.ReadyStockId)
                .ToList();

            var available = candidates.Sum(b => b.AvailableQuantity);
            if (available < requested)
                return (false, plan, $"{InsufficientStockMessage} Available {available:N0}, requested {requested:N0}.");

            var left = requested;
            foreach (var b in candidates)
            {
                if (left <= 0) break;
                var take = Math.Min(left, decimal.Truncate(b.AvailableQuantity));
                if (take <= 0) continue;
                plan.Add((b.ReadyStockId, take));
                left -= take;
            }
            if (left > 0)
                return (false, new List<(int, decimal)>(), $"{InsufficientStockMessage} Available {available - left:N0} whole plants, requested {requested:N0}.");
            return (true, plan, null);
        }

        // Releases are taken from the NEWEST allocation lines first, so the
        // oldest batches stay reserved (mirror image of FIFO reservation).
        public static List<(int AllocationId, decimal Quantity)> PlanRelease(
            IEnumerable<(int AllocationId, decimal OpenQuantity)> lines, decimal toRelease)
        {
            var plan = new List<(int, decimal)>();
            var left = toRelease;
            foreach (var l in lines.Where(l => l.OpenQuantity > 0).OrderByDescending(l => l.AllocationId))
            {
                if (left <= 0) break;
                var take = Math.Min(left, l.OpenQuantity);
                plan.Add((l.AllocationId, take));
                left -= take;
            }
            return plan;
        }

        // Revision of quantity and/or variety.
        //   - never below what has already been dispatched;
        //   - the variety can only change while nothing has been dispatched;
        //   - reservations above the new need are released (newest first).
        public static (bool Ok, decimal ReleaseQuantity, string? Error) ValidateRevision(
            decimal dispatched, decimal reserved, int newQuantity, bool varietyChanged)
        {
            if (newQuantity < 1)
                return (false, 0, "The revised quantity must be at least 1.");
            if (newQuantity < dispatched)
                return (false, 0, $"The revised quantity ({newQuantity:N0}) cannot be below what has already been dispatched ({dispatched:N0}).");
            if (varietyChanged && dispatched > 0)
                return (false, 0, "The variety cannot be changed after plants have been dispatched. Revise the quantity and add the new variety as a split booking instead.");
            var release = varietyChanged ? reserved : Math.Max(0, reserved + dispatched - newQuantity);
            return (true, release, null);
        }

        // Batch allocation at the Dispatch stage. Same variety = normal
        // allocation. Another variety of the SAME species (plant type) =
        // substitution, which needs a reason. Anything else is refused.
        public static (bool Ok, bool IsSubstitution, string? Error) ValidateBatchChoice(
            int? bookedSpeciesId, int bookedPlantTypeId, int batchSpeciesId, int batchPlantTypeId, string? substitutionReason)
        {
            if (bookedSpeciesId.HasValue && batchSpeciesId == bookedSpeciesId.Value)
                return (true, false, null);
            if (batchPlantTypeId != bookedPlantTypeId)
                return (false, false, "Only a variety of the same species can be substituted.");
            if (string.IsNullOrWhiteSpace(substitutionReason))
                return (false, true, "A reason is required when a different variety is substituted.");
            if (substitutionReason.Trim().Length > 500)
                return (false, true, "The substitution reason is too long (max 500 characters).");
            return (true, true, null);
        }

        public static string? ValidateDispatchLine(decimal openQuantity, decimal quantity)
        {
            if (!IsWholePositive(quantity))
                return "Dispatch quantities must be whole numbers greater than zero.";
            if (quantity > openQuantity)
                return $"Dispatch quantity ({quantity:N0}) exceeds the quantity allocated on this batch ({openQuantity:N0}).";
            return null;
        }

        // Top-level status keeps the existing values (Pending/Completed/Cancelled).
        public static string StatusAfterDispatch(int bookedQuantity, decimal dispatchedQuantity)
            => dispatchedQuantity >= bookedQuantity ? "Completed" : "Pending";

        // Detailed stage shown under the existing status.
        public static string Stage(string? status, int quantity, decimal reserved, decimal dispatched, bool legacyFulfilled, bool hadReservations)
        {
            switch (status)
            {
                case "Cancelled":
                    return hadReservations ? "Cancelled – reservation released" : "Cancelled";
                case "Completed":
                    if (legacyFulfilled) return "Completed – legacy Inventory fulfilment";
                    return dispatched >= quantity ? "Fully dispatched" : "Completed";
                default:
                    if (dispatched > 0)
                        return reserved > 0 ? "Partially dispatched – remainder reserved" : "Partially dispatched";
                    if (reserved > 0)
                        return reserved >= quantity ? "Reserved" : "Partially reserved";
                    return "Pre-booking – not reserved";
            }
        }
    }
}
