namespace PlantStockManager.Models
{
    public class VendorPurchase
    {
        public int Id { get; set; }
        public string VendorName { get; set; }
        public int SpeciesId { get; set; } // FK to PlantSpecies
        public int Quantity { get; set; }
        public DateTime PurchaseDate { get; set; } = DateTime.UtcNow;
        public decimal Price { get; set; }

        // Navigation Property
        public PlantSpecies? Species { get; set; }
    }
}
