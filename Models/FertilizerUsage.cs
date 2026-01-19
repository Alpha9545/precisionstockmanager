namespace PlantStockManager.Models
{
    public class FertilizerUsage
    {
        public int UsageId { get; set; }
        public int StockId { get; set; }
        public decimal UsedQuantity { get; set; }
        public int UnitId { get; set; }
        public DateTime IssueDate { get; set; }
        public string ReceivedBy { get; set; }
        public string Remarks { get; set; }
    }

}
