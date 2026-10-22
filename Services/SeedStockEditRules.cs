namespace PlantStockManager.Services
{
    // 2026-10-20: administrator correction of a Seed Stock receipt (Pages/Production/SeedStock/Edit).
    //
    // A Seed Stock lot's balance is its ledger (dbo.SeedStockTransactions; PhysicalQuantity is only ever
    // changed together with a ledger row), so a receipt is never rewritten in place. An edit appends, in ONE
    // transaction (SeedStockRepository.EditReceiptAsync):
    //   1. the corrected receipt -- an ordinary 'StockIn' / 'SeedStock' row on the new Received On date
    //      (so it is itself a normal, editable receipt), and
    //   2. the reversal of the original -- 'StockIn' / ReversalReference, ReferenceId = the original row,
    //      minus the original quantity on the ORIGINAL date, with the old and new values and the reason
    //      in its Remarks (the audit trail).
    // The original row stays in the history untouched; a receipt with a reversal is "superseded" and can no
    // longer be edited. Same shape as the Bed Sowing edit (original reversed, revised one recorded).
    // Only values the receipt form already has are editable: Received On, Quantity and Notes. The lot's
    // variety / Area / lot number / source / unit are copied into sowings and are deliberately not editable.
    public static class SeedStockEditRules
    {
        public const string EditPage = "/Production/SeedStock/Edit";

        // ReferenceType of an ordinary receipt (AddSeedStock / AddStock / data-entry scripts) and of the reversal.
        public const string ReceiptReference = "SeedStock";
        public const string ReversalReference = "SeedStockEdit";

        public const int NotesMaxLength = 500;      // dbo.SeedStockTransactions.Remarks
        public const int ReasonMaxLength = 200;
        public static readonly DateTime EarliestDate = new(2000, 1, 1);

        // 2026-10-21 (security review): the largest whole quantity dbo.SeedStock / dbo.SeedStockTransactions can hold
        // (decimal(18,2)). Checked on every receipt form so an absurd value is refused with a message instead of reaching
        // SQL Server (whose raw overflow text was shown to the user). Not a business cap -- that is the owner's decision.
        public const decimal MaxQuantity = 9_999_999_999_999_999m;
        public static string MaxQuantityMessage => $"Quantity cannot be more than {MaxQuantity:N0}.";
        public static string EarliestDateMessage => $"Received On must be on or after {EarliestDate:dd/MM/yyyy}.";

        public const string StaleMessage = "This receipt was already edited (or is not a receipt) -- open the lot again and edit its current receipt.";
        public const string NoChangeMessage = "No changes -- the receipt already has these values.";

        public sealed record Error(string Field, string Message);

        // Server-side validation of the edit form. today = the business date (AppClock.Today / DateTime.Today).
        public static List<Error> Validate(decimal? quantity, DateTime? receivedOn, string? notes, string? reason, DateTime today)
        {
            var errors = new List<Error>();

            if (quantity is null)
                errors.Add(new("Quantity", "Quantity is required."));
            else if (quantity <= 0)
                errors.Add(new("Quantity", "Quantity must be greater than zero."));
            else if (!DirectSowingRules.IsWholeNumber(quantity.Value))
                errors.Add(new("Quantity", "Quantity must be a whole number."));
            else if (quantity > MaxQuantity)
                errors.Add(new("Quantity", MaxQuantityMessage));

            if (receivedOn is null)
                errors.Add(new("ReceivedOn", "Received On is required."));
            else if (receivedOn.Value.Date > today.Date)
                errors.Add(new("ReceivedOn", "Received On cannot be in the future."));
            else if (receivedOn.Value.Date < EarliestDate)
                errors.Add(new("ReceivedOn", EarliestDateMessage));

            if (notes != null && notes.Trim().Length > NotesMaxLength)
                errors.Add(new("Notes", $"Notes cannot be longer than {NotesMaxLength} characters."));

            if (string.IsNullOrWhiteSpace(reason))
                errors.Add(new("Reason", "A reason for the correction is required."));
            else if (reason.Trim().Length > ReasonMaxLength)
                errors.Add(new("Reason", $"The reason cannot be longer than {ReasonMaxLength} characters."));

            return errors;
        }

        // Received On is entered to the minute (datetime-local); compare at that precision.
        public static DateTime ToMinute(DateTime value) => new(value.Year, value.Month, value.Day, value.Hour, value.Minute, 0, value.Kind);

        public static bool IsUnchanged(decimal oldQuantity, DateTime oldReceivedOnLocal, string? oldNotes,
                                       decimal newQuantity, DateTime newReceivedOnLocal, string? newNotes)
            => oldQuantity == newQuantity
               && ToMinute(oldReceivedOnLocal) == ToMinute(newReceivedOnLocal)
               && string.Equals((oldNotes ?? "").Trim(), (newNotes ?? "").Trim(), StringComparison.Ordinal);

        // The reversal row's Remarks: what changed, who asked and why (truncated to the column).
        public static string AuditRemarks(int receiptId, decimal oldQuantity, DateTime oldReceivedOnLocal,
                                          decimal newQuantity, DateTime newReceivedOnLocal, string reason)
        {
            var text = $"Admin edit of receipt #{receiptId}: {QuantityFormat.Qty(oldQuantity)} on {oldReceivedOnLocal:dd/MM/yyyy HH:mm}"
                     + $" -> {QuantityFormat.Qty(newQuantity)} on {newReceivedOnLocal:dd/MM/yyyy HH:mm}. Reason: {reason.Trim()}";
            return text.Length <= NotesMaxLength ? text : text[..NotesMaxLength];
        }
    }
}
