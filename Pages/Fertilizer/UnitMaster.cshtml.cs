using ClosedXML.Excel;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Data.SqlClient;
using PlantStockManager.Data;
using PlantStockManager.Models;

namespace PlantStockManager.Pages.Fertilizer
{
    public class UnitMasterModel : PageModel
    {
        private readonly DatabaseHelper _db;

        public UnitMasterModel(DatabaseHelper dbHelper)
        {
            _db = dbHelper;
        }

        public List<UnitMaster> Units { get; set; } = new();

        [BindProperty]
        public UnitMaster Unit { get; set; }

        public void OnGet()
        {
            LoadData();
        }

        public IActionResult OnPostSave()
        {
            using var con = _db.GetConnection();
            var sql = Unit.UnitId == 0
                ? "INSERT INTO UnitMaster(UnitName,UnitSymbol) VALUES(@n,@s)"
                : "UPDATE UnitMaster SET UnitName=@n, UnitSymbol=@s WHERE UnitId=@id";

            var cmd = new SqlCommand(sql, con);
            cmd.Parameters.AddWithValue("@n", Unit.UnitName);
            cmd.Parameters.AddWithValue("@s", Unit.UnitSymbol);
            if (Unit.UnitId != 0)
                cmd.Parameters.AddWithValue("@id", Unit.UnitId);

            con.Open();
            cmd.ExecuteNonQuery();
            return RedirectToPage();
        }

        public IActionResult OnPostEdit(int id)
        {
            using var con = _db.GetConnection();
            var cmd = new SqlCommand(
                "SELECT * FROM UnitMaster WHERE UnitId=@id", con);
            cmd.Parameters.AddWithValue("@id", id);

            con.Open();
            var r = cmd.ExecuteReader();
            if (r.Read())
            {
                Unit = new UnitMaster
                {
                    UnitId = (int)r["UnitId"],
                    UnitName = r["UnitName"].ToString(),
                    UnitSymbol = r["UnitSymbol"].ToString()
                };
            }
            LoadData();
            return Page();
        }

        public IActionResult OnPostDelete(int id)
        {
            using var con = _db.GetConnection();
            var cmd = new SqlCommand(
                "DELETE FROM UnitMaster WHERE UnitId=@id", con);
            cmd.Parameters.AddWithValue("@id", id);

            con.Open();
            cmd.ExecuteNonQuery();
            return RedirectToPage();
        }

        private void LoadData()
        {
            using var con = _db.GetConnection();
            var cmd = new SqlCommand("SELECT * FROM UnitMaster", con);
            con.Open();
            var r = cmd.ExecuteReader();
            while (r.Read())
            {
                Units.Add(new UnitMaster
                {
                    UnitId = (int)r["UnitId"],
                    UnitName = r["UnitName"].ToString(),
                    UnitSymbol = r["UnitSymbol"].ToString()
                });
            }
        }
    }
}
