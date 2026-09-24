using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Data.SqlClient;
using PlantStockManager.Authorization;
using PlantStockManager.Data;
using PlantStockManager.Models;

namespace PlantStockManager.Pages.Admin
{
    // User Management (Phase A).
    //
    // Add User = Username, Name, Password, ROLE, Area (optional), Active.
    // Saving creates the dbo.IMSUsers row AND its dbo.UserRoles row in ONE
    // transaction. The user's permissions then come ONLY from that Role's
    // dbo.RolePermissions -- no per-user permission rows are ever written.
    // dbo.IMSUsers.DesignationID (legacy job title) is kept in step with the
    // Role when a Designation of the same name exists, purely for display;
    // it grants nothing.
    //
    // Page access: "Admin.ManageUsers" (Authorization/FeatureAuthorizationConventions.cs).
    // Anti-escalation (AdministrativeAccessGuard + checks below):
    //   * nobody may change their own role/area or deactivate themselves;
    //   * only a full-access user (System Administrator) may assign an
    //     administrative role, or edit / reset the password of / deactivate
    //     ANOTHER administrator account.
    // Target state is always read from the database, never from the form.
    public class UsersModel : PageModel
    {
        private readonly DatabaseHelper _db;
        private readonly RoleRepository _roleRepo;
        private readonly UserRoleRepository _userRoleRepo;
        private readonly AreaRepository _areaRepo;
        private readonly AdministrativeAccessGuard _guard;

        public UsersModel(
            DatabaseHelper dbHelper,
            RoleRepository roleRepo,
            UserRoleRepository userRoleRepo,
            AreaRepository areaRepo,
            AdministrativeAccessGuard guard)
        {
            _db = dbHelper;
            _roleRepo = roleRepo;
            _userRoleRepo = userRoleRepo;
            _areaRepo = areaRepo;
            _guard = guard;
        }

        public List<UserRow> Users { get; set; } = new();
        public List<Role> Roles { get; set; } = new();
        public List<Area> Areas { get; set; } = new();
        public bool CanManageAdministrators { get; set; }

        [BindProperty(SupportsGet = true)]
        public bool ShowInactive { get; set; }

        [TempData]
        public string? WarningMessage { get; set; }

        [TempData]
        public string? SuccessMessage { get; set; }

        public async Task OnGetAsync()
        {
            await LoadAsync();
        }

        // ---------- ADD ----------
        public async Task<IActionResult> OnPostAddAsync(
            [Bind(Prefix = "NewUser")] NewUserVM newUser,
            [FromForm] string? NewPassword)
        {
            if (!TryValidateModel(newUser, nameof(NewUserVM)) || string.IsNullOrWhiteSpace(NewPassword) || newUser.RoleId is null or <= 0)
                return Deny("Username, Name, Password and Role are required.");

            var role = await _roleRepo.GetRoleByIdAsync(newUser.RoleId.Value);
            if (role == null)
                return Deny("Selected role does not exist.");

            if (!_guard.CanManageAdministrators(User) && await _guard.IsAdministrativeRoleAsync(role.Id))
                return Deny("Only a System Administrator can create a user with an administrative role.");

            if (newUser.AreaId.HasValue && await _areaRepo.GetAreaById(newUser.AreaId.Value) == null)
                return Deny("Selected Area does not exist.");

            var passwordHash = BCrypt.Net.BCrypt.HashPassword(NewPassword);

            using var conn = _db.GetConnection();
            await conn.OpenAsync();
            using var tx = conn.BeginTransaction();
            try
            {
                using (var checkCmd = new SqlCommand(@"
SELECT 1 FROM dbo.IMSUsers WITH (UPDLOCK, HOLDLOCK)
WHERE Username = @Username OR (Email IS NOT NULL AND Email = @Email);", conn, tx))
                {
                    checkCmd.Parameters.AddWithValue("@Username", newUser.Username.Trim());
                    checkCmd.Parameters.AddWithValue("@Email", (object?)NullIfBlank(newUser.Email) ?? DBNull.Value);
                    if (await checkCmd.ExecuteScalarAsync() != null)
                    {
                        tx.Rollback();
                        return Deny("Username or Email already exists!");
                    }
                }

                int newUserId;
                using (var cmd = new SqlCommand(@"
INSERT INTO dbo.IMSUsers (Username, Password, Name, Email, DesignationID, IsActive)
VALUES (@Username, @Password, @Name, @Email,
        (SELECT TOP 1 d.DesignationID FROM dbo.Designation d WHERE d.DesignationName = @RoleName),
        @IsActive);
SELECT CAST(SCOPE_IDENTITY() AS INT);", conn, tx))
                {
                    cmd.Parameters.AddWithValue("@Username", newUser.Username.Trim());
                    cmd.Parameters.AddWithValue("@Password", passwordHash);
                    cmd.Parameters.AddWithValue("@Name", newUser.Name.Trim());
                    cmd.Parameters.AddWithValue("@Email", (object?)NullIfBlank(newUser.Email) ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("@RoleName", role.Name);
                    cmd.Parameters.AddWithValue("@IsActive", newUser.IsActive);
                    newUserId = (int)(await cmd.ExecuteScalarAsync())!;
                }

                // The ONLY access record for this user: their Role (+ Area).
                await UserRoleRepository.InsertAssignmentAsync(conn, tx, newUserId, role.Id, newUser.AreaId);

                tx.Commit();
            }
            catch
            {
                tx.Rollback();
                throw;
            }

            SuccessMessage = $"User '{newUser.Username.Trim()}' created with role '{role.Name}'.";
            return RedirectToPage();
        }

        // ---------- EDIT ----------
        public async Task<IActionResult> OnPostEditAsync(
            [Bind(Prefix = "EditUser")] EditUserVM editUser,
            [FromForm] string? EditPassword)
        {
            if (!TryValidateModel(editUser, nameof(EditUserVM)))
                return Deny("Username and Name are required.");

            var target = await GetUserStateAsync(editUser.Id);
            if (target == null)
                return Deny("User not found.");

            var actorId = User.GetUserId();
            var isSelf = actorId == editUser.Id;
            var fullAccess = _guard.CanManageAdministrators(User);

            var assignments = await _userRoleRepo.GetAssignmentsForUserAsync(editUser.Id);
            var current = assignments.Count == 1 ? assignments[0] : null;
            var roleChangeRequested = editUser.RoleId.HasValue
                && assignments.Count <= 1
                && (current == null || current.RoleId != editUser.RoleId.Value || current.AreaId != editUser.AreaId);

            if (isSelf && roleChangeRequested)
                return Deny("You cannot change your own role or area. Ask another administrator.");
            if (isSelf && !editUser.IsActive)
                return Deny("You cannot deactivate your own account.");

            if (!isSelf && !fullAccess && await _guard.IsAdministratorAccountAsync(editUser.Id))
                return Deny("Only a System Administrator can edit, reset the password of, or deactivate an administrator account.");

            Role? newRole = null;
            if (roleChangeRequested)
            {
                newRole = await _roleRepo.GetRoleByIdAsync(editUser.RoleId!.Value);
                if (newRole == null)
                    return Deny("Selected role does not exist.");
                if (!fullAccess && await _guard.IsAdministrativeRoleAsync(newRole.Id))
                    return Deny("Only a System Administrator can assign an administrative role.");
                if (editUser.AreaId.HasValue && await _areaRepo.GetAreaById(editUser.AreaId.Value) == null)
                    return Deny("Selected Area does not exist.");
            }

            using var conn = _db.GetConnection();
            await conn.OpenAsync();
            using var tx = conn.BeginTransaction();
            try
            {
                using (var checkCmd = new SqlCommand(@"
SELECT 1 FROM dbo.IMSUsers
WHERE (Username = @Username OR (Email IS NOT NULL AND Email = @Email))
  AND Id <> @Id;", conn, tx))
                {
                    checkCmd.Parameters.AddWithValue("@Username", editUser.Username.Trim());
                    checkCmd.Parameters.AddWithValue("@Email", (object?)NullIfBlank(editUser.Email) ?? DBNull.Value);
                    checkCmd.Parameters.AddWithValue("@Id", editUser.Id);
                    if (await checkCmd.ExecuteScalarAsync() != null)
                    {
                        tx.Rollback();
                        return Deny("Username or Email already exists.");
                    }
                }

                var setPassword = !string.IsNullOrWhiteSpace(EditPassword);
                using (var cmd = new SqlCommand(@"
UPDATE dbo.IMSUsers
SET Username = @Username,
    Name = @Name,
    Email = @Email,
    IsActive = @IsActive,
    DesignationID = CASE WHEN @RoleName IS NULL THEN DesignationID
                         ELSE COALESCE((SELECT TOP 1 d.DesignationID FROM dbo.Designation d WHERE d.DesignationName = @RoleName), DesignationID) END"
                    + (setPassword ? ",\n    Password = @Password" : "") + @"
WHERE Id = @Id;", conn, tx))
                {
                    cmd.Parameters.AddWithValue("@Username", editUser.Username.Trim());
                    cmd.Parameters.AddWithValue("@Name", editUser.Name.Trim());
                    cmd.Parameters.AddWithValue("@Email", (object?)NullIfBlank(editUser.Email) ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("@IsActive", editUser.IsActive);
                    cmd.Parameters.AddWithValue("@RoleName", (object?)newRole?.Name ?? DBNull.Value);
                    if (setPassword)
                        cmd.Parameters.AddWithValue("@Password", BCrypt.Net.BCrypt.HashPassword(EditPassword));
                    cmd.Parameters.AddWithValue("@Id", editUser.Id);
                    await cmd.ExecuteNonQueryAsync();
                }

                if (newRole != null)
                    await UserRoleRepository.ReplaceAssignmentsAsync(conn, tx, editUser.Id, newRole.Id, editUser.AreaId);

                tx.Commit();
            }
            catch
            {
                tx.Rollback();
                throw;
            }

            SuccessMessage = "User updated." + (assignments.Count > 1 && editUser.RoleId.HasValue
                ? " (This user has several role assignments; change them on User Role / Area Assignments.)"
                : "");
            return RedirectToPage(new { ShowInactive });
        }

        public Task<IActionResult> OnPostDeactivateAsync(int id) => SetActiveAsync(id, false);

        public Task<IActionResult> OnPostActivateAsync(int id) => SetActiveAsync(id, true);

        private async Task<IActionResult> SetActiveAsync(int id, bool active)
        {
            if (!active && User.GetUserId() == id)
                return Deny("You cannot deactivate your own account.");

            if (await GetUserStateAsync(id) == null)
                return Deny("User not found.");

            if (!_guard.CanManageAdministrators(User) && await _guard.IsAdministratorAccountAsync(id))
                return Deny("Only a System Administrator can activate or deactivate an administrator account.");

            using var conn = _db.GetConnection();
            await conn.OpenAsync();
            using var cmd = new SqlCommand("UPDATE dbo.IMSUsers SET IsActive = @IsActive WHERE Id = @Id;", conn);
            cmd.Parameters.AddWithValue("@IsActive", active);
            cmd.Parameters.AddWithValue("@Id", id);
            await cmd.ExecuteNonQueryAsync();

            SuccessMessage = active ? "User activated." : "User deactivated. They are signed out within a few minutes.";
            return RedirectToPage(new { ShowInactive });
        }

        private IActionResult Deny(string message)
        {
            WarningMessage = message;
            return RedirectToPage(new { ShowInactive });
        }

        private static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

        private async Task<(int Id, bool IsActive)?> GetUserStateAsync(int userId)
        {
            using var conn = _db.GetConnection();
            await conn.OpenAsync();
            using var cmd = new SqlCommand("SELECT Id, ISNULL(IsActive, 0) FROM dbo.IMSUsers WHERE Id = @Id;", conn);
            cmd.Parameters.AddWithValue("@Id", userId);
            using var r = await cmd.ExecuteReaderAsync();
            if (!await r.ReadAsync())
                return null;
            return (r.GetInt32(0), r.GetBoolean(1));
        }

        private async Task LoadAsync()
        {
            CanManageAdministrators = _guard.CanManageAdministrators(User);
            var allRoles = await _roleRepo.GetAllRolesAsync();
            if (CanManageAdministrators)
            {
                Roles = allRoles;
            }
            else
            {
                // A non-full-access administrator is not offered administrative roles.
                Roles = new List<Role>();
                foreach (var r in allRoles)
                    if (!await _guard.IsAdministrativeRoleAsync(r.Id))
                        Roles.Add(r);
            }
            Areas = await _areaRepo.GetAllAreas();

            var assignments = (await _userRoleRepo.GetAllAssignmentsAsync()).GroupBy(a => a.UserId).ToDictionary(g => g.Key, g => g.ToList());

            Users = new();
            using var conn = _db.GetConnection();
            await conn.OpenAsync();
            var sql = @"
SELECT u.Id, u.Username, u.Name, u.Email, d.DesignationName, ISNULL(u.IsActive, 0) AS IsActive
FROM dbo.IMSUsers u
LEFT JOIN dbo.Designation d ON d.DesignationID = u.DesignationID
" + (ShowInactive ? "" : "WHERE u.IsActive = 1\n") + "ORDER BY u.Id DESC;";
            using var cmd = new SqlCommand(sql, conn);
            using var r2 = await cmd.ExecuteReaderAsync();
            while (await r2.ReadAsync())
            {
                var id = r2.GetInt32(0);
                var list = assignments.TryGetValue(id, out var a) ? a : new List<UserRoleAssignment>();
                Users.Add(new UserRow
                {
                    Id = id,
                    Username = r2.GetString(1),
                    Name = r2.GetString(2),
                    Email = r2.IsDBNull(3) ? null : r2.GetString(3),
                    DesignationName = r2.IsDBNull(4) ? null : r2.GetString(4),
                    IsActive = r2.GetBoolean(5),
                    Assignments = list
                });
            }
        }

        // ----- DTOs / VMs -----
        public class UserRow
        {
            public int Id { get; set; }
            public string Username { get; set; } = "";
            public string Name { get; set; } = "";
            public string? Email { get; set; }
            public string? DesignationName { get; set; }
            public bool IsActive { get; set; }
            public List<UserRoleAssignment> Assignments { get; set; } = new();
            public UserRoleAssignment? SingleAssignment => Assignments.Count == 1 ? Assignments[0] : null;
        }

        public class NewUserVM
        {
            [Required, MaxLength(100)] public string Username { get; set; } = "";
            [Required, MaxLength(150)] public string Name { get; set; } = "";
            [EmailAddress, MaxLength(256)] public string? Email { get; set; }
            [Required] public int? RoleId { get; set; }
            public int? AreaId { get; set; }
            public bool IsActive { get; set; } = true;
        }

        public class EditUserVM
        {
            [Required] public int Id { get; set; }
            [Required, MaxLength(100)] public string Username { get; set; } = "";
            [Required, MaxLength(150)] public string Name { get; set; } = "";
            [EmailAddress, MaxLength(256)] public string? Email { get; set; }
            public int? RoleId { get; set; }
            public int? AreaId { get; set; }
            public bool IsActive { get; set; } = true;
        }
    }
}
