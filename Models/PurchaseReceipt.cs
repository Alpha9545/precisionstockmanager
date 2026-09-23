namespace PlantStockManager.Models
{
    public class PurchaseReceipt
    {
        public int Id { get; set; }
        public int PurchaseOrderId { get; set; }

        public DateTime ReceiptDate { get; set; } = DateTime.UtcNow;
        public int? ReceivedById { get; set; }
        public string? Remarks { get; set; }

        public DateTime CreatedDate { get; set; } = DateTime.UtcNow;
        public string? CreatedBy { get; set; }

        // Display-only, populated by joins in PurchaseOrderRepository.
        public string? ReceivedByName { get; set; }
        public string? PurchaseOrderCode { get; set; }

        public List<PurchaseReceiptItem> Items { get; set; } = new();
    }

    public class PurchaseReceiptItem
    {
        public int Id { get; set; }
        public int PurchaseReceiptId { get; set; }
        public int PurchaseOrderItemId { get; set; }
        public decimal ReceivedQuantity { get; set; }

        public DateTime CreatedDate { get; set; } = DateTime.UtcNow;

        // Display-only, populated by joins in PurchaseOrderRepository.
        public string? ItemDisplayName { get; set; }
    }
}
