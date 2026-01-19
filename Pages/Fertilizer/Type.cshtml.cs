using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Data.SqlClient;
using PlantStockManager.Data;
using PlantStockManager.Models;

namespace PlantStockManager.Pages.Fertilizer
{
    public class TypeModel : PageModel
    {
        private readonly DatabaseHelper _db;

        public TypeModel(DatabaseHelper dbHelper)
        {
            _db = dbHelper;
        }

        public List<FertilizerType> Types { get; set; } = new();

        [BindProperty]
        public FertilizerType Type { get; set; }

        public void OnGet()
        {
            LoadData();
        }

        public IActionResult OnPostSave()
        {
            // Validate binding
            if (Type == null || !ModelState.IsValid)
            {
                // Debug: capture posted values and modelstate errors
                var form = Request.Form.ToDictionary(k => k.Key, v => v.Value.ToString());
                var errors = ModelState
                    .Where(kv => kv.Value.Errors.Count > 0)
                    .Select(kv => new { Key = kv.Key, Errors = kv.Value.Errors.Select(e => e.ErrorMessage).ToArray() })
                    .ToList();

                // Store for quick inspection (or use logging)
                TempData["debugForm"] = System.Text.Json.JsonSerializer.Serialize(form);
                TempData["debugModelState"] = System.Text.Json.JsonSerializer.Serialize(errors);

                LoadData();
                return Page();
            }

            using var con = _db.GetConnection();
            var sql = Type.FertilizerTypeId == 0
                ? "INSERT INTO FertilizerType(TypeName,Description) VALUES(@n,@s)"
                : "UPDATE FertilizerType SET TypeName=@n, Description=@s WHERE FertilizerTypeId=@id";

            using var cmd = new SqlCommand(sql, con);

            // Ensure nulls are sent as DBNull.Value and parameter names match SQL
            cmd.Parameters.AddWithValue("@n", (object?)Type.TypeName ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@s", (object?)Type.Description ?? DBNull.Value);
            if (Type.FertilizerTypeId != 0)
                cmd.Parameters.AddWithValue("@id", Type.FertilizerTypeId);

            con.Open();
            cmd.ExecuteNonQuery();
            return RedirectToPage();
        }

        public IActionResult OnPostEdit(int id)
        {
            using var con = _db.GetConnection();
            var cmd = new SqlCommand(
                "SELECT * FROM FertilizerType WHERE FertilizerTypeId=@id", con);
            cmd.Parameters.AddWithValue("@id", id);

            con.Open();
            var r = cmd.ExecuteReader();
            if (r.Read())
            {
                Type = new FertilizerType
                {
                    FertilizerTypeId = (int)r["FertilizerTypeId"],
                    TypeName = r["TypeName"].ToString(),
                    Description = r["Description"].ToString()
                };
            }
            LoadData();
            return Page();
        }

        public IActionResult OnPostDelete(int id)
        {
            using var con = _db.GetConnection();
            var cmd = new SqlCommand(
                "DELETE FROM FertilizerType WHERE FertilizerTypeId=@id", con);
            cmd.Parameters.AddWithValue("@id", id);

            con.Open();
            cmd.ExecuteNonQuery();
            return RedirectToPage();
        }

        private void LoadData()
        {
            using var con = _db.GetConnection();
            var cmd = new SqlCommand("SELECT * FROM FertilizerType", con);
            con.Open();
            var r = cmd.ExecuteReader();
            while (r.Read())
            {
                Types.Add(new FertilizerType
                {
                    FertilizerTypeId = (int)r["FertilizerTypeId"],
                    TypeName = r["TypeName"].ToString(),
                    Description = r["Description"].ToString()
                });
            }
        }
    }
}
