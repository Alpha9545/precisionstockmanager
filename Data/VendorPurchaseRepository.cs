namespace PlantStockManager.Data
{
    public class VendorPurchaseRepository
    {
        private readonly DatabaseHelper _dbHelper;

        public VendorPurchaseRepository(DatabaseHelper dbHelper)
        {
            _dbHelper = dbHelper;
        }
    }
}
