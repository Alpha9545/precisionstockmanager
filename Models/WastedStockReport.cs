namespace PlantStockManager.Models
{
    public class WastedStockReport
    {
        public string PolyhouseName { get; set; }
        public string PlantTypeName { get; set; }
        public string SpeciesName { get; set; }
        public int TrayWasted { get; set; }
        public int HardeningWasted { get; set; }
        public int InventoryWasted { get; set; }
        public int SortingWasted { get; set; }
        public int TotalWasted { get; set; }
        public DateTime LastUpdated { get; set; }
        public string LocationDesc { get; set; }
    }

}
