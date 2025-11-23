using Microsoft.AspNetCore.Identity;
using Microsoft.Data.SqlClient;
using PlantStockManager.Models;
using PlantStockManager.Data;

namespace PlantStockManager.Services
{


    public class ApplicationUserStore : IUserStore<ApplicationUser>,  IUserPasswordStore<ApplicationUser>
    {
        private readonly DatabaseHelper _dbHelper;

        public ApplicationUserStore(DatabaseHelper dbHelper)
        {
            _dbHelper = dbHelper;
        }

        // Required IUserStore methods
        public async Task<IdentityResult> CreateAsync(ApplicationUser user, CancellationToken cancellationToken)
        {
            using var connection = _dbHelper.GetConnection();
            await connection.OpenAsync(cancellationToken);

            var command = new SqlCommand(
                "INSERT INTO Users (UserName, PasswordHash, RoleId) OUTPUT INSERTED.Id VALUES (@UserName, @PasswordHash, @RoleId)",
                connection);

            command.Parameters.AddWithValue("@UserName", user.UserName);
            command.Parameters.AddWithValue("@PasswordHash", user.PasswordHash);
            command.Parameters.AddWithValue("@RoleId", user.RoleId);

            user.Id = (int)await command.ExecuteScalarAsync(cancellationToken);
            return IdentityResult.Success;
        }

        public async Task<IdentityResult> DeleteAsync(ApplicationUser user, CancellationToken cancellationToken)
        {
            using var connection = _dbHelper.GetConnection();
            await connection.OpenAsync(cancellationToken);

            var command = new SqlCommand(
                "DELETE FROM Users WHERE Id = @Id",
                connection);
            command.Parameters.AddWithValue("@Id", user.Id);

            await command.ExecuteNonQueryAsync(cancellationToken);
            return IdentityResult.Success;
        }

        public async Task<ApplicationUser> FindByIdAsync(string userId, CancellationToken cancellationToken)
        {
            using var connection = _dbHelper.GetConnection();
            await connection.OpenAsync(cancellationToken);

            var command = new SqlCommand(
                "SELECT * FROM Users WHERE Id = @Id",
                connection);
            command.Parameters.AddWithValue("@Id", userId);

            using var reader = await command.ExecuteReaderAsync(cancellationToken);
            return await reader.ReadAsync(cancellationToken) ? MapUser(reader) : null;
        }

        public async Task<ApplicationUser> FindByNameAsync(string normalizedUserName, CancellationToken cancellationToken)
        {
            using var connection = _dbHelper.GetConnection();
            await connection.OpenAsync(cancellationToken);

            var command = new SqlCommand(
                "SELECT * FROM Users WHERE UserName = @UserName",
                connection);
            command.Parameters.AddWithValue("@UserName", normalizedUserName);

            using var reader = await command.ExecuteReaderAsync(cancellationToken);
            return await reader.ReadAsync(cancellationToken) ? MapUser(reader) : null;
        }

        public Task<string> GetNormalizedUserNameAsync(ApplicationUser user, CancellationToken cancellationToken)
        {
            return Task.FromResult(user.UserName.ToUpper());
        }

        public Task<string> GetUserIdAsync(ApplicationUser user, CancellationToken cancellationToken)
        {
            return Task.FromResult(user.Id.ToString());
        }

        public Task<string> GetUserNameAsync(ApplicationUser user, CancellationToken cancellationToken)
        {
            return Task.FromResult(user.UserName);
        }

        public Task SetNormalizedUserNameAsync(ApplicationUser user, string normalizedName, CancellationToken cancellationToken)
        {
            // No action needed since we're not using normalized names
            return Task.CompletedTask;
        }

        public Task SetUserNameAsync(ApplicationUser user, string userName, CancellationToken cancellationToken)
        {
            user.UserName = userName;
            return Task.CompletedTask;
        }

        public async Task<IdentityResult> UpdateAsync(ApplicationUser user, CancellationToken cancellationToken)
        {
            using var connection = _dbHelper.GetConnection();
            await connection.OpenAsync(cancellationToken);

            var command = new SqlCommand(
                "UPDATE Users SET UserName = @UserName, PasswordHash = @PasswordHash, RoleId = @RoleId WHERE Id = @Id",
                connection);

            command.Parameters.AddWithValue("@Id", user.Id);
            command.Parameters.AddWithValue("@UserName", user.UserName);
            command.Parameters.AddWithValue("@PasswordHash", user.PasswordHash);
            command.Parameters.AddWithValue("@RoleId", user.RoleId);

            await command.ExecuteNonQueryAsync(cancellationToken);
            return IdentityResult.Success;
        }

        // IUserPasswordStore methods
        public Task<string> GetPasswordHashAsync(ApplicationUser user, CancellationToken cancellationToken)
        {
            return Task.FromResult(user.PasswordHash);
        }

        public Task<bool> HasPasswordAsync(ApplicationUser user, CancellationToken cancellationToken)
        {
            return Task.FromResult(!string.IsNullOrEmpty(user.PasswordHash));
        }

        public Task SetPasswordHashAsync(ApplicationUser user, string passwordHash, CancellationToken cancellationToken)
        {
            user.PasswordHash = passwordHash;
            return Task.CompletedTask;
        }

        private ApplicationUser MapUser(SqlDataReader reader)
        {
            return new ApplicationUser
            {
                Id = reader.GetInt32(0),
                UserName = reader.GetString(1),
                PasswordHash = reader.GetString(2),
                RoleId = reader.GetInt32(3)
            };
        }

        public void Dispose()
        {
            // No resources to dispose
        }

        // Optional: Implement other interface methods you might need
    }
}
