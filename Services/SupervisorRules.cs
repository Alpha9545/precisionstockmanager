using PlantStockManager.Models;

namespace PlantStockManager.Services
{
    // Phase 1 (Clean user / supervisor selection): the ONE rule that decides
    // who may be chosen as a supervisor, used by every supervisor dropdown AND
    // re-applied on every save (a posted id is never trusted). Pure and
    // database-free; UserRoleRepository.GetSupervisorCandidatesAsync supplies
    // the candidate rows.
    //
    // A user is eligible for a supervisor kind in Area X when
    //   * the user is active, and
    //   * the user holds the kind's permission through a dbo.UserRoles row
    //     scoped to Area X, or through an all-Area role (full-access role such
    //     as System Administrator, or Admin/Management/MainOfficeOfficer --
    //     the same all-Area rule as AreaAccessService).
    // When there is no Area to check (record has no Area, or the seedling
    // Area scope is switched off) any active permission holder is eligible.
    public enum SupervisorKind
    {
        Sowing,          // approves tray (sowing) batches
        ProductionArea,  // Mother Plant / Production Areas (MotherPlant, Kunjir, Kiran)
        MainOffice,      // Main Office Areas
        Outlet           // Outlet Areas
    }

    // One row per (user, qualifying role assignment). IsAllAreas marks an
    // assignment that reaches every Area.
    public sealed record SupervisorCandidate(int UserId, string Name, string Designation, bool IsActive, int? AreaId, bool IsAllAreas);

    // One selectable supervisor and the Areas they may supervise. Used where
    // the Area is chosen on the SAME form (e.g. a Mother Plant's Area or a
    // source stock pool): the dropdown carries every eligible supervisor with
    // their Areas (AreaAttribute), site.js narrows it to the chosen Area, and
    // the server re-checks the choice against the real Area on save.
    public sealed class SupervisorOption
    {
        public int EmployeeID { get; init; }
        public string Name { get; init; } = string.Empty;
        public bool AllAreas { get; init; }
        public IReadOnlyList<int> AreaIds { get; init; } = Array.Empty<int>();

        // No Area to check = any eligible supervisor.
        public bool CanServe(int? areaId) => !areaId.HasValue || AllAreas || AreaIds.Contains(areaId.Value);

        // data-areas attribute: "*" = every Area.
        public string AreaAttribute => AllAreas ? "*" : string.Join(",", AreaIds);
    }

    public static class SupervisorRules
    {
        // Every eligible supervisor (active, holds the permission) with the
        // Areas they may supervise; excluded users (e.g. the creator) removed.
        public static List<SupervisorOption> Options(IEnumerable<SupervisorCandidate> candidates, IEnumerable<int>? excludeUserIds = null)
        {
            var excluded = excludeUserIds?.ToHashSet() ?? new HashSet<int>();
            return candidates
                .Where(c => c.IsActive && !excluded.Contains(c.UserId))
                .GroupBy(c => c.UserId)
                .Select(g => new SupervisorOption
                {
                    EmployeeID = g.Key,
                    Name = g.First().Name,
                    AllAreas = g.Any(c => c.IsAllAreas),
                    AreaIds = g.Where(c => c.AreaId.HasValue).Select(c => c.AreaId!.Value).Distinct().OrderBy(a => a).ToList()
                })
                .OrderBy(o => o.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        // Edit pages: keep a record's CURRENT supervisor selectable even if no
        // longer eligible, so saving other fields never clears it silently
        // (ValidateChoice accepts an unchanged value).
        public static List<SupervisorOption> IncludeCurrent(List<SupervisorOption> options, int? currentId, string? currentName)
        {
            if (!currentId.HasValue || currentId.Value <= 0 || options.Any(o => o.EmployeeID == currentId.Value))
                return options;
            var list = new List<SupervisorOption>(options)
            {
                new SupervisorOption { EmployeeID = currentId.Value, Name = (currentName ?? "#" + currentId.Value) + " (current)", AllAreas = true }
            };
            return list;
        }

        // The Area-based kinds (every kind except Sowing), for forms whose
        // source Area can be of any type.
        public static readonly IReadOnlyList<SupervisorKind> AreaKinds =
            new[] { SupervisorKind.ProductionArea, SupervisorKind.MainOffice, SupervisorKind.Outlet };

        // data-kind / data-area-kind attribute values used by site.js.
        public static string KindKey(SupervisorKind kind) => kind.ToString();
        public static string? KindKeyForAreaType(string? areaType)
            => KindForAreaType(areaType) is SupervisorKind k ? KindKey(k) : null;

        public static string PermissionFor(SupervisorKind kind) => kind switch
        {
            SupervisorKind.Sowing => "ReadyStock.Confirm",
            SupervisorKind.ProductionArea => "MotherPlant.Enter",
            SupervisorKind.MainOffice => "MainOffice.Confirm",
            SupervisorKind.Outlet => "Outlet.Confirm",
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        };

        public static string Label(SupervisorKind kind) => kind switch
        {
            SupervisorKind.Sowing => "Sowing Supervisor",
            SupervisorKind.ProductionArea => "Production Area Supervisor",
            SupervisorKind.MainOffice => "Main Office Supervisor",
            SupervisorKind.Outlet => "Outlet Supervisor",
            _ => "Supervisor"
        };

        // dbo.Area.AreaType -> the supervisor kind that runs that Area.
        // Kunjir/Kiran keep their stored values but are Production Areas.
        // Null for an unknown / empty type (no supervisor can be offered).
        public static SupervisorKind? KindForAreaType(string? areaType) => areaType switch
        {
            "MotherPlant" or "Kunjir" or "Kiran" => SupervisorKind.ProductionArea,
            "MainOffice" => SupervisorKind.MainOffice,
            "Outlet" => SupervisorKind.Outlet,
            _ => null
        };

        // The eligible users, one entry per user, ordered by name.
        // areaId null or enforceArea false = no Area condition.
        public static List<Employee> Eligible(
            IEnumerable<SupervisorCandidate> candidates, int? areaId, bool enforceArea = true, IEnumerable<int>? excludeUserIds = null)
        {
            var list = candidates.ToList();
            var designation = list.GroupBy(c => c.UserId).ToDictionary(g => g.Key, g => g.First().Designation);
            return Options(list, excludeUserIds)
                .Where(o => o.CanServe(enforceArea ? areaId : null))
                .Select(o => new Employee { EmployeeID = o.EmployeeID, Name = o.Name, Designation = designation[o.EmployeeID] })
                .ToList();
        }

        // Server-side check against the option list (see SupervisorOption).
        public static (bool Ok, string? Error) ValidateChoice(
            int? chosenId, int? currentId, IEnumerable<SupervisorOption> options, int? areaId, SupervisorKind kind, bool required = false)
            => ValidateChoice(chosenId, currentId,
                options.Where(o => o.CanServe(areaId)).Select(o => new Employee { EmployeeID = o.EmployeeID, Name = o.Name }),
                kind, required);

        public const string RequiredMessage = "is required.";

        // Server-side check of a chosen supervisor.
        //   * nothing chosen: allowed unless required;
        //   * unchanged value (Edit of an existing record): allowed even if no
        //     longer eligible, so older records can still be saved;
        //   * anything else must be in the eligible list.
        public static (bool Ok, string? Error) ValidateChoice(
            int? chosenId, int? currentId, IEnumerable<Employee> eligible, SupervisorKind kind, bool required = false)
        {
            if (!chosenId.HasValue || chosenId.Value <= 0)
                return required ? (false, $"{Label(kind)} {RequiredMessage}") : (true, null);
            if (currentId.HasValue && chosenId.Value == currentId.Value)
                return (true, null);
            if (eligible.Any(e => e.EmployeeID == chosenId.Value))
                return (true, null);
            return (false, $"The selected {Label(kind)} is not an eligible supervisor for this Area.");
        }
    }
}
