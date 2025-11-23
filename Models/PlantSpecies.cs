namespace PlantStockManager.Models
{
    public class PlantSpecies
    {
        public int Id { get; set; }
        public int PlantTypeId { get; set; } // FK to PlantType
        public string Name { get; set; }
        public string? ScientificName { get; set; } // Nullable

        // Navigation Property (Not stored in DB, used for reference)
        public PlantType? PlantType { get; set; }
    }
}
