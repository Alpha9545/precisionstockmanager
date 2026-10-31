using PlantStockManager.Models;

namespace PlantStockManager.Services
{
    // 2026-10-19 (owner requirement 2026-10-12): Mother Plant & Cutting > Ready Stock offers two actions on a ready tray batch,
    // each the EXISTING workflow, and says why one is not available instead of hiding it:
    //   Send to Outlet   -> ReadyStock/Move (InternalTransferRepository.InsertAsync, StockType 'ReadyStock'): the WHOLE approved
    //                       sowing batch moves to an Outlet / Main Office -- never split, and only before any of it is reserved,
    //                       sold, dispatched or in transit (ReadyStockTransferRules). That whole-batch rule is unchanged here:
    //                       sending PART of a batch that already has sales / reservations would need a batch-splitting feature
    //                       (a separate business decision).
    //   Sell to customer -> MotherPlant/SellTrays (SeedlingFulfilmentRepository.DirectTraySaleAsync): whole trays of the batch's
    //                       cavity, at most the available complete trays; booking + allocation + reservation + dispatch in one
    //                       transaction, once per form (SeedlingDispatches.SubmissionToken).
    // Display only: both workflows re-check every one of these conditions on the server, under the batch's lock.
    public static class AreaReadyStockActionRules
    {
        public sealed record Availability(bool Available, string? Reason)
        {
            public static readonly Availability Yes = new(true, null);
            public static Availability No(string reason) => new(false, reason);
        }

        public const string PotProductionMessage = "Received for Pot Production: these seedlings are potted, not sent or sold as trays.";

        public static Availability SendToOutlet(ReadyStock b, bool canTransfer, bool forPotProduction)
        {
            if (!canTransfer)
                return Availability.No("Sending stock to an Outlet is not part of your role.");
            if (forPotProduction)
                return Availability.No(PotProductionMessage);
            if (!b.IsSowingBatch)
                return Availability.No("Only a whole approved tray batch is sent; a received or transplanted batch stays in this Area (sell it or transplant it here).");
            if (b.InTransitQuantity > 0)
                return Availability.No("Part of this batch is on its way to a Mother Plant Area; it can be sent once that transfer is received, rejected or cancelled.");
            if (b.ReservedQuantity > 0 || b.DispatchedQuantity > 0)
                return Availability.No($"Already {QuantityFormat.Qty(b.ReservedQuantity)} plants reserved and {QuantityFormat.Qty(b.DispatchedQuantity)} sold / dispatched: "
                    + "a batch moves to an Outlet only as a whole, before any of it is reserved or sold.");
            if (b.AvailableQuantity <= 0)
                return Availability.No("Nothing of this batch is available.");
            return Availability.Yes;
        }

        public static Availability SellToCustomer(ReadyStock b, bool canSell, bool forPotProduction, bool saleSchemaReady)
        {
            if (!canSell)
                return Availability.No("Selling to customers is not part of your role.");
            if (forPotProduction)
                return Availability.No(PotProductionMessage);
            if (!saleSchemaReady)
                return Availability.No("Direct tray sales need the database update 2026-10-18_SeedlingDirectSaleSubmissionToken.sql.");
            if (b.TraySize is not > 0)
                return Availability.No("The batch's tray cavity is not known, so trays cannot be counted.");
            if (b.AvailableQuantity < b.TraySize.Value)
                return Availability.No(b.IsPartialTrayBatch
                    ? PartialTrayRules.WholeBatchMessage(b.Quantity, b.CavityType)
                    : $"Less than one complete {b.CavityType} tray available ({QuantityFormat.Qty(Math.Max(b.AvailableQuantity, 0))} plants).");
            return Availability.Yes;
        }

        // Complete trays that can be sold now: floor(available plants / cavity).
        // 2026-10-31: a partial-tray batch (PartialTrayRules) is one tray of its whole quantity -- 1 while none of it is used.
        public static int AvailableCompleteTrays(ReadyStock b)
            => b.TraySize is > 0 ? (int)decimal.Floor(Math.Max(b.AvailableQuantity, 0) / b.TraySize.Value) : 0;
    }
}
