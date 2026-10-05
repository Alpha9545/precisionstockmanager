using System.Security.Claims;
using PlantStockManager.Authorization;

namespace PlantStockManager.Services
{
    // Cutting -> Pot Production Batch -> Daily Production -> READY -> Potted
    // Plant Stock. One cutting makes one pot.
    //
    //   Cuttings allocated  (taken out of Cutting Stock when the batch starts)
    //   Pots produced       = sum of the daily entries (each uses empty pots
    //                         from the batch Area's own stock)
    //   Unused cuttings     = allocated - produced   (returned to stock or wastage, at READY)
    //   Ready pots          (confirmed by any authorized user assigned to the
    //                         batch Area -- see CanConfirmReady) -> Potted Plant Stock
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

        // ---- READY confirmation: who may do it ----------------------------
        //
        // ANY user who (a) holds a Pot Production permission and (b) is
        // assigned to the batch's own Area may confirm it READY. Nothing else:
        //   * NOT tied to the "Mother Plant Supervisor" role (or any role name);
        //   * the batch creator may confirm their own batch;
        //   * the batch Area is the boundary -- a user assigned to Area A
        //     cannot confirm a batch of Area B unless also assigned to B.
        //
        // (a) is the SAME rule the page map already applies to every POST on
        // the batch page (Details "Write" policy) -- read from there so there is
        // one source of truth. (b) is strict Area ASSIGNMENT (an "AreaAccess"
        // claim, i.e. a dbo.UserRoles row with that AreaId); the cross-Area
        // full-access convention of AreaAccessService.CanAccessArea is
        // deliberately NOT applied to the confirmation itself.
        public static string ReadyConfirmPermissions
            => FeatureAuthorizationConventions.GetRule("/Production/PotBatch/Details").Write
               ?? FeatureAuthorizationConventions.GetRule("/Production/PotBatch/Details").Read;

        // Claims-based check (the signed-in user, from the auth cookie).
        public static bool CanConfirmReady(ClaimsPrincipal user, int batchAreaId)
            => user.Identity?.IsAuthenticated == true
               && user.GetUserId().HasValue
               && user.HasPermission(ReadyConfirmPermissions)
               && user.HasClaim(AreaAccessService.AreaAccessClaimType, batchAreaId.ToString());

        // One dbo.UserRoles row of a user: the Area it is scoped to (null =
        // no Area) and the permission codes the row's role carries.
        public sealed record ReadyConfirmerGrant(int UserId, string UserName, int? AreaId, bool IsActive, IReadOnlyCollection<string> PermissionCodes);

        // DB-side (authoritative) check used inside the READY transaction, and
        // mirrored by TR_PotBatches_Update: an ACTIVE user with a role
        // assignment for the batch Area (whatever the role is called).
        public static bool IsActiveAssignedToArea(IEnumerable<ReadyConfirmerGrant> grants, int userId, int areaId)
            => grants.Any(g => g.UserId == userId && g.IsActive && g.AreaId == areaId);

        // The "Ready Confirmation By" list for an Area: active users assigned
        // to that Area who hold a Pot Production permission. Permission codes
        // are pooled across ALL of a user's role rows, exactly as the login
        // claims are (permission from any role, Area from any assignment).
        public static List<(int UserId, string UserName)> EligibleReadyConfirmers(
            IEnumerable<ReadyConfirmerGrant> grants, int areaId, string requiredPermissions)
        {
            var required = PermissionPolicy.Split(requiredPermissions);
            return grants
                .Where(g => g.IsActive)
                .GroupBy(g => g.UserId)
                .Where(u => u.Any(g => g.AreaId == areaId)
                            && u.SelectMany(g => g.PermissionCodes).Any(c => required.Contains(c, StringComparer.OrdinalIgnoreCase)))
                .Select(u => (u.Key, u.First().UserName))
                .OrderBy(u => u.Item2, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        // readyConfirmerId is the "Ready Confirmation By" choice: a user who is
        // eligible for the Area (see EligibleReadyConfirmers) -- the batch
        // creator included. It records who is expected to confirm; it does not
        // limit who may (see CanConfirmReady).
        public static (bool Ok, string? Error) ValidateCreate(
            decimal cuttingAllocated, decimal cuttingAvailable, DateTime startDate, DateTime expectedReadyDate,
            int? readyConfirmerId, IReadOnlyCollection<int> eligibleReadyConfirmerIds)
        {
            if (cuttingAllocated <= 0 || !DirectSowingRules.IsWholeNumber(cuttingAllocated))
                return (false, "Cuttings allocated must be a whole number greater than zero.");
            if (cuttingAllocated > cuttingAvailable)
                return (false, $"Only {QuantityFormat.Qty(cuttingAvailable)} cuttings are available in this Cutting Stock.");
            if (expectedReadyDate.Date < startDate.Date)
                return (false, "Expected Ready Date cannot be before the production start date.");
            if (!readyConfirmerId.HasValue || readyConfirmerId.Value <= 0)
                return (false, "Choose who will confirm this batch READY.");
            if (!eligibleReadyConfirmerIds.Contains(readyConfirmerId.Value))
                return (false, "The selected user is not authorized to confirm READY for this Area (needs Pot Production permission and assignment to the Area).");
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
                return (false, $"Only {QuantityFormat.Qty(remaining)} cuttings are left in this batch ({QuantityFormat.Qty(alreadyProduced)} of {QuantityFormat.Qty(cuttingAllocated)} already potted).");
            if (quantity > emptyPotsAvailable)
                return (false, $"Only {QuantityFormat.Qty(emptyPotsAvailable)} empty pots of this size are available in this Area. Issue more pots to the Area first.");
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
                return (false, 0, 0, $"Ready pots ({QuantityFormat.Qty(readyQuantity)}) cannot be more than the {QuantityFormat.Qty(produced)} pots produced.");
            var wastage = produced - readyQuantity;
            var unused = cuttingAllocated - produced;
            if (readyQuantity == 0 && !DirectSowingRules.IsValidWastageReason(wastageReason))
                return (false, wastage, unused, "No plants are ready -- choose the loss reason to close the batch as a complete loss.");
            if (wastage > 0 && !DirectSowingRules.IsValidWastageReason(wastageReason))
                return (false, wastage, unused, $"{QuantityFormat.Qty(wastage)} pots were lost -- choose the wastage reason.");
            if (unused > 0 && unusedCuttingAction != ReturnedToStock && unusedCuttingAction != UnusedAsWastage)
                return (false, wastage, unused, $"{QuantityFormat.Qty(unused)} allocated cuttings were not potted -- return them to Cutting Stock or record them as wastage.");
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
