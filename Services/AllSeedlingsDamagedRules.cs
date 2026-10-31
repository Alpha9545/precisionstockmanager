namespace PlantStockManager.Services
{
    // 2026-10-31 (owner requirement): Supervisor Approval option "All Seedlings Damaged -- Approve with 0 Ready Seedlings".
    // When every seedling the sowing still expects was damaged, the approval is recorded with the EXISTING approval model:
    //   ReadyConfirmations.ActualTrayQuantity = 0   (the marker: only this workflow may store 0 trays)
    //   ConfirmedQuantity = 0                        (nothing reaches Ready Stock; no 'Confirmed' ledger row is written)
    //   WastageQuantity   = the sowing's still-expected quantity (QuantitySown - already approved - already wasted)
    //   WastageReason     = 'Damaged plants'         (the existing closed reason -- damage stays distinct from the others)
    //   Remarks           = "All seedlings damaged: <reason>" (+ the optional remarks), shown in History and Wasted Stock.
    // The sowing then closes (Ready + Wastage = Sown -> Completed) exactly as an ordinary approval does. No extra cuttings,
    // trays or growing material are taken (no trays ready = no extra), and nothing that was consumed at sowing comes back.
    // Ordinary approvals are unchanged: 0 trays is accepted ONLY through this option, never by the normal form.
    // Needs 2026-10-31_AllSeedlingsDamagedApproval.sql (WorkflowCorrectionsSchema.AllSeedlingsDamagedAsync): without it
    // the database refuses 0 Actual Ready Trays, so the option is not offered and the server refuses it.
    public static class AllSeedlingsDamagedRules
    {
        public const string WastageReason = "Damaged plants";   // must stay in DirectSowingRules.WastageReasons / the CHECK
        public const int MaxReasonLength = 200;
        public const int MaxRemarksLength = 500;                 // dbo.ReadyConfirmations.Remarks NVARCHAR(500)
        public const string RemarksPrefix = "All seedlings damaged: ";

        public const string Warning = "All seedlings are marked as damaged. This sowing will be approved with 0 ready seedlings. "
            + "Please confirm that no healthy seedlings remain.";
        public const string ReasonRequiredMessage = "A reason for the damage is required when All Seedlings Damaged is selected.";
        public const string NotAvailableMessage = "All Seedlings Damaged needs a database update that is not applied yet. Nothing was recorded.";

        // Ready 0, Wastage = everything the sowing still expects. Refused when nothing is left to approve, when the
        // reason is missing / too long, or when the stored remarks would not fit.
        public static (bool Ok, decimal Wastage, decimal WastagePercent, string? Remarks, string? Error) Compute(
            decimal quantitySown, decimal alreadyReady, decimal alreadyWasted, string? damageReason, string? remarks)
        {
            var reason = damageReason?.Trim();
            if (string.IsNullOrEmpty(reason))
                return (false, 0, 0, null, ReasonRequiredMessage);
            if (reason.Length > MaxReasonLength)
                return (false, 0, 0, null, $"The damage reason can be at most {MaxReasonLength} characters.");
            var remaining = quantitySown - alreadyReady - alreadyWasted;
            if (remaining <= 0)
                return (false, 0, 0, null, "This sowing has already been fully approved.");
            var stored = BuildRemarks(reason, remarks);
            if (stored.Length > MaxRemarksLength)
                return (false, 0, 0, null, $"The damage reason and remarks together can be at most {MaxRemarksLength - RemarksPrefix.Length} characters.");
            return (true, remaining, DirectSowingRules.WastagePercent(remaining, quantitySown), stored, null);
        }

        public static string BuildRemarks(string reason, string? remarks)
            => RemarksPrefix + reason.Trim() + (string.IsNullOrWhiteSpace(remarks) ? "" : " | " + remarks.Trim());

        // An approval recorded through this option (0 Actual Ready Trays is stored by nothing else).
        public static bool IsAllDamagedApproval(int? actualTrayQuantity) => actualTrayQuantity == 0;
    }
}
