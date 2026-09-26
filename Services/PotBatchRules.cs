namespace PlantStockManager.Services
{
    // Cutting -> Pot Production Batch -> Daily Production -> READY -> Potted
    // Plant Stock. One cutting makes one pot.
    //
    //   Cuttings allocated  (taken out of Cutting Stock when the batch starts)
    //   Pots produced       = sum of the daily entries (each uses empty pots
    //                         from the batch Area's own stock)
    //   Unused cuttings     = allocated - produced   (returned to stock or wastage, at READY)
    //   Ready pots          (confirmed by the assigned supervisor) -> Potted Plant Stock
    //   Pot wastage         = produced - ready
    //   Complete loss       ready = 0: the batch is closed as Lost with a
    //                       mandatory loss reason (nothing reaches stock)
    public static class PotBatchRules
    {
        public const string InProduction = "InProduction";
        public const string Ready = "Ready";
        public const string Lost = "Lost";              // closed: complete loss, zero plants ready
        public const string Cancelled = "Cancelled";

        // Default pot production ratio: one rooted cutting makes one pot.
        public const int CuttingsPerPot = 1;

        public static bool IsClosed(string status) => status == Ready || status == Lost || status == Cancelled;
        public static string StatusFor(decimal readyQuantity) => readyQuantity == 0 ? Lost : Ready;

        public const string ReturnedToStock = "ReturnedToStock";
        public const string UnusedAsWastage = "Wastage";

        // How close a batch is to its expected ready date.
        public const string DueUpcoming = "Upcoming";
        public const string DueSoon = "Due soon";
        public const string DueToday = "Due today";
        public const string DueOverdue = "Overdue";
        public const int DueSoonDays = 7;

        public static (bool Ok, string? Error) ValidateCreate(
            decimal cuttingAllocated, decimal cuttingAvailable, DateTime startDate, DateTime expectedReadyDate,
            int? supervisorId, int creatorId, IReadOnlyCollection<int> eligibleSupervisorIds)
        {
            if (cuttingAllocated <= 0 || !DirectSowingRules.IsWholeNumber(cuttingAllocated))
                return (false, "Cuttings allocated must be a whole number greater than zero.");
            if (cuttingAllocated > cuttingAvailable)
                return (false, $"Only {cuttingAvailable:N0} cuttings are available in this Cutting Stock.");
            if (expectedReadyDate.Date < startDate.Date)
                return (false, "Expected Ready Date cannot be before the production start date.");
            if (!supervisorId.HasValue || supervisorId.Value <= 0)
                return (false, "Choose the supervisor who will confirm this batch READY.");
            if (supervisorId.Value == creatorId)
                return (false, "You cannot assign yourself: the batch must be confirmed READY by another supervisor.");
            if (!eligibleSupervisorIds.Contains(supervisorId.Value))
                return (false, "The selected supervisor is not a Mother Plant Supervisor of this Area.");
            return (true, null);
        }

        public static (bool Ok, string? Error) ValidateEntry(
            string status, decimal quantity, decimal alreadyProduced, decimal cuttingAllocated,
            decimal emptyPotsAvailable, DateTime productionDate, DateTime startDate, DateTime today)
        {
            if (status != InProduction)
                return (false, "Production can only be entered while the batch is In Production.");
            if (quantity <= 0 || !DirectSowingRules.IsWholeNumber(quantity))
                return (false, "Pots produced must be a whole number greater than zero.");
            if (productionDate.Date < startDate.Date)
                return (false, "Production date cannot be before the batch start date.");
            if (productionDate.Date > today.Date)
                return (false, "Production date cannot be in the future.");
            var remaining = cuttingAllocated - alreadyProduced;
            if (quantity > remaining)
                return (false, $"Only {remaining:N0} cuttings are left in this batch ({alreadyProduced:N0} of {cuttingAllocated:N0} already potted).");
            if (quantity > emptyPotsAvailable)
                return (false, $"Only {emptyPotsAvailable:N0} empty pots of this size are available in this Area. Issue more pots to the Area first.");
            return (true, null);
        }

        // READY (ready >= 1) or complete loss (ready = 0). Lost pots and a
        // complete loss always need a reason; cuttings never potted are either
        // returned to Cutting Stock or recorded as wastage.
        public static (bool Ok, decimal PotWastage, decimal UnusedCuttings, string? Error) ValidateReady(
            string status, decimal readyQuantity, decimal produced, decimal cuttingAllocated,
            string? wastageReason, string? unusedCuttingAction)
        {
            if (status != InProduction)
                return (false, 0, 0, "This batch is already closed.");
            if (readyQuantity < 0 || !DirectSowingRules.IsWholeNumber(readyQuantity))
                return (false, 0, 0, "Ready pots must be a whole number (enter 0 if every plant was lost).");
            if (readyQuantity > produced)
                return (false, 0, 0, $"Ready pots ({readyQuantity:N0}) cannot be more than the {produced:N0} pots produced.");
            var wastage = produced - readyQuantity;
            var unused = cuttingAllocated - produced;
            if (readyQuantity == 0 && !DirectSowingRules.IsValidWastageReason(wastageReason))
                return (false, wastage, unused, "No plants are ready -- choose the loss reason to close the batch as a complete loss.");
            if (wastage > 0 && !DirectSowingRules.IsValidWastageReason(wastageReason))
                return (false, wastage, unused, $"{wastage:N0} pots were lost -- choose the wastage reason.");
            if (unused > 0 && unusedCuttingAction != ReturnedToStock && unusedCuttingAction != UnusedAsWastage)
                return (false, wastage, unused, $"{unused:N0} allocated cuttings were not potted -- return them to Cutting Stock or record them as wastage.");
            return (true, wastage, unused, null);
        }

        // Status shown on the batch list and the dashboard alert.
        public static string ReadinessStatus(string status, DateTime expectedReadyDate, DateTime today)
        {
            if (status == Ready) return "Ready";
            if (status == Lost) return "Complete loss";
            if (status == Cancelled) return "Cancelled";
            var days = (expectedReadyDate.Date - today.Date).Days;
            if (days < 0) return DueOverdue;
            if (days == 0) return DueToday;
            if (days <= DueSoonDays) return DueSoon;
            return DueUpcoming;
        }
    }
}
