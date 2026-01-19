using ClosedXML.Excel;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Data.SqlClient;
using PlantStockManager.Data;
using PlantStockManager.Models;

namespace PlantStockManager.Pages.Fertilizer
{
    public class SourceModel : PageModel
    {

        private readonly DatabaseHelper _db;
        public SourceModel(DatabaseHelper db)
        {
            _db = db;
        }

        public List<FertilizerSource> Sources { get; set; } = new();

        [BindProperty]
        public FertilizerSource Source { get; set; }

        public void OnGet()
        {
            LoadData();
        }

        // INSERT + UPDATE
        public IActionResult OnPostSave()
        {
            using var con = _db.GetConnection();

            string sql = Source.SourceId == 0
                ? @"INSERT INTO FertilizerSource
                (SourceName, ContactPerson, Phone, Address)
                VALUES (@n, @c, @p, @a)"
                : @"UPDATE FertilizerSource
                SET SourceName=@n, ContactPerson=@c, Phone=@p, Address=@a
                WHERE SourceId=@id";

            var cmd = new SqlCommand(sql, con);
            cmd.Parameters.AddWithValue("@n", Source.SourceName);
            cmd.Parameters.AddWithValue("@c", (object?)Source.ContactPerson ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@p", (object?)Source.Phone ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@a", (object?)Source.Address ?? DBNull.Value);

            if (Source.SourceId != 0)
                cmd.Parameters.AddWithValue("@id", Source.SourceId);

            con.Open();
            cmd.ExecuteNonQuery();

            return RedirectToPage();
        }

        // EDIT (Load data into form)
        public IActionResult OnPostEdit(int id)
        {
            using var con = _db.GetConnection();
            var cmd = new SqlCommand(
                "SELECT * FROM FertilizerSource WHERE SourceId=@id", con);
            cmd.Parameters.AddWithValue("@id", id);

            con.Open();
            var r = cmd.ExecuteReader();

            if (r.Read())
            {
                Source = new FertilizerSource
                {
                    SourceId = (int)r["SourceId"],
                    SourceName = r["SourceName"].ToString(),
                    ContactPerson = r["ContactPerson"]?.ToString(),
                    Phone = r["Phone"]?.ToString(),
                    Address = r["Address"]?.ToString()
                };
            }

            LoadData();
            return Page();
        }

        // DELETE
        public IActionResult OnPostDelete(int id)
        {
            using var con = _db.GetConnection();
            var cmd = new SqlCommand(
                "DELETE FROM FertilizerSource WHERE SourceId=@id", con);
            cmd.Parameters.AddWithValue("@id", id);

            con.Open();
            cmd.ExecuteNonQuery();

            return RedirectToPage();
        }

        private void LoadData()
        {
            Sources.Clear();

            using var con = _db.GetConnection();
            var cmd = new SqlCommand("SELECT * FROM FertilizerSource", con);
            con.Open();

            var r = cmd.ExecuteReader();
            while (r.Read())
            {
                Sources.Add(new FertilizerSource
                {
                    SourceId = (int)r["SourceId"],
                    SourceName = r["SourceName"].ToString(),
                    ContactPerson = r["ContactPerson"]?.ToString(),
                    Phone = r["Phone"]?.ToString(),
                    Address = r["Address"]?.ToString()
                });
            }
        }
    }
}
