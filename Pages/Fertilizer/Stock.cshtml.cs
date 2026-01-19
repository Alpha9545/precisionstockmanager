using ClosedXML.Excel;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Data.SqlClient;
using PlantStockManager.Data;
using PlantStockManager.Models;

namespace PlantStockManager.Pages.Fertilizer
{
    public class StockModel : PageModel
    {
        private readonly DatabaseHelper _db;
        public StockModel(DatabaseHelper db)
        {
            _db = db;
        }

        // List
        public List<FertilizerStockView> Stocks { get; set; } = new();

        // Dropdowns
        public List<DropdownItem> Fertilizers { get; set; } = new();
        public List<DropdownItem> Units { get; set; } = new();
        public List<DropdownItem> Sources { get; set; } = new();

        [BindProperty]
        public FertilizerStock Stock { get; set; }

        public void OnGet()
        {
            LoadAll();
        }

        // INSERT + UPDATE
        public IActionResult OnPostSave()
        {
            using var con = _db.GetConnection();

            string sql = Stock.StockId == 0
  ? @"INSERT INTO FertilizerStock
    (FertilizerId, Quantity, LatestAvailableQuantity, UnitId,
     PurchaseDate, ExpiryDate, SourceId, BatchNumber, IsUtilized)
   VALUES
    (@f, @pq, @pq, @u, @p, @e, @s, @b, 0)"
  : @"UPDATE FertilizerStock
    SET FertilizerId=@f,
        Quantity=@pq,
        UnitId=@u,
        PurchaseDate=@p,
        ExpiryDate=@e,
        SourceId=@s,
        BatchNumber=@b
    WHERE StockId=@id";


            var cmd = new SqlCommand(sql, con);
            cmd.Parameters.AddWithValue("@f", Stock.FertilizerId);
            cmd.Parameters.AddWithValue("@pq", Stock.Quantity);
            cmd.Parameters.AddWithValue("@u", Stock.UnitId);
            cmd.Parameters.AddWithValue("@p", Stock.PurchaseDate);
            cmd.Parameters.AddWithValue("@e", (object?)Stock.ExpiryDate ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@s", Stock.SourceId);
            cmd.Parameters.AddWithValue("@b", (object?)Stock.BatchNumber ?? DBNull.Value);

            if (Stock.StockId != 0)
                cmd.Parameters.AddWithValue("@id", Stock.StockId);

            con.Open();
            cmd.ExecuteNonQuery();

            return RedirectToPage();
        }

        // EDIT
        public IActionResult OnPostEdit(int id)
        {
            using var con = _db.GetConnection();
            var cmd = new SqlCommand(
                "SELECT * FROM FertilizerStock WHERE StockId=@id", con);
            cmd.Parameters.AddWithValue("@id", id);

            con.Open();
            var r = cmd.ExecuteReader();

            if (r.Read())
            {
                decimal purchasedQty = (decimal)r["Quantity"];
                decimal availableQty = (decimal)r["LatestAvailableQuantity"];

                if (purchasedQty != availableQty)
                {
                    ModelState.AddModelError("",
                        "This stock is already partially or fully utilized and cannot be edited.");
                    LoadAll();
                    return Page();
                }


                Stock = new FertilizerStock
                {
                    StockId = (int)r["StockId"],
                    FertilizerId = (int)r["FertilizerId"],
                    Quantity = (decimal)r["Quantity"],
                    LatestAvailableQuantity = (decimal)r["LatestAvailableQuantity"],
                    IsUtilized = (bool)r["IsUtilized"],

                    UnitId = (int)r["UnitId"],
                    PurchaseDate = (DateTime)r["PurchaseDate"],
                    ExpiryDate = r["ExpiryDate"] == DBNull.Value ? null : (DateTime?)r["ExpiryDate"],
                    SourceId = (int)r["SourceId"],
                    BatchNumber = r["BatchNumber"]?.ToString()
                };
            }

            LoadAll();
            return Page();
        }

        // DELETE
        public IActionResult OnPostDelete(int id)
        {
            using var con = _db.GetConnection();
            var cmd = new SqlCommand(
                "DELETE FROM FertilizerStock WHERE StockId=@id", con);
            cmd.Parameters.AddWithValue("@id", id);

            con.Open();
            cmd.ExecuteNonQuery();

            return RedirectToPage();
        }

        // LOAD DATA
        private void LoadAll()
        {
            using var con = _db.GetConnection();
            con.Open();

            // Stock list
            var cmd = new SqlCommand(@"
          SELECT fs.StockId, fm.FertilizerName,
       fs.Quantity,
       fs.LatestAvailableQuantity,
       um.UnitName,
       fs.PurchaseDate,
       fs.ExpiryDate,
       src.SourceName,
       fs.BatchNumber,
       fs.IsUtilized
FROM FertilizerStock fs
JOIN FertilizerMaster fm ON fs.FertilizerId = fm.FertilizerId
JOIN UnitMaster um ON fs.UnitId = um.UnitId
JOIN FertilizerSource src ON fs.SourceId = src.SourceId
WHERE fs.IsUtilized = 0", con);

            var r = cmd.ExecuteReader();
            while (r.Read())
            {
                Stocks.Add(new FertilizerStockView
                {
                    StockId = (int)r["StockId"],
                    FertilizerName = r["FertilizerName"].ToString(),
                    Quantity = (decimal)r["Quantity"],
                    LatestAvailableQuantity = (decimal)r["LatestAvailableQuantity"],
                    UnitName = r["UnitName"].ToString(),
                    PurchaseDate = (DateTime)r["PurchaseDate"],
                    ExpiryDate = r["ExpiryDate"] as DateTime?,
                    SourceName = r["SourceName"].ToString(),
                    BatchNumber = r["BatchNumber"]?.ToString()
                });
            }
            r.Close();

            LoadDropdowns(con);
        }

        private void LoadDropdowns(SqlConnection con)
        {
            Fertilizers = LoadDropdown(con, "SELECT FertilizerId, FertilizerName FROM FertilizerMaster");
            Units = LoadDropdown(con, "SELECT UnitId, UnitName FROM UnitMaster");
            Sources = LoadDropdown(con, "SELECT SourceId, SourceName FROM FertilizerSource");
        }

        private List<DropdownItem> LoadDropdown(SqlConnection con, string sql)
        {
            var list = new List<DropdownItem>();
            var cmd = new SqlCommand(sql, con);
            var r = cmd.ExecuteReader();
            while (r.Read())
            {
                list.Add(new DropdownItem
                {
                    Id = (int)r[0],
                    Name = r[1].ToString()
                });
            }
            r.Close();
            return list;
        }
    }
}


// Helper view model
public class FertilizerStockView
{
    public int StockId { get; set; }
    public string FertilizerName { get; set; }

    public decimal Quantity { get; set; }
    public decimal LatestAvailableQuantity { get; set; }

    public string UnitName { get; set; }
    public DateTime PurchaseDate { get; set; }
    public DateTime? ExpiryDate { get; set; }
    public string SourceName { get; set; }
    public string BatchNumber { get; set; }
    public bool IsUtilized { get; set; }
}


public class DropdownItem
{
    public int Id { get; set; }
    public string Name { get; set; }
}