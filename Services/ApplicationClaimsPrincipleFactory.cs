using Microsoft.AspNetCore.Identity;
using Microsoft.Data.SqlClient;
using PlantStockManager.Data;
using PlantStockManager.Models;
using System.Security.Claims;

namespace PlantStockManager.Services
{
    public class ApplicationClaimsPrincipalFactory : IUserClaimsPrincipalFactory<ApplicationUser>
    {
        private readonly DatabaseHelper _dbHelper;

        public ApplicationClaimsPrincipalFactory(DatabaseHelper dbHelper)
        {
            _dbHelper = dbHelper;
        }

        public async Task<ClaimsPrincipal> CreateAsync(ApplicationUser user)
        {
            var identity = new ClaimsIdentity(IdentityConstants.ApplicationScheme);
            identity.AddClaim(new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()));
            identity.AddClaim(new Claim(ClaimTypes.Name, user.UserName));

            // Get role name from database
            using var connection = _dbHelper.GetConnection();
            await connection.OpenAsync();

            var command = new SqlCommand(
                "SELECT RoleName FROM Roles WHERE Id = @RoleId",
                connection);
            command.Parameters.AddWithValue("@RoleId", user.RoleId);

            var roleName = (await command.ExecuteScalarAsync())?.ToString();
            if (!string.IsNullOrEmpty(roleName))
            {
                identity.AddClaim(new Claim(ClaimTypes.Role, roleName));
            }

            return new ClaimsPrincipal(identity);
        }
    }
}
