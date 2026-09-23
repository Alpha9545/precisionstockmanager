namespace PlantStockManager.Models
{
    // dbo.CuttingStockTransactions -- the dedicated ledger for
    // CuttingStock, mirroring EmptyPotInventoryTransactions/
    // PottedPlantStockTransactions in shape (Decision 1 in
    // PROJECT_DOCUMENTATION.md).
    public class CuttingStockTransaction
    {
        public int Id { get; set; }
        public int CuttingStockId { get; set; }
        public string? SpeciesName { get; set; }
        public string? AreaName { get; set; }

        public DateTime TransactionDate { get; set; } = DateTime.UtcNow;
        public string TransactionType { get; set; } = string.Empty;
        public string? ReferenceType { get; set; }
        public int? ReferenceId { get; set; }

        public decimal Quantity { get; set; }
        public decimal BeforeQuantity { get; set; }
        public decimal AfterQuantity { get; set; }

        public int? UserId { get; set; }
        public string? UserName { get; set; }
        public string? Remarks { get; set; }
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    }
}
