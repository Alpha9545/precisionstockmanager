namespace PlantStockManager.Services
{
    // 2026-10-31 (owner requirement): a CUTTING tray sowing whose Cutting Quantity is less than one complete tray
    // (e.g. 62 cuttings at 150 Cavity) is no longer refused. It is recorded as ONE PARTIAL TRAY:
    //   SeedSowings.NumberOfTrays = 1, QuantitySown = SeedQuantity = the cuttings entered (never rounded up to the cavity),
    //   'Sown' Cutting Stock ledger row = -QuantitySown, no automatic sub-tray 'Wastage' row (nothing is left over),
    //   Tray Stock / growing material for CEILING(quantity / cavity) = 1 physical tray (the existing rule, unchanged).
    // Supervisor Approval: 1 Actual Ready Tray = the sowing's own cuttings (62), never trays x cavity, and never extra
    // cuttings. Its Ready Stock batch then holds fewer plants than one tray (a "partial-tray batch") and moves only AS A
    // WHOLE: wherever Ready Stock is counted in trays, one tray of that batch = its whole quantity.
    // Seed sowings and every quantity of one tray or more keep the existing whole-tray rules exactly.
    // Needs 2026-10-31_CuttingPartialTraySowing.sql (WorkflowCorrectionsSchema.CuttingPartialTrayAsync); without it the
    // sowing is refused as before.
    public static class PartialTrayRules
    {
        // A whole-number quantity greater than zero and less than one complete tray of the cavity.
        public static bool IsUnderOneTray(decimal quantity, string? cavityType)
        {
            var cavity = DirectSowingRules.CavityCount(cavityType);
            return cavity is > 0 && quantity > 0 && quantity < cavity.Value && DirectSowingRules.IsWholeNumber(quantity);
        }

        // A recorded sowing that is one partial tray: a cutting sowing whose cuttings sown are under one tray.
        // (A seed sowing's QuantitySown is always complete trays, so it is never one.)
        public static bool IsPartialTraySowing(string? sourceType, decimal quantitySown, string? cavityType)
            => sourceType == Models.SeedSowing.SourceCutting && IsUnderOneTray(quantitySown, cavityType);

        // A Ready Stock SOWING batch holding fewer plants than one tray. TR_ReadyStock_WholeTrays keeps every other
        // sowing batch at whole trays, so only a partial-tray cutting sowing's batch can be one.
        public static bool IsPartialTrayBatch(string? originType, decimal quantity, string? cavityType)
            => originType == Models.ReadyStock.OriginSowing && IsUnderOneTray(quantity, cavityType);

        // Plants in ONE tray of a batch when it is counted in trays: its whole quantity for a partial-tray batch,
        // otherwise the cavity. Null when the cavity is not known.
        public static int? TraySize(string? originType, decimal quantity, string? cavityType)
            => IsPartialTrayBatch(originType, quantity, cavityType) ? (int)quantity : DirectSowingRules.CavityCount(cavityType);

        // The yellow (non-blocking) message shown on the sowing form.
        public static string SowingWarning(decimal quantity, string? cavityType, string quantityLabel = DirectSowingRules.CuttingQuantityLabel)
        {
            var cavity = DirectSowingRules.CavityCount(cavityType);
            return $"Warning: {quantityLabel} ({QuantityFormat.Qty(quantity)} seedlings) is less than one complete {cavity}-cavity tray. "
                + $"One tray will be deducted, but only {QuantityFormat.Qty(quantity)} seedlings will be recorded as sown. You can still approve this sowing.";
        }

        public static string ApprovalTrayMessage(decimal quantitySown, string? cavityType)
            => $"This cutting sowing is one partial {cavityType} tray of {QuantityFormat.Qty(quantitySown)} cuttings: enter 1 Actual Ready Tray "
                + $"(= {QuantityFormat.Qty(quantitySown)} seedlings).";

        public static string WholeBatchMessage(decimal batchQuantity, string? cavityType)
            => $"This batch is one partial {cavityType} tray of {QuantityFormat.Qty(batchQuantity)} plants: it moves only as a whole "
                + $"(1 tray = {QuantityFormat.Qty(batchQuantity)} plants), before any of it is reserved, sold or in transit.";
    }
}
