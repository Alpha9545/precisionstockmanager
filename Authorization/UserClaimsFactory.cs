using System.Globalization;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;
using PlantStockManager.Data;

namespace PlantStockManager.Authorization
{
    // The ONE place the authentication cookie's claims are computed, used by
    // BOTH Login and the cookie's periodic OnValidatePrincipal revalidation
    // (Program.cs), so the claim set is identical either way and always
    // reflects the database within SecurityOptions.PrincipalRevalidationMinutes.
    //
    // Access is decided ONLY by the user's roles:
    //   dbo.IMSUsers -> dbo.UserRoles -> dbo.Roles -> dbo.RolePermissions
    //   -> dbo.Permissions -> "Permission" claims.
    // Multiple roles are combined (union of their permissions). There are no
    // per-user permission rows. dbo.Designation (job title) is kept only as
    // a display value -- it grants nothing.
    //
    // Claims stamped:
    //   NameIdentifier / Name / UserId            -- identity
    //   DesignationId / DesignationName           -- display only
    //   Role + RoleName (one per assigned role)   -- RoleName is read by AreaAccessService
    //   Permission (one per code from the roles)  -- read by MinimumAuthorizationLevelHandler
    //   AreaAccess (one per Area-scoped UserRoles row)
    //   FullAccess=true  when any assigned role is a full-access role
    //                    (SecurityOptions.FullAccessRoleNames, default
    //                    "System Administrator"): passes every permission
    //                    policy and every Area check without RolePermissions.
    //   AuthValidatedUtc -- time of the last database re-check
    public class UserClaimsFactory
    {
        public const string ValidatedUtcClaimType = "AuthValidatedUtc";

        private readonly DatabaseHelper _db;
        private readonly UserRoleRepository _userRoleRepo;
        private readonly SecurityOptions _options;

        public UserClaimsFactory(
            DatabaseHelper db,
            UserRoleRepository userRoleRepo,
            IOptions<SecurityOptions> options)
        {
            _db = db;
            _userRoleRepo = userRoleRepo;
            _options = options.Value;
        }

        public sealed record UserIdentityRow(int Id, string Username, int? DesignationId, string? DesignationName);

        // Loads an ACTIVE user by id (null when missing or IsActive = 0).
        public async Task<UserIdentityRow?> GetActiveUserAsync(int userId)
        {
            using var conn = _db.GetConnection();
            await conn.OpenAsync();
            const string sql = @"
SELECT u.Id, u.Username, u.DesignationID, d.DesignationName
FROM dbo.IMSUsers u
LEFT JOIN dbo.Designation d ON d.DesignationID = u.DesignationID
WHERE u.Id = @Id AND u.IsActive = 1;";
            using var cmd = new SqlCommand(sql, conn);
            cmd.Parameters.AddWithValue("@Id", userId);
            using var r = await cmd.ExecuteReaderAsync();
            if (!await r.ReadAsync())
                return null;
            return new UserIdentityRow(
                r.GetInt32(0),
                r.GetString(1),
                r.IsDBNull(2) ? null : r.GetInt32(2),
                r.IsDBNull(3) ? null : r.GetString(3));
        }

        public async Task<ClaimsPrincipal> CreatePrincipalAsync(UserIdentityRow user)
        {
            var claims = new List<Claim>
            {
                new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()),
                new Claim(ClaimTypes.Name, user.Username),
                new Claim("UserId", user.Id.ToString()),
                new Claim("DesignationId", user.DesignationId?.ToString() ?? string.Empty),
                new Claim(ValidatedUtcClaimType, DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture))
            };

            if (!string.IsNullOrWhiteSpace(user.DesignationName))
                claims.Add(new Claim("DesignationName", user.DesignationName));

            var roleNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var areaIds = new HashSet<int>();
            foreach (var assignment in await _userRoleRepo.GetAssignmentsForUserAsync(user.Id))
            {
                if (!string.IsNullOrWhiteSpace(assignment.RoleName))
                    roleNames.Add(assignment.RoleName.Trim());
                if (assignment.AreaId.HasValue)
                    areaIds.Add(assignment.AreaId.Value);
            }

            foreach (var role in roleNames)
            {
                claims.Add(new Claim(ClaimTypes.Role, role));
                claims.Add(new Claim(AreaAccessService.RoleNameClaimType, role));
            }

            if (roleNames.Any(_options.IsFullAccessRole))
                claims.Add(new Claim(ClaimsPrincipalSecurityExtensions.FullAccessClaimType, "true"));

            // Union of the permissions of ALL the user's roles.
            foreach (var code in (await _userRoleRepo.GetPermissionCodesForUserAsync(user.Id)).Distinct(StringComparer.Ordinal))
                claims.Add(new Claim(MinimumAuthorizationLevelHandler.PermissionClaimType, code));

            foreach (var areaId in areaIds)
                claims.Add(new Claim(AreaAccessService.AreaAccessClaimType, areaId.ToString()));

            var identity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
            return new ClaimsPrincipal(identity);
        }

        // Cookie OnValidatePrincipal hook (wired in Program.cs). Within the
        // configured interval the cookie is trusted as-is; after it, the
        // user is re-loaded: inactive/deleted -> rejected (signed out),
        // otherwise the principal is rebuilt from the current database
        // state and the cookie is re-issued.
        public static async Task ValidatePrincipalAsync(CookieValidatePrincipalContext context)
        {
            var principal = context.Principal;
            if (principal?.Identity?.IsAuthenticated != true)
                return;

            var services = context.HttpContext.RequestServices;
            var options = services.GetRequiredService<IOptions<SecurityOptions>>().Value;

            var validatedClaim = principal.FindFirst(ValidatedUtcClaimType)?.Value;
            if (long.TryParse(validatedClaim, NumberStyles.Integer, CultureInfo.InvariantCulture, out var validatedUnix))
            {
                var age = DateTimeOffset.UtcNow - DateTimeOffset.FromUnixTimeSeconds(validatedUnix);
                if (age >= TimeSpan.Zero && age < TimeSpan.FromMinutes(Math.Max(0, options.PrincipalRevalidationMinutes)))
                    return;
            }
            // A cookie issued before F1 has no AuthValidatedUtc claim and is
            // therefore re-checked immediately on its next request.

            if (!int.TryParse(principal.FindFirst("UserId")?.Value, out var userId))
            {
                await RejectAsync(context);
                return;
            }

            var factory = services.GetRequiredService<UserClaimsFactory>();
            var user = await factory.GetActiveUserAsync(userId);
            if (user == null)
            {
                await RejectAsync(context);
                return;
            }

            context.ReplacePrincipal(await factory.CreatePrincipalAsync(user));
            context.ShouldRenew = true;
        }

        private static async Task RejectAsync(CookieValidatePrincipalContext context)
        {
            context.RejectPrincipal();
            await context.HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        }
    }
}
