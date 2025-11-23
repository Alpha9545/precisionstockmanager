using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Data.SqlClient;
using PlantStockManager.Data;
using System.Security.Claims;

namespace PlantStockManager.Pages.Account
{
    public class LoginModel : PageModel
    {
        private readonly IConfiguration _config;
        private readonly DatabaseHelper _db;

        public LoginModel(IConfiguration config, DatabaseHelper db)
        {
            _config = config;
            _db = db;
        }

        [BindProperty] public string Username { get; set; } = "";
        [BindProperty] public string Password { get; set; } = "";
        public string ErrorMessage { get; set; } = "";

        public void OnGet() { }

        public async Task<IActionResult> OnPostAsync()
        {
            if (string.IsNullOrWhiteSpace(Username) || string.IsNullOrWhiteSpace(Password))
            {
                ErrorMessage = "Username and password are required.";
                return Page();
            }

            using var conn = _db.GetConnection();
            await conn.OpenAsync();

            var sql = @"
SELECT u.Id,
       u.Username,
       u.Password,           -- bcrypt hash
       u.DesignationID,
       d.DesignationName
FROM dbo.IMSUsers u
LEFT JOIN dbo.Designation d ON d.DesignationID = u.DesignationID
WHERE u.Username = @u;";

            using var cmd = new SqlCommand(sql, conn);
            cmd.Parameters.AddWithValue("@u", Username);

            using var reader = await cmd.ExecuteReaderAsync();
            if (await reader.ReadAsync())
            {
                int userId = reader.GetInt32(0);
                string dbUsername = reader.GetString(1);
                string storedHash = reader.GetString(2);
                int? designationId = reader.IsDBNull(3) ? (int?)null : reader.GetInt32(3);
                string? designationName = reader.IsDBNull(4) ? null : reader.GetString(4);

                // Verify BCrypt
                if (BCrypt.Net.BCrypt.Verify(Password, storedHash))
                {
                    var claims = new List<Claim>
                    {
                        new Claim(ClaimTypes.NameIdentifier, userId.ToString()),
                        new Claim(ClaimTypes.Name, dbUsername),
                            new Claim("UserId", userId.ToString()), // ✅ explicit claim for convenience

                        // Store designation id as custom claim:
                        new Claim("DesignationId", designationId?.ToString() ?? string.Empty)
                    };

                    // If you still use [Authorize(Roles="...")], put the DesignationName in Role claim:
                    if (!string.IsNullOrWhiteSpace(designationName))
                    {
                        claims.Add(new Claim(ClaimTypes.Role, designationName));
                        // Optional: also store the readable name as a separate custom claim
                        claims.Add(new Claim("DesignationName", designationName));
                    }

                    var identity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
                    var principal = new ClaimsPrincipal(identity);

                    await HttpContext.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, principal);
                    return RedirectToPage("/Index");
                }
            }

            ErrorMessage = "Invalid username or password.";
            return Page();
        }
    }
}
