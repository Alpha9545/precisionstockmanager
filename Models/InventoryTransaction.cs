namespace PlantStockManager.Models
{
    public class InventoryTransaction
    {
        public int Id { get; set; }
        public int? BookingId { get; set; }
        public int InventoryId { get; set; }
        public int QuantityUtilized { get; set; }
        public string TransactionType { get; set; } // Allocation, Return, Adjustment, Sorting
        public DateTime TransactionDate { get; set; }
        public string AddedBy { get; set; }
        public DateTime UpdatedOn { get; set; }
        public string Notes { get; set; }
        public int? QuantityWasted { get; set; }


        public string PlantTypeName { get; set; }
        public string SpeciesName { get; set; }
    }
}
