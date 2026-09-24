using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Data.SqlClient;
using PlantStockManager.Authorization;
using PlantStockManager.Data;

namespace PlantStockManager.Pages.Account
{
    public class LoginModel : PageModel
    {
        private readonly IConfiguration _config;
        private readonly DatabaseHelper _db;
        private readonly UserClaimsFactory _claimsFactory;

        public LoginModel(IConfiguration config, DatabaseHelper db, UserClaimsFactory claimsFactory)
        {
            _config = config;
            _db = db;
            _claimsFactory = claimsFactory;
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
WHERE u.Username = @u and u.IsActive = 1;";

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
                    // F1: claims are now built by UserClaimsFactory -- the
                    // same claim types/values as before (NameIdentifier,
                    // Name, UserId, DesignationId, Role, DesignationName,
                    // Permission, RoleName, AreaAccess), shared with the
                    // cookie's periodic OnValidatePrincipal re-check so a
                    // deactivated user or a changed UserRoles assignment
                    // takes effect without waiting for the 7-day cookie to
                    // expire. See Authorization/UserClaimsFactory.cs.
                    await reader.CloseAsync();
                    var principal = await _claimsFactory.CreatePrincipalAsync(
                        new UserClaimsFactory.UserIdentityRow(userId, dbUsername, designationId, designationName));

                    await HttpContext.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, principal);
                    return RedirectToPage("/Index");
                }
            }

            ErrorMessage = "Invalid username or password.";
            return Page();
        }
    }
}
