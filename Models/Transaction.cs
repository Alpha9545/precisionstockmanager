namespace PlantStockManager.Models
{
    public class Transaction
    {
        public int Id { get; set; }
        public int? SeedEntryId { get; set; } // Nullable FK
        public int? InventoryId { get; set; } // Nullable FK
        public int? BookingId { get; set; } // Nullable FK
        public int? VendorPurchaseId { get; set; } // Nullable FK
        public string TransactionType { get; set; }
        public int Quantity { get; set; }
        public DateTime TransactionDate { get; set; } = DateTime.UtcNow;
        public string UpdatedBy { get; set; } // UserId from AspNetUsers
        public DateTime UpdatedOn { get; set; } = DateTime.UtcNow;

        // Navigation Properties
        public SeedEntries? SeedEntry { get; set; }
        public Inventory? Inventory { get; set; }
        public Booking? Booking { get; set; }
        public VendorPurchase? VendorPurchase { get; set; }
    }
}
