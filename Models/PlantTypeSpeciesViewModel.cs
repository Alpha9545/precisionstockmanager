namespace PlantStockManager.Models
{
    public class PlantTypeSpeciesViewModel
    {
        public PlantType PlantType { get; set; }
        public List<PlantSpecies> Species { get; set; } = new();
    }
}
