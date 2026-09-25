namespace PlantStockManager.Models
{
    // Phase 8: one stock pool's Opening + IN - OUT - Wastage = Closing
    // check. Opening is always 0 (every stock row starts at 0 and only
    // ever grows/shrinks through its own ledger -- Data/StockLedgerRepository.cs
    // GetReconciliationAsync), so this reduces to "does SUM(ledger's
    // signed deltas, restricted to the transaction types that actually
    // affect this row's own physical/closing quantity column) equal the
    // column's current stored value" -- a pure data-integrity check, not
    // a new business rule.
    public class StockReconciliationRow
    {
        public string StockType { get; set; } = string.Empty;
        public int StockRowId { get; set; }
        public string? SpeciesName { get; set; }
        public string? PotSize { get; set; }
        public string? AreaName { get; set; }

        public decimal TotalIn { get; set; }
        public decimal TotalOut { get; set; }
        public decimal TotalWastage { get; set; }

        // SUM of every reconciliation-relevant ledger delta (IN positive,
        // OUT/Wastage already negative on the ledger) -- what the
        // Closing quantity SHOULD be, purely from history.
        public decimal LedgerComputedClosing => TotalIn + TotalOut + TotalWastage;

        // The stock row's own actual, currently-stored quantity column
        // (PhysicalQuantity for Seed/Cutting/EmptyPot/PottedPlant,
        // Quantity for ReadyStock).
        public decimal ActualClosing { get; set; }

        public decimal Discrepancy => ActualClosing - LedgerComputedClosing;
        public bool IsBalanced => Discrepancy == 0m;
    }
}
