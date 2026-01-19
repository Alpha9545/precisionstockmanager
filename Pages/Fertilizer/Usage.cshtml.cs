using ClosedXML.Excel;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Data.SqlClient;
using PlantStockManager.Data;
using PlantStockManager.Models;

namespace PlantStockManager.Pages.Fertilizer
{
    public class UsageModel : PageModel
    {
        private readonly DatabaseHelper _db;
        public UsageModel(DatabaseHelper db) => _db = db;

        // Show only available stock
        public List<AvailableStockTableVM> AvailableStocks { get; set; } = new();

        [BindProperty]
        public FertilizerUsage Usage { get; set; }

        public void OnGet()
        {
            LoadAvailableStock();
        }

        public IActionResult OnPostSave()
        {
            using var con = _db.GetConnection();
            con.Open();

            using var tran = con.BeginTransaction();

            try
            {
                // 1️⃣ Get current available quantity
                decimal availableQty;

                var checkCmd = new SqlCommand(
                    "SELECT LatestAvailableQuantity FROM FertilizerStock WHERE StockId=@id",
                    con, tran);

                checkCmd.Parameters.AddWithValue("@id", Usage.StockId);

                availableQty = (decimal)checkCmd.ExecuteScalar();

                if (Usage.UsedQuantity > availableQty)
                    throw new Exception("Used quantity exceeds available stock");

                // 2️⃣ Insert usage
                var usageCmd = new SqlCommand(@"
                INSERT INTO FertilizerUsage
                (StockId, UsedQuantity, IssueDate, ReceivedBy, Remarks)
                VALUES (@s, @q, @d, @r, @rm)", con, tran);

                usageCmd.Parameters.AddWithValue("@s", Usage.StockId);
                usageCmd.Parameters.AddWithValue("@q", Usage.UsedQuantity);
                usageCmd.Parameters.AddWithValue("@d", Usage.IssueDate);
                usageCmd.Parameters.AddWithValue("@r", Usage.ReceivedBy);
                usageCmd.Parameters.AddWithValue("@rm", (object?)Usage.Remarks ?? DBNull.Value);

                usageCmd.ExecuteNonQuery();

                // 3️⃣ Update stock
                var updateCmd = new SqlCommand(@"
                UPDATE FertilizerStock
                SET LatestAvailableQuantity = LatestAvailableQuantity - @q,
                    IsUtilized = CASE
                        WHEN LatestAvailableQuantity - @q = 0 THEN 1
                        ELSE 0
                    END
                WHERE StockId=@id", con, tran);

                updateCmd.Parameters.AddWithValue("@q", Usage.UsedQuantity);
                updateCmd.Parameters.AddWithValue("@id", Usage.StockId);

                updateCmd.ExecuteNonQuery();

                tran.Commit();
                return RedirectToPage();
            }
            catch
            {
                tran.Rollback();
                throw;
            }
        }


        private void LoadAvailableStock()
        {
            using var con = _db.GetConnection();
            var cmd = new SqlCommand(@"
            SELECT fs.StockId,
                   fm.FertilizerName,
                   fs.BatchNumber,
                   fs.LatestAvailableQuantity,
                   um.UnitName,
                   fs.ExpiryDate
            FROM FertilizerStock fs
            JOIN FertilizerMaster fm ON fs.FertilizerId = fm.FertilizerId
            JOIN UnitMaster um ON fs.UnitId = um.UnitId
            WHERE fs.LatestAvailableQuantity > 0
              AND fs.IsUtilized = 0
            ORDER BY fs.ExpiryDate, fs.CreatedAt", con);

            con.Open();
            var r = cmd.ExecuteReader();

            while (r.Read())
            {
                AvailableStocks.Add(new AvailableStockTableVM
                {
                    StockId = (int)r["StockId"],
                    FertilizerName = r["FertilizerName"].ToString(),
                    BatchNumber = r["BatchNumber"]?.ToString(),
                    AvailableQuantity = (decimal)r["LatestAvailableQuantity"],
                    UnitName = r["UnitName"].ToString(),
                    ExpiryDate = r["ExpiryDate"] as DateTime?
                });
            }
        }

    }
}

public class AvailableStockTableVM
{
    public int StockId { get; set; }
    public string FertilizerName { get; set; }
    public string BatchNumber { get; set; }
    public decimal AvailableQuantity { get; set; }
    public string UnitName { get; set; }
    public DateTime? ExpiryDate { get; set; }
}


// View model
public class FertilizerUsage
{
    public int UsageId { get; set; }
    public int StockId { get; set; }
    public decimal UsedQuantity { get; set; }
    public DateTime IssueDate { get; set; }
    public string ReceivedBy { get; set; }
    public string Remarks { get; set; }
}

