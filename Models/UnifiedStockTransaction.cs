namespace PlantStockManager.Models
{
    // Phase 8 (Stock History and Wastage Integration): one row of the
    // unified, cross-stock-type transaction history -- built by UNION
    // ALL over the FIVE existing, already-dedicated ledger tables
    // (dbo.SeedStockTransactions, dbo.CuttingStockTransactions,
    // dbo.EmptyPotInventoryTransactions, dbo.PottedPlantStockTransactions,
    // dbo.ReadyStockTransactions -- see Data/StockLedgerRepository.cs).
    // No new history table: every one of these ledgers already existed
    // and already had this exact same column shape (Id, <Stock>Id,
    // TransactionDate, TransactionType, ReferenceType, ReferenceId,
    // Quantity, BeforeQuantity, AfterQuantity, UserId, Remarks, CreatedAt)
    // -- this model is purely a read-side projection, not a new
    // persisted concept.
    public class UnifiedStockTransaction
    {
        // 'SeedStock' | 'CuttingStock' | 'EmptyPot' | 'PottedPlant' | 'ReadyStock'
        public string StockType { get; set; } = string.Empty;
        public int StockRowId { get; set; }

        public string? SpeciesName { get; set; }
        public string? PlantTypeName { get; set; }
        public string? PotSize { get; set; }
        public string? AreaName { get; set; }

        public DateTime TransactionDate { get; set; }
        public string TransactionType { get; set; } = string.Empty;
        public string? ReferenceType { get; set; }
        public int? ReferenceId { get; set; }

        // SIGNED delta, exactly as stored on the underlying ledger row --
        // never re-derived or re-signed here.
        public decimal Quantity { get; set; }
        public decimal BeforeQuantity { get; set; }
        public decimal AfterQuantity { get; set; }

        public string? UserName { get; set; }
        public string? Remarks { get; set; }

        // Rule-of-thumb classification for display only (Purchase -> IN,
        // Issue -> OUT, Production -> Consumption+Production, Wastage ->
        // Wastage, Ready -> Ready Stock, Sale/Dispatch -> OUT) -- purely
        // a label computed from TransactionType/Quantity sign, never
        // written back anywhere.
        public string MovementLabel => (TransactionType, Math.Sign(Quantity)) switch
        {
            ("StockIn", _) => "Purchase / Stock IN",
            ("Harvest", _) => "Purchase / Stock IN",
            ("Production", _) => "Production",
            ("Confirmed", _) => "Ready Stock",
            ("Wastage", _) => "Wastage",
            ("Dispatch", _) => "Sale / Dispatch (OUT)",
            ("Consumption", _) => "Issue (OUT)",
            ("Potted", _) => "Issue (OUT) -- Consumed into Pot Production",
            ("Sown", _) => "Issue (OUT) -- Consumed into Sowing",
            ("Transfer", var sign) when sign < 0 => "Issue (OUT)",
            ("Transfer", var sign) when sign > 0 => "Transfer IN",
            ("Transplanted", _) => "Issue (OUT) -- Transplanted",
            ("ReversalRemoval", _) => "Reversal",
            ("ReversalReturn", _) => "Reversal (Returned)",
            ("Reservation", _) => "Reserved (not yet physical movement)",
            ("ReservationRelease", _) => "Reservation Released",
            ("LabSent", _) => "Issue (OUT) -- Sent to Lab",
            ("LabReceived", _) => "Returned from Lab",
            ("Adjustment", _) => "Manual Adjustment",
            _ => TransactionType
        };
    }
}
