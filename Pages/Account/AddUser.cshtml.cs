using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Data.SqlClient;
using PlantStockManager.Data;
using PlantStockManager.Models;

namespace PlantStockManager.Pages.Account
{
    public class AddUserModel : PageModel
    {
        private readonly DatabaseHelper _dbHelper;
        private readonly IPasswordHasher<User> _passwordHasher;

        public AddUserModel(DatabaseHelper dbHelper, IPasswordHasher<User> passwordHasher)
        {
            _dbHelper = dbHelper;
            _passwordHasher = passwordHasher;
        }

        [BindProperty] public string Username { get; set; }
        [BindProperty] public string Password { get; set; }
        [BindProperty] public string Role { get; set; }
        public string Message { get; set; }

        public void OnGet() { }

        public async Task<IActionResult> OnPostAsync()
        {
            if (!ModelState.IsValid) return Page();

            var user = new User { Username = Username, Role = Role };
            var hashedPassword = _passwordHasher.HashPassword(user, Password);

            using var conn = _dbHelper.GetConnection();
            await conn.OpenAsync();

            var cmd = new SqlCommand("INSERT INTO IMSUsers (Username, Password, Role) VALUES (@Username, @PasswordHash, @Role)", conn);
            cmd.Parameters.AddWithValue("@Username", Username);
            cmd.Parameters.AddWithValue("@PasswordHash", hashedPassword);
            cmd.Parameters.AddWithValue("@Role", Role);

            await cmd.ExecuteNonQueryAsync();

            Message = "User added successfully!";
            ModelState.Clear();
            return Page();
        }

    
    }
}
