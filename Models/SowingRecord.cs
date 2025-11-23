namespace PlantStockManager.Models
{
    public class SowingRecord
    {
        public int Id { get; set; }
        public string Polyhouse { get; set; }  // Name of the Polyhouse
        public string PlantType { get; set; }  // Name of the Plant Type (Mango, Guava)
        public string Species { get; set; }    // Name of the Species (e.g., Alphonso Mango)
        public int SeedsPlanted { get; set; }  // Number of seeds planted
        public DateTime SeedingDate { get; set; }  // Date of sowing
        public int DaysPassed { get; set; }
    }
}
