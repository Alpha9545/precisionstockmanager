namespace PlantStockManager.Models
{
    // Phase 25 (Phase K): the dedicated ledger for dbo.ReadyStock,
    // exactly the same shape as every other stock ledger in this app
    // (SeedStockTransaction/PottedPlantStockTransaction) -- signed
    // Quantity delta, BeforeQuantity captured under lock, AfterQuantity
    // a persisted computed column so the ledger can never drift out of
    // sync with its own arithmetic.
    public class ReadyStockTransaction
    {
        public int Id { get; set; }
        public int ReadyStockId { get; set; }
        public DateTime TransactionDate { get; set; } = DateTime.UtcNow;

        // 'Confirmed' (positive -- a Ready Confirmation was recorded) |
        // 'ReversalRemoval' (negative -- a Ready Confirmation was
        // cancelled). Decision 15's naming convention: 'ReversalReturn'
        // is reserved for crediting stock back to a SOURCE pool it was
        // taken FROM; there is no source pool being credited back
        // here -- only an addition being undone -- so 'ReversalRemoval'
        // is the correct existing name to reuse, never a new one.
        public string TransactionType { get; set; } = string.Empty;
        public string? ReferenceType { get; set; }
        public int? ReferenceId { get; set; }
        public decimal Quantity { get; set; }
        public decimal BeforeQuantity { get; set; }
        public decimal AfterQuantity { get; set; }
        public int? UserId { get; set; }
        public string? Remarks { get; set; }
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

        // Display-only, populated by joins.
        public string? SpeciesName { get; set; }
        public string? BatchNo { get; set; }
        public string? UserName { get; set; }
    }
}
