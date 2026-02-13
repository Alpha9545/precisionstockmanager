using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Data.SqlClient;
using PlantStockManager.Data;
using PlantStockManager.Models;

namespace PlantStockManager.Pages.Admin
{
    public class UsersModel : PageModel
    {
        private readonly DatabaseHelper _db;
        public UsersModel(DatabaseHelper dbHelper) => _db = dbHelper;

        public List<UserRow> Users { get; set; } = new();
        public List<Designation> Designations { get; set; } = new();

        [TempData]
        public string? WarningMessage { get; set; }

        public async Task OnGetAsync()
        {
            await LoadDesignations();
            await LoadUsers();
        }

        private async Task LoadDesignations()
        {
            Designations = new();
            using var conn = _db.GetConnection();
            await conn.OpenAsync();
            var cmd = new SqlCommand("SELECT DesignationID, DesignationName FROM Designation ORDER BY DesignationName", conn);
            using var r = await cmd.ExecuteReaderAsync();
            while (await r.ReadAsync())
            {
                Designations.Add(new Designation
                {
                    DesignationId = r.GetInt32(0),
                    DesignationName = r.GetString(1)
                });
            }
        }

        private async Task LoadUsers()
        {
            Users = new();
            using var conn = _db.GetConnection();
            await conn.OpenAsync();
            var sql = @"
SELECT u.Id, u.Username, u.Name, u.Email, u.DesignationID, d.DesignationName
FROM dbo.IMSUsers u
LEFT JOIN dbo.Designation d ON d.DesignationID = u.DesignationID
WHERE u.IsActive = 1    
ORDER BY u.Id DESC;";
            using var cmd = new SqlCommand(sql, conn);
            using var r = await cmd.ExecuteReaderAsync();
            while (await r.ReadAsync())
            {
                Users.Add(new UserRow
                {
                    Id = r.GetInt32(0),
                    Username = r.GetString(1),
                    Name = r.GetString(2),
                    Email = r.IsDBNull(3) ? null : r.GetString(3),
                    DesignationId = r.IsDBNull(4) ? (int?)null : r.GetInt32(4),
                    DesignationName = r.IsDBNull(5) ? null : r.GetString(5)
                });
            }
        }

        // ---------- ADD ----------
        public async Task<IActionResult> OnPostAddAsync(
            [Bind(Prefix = "NewUser")] NewUserVM newUser,
            [FromForm] string NewPassword)
        {
            // Validate only the Add form
            if (!TryValidateModel(newUser, nameof(NewUserVM)) || string.IsNullOrWhiteSpace(NewPassword))
            {
                if (string.IsNullOrWhiteSpace(NewPassword))
                    ModelState.AddModelError("NewPassword", "Password is required.");

                await OnGetAsync();
                return Page();
            }

            var passwordHash = BCrypt.Net.BCrypt.HashPassword(NewPassword);

            using var conn = _db.GetConnection();
            await conn.OpenAsync();

            var checkCmd = new SqlCommand(@"
SELECT 1 FROM dbo.IMSUsers
WHERE Username = @Username OR (Email IS NOT NULL AND Email = @Email);", conn);
            checkCmd.Parameters.AddWithValue("@Username", newUser.Username);
            checkCmd.Parameters.AddWithValue("@Email", (object?)newUser.Email ?? DBNull.Value);
            var exists = await checkCmd.ExecuteScalarAsync();
            if (exists != null)
            {
                WarningMessage = "⚠️ Username or Email already exists!";
                await OnGetAsync();
                return Page();
            }

            var insertSql = @"
INSERT INTO dbo.IMSUsers (Username, Password, Name, Email, DesignationID, IsActive)
VALUES (@Username, @Password, @Name, @Email, @DesignationId, 1);";
            using var cmd = new SqlCommand(insertSql, conn);
            cmd.Parameters.AddWithValue("@Username", newUser.Username);
            cmd.Parameters.AddWithValue("@Password", passwordHash);
            cmd.Parameters.AddWithValue("@Name", newUser.Name);
            cmd.Parameters.AddWithValue("@Email", (object?)newUser.Email ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@DesignationId", (object?)newUser.DesignationId ?? DBNull.Value);
            await cmd.ExecuteNonQueryAsync();

            return RedirectToPage();
        }

        // ---------- EDIT ----------
        public async Task<IActionResult> OnPostEditAsync(
            [Bind(Prefix = "EditUser")] EditUserVM editUser,
            [FromForm] string? EditPassword)
        {
            if (!TryValidateModel(editUser, nameof(EditUserVM)))
            {
                await OnGetAsync();
                return Page();
            }

            using var conn = _db.GetConnection();
            await conn.OpenAsync();

            var checkSql = @"
SELECT 1 FROM dbo.IMSUsers
WHERE (Username = @Username OR (Email IS NOT NULL AND Email = @Email))
  AND Id <> @Id;";
            using (var checkCmd = new SqlCommand(checkSql, conn))
            {
                checkCmd.Parameters.AddWithValue("@Username", editUser.Username);
                checkCmd.Parameters.AddWithValue("@Email", (object?)editUser.Email ?? DBNull.Value);
                checkCmd.Parameters.AddWithValue("@Id", editUser.Id);
                var exists = await checkCmd.ExecuteScalarAsync();
                if (exists != null)
                {
                    ModelState.AddModelError(string.Empty, "Username or Email already exists.");
                    await OnGetAsync();
                    return Page();
                }
            }

            string sql;
            if (!string.IsNullOrWhiteSpace(EditPassword))
            {
                var newHash = BCrypt.Net.BCrypt.HashPassword(EditPassword);
                sql = @"
UPDATE dbo.IMSUsers
SET Username = @Username,
    Name = @Name,
    Email = @Email,
    DesignationID = @DesignationId,
    Password = @Password
WHERE Id = @Id;";
                using var cmd = new SqlCommand(sql, conn);
                cmd.Parameters.AddWithValue("@Username", editUser.Username);
                cmd.Parameters.AddWithValue("@Name", editUser.Name);
                cmd.Parameters.AddWithValue("@Email", (object?)editUser.Email ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@DesignationId", (object?)editUser.DesignationId ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@Password", newHash);
                cmd.Parameters.AddWithValue("@Id", editUser.Id);
                await cmd.ExecuteNonQueryAsync();
            }
            else
            {
                sql = @"
UPDATE dbo.IMSUsers
SET Username = @Username,
    Name = @Name,
    Email = @Email,
    DesignationID = @DesignationId
WHERE Id = @Id;";
                using var cmd = new SqlCommand(sql, conn);
                cmd.Parameters.AddWithValue("@Username", editUser.Username);
                cmd.Parameters.AddWithValue("@Name", editUser.Name);
                cmd.Parameters.AddWithValue("@Email", (object?)editUser.Email ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@DesignationId", (object?)editUser.DesignationId ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@Id", editUser.Id);
                await cmd.ExecuteNonQueryAsync();
            }

            return RedirectToPage();
        }

        public async Task<IActionResult> OnPostDeactivateAsync(int id)
{
    using var conn = _db.GetConnection();
    await conn.OpenAsync();

    var cmd = new SqlCommand(@"
UPDATE dbo.IMSUsers
SET IsActive = 0
WHERE Id = @Id;", conn);

    cmd.Parameters.AddWithValue("@Id", id);
    await cmd.ExecuteNonQueryAsync();

    return RedirectToPage();
}


        // ----- DTOs / VMs -----
        public class UserRow
        {
            public int Id { get; set; }
            public string Username { get; set; } = "";
            public string Name { get; set; } = "";
            public string? Email { get; set; }
            public int? DesignationId { get; set; }
            public string? DesignationName { get; set; }
                public bool IsActive { get; set; }   // ✅ ADD
        }

        public class NewUserVM
        {
            [Required, MaxLength(100)] public string Username { get; set; } = "";
            [Required, MaxLength(150)] public string Name { get; set; } = "";
            [EmailAddress, MaxLength(256)] public string? Email { get; set; }
            [Required] public int? DesignationId { get; set; }
        }

        public class EditUserVM
        {
            [Required] public int Id { get; set; }
            [Required, MaxLength(100)] public string Username { get; set; } = "";
            [Required, MaxLength(150)] public string Name { get; set; } = "";
            [EmailAddress, MaxLength(256)] public string? Email { get; set; }
            [Required] public int? DesignationId { get; set; }
        }
    }
}
