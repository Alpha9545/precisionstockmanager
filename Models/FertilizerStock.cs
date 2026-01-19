namespace PlantStockManager.Models
{
    public class FertilizerStock
    {
        public int StockId { get; set; }
        public int FertilizerId { get; set; }

        public decimal Quantity { get; set; }
        public decimal LatestAvailableQuantity { get; set; }

        public int UnitId { get; set; }
        public DateTime PurchaseDate { get; set; }
        public DateTime? ExpiryDate { get; set; }
        public int SourceId { get; set; }
        public string BatchNumber { get; set; }

        public bool IsUtilized { get; set; }
    }
}
