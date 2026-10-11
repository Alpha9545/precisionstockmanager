using PlantStockManager.Models;

namespace PlantStockManager.Services
{
    // 2026-10-13: the shared rules of a transplant into cavity trays that becomes a growing tray batch
    // in Sowing Approvals (Data/TrayBatchWriter.cs) -- from a Bed Sowing or from received Ready Stock.
    //   Ready Date   = ACTUAL TRANSPLANT DATE + the variety's configured Ready Days (India business date).
    //   Output trays = transplanted plants / cavity, COMPLETE trays only: the sowing approval path keeps
    //                  whole trays (approved Ready Stock is a whole number of trays), so no tray is
    //                  partly filled and no seedling is invented; the remainder stays where it was.
    public static class TrayBatchRules
    {
        public const string NoReadyDaysMessageFormat =
            "Ready Days are not configured for variety '{0}'. Set them in Administration > Plant Master before transplanting -- no Ready Date is assumed.";

        // null when the Ready Days are missing or not a positive number (the caller refuses).
        public static DateTime? ReadyDate(DateTime transplantDate, int? readyDays)
            => readyDays is > 0 ? transplantDate.Date.AddDays(readyDays.Value) : null;

        public static (bool Ok, int Trays, string? Error) CompleteTrays(decimal quantity, string? cavityType, string remainderWhere)
        {
            var cavity = DirectSowingRules.CavityCount(cavityType);
            if (cavity is not > 0)
                return (false, 0, $"Cavity must be one of: {string.Join(", ", DirectSowingRules.CavityTypes)}.");
            if (quantity <= 0 || !DirectSowingRules.IsWholeNumber(quantity))
                return (false, 0, "The transplanted quantity must be a whole number greater than zero.");
            if (quantity % cavity.Value != 0)
            {
                var whole = decimal.Floor(quantity / cavity.Value);
                return (false, 0, $"{QuantityFormat.Qty(quantity)} plants do not fill complete {cavityType} trays. "
                    + (whole > 0 ? $"Transplant {QuantityFormat.Qty(whole * cavity.Value)} ({QuantityFormat.Qty(whole)} trays); " : "")
                    + $"the rest stays {remainderWhere}.");
            }
            return (true, (int)(quantity / cavity.Value), null);
        }

        // The sowing source types whose tray batches are made by a transplant (no supervisor assigned).
        public static bool IsTransplantedBatch(string? sourceType) => sourceType is SeedSowing.SourceBedSowing or SeedSowing.SourceTransplant;
    }
}
