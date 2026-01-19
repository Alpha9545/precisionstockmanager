using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Data.SqlClient;
using PlantStockManager.Data;

namespace PlantStockManager.Pages.Fertilizer
{
    public class ViewModel : PageModel
    {

        private readonly DatabaseHelper _db;
        public ViewModel(DatabaseHelper db) => _db = db;

        public List<AvailableStockVM> Stocks { get; set; } = new();

        [BindProperty(SupportsGet = true)]
        public string Filter { get; set; } = "all"; // all | available | utilized

        public void OnGet()
        {
            LoadStock();
        }

        private void LoadStock()
        {
            using var con = _db.GetConnection();

            string whereClause = Filter switch
            {
                "available" => "WHERE IsUtilized = 0 AND AvailableQuantity > 0",
                "utilized" => "WHERE IsUtilized = 1",
                _ => ""
            };

            var cmd = new SqlCommand($@"
            SELECT *
            FROM vw_FertilizerAvailableStock
            {whereClause}
            ORDER BY FertilizerName, PurchaseDate", con);

            con.Open();
            var r = cmd.ExecuteReader();

            while (r.Read())
            {
                Stocks.Add(new AvailableStockVM
                {
                    StockId = (int)r["StockId"],
                    FertilizerName = r["FertilizerName"].ToString(),
                    FertilizerType = r["FertilizerType"].ToString(),
                    PurchasedQuantity = (decimal)r["PurchasedQuantity"],
                    AvailableQuantity = (decimal)r["AvailableQuantity"],
                    UnitName = r["UnitName"].ToString(),
                    PurchaseDate = (DateTime)r["PurchaseDate"],
                    ExpiryDate = r["ExpiryDate"] as DateTime?,
                    SourceName = r["SourceName"].ToString(),
                    BatchNumber = r["BatchNumber"]?.ToString(),
                    IsUtilized = (bool)r["IsUtilized"]
                });
            }
        }
    }
}

public class AvailableStockVM
{
    public int StockId { get; set; }

    public string FertilizerName { get; set; }
    public string FertilizerType { get; set; }

    public decimal PurchasedQuantity { get; set; }
    public decimal AvailableQuantity { get; set; }

    public string UnitName { get; set; }

    public DateTime PurchaseDate { get; set; }
    public DateTime? ExpiryDate { get; set; }

    public string SourceName { get; set; }
    public string BatchNumber { get; set; }

    public bool IsUtilized { get; set; }
}

