namespace PlantStockManager.Models
{
    // Phase D: controlled pot-size list (dbo.PotSizes). Every PotSize column
    // references PotSizes.Name, so only listed sizes can be stored.
    public class PotSize
    {
        public int Id { get; set; }
        public string Name { get; set; } = string.Empty;
        public int SortOrder { get; set; }
        public bool IsActive { get; set; } = true;
        public DateTime CreatedDate { get; set; }
        public string? CreatedBy { get; set; }
    }
}
