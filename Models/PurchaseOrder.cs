namespace PlantStockManager.Models
{
    public class PurchaseOrder
    {
        public int Id { get; set; }
        public string PurchaseOrderCode { get; set; } = string.Empty;

        public int VendorId { get; set; }

        public DateTime OrderDate { get; set; } = DateTime.UtcNow;
        public DateTime? ExpectedDeliveryDate { get; set; }

        // 'Pending' | 'PartiallyReceived' | 'Completed' | 'Cancelled'.
        // Recomputed by PurchaseOrderRepository after every receipt --
        // never set directly from the UI beyond the initial 'Pending'
        // and the explicit Cancel action (only while still 'Pending').
        public string Status { get; set; } = "Pending";

        public string? Remarks { get; set; }

        public DateTime CreatedDate { get; set; } = DateTime.UtcNow;
        public string? CreatedBy { get; set; }
        public DateTime? ModifiedDate { get; set; }
        public string? ModifiedBy { get; set; }

        // Display-only, populated by joins in PurchaseOrderRepository.
        public string? VendorName { get; set; }

        // Populated by GetByIdAsync for the Create/Details/Receive pages.
        public List<PurchaseOrderItem> Items { get; set; } = new();
    }

    public class PurchaseOrderItem
    {
        public int Id { get; set; }
        public int PurchaseOrderId { get; set; }

        // 'Fertilizer' | 'EmptyPot' | 'Other'. Which of the fields below
        // are populated is enforced by CK_PurchaseOrderItems_CategoryFields.
        public string ItemCategory { get; set; } = string.Empty;

        // Fertilizer category only.
        public int? FertilizerId { get; set; }
        public int? FertilizerSourceId { get; set; }
        public int? FertilizerUnitId { get; set; }
        public DateTime? ExpiryDate { get; set; }

        // EmptyPot category only.
        public string? PotSize { get; set; }
        public int? AreaId { get; set; }

        // Other category only.
        public string? ItemName { get; set; }

        public decimal OrderedQuantity { get; set; }
        public decimal ReceivedQuantity { get; set; }
        public decimal? UnitPrice { get; set; }
        public string? Remarks { get; set; }

        public decimal PendingQuantity => OrderedQuantity - ReceivedQuantity;

        public DateTime CreatedDate { get; set; } = DateTime.UtcNow;
        public string? CreatedBy { get; set; }
        public DateTime? ModifiedDate { get; set; }
        public string? ModifiedBy { get; set; }

        // Display-only, populated by joins in PurchaseOrderRepository.
        public string? FertilizerName { get; set; }
        public string? FertilizerSourceName { get; set; }
        public string? FertilizerUnitName { get; set; }
        public string? AreaName { get; set; }

        // Convenience label for dropdowns/lists spanning all three
        // categories in one place.
        public string DisplayName => ItemCategory switch
        {
            "Fertilizer" => FertilizerName ?? "(Fertilizer)",
            "EmptyPot" => $"Empty Pot - {PotSize} ({AreaName ?? "Unassigned"})",
            _ => ItemName ?? "(Other)"
        };
    }
}
