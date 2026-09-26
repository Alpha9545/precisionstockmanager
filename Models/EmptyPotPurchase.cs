namespace PlantStockManager.Models
{
    // Phase D: empty pots bought by the Office (dbo.EmptyPotPurchases).
    // Saving a purchase credits the receiving Office store's Empty Pot stock.
    public class EmptyPotPurchase
    {
        public int Id { get; set; }
        public string PurchaseCode { get; set; } = string.Empty;
        public DateTime PurchaseDate { get; set; } = DateTime.Today;
        public string PotSize { get; set; } = string.Empty;
        public decimal Quantity { get; set; }
        public string SupplierName { get; set; } = string.Empty;
        public string? InvoiceRef { get; set; }
        public int AreaId { get; set; }
        public int EmptyPotInventoryId { get; set; }
        public string? Remarks { get; set; }
        public int? CreatedById { get; set; }
        public string? CreatedBy { get; set; }
        public DateTime CreatedDate { get; set; }

        public string? AreaName { get; set; }
    }
}
