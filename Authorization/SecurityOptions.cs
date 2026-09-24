namespace PlantStockManager.Authorization
{
    // Security knobs bound from the "Authorization" configuration section
    // (appsettings / environment variables, e.g.
    // Authorization__PrincipalRevalidationMinutes=5). Every value has a safe
    // default, so the application behaves correctly when the section is
    // absent (appsettings.json is not committed to the repository).
    public class SecurityOptions
    {
        public const string SectionName = "Authorization";

        public const string DefaultFullAccessRoleName = "System Administrator";

        // Roles (dbo.Roles.Name) whose holders automatically pass EVERY
        // feature-permission policy and see every Area -- without needing
        // any dbo.RolePermissions rows. Default: "System Administrator".
        // Left null on purpose: the configuration binder APPENDS to a
        // pre-populated array instead of replacing it, so the default is
        // applied in EffectiveFullAccessRoleNames.
        public string[]? FullAccessRoleNames { get; set; }

        public IReadOnlyCollection<string> EffectiveFullAccessRoleNames =>
            FullAccessRoleNames is { Length: > 0 } names
                ? names.Where(n => !string.IsNullOrWhiteSpace(n)).Select(n => n.Trim()).ToArray()
                : new[] { DefaultFullAccessRoleName };

        public bool IsFullAccessRole(string? roleName) =>
            !string.IsNullOrWhiteSpace(roleName)
            && EffectiveFullAccessRoleNames.Contains(roleName.Trim(), StringComparer.OrdinalIgnoreCase);

        // How often (minutes) an authenticated cookie is re-checked against
        // the database: an inactive user is signed out, and changed
        // roles / role permissions / areas are re-stamped onto the cookie
        // (so a change to a role's permissions reaches every user holding
        // that role within this interval, without a new login).
        // 0 = re-check on every request.
        public int PrincipalRevalidationMinutes { get; set; } = 5;
    }
}
