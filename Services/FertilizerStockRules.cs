namespace PlantStockManager.Services
{
    // Correction I3 -- Fertilizer Stock (a purchase batch, dbo.FertilizerStock) edit/delete rules. Pure, so they are
    // unit-tested directly; the repository re-applies EditAllowed under a database row lock before writing anything
    // (the page's Edit button visibility was, until now, the ONLY thing stopping an edit after usage -- OnPostSave
    // itself never re-checked it, so a stale form or a crafted POST could overwrite Quantity without touching
    // LatestAvailableQuantity).
    public static class FertilizerStockRules
    {
        // A batch may be edited (or deleted -- deletion also uses DeletionRules.FertilizerStockDependencies as a
        // second, independent check) only while NOTHING has been issued from it: Quantity == LatestAvailableQuantity.
        // Once anything is issued the two diverge and never move back together (see FertilizerTransactionRepository
        // .IssueAsync), so this single comparison is exactly "no usage yet" -- the same condition the Edit button's
        // visibility already used, now the one the server actually enforces.
        public static bool EditAllowed(decimal quantity, decimal latestAvailableQuantity)
            => quantity == latestAvailableQuantity;

        public const string AlreadyUsedMessage = "This stock is already partially or fully utilized and cannot be edited.";
        public const string NotFoundMessage = "That fertilizer stock batch was not found.";
        public const string ChangedSinceOpenedMessage =
            "This stock has been issued since the edit form was opened and can no longer be edited. Nothing was changed.";

        // ---- input validation (insert AND update; a value the user typed, never trusted as-is) --------------------
        // Returns the first problem found, or null when the input is acceptable. Existence of the Fertilizer/Unit/
        // Source ids is checked by the repository (it needs the database); this only checks what pure values alone
        // can tell -- the same split every other module's *Rules class uses.
        public static string? ValidateQuantity(decimal quantity)
            => quantity > 0 ? null : "Quantity must be greater than 0.";

        public static string? ValidateDate(DateTime purchaseDate)
            => purchaseDate == default ? "Purchase date is required." : null;

        public const string FertilizerNotFoundMessage = "The selected Fertilizer does not exist.";
        public const string UnitNotFoundMessage = "The selected Unit does not exist.";
        public const string SourceNotFoundMessage = "The selected Source does not exist.";
    }
}
