namespace PlantStockManager.Models
{
    // Phase 22/Phase H: the dedicated ledger for dbo.SeedStock, same
    // shape as every other Phase 7+ stock ledger (PottedPlantStockTransaction,
    // EmptyPotInventoryTransaction) -- signed Quantity delta,
    // BeforeQuantity captured under lock, AfterQuantity a persisted
    // computed column so the ledger can never drift out of sync with
    // its own arithmetic.
    public class SeedStockTransaction
    {
        public int Id { get; set; }
        public int SeedStockId { get; set; }
        public DateTime TransactionDate { get; set; } = DateTime.UtcNow;
        public string TransactionType { get; set; } = string.Empty; // 'StockIn' | 'Transfer'
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
        public string? AreaName { get; set; }
        public string? UserName { get; set; }
    }
}
