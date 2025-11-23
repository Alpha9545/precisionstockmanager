namespace PlantStockManager.Models
{
    public class AddToInventoryRequest
    {
        public int SeedEntryId { get; set; }
        public int AliveCount { get; set; }
    }
}
