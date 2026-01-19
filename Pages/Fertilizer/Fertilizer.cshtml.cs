using ClosedXML.Excel;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Data.SqlClient;
using PlantStockManager.Data;
using PlantStockManager.Models;

namespace PlantStockManager.Pages.Fertilizer
{
    public class FertilizerModel : PageModel
    {
        private readonly DatabaseHelper _db;

       
        public List<FertilizerMaster> Fertilizers { get; set; } = new();
        public List<FertilizerType> Types { get; set; } = new();

        [BindProperty]
        public FertilizerMaster Fertilizer { get; set; }

        public FertilizerModel(DatabaseHelper dbHelper)
        {
            _db = dbHelper;
        }
        public void OnGet() => LoadAll();

        public IActionResult OnPostSave()
        {
            using var con = _db.GetConnection();
            var sql = Fertilizer.FertilizerId == 0
                ? "INSERT INTO FertilizerMaster(FertilizerName,FertilizerTypeId) VALUES(@n,@t)"
                : "UPDATE FertilizerMaster SET FertilizerName=@n, FertilizerTypeId=@t WHERE FertilizerId=@id";

            var cmd = new SqlCommand(sql, con);
            cmd.Parameters.AddWithValue("@n", Fertilizer.FertilizerName);
            cmd.Parameters.AddWithValue("@t", Fertilizer.FertilizerTypeId);
            if (Fertilizer.FertilizerId != 0)
                cmd.Parameters.AddWithValue("@id", Fertilizer.FertilizerId);

            con.Open();
            cmd.ExecuteNonQuery();
            return RedirectToPage();
        }

        public IActionResult OnPostEdit(int id)
        {
            using var con = _db.GetConnection();
            var cmd = new SqlCommand(
                "SELECT * FROM FertilizerMaster WHERE FertilizerId=@id", con);
            cmd.Parameters.AddWithValue("@id", id);
            con.Open();
            var r = cmd.ExecuteReader();
            if (r.Read())
            {
                Fertilizer = new FertilizerMaster
                {
                    FertilizerId = (int)r["FertilizerId"],
                    FertilizerName = r["FertilizerName"].ToString(),
                    FertilizerTypeId = (int)r["FertilizerTypeId"]
                };
            }
            LoadAll();
            return Page();
        }

        public IActionResult OnPostDelete(int id)
        {
            using var con = _db.GetConnection();
            var cmd = new SqlCommand(
                "DELETE FROM FertilizerMaster WHERE FertilizerId=@id", con);
            cmd.Parameters.AddWithValue("@id", id);
            con.Open();
            cmd.ExecuteNonQuery();
            return RedirectToPage();
        }

        private void LoadAll()
        {
            using var con = _db.GetConnection();
            con.Open();

            var cmd1 = new SqlCommand("SELECT * FROM FertilizerMaster", con);
            var r1 = cmd1.ExecuteReader();
            while (r1.Read())
            {
                Fertilizers.Add(new FertilizerMaster
                {
                    FertilizerId = (int)r1["FertilizerId"],
                    FertilizerName = r1["FertilizerName"].ToString(),
                    FertilizerTypeId = (int)r1["FertilizerTypeId"]
                });
            }
            r1.Close();

            var cmd2 = new SqlCommand("SELECT * FROM FertilizerType", con);
            var r2 = cmd2.ExecuteReader();
            while (r2.Read())
            {
                Types.Add(new FertilizerType
                {
                    FertilizerTypeId = (int)r2["FertilizerTypeId"],
                    TypeName = r2["TypeName"].ToString()
                });
            }
        }
    }
}
