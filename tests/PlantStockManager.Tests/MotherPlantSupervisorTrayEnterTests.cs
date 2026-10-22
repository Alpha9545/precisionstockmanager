using System.Security.Claims;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using PlantStockManager.Authorization;
using PlantStockManager.Data;
using PlantStockManager.Models;
using PlantStockManager.Services;

namespace PlantStockManager.Tests
{
    // 2026-10-16_MotherPlantSupervisorTrayEnter.sql (owner approval 2026-10-12): the Mother Plant Supervisor role holds
    // TrayStock.Enter -- empty trays for the Mother Plant Area(s) he is ASSIGNED to, nothing else.
    //   * pure: the existing per-Area rule (TrayAreaScope) with his permission set;
    //   * page rules: which pages the grant opens (and which it does not);
    //   * database (scratch copy with the grant, SKIPPED otherwise): his real permissions and Area claims, the
    //     Areas offered, saves in his own Area, forged / invalid saves refused, other roles unchanged.
    public class MotherPlantSupervisorTrayEnterTests
    {
        // Permission set of the role in PlantsIMS2_Test after the grant.
        private static readonly string[] MotherPlantSupervisor =
            { "MotherPlant.Enter", "MotherPlant.View", "PotProduction.View", "ReadyStock.Confirm", "Transplant.Enter", "Transplant.View",
              "TrayStock.ReceiveTransfer", "TrayStock.ViewMotherPlant", "TrayStock.Enter" };

        private static ClaimsPrincipal User(IEnumerable<string> permissions, params int[] areas)
            => new(new ClaimsIdentity(permissions.Select(p => new Claim(MinimumAuthorizationLevelHandler.PermissionClaimType, p))
                .Concat(areas.Select(a => new Claim(AreaAccessService.AreaAccessClaimType, a.ToString()))), "test"));

        private static readonly Area OwnMp = new() { Id = 127, Name = "Green Bless Nursery", AreaType = "MotherPlant", IsActive = true };
        private static readonly Area OtherMp = new() { Id = 123, Name = "Samarth Ropvatika", AreaType = "MotherPlant", IsActive = true };
        private static readonly Area Main = new() { Id = 2, Name = "Main-Office-Areas", AreaType = "MainOffice", IsActive = true };
        private static readonly Area Outlet = new() { Id = 124, Name = "Outlet", AreaType = "Outlet", IsActive = true };
        private static readonly Area InactiveOwn = new() { Id = 127, Name = "Green Bless Nursery", AreaType = "MotherPlant", IsActive = false };

        [Fact]
        public void TrayArea_MotherPlantSupervisor_OnlyHisOwnActiveMotherPlantArea()
        {
            var u = User(MotherPlantSupervisor, 127);
            bool Assigned(int id) => id == 127;
            Assert.True(TrayAreaScope.IsAddableArea(u, OwnMp, Assigned));
            Assert.False(TrayAreaScope.IsAddableArea(u, OtherMp, Assigned));         // another Mother Plant Area
            Assert.False(TrayAreaScope.IsAddableArea(u, Main, Assigned));            // never the Main Office
            Assert.False(TrayAreaScope.IsAddableArea(u, Outlet, _ => true));         // never an Outlet, even if assigned
            Assert.False(TrayAreaScope.IsAddableArea(u, InactiveOwn, Assigned));
            // without the grant (the role before 2026-10-16): nowhere
            Assert.False(TrayAreaScope.IsAddableArea(User(MotherPlantSupervisor.Where(p => p != "TrayStock.Enter"), 127), OwnMp, Assigned));
        }

        [Fact]
        public void Grant_OpensOnlyTheTrayAddPages_NoAdjustTransferOrGrowingMaterial()
        {
            var u = User(MotherPlantSupervisor, 127);
            bool Opens(string page) { var r = FeatureAuthorizationConventions.GetRule(page); return u.HasPermission(r.Read); }
            Assert.True(Opens("/Production/MotherPlant/AddEmptyTrays"));
            Assert.True(u.CanAddTrays());
            Assert.False(u.CanAdjustTrays());
            Assert.False(u.CanSendTrayTransfers());
            Assert.False(u.CanAddMaterials());
            Assert.False(Opens("/Production/MaterialStock/TrayAdjust"));
            Assert.False(u.HasPermission("MaterialStock.Add"));
        }

        [Fact]
        public void OtherRoles_TrayRuleUnchanged()
        {
            var sowingSupervisor = User(new[] { "Sowing.Enter", "TrayStock.Enter", "TrayStock.Adjust", "TrayStock.Transfer" }, 2);
            Assert.True(TrayAreaScope.IsAddableArea(sowingSupervisor, Main, id => id == 2));
            Assert.False(TrayAreaScope.IsAddableArea(sowingSupervisor, OwnMp, id => id == 2));
            var officeCoordinator = User(new[] { "TrayStock.EnterMotherPlant", "TrayStock.Transfer" }, 2);
            Assert.True(TrayAreaScope.IsAddableArea(officeCoordinator, OwnMp, id => id == 2));
            Assert.True(TrayAreaScope.IsAddableArea(officeCoordinator, OtherMp, id => id == 2));
            Assert.False(TrayAreaScope.IsAddableArea(officeCoordinator, Main, id => id == 2));
        }

        // ---- database: scratch copy with the grant -------------------------------------------------------

        private sealed class StaticMonitor<T> : IOptionsMonitor<T>
        {
            public StaticMonitor(T value) => CurrentValue = value;
            public T CurrentValue { get; }
            public T Get(string? name) => CurrentValue;
            public IDisposable? OnChange(Action<T, string?> listener) => null;
        }

        private static async Task<T> ScalarAsync<T>(string cs, string sql, params (string, object)[] args)
        {
            using var conn = new SqlConnection(cs);
            await conn.OpenAsync();
            using var cmd = new SqlCommand(sql, conn);
            foreach (var (n, v) in args) cmd.Parameters.AddWithValue(n, v);
            var r = await cmd.ExecuteScalarAsync();
            return r == null || r == DBNull.Value ? default! : (T)Convert.ChangeType(r, typeof(T));
        }

        private static async Task<List<string>> ListAsync(string cs, string sql, int id)
        {
            var list = new List<string>();
            using var conn = new SqlConnection(cs);
            await conn.OpenAsync();
            using var cmd = new SqlCommand(sql, conn);
            cmd.Parameters.AddWithValue("@U", id);
            using var r = await cmd.ExecuteReaderAsync();
            while (await r.ReadAsync()) list.Add(Convert.ToString(r.GetValue(0))!);
            return list;
        }

        [SkippableFact]
        public async Task Database_RealSupervisor_OwnAreaOnly_SaveOnce_ForgedAndInvalidRefused_OtherRolesUnchanged()
        {
            var cs = Environment.GetEnvironmentVariable("PSM_SCRATCH_CONNECTION");
            Skip.If(string.IsNullOrWhiteSpace(cs), "Set PSM_SCRATCH_CONNECTION to a PlantsIMS2_Scratch_* database to run this test.");
            var db = await ScalarAsync<string>(cs!, "SELECT DB_NAME()");
            if (!db.StartsWith("PlantsIMS2_Scratch_", StringComparison.Ordinal))
                throw new InvalidOperationException($"Refusing to run against '{db}'.");
            Skip.If(await ScalarAsync<int>(cs!, MotherPlantAreaSchema.ProbeSql) != 1, "2026-10-14 is not applied to this scratch copy.");
            Skip.If(await ScalarAsync<int>(cs!, @"SELECT COUNT(*) FROM dbo.RolePermissions rp JOIN dbo.Roles r ON r.Id = rp.RoleId JOIN dbo.Permissions p ON p.Id = rp.PermissionId
WHERE r.Name = N'Mother Plant Supervisor' AND p.Code = N'TrayStock.Enter'") != 1, "2026-10-16_MotherPlantSupervisorTrayEnter.sql is not applied to this scratch copy.");

            // a real Mother Plant Supervisor: his permissions (every role) and Area claims, exactly as the login builds them
            var supervisor = await ScalarAsync<int>(cs!, @"SELECT TOP 1 ur.UserId FROM dbo.UserRoles ur JOIN dbo.Roles r ON r.Id = ur.RoleId
JOIN dbo.IMSUsers u ON u.Id = ur.UserId AND u.IsActive = 1 WHERE r.Name = N'Mother Plant Supervisor' AND ur.AreaId = 127 ORDER BY ur.UserId");
            Skip.If(supervisor == 0, "No Mother Plant Supervisor assigned to Green Bless Nursery in this copy.");
            var perms = await ListAsync(cs!, @"SELECT DISTINCT p.Code FROM dbo.UserRoles ur JOIN dbo.RolePermissions rp ON rp.RoleId = ur.RoleId
JOIN dbo.Permissions p ON p.Id = rp.PermissionId WHERE ur.UserId = @U", supervisor);
            var areas = (await ListAsync(cs!, "SELECT AreaId FROM dbo.UserRoles WHERE UserId = @U AND AreaId IS NOT NULL", supervisor)).Select(int.Parse).ToArray();
            var user = User(perms, areas);
            Assert.Contains("TrayStock.Enter", perms);
            Assert.DoesNotContain("TrayStock.Adjust", perms);
            Assert.DoesNotContain("TrayStock.Transfer", perms);
            Assert.DoesNotContain("TrayStock.EnterMotherPlant", perms);

            var services = new ServiceCollection();
            services.AddSingleton<IConfiguration>(new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:DefaultConnection"] = cs }).Build());
            services.AddSingleton<DatabaseHelper>();
            services.AddSingleton<AreaAccessService>();
            services.AddSingleton<IOptions<SecurityOptions>>(Options.Create(new SecurityOptions()));
            services.AddSingleton<IOptionsMonitor<SeedlingWorkflowOptions>>(new StaticMonitor<SeedlingWorkflowOptions>(new SeedlingWorkflowOptions()));
            services.AddScoped<AreaRepository>();
            services.AddScoped<TrayStockRepository>();
            using var provider = services.BuildServiceProvider();
            var scope = new TrayAreaScope(provider.GetRequiredService<AreaRepository>(), provider.GetRequiredService<AreaAccessService>());
            var repo = provider.GetRequiredService<TrayStockRepository>();

            // the Areas offered (the page and the save use this same list): his own Mother Plant Area(s) only
            var offered = (await scope.AddableAreasAsync(user)).ToList();
            Assert.Contains(offered, a => a.Id == 127);
            Assert.All(offered, a => Assert.True(a.AreaType == "MotherPlant" && areas.Contains(a.Id), $"{a.Name} offered"));
            var allowed = offered.Select(a => a.Id).ToHashSet();

            const string cavity = "102 Cavity";
            async Task<decimal> Balance(int area) => await ScalarAsync<decimal>(cs!,
                "SELECT ISNULL(SUM(PhysicalQuantity), 0) FROM dbo.TrayStock WHERE AreaId = @A AND PolyhouseId IS NULL AND TraySize = @C AND IsActive = 1", ("@A", area), ("@C", cavity));
            var own0 = await Balance(127);
            var other0 = await Balance(123);

            var token = Random.Shared.Next(1, int.MaxValue);
            var (ok, msg, dup, after) = await repo.AddStockAsync(127, null, cavity, 6, DateTime.UtcNow, token, supervisor, "mps-tray-test", "mps tray test", allowed.Contains);
            Assert.True(ok, msg);
            Assert.False(dup);
            Assert.Equal(own0 + 6, after);
            var again = await repo.AddStockAsync(127, null, cavity, 6, DateTime.UtcNow, token, supervisor, "mps-tray-test", "mps tray test", allowed.Contains);
            Assert.True(again.Duplicate);                                                                   // the replayed form adds nothing
            Assert.Equal(1, await ScalarAsync<int>(cs!, @"SELECT COUNT(*) FROM dbo.TrayStockTransactions tr JOIN dbo.TrayStock t ON t.Id = tr.TrayStockId
WHERE t.AreaId = 127 AND tr.ReferenceType = N'TrayStockAdd' AND tr.ReferenceId = @T", ("@T", token)));

            // forged Area ids (another Mother Plant Area, the Main Office) and bad quantities: refused, nothing changes
            foreach (var forgedArea in new[] { 123, 2 })
            {
                var (fOk, fMsg, _, _) = await repo.AddStockAsync(forgedArea, forgedArea == 2 ? 4 : null, cavity, 5, DateTime.UtcNow,
                    Random.Shared.Next(1, int.MaxValue), supervisor, "mps-tray-test", null, allowed.Contains);
                Assert.False(fOk);
                Assert.Contains("not authorized", fMsg);
            }
            foreach (var bad in new[] { 0m, -1m, 2.5m })
                Assert.False((await repo.AddStockAsync(127, null, cavity, bad, DateTime.UtcNow, Random.Shared.Next(1, int.MaxValue), supervisor, "x", null, allowed.Contains)).Success);
            Assert.Equal(own0 + 6, await Balance(127));
            Assert.Equal(other0, await Balance(123));

            // the grant is the only role-permission change of the 2026-10-16 scripts
            Assert.Equal(1, await ScalarAsync<int>(cs!, @"SELECT SUM(CASE Change WHEN N'Added' THEN 1 ELSE -1 END) FROM dbo.RolePermissionChangeLog
WHERE Script LIKE N'2026-10-16%' AND RoleName = N'Mother Plant Supervisor' AND PermissionCode = N'TrayStock.Enter'"));   // net: one grant
            Assert.Equal(0, await ScalarAsync<int>(cs!, @"SELECT COUNT(*) FROM dbo.RolePermissionChangeLog
WHERE Script LIKE N'2026-10-16%' AND NOT (RoleName = N'Mother Plant Supervisor' AND PermissionCode = N'TrayStock.Enter')"));   // and nothing else
            Assert.Equal(0, await ScalarAsync<int>(cs!, @"SELECT COUNT(*) FROM dbo.RolePermissions rp JOIN dbo.Roles r ON r.Id = rp.RoleId JOIN dbo.Permissions p ON p.Id = rp.PermissionId
WHERE p.Code = N'TrayStock.Enter' AND r.Name NOT IN (N'Mother Plant Supervisor', N'Sowing Supervisor', N'System Administrator')"));
        }
    }
}
