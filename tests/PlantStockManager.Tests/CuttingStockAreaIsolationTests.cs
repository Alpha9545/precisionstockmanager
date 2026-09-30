using System.Security.Claims;
using System.Text.RegularExpressions;
using PlantStockManager.Authorization;
using PlantStockManager.Services;

namespace PlantStockManager.Tests
{
    // CORRECTION #3 -- AREA-WISE CUTTING STOCK VISIBILITY.
    //
    // ROOT CAUSE (found by tracing Cutting Entry -> Cutting Stock -> Area -> UserRoles -> claims -> queries -> UI):
    //   * dbo.CuttingStock is keyed by (Species, Area); UserRoles.AreaId -> "AreaAccess" claims -> AreaAccessService.
    //   * Cutting Stock > Index was ALREADY Area-scoped (AreaAccessService.FilterByArea).
    //   * The leak was CuttingRules.CanUseAsSource: "Main Office stock is usable by every Area's users". It fed the
    //     "Cutting Stock" pickers of Pot Batch > Start (reachable with MotherPlant.Enter, i.e. by every Mother Plant
    //     Supervisor, e.g. Green Bless Nursery) and Cutting Tray Sowing, and the server-side checks of both.
    // FIX: the rule now is "you may use a pool only if you may access the pool's OWN Area" -- one rule, applied to the
    // pickers, the repository checks under the pool's lock, and the Supervisor Approval's extra cuttings.
    //
    // Real pools of the test data: 166 = Green Bless Nursery (Area 127); 160 / 163 / 165 = Main Office (Area 2);
    // 157 = Ashirawad Cutting (Area 122); 161 = Tilekarwadi (Area 128); 162 / 164 = Shree Swami Samarth Agro (Area 1).
    public class CuttingStockAreaIsolationTests
    {
        private const int GreenBless = 127, MainOffice = 2, Ashirawad = 122, Tilekarwadi = 128, Swami = 1;
        private static readonly AreaAccessService Access = new();

        private static readonly (int Pool, int Area)[] Pools =
        {
            (166, GreenBless), (160, MainOffice), (163, MainOffice), (165, MainOffice),
            (157, Ashirawad), (161, Tilekarwadi), (162, Swami), (164, Swami)
        };

        // exactly what the Pot Batch / Tray Sowing pickers compute for each pool
        private static int[] Usable(ClaimsPrincipal user)
            => Pools.Where(p => CuttingRules.CanUseAsSource(Access.CanAccessArea(user, p.Area))).Select(p => p.Pool).OrderBy(i => i).ToArray();

        // exactly what Cutting Stock > Index computes
        private static int[] OnIndex(ClaimsPrincipal user)
            => Access.FilterByArea(user, Pools, p => (int?)p.Area).Select(p => p.Pool).OrderBy(i => i).ToArray();

        // a user as UserClaimsFactory builds them: RoleName claim per role, AreaAccess claim per UserRoles.AreaId
        private static ClaimsPrincipal User(string role, params int[] areas)
        {
            var claims = new List<Claim> { new(AreaAccessService.RoleNameClaimType, role) };
            claims.AddRange(areas.Select(a => new Claim(AreaAccessService.AreaAccessClaimType, a.ToString())));
            return new ClaimsPrincipal(new ClaimsIdentity(claims, "test"));
        }

        // ---- Green Bless Nursery user ---------------------------------------------------------------------

        [Fact]
        public void GreenBlessUser_SeesAndUsesOnlyGreenBlessStock_NeverMainOfficeStock()
        {
            var vasant = User("Mother Plant Supervisor", GreenBless);
            Assert.Equal(new[] { 166 }, OnIndex(vasant));                 // Cutting Stock > Index
            Assert.Equal(new[] { 166 }, Usable(vasant));                  // Pot Batch / Tray Sowing pickers
            Assert.DoesNotContain(Usable(vasant), p => p is 160 or 163 or 165);   // no Main Office pool
        }

        [Fact]
        public void GreenBlessUser_CannotUseAnotherAreasStock_CrossAreaAccess()
        {
            var vasant = User("Mother Plant Supervisor", GreenBless);
            foreach (var (pool, area) in Pools.Where(p => p.Area != GreenBless))
                Assert.False(CuttingRules.CanUseAsSource(Access.CanAccessArea(vasant, area)), $"pool {pool} (Area {area})");
        }

        // ---- Main Office users -------------------------------------------------------------------------

        [Theory]
        [InlineData("Sowing Supervisor")]
        [InlineData("Sowing Operator")]
        [InlineData("Main Office Store Keeper")]
        [InlineData("Dispatch Executive")]
        public void MainOfficeUsers_SeeAndUseMainOfficeStock_AndNothingOfOtherAreas(string role)
        {
            var mainOfficeUser = User(role, MainOffice);
            Assert.Equal(new[] { 160, 163, 165 }, OnIndex(mainOfficeUser));
            Assert.Equal(new[] { 160, 163, 165 }, Usable(mainOfficeUser));
        }

        // ---- other Areas stay isolated -------------------------------------------------------------------

        [Theory]
        [InlineData(Ashirawad, 157)]
        [InlineData(Tilekarwadi, 161)]
        public void AnotherAreasUser_SeesOnlyThatAreasStock(int area, int ownPool)
        {
            var user = User("Mother Plant Supervisor", area);
            Assert.Equal(new[] { ownPool }, OnIndex(user));
            Assert.Equal(new[] { ownPool }, Usable(user));
        }

        [Fact]
        public void AnAreaWithoutPools_SeesNothing_NotMainOfficeStockEither()
        {
            var user = User("Mother Plant Supervisor", 123);              // Samarth Ropvatika: no pool yet
            Assert.Empty(OnIndex(user));
            Assert.Empty(Usable(user));
        }

        [Fact]
        public void AUserWithNoAreaAssignment_SeesNothing()
        {
            var nobody = User("Mother Plant Supervisor");
            Assert.Empty(OnIndex(nobody));
            Assert.Empty(Usable(nobody));
        }

        // ---- authorized access (explicit assignment / full access) ------------------------------------

        [Fact]
        public void AnExplicitMainOfficeAssignment_GrantsMainOfficeStock_ByTheOrdinaryAreaRules()
        {
            // a Green Bless supervisor who is ALSO assigned to the Main Office Area (UserRoles.AreaId = 2) is authorized for both
            var both = User("Mother Plant Supervisor", GreenBless, MainOffice);
            Assert.Equal(new[] { 160, 163, 165, 166 }, OnIndex(both));
            Assert.Equal(new[] { 160, 163, 165, 166 }, Usable(both));
        }

        [Theory]
        [InlineData("Admin")]
        [InlineData("Management")]
        [InlineData("MainOfficeOfficer")]
        public void FullAreaAccessRoles_SeeEveryAreasStock(string role)
        {
            var user = User(role);
            Assert.Equal(Pools.Select(p => p.Pool).OrderBy(i => i).ToArray(), OnIndex(user));
            Assert.Equal(Pools.Select(p => p.Pool).OrderBy(i => i).ToArray(), Usable(user));
        }

        [Fact]
        public void TheSystemAdministratorFullAccessClaim_SeesEveryAreasStock()
        {
            var admin = new ClaimsPrincipal(new ClaimsIdentity(new[]
            {
                new Claim(ClaimsPrincipalSecurityExtensions.FullAccessClaimType, "true"),
                new Claim(AreaAccessService.RoleNameClaimType, "System Administrator")
            }, "test"));
            Assert.Equal(Pools.Length, Usable(admin).Length);
            Assert.Equal(Pools.Length, OnIndex(admin).Length);
        }

        // ---- the rule itself -----------------------------------------------------------------------------

        [Fact]
        public void TheRuleTakesNoMainOfficeShortcut()
        {
            var rule = typeof(CuttingRules).GetMethod(nameof(CuttingRules.CanUseAsSource))!;
            Assert.Equal(new[] { "canAccessStockArea" }, rule.GetParameters().Select(p => p.Name).ToArray());
            Assert.True(CuttingRules.CanUseAsSource(true));
            Assert.False(CuttingRules.CanUseAsSource(false));
        }

        // ---- wiring (source scans): the same rule everywhere, and enforced on the server -----------------

        private static string Repo(params string[] parts)
        {
            var dir = AppContext.BaseDirectory;
            while (dir != null && !Directory.GetFiles(dir, "*.sln").Any()) dir = Path.GetDirectoryName(dir);
            Assert.NotNull(dir);
            return File.ReadAllText(Path.Combine(new[] { dir! }.Concat(parts).ToArray()));
        }

        [Fact]
        public void PickersUseTheAreaRule_AndNoLongerTreatMainOfficeAsSharedStock()
        {
            var sowing = Repo("Pages", "Production", "SeedSowing", "CreateFromCutting.cshtml.cs");
            var pot = Repo("Pages", "Production", "PotBatch", "Create.cshtml.cs");
            foreach (var page in new[] { sowing, pot })
            {
                Assert.Contains("CuttingRules.CanUseAsSource(_areaAccess.CanAccessArea(User, s.AreaId))", page);
                Assert.Contains("s.AvailableQuantity > 0 && CanUseSource(s)", page);            // still AVAILABLE stock only
                Assert.DoesNotContain("s.AreaType == DirectSowingRules.MainOfficeAreaType, _areaAccess", page);
                Assert.DoesNotContain("mainOfficeIds", page);
            }
        }

        [Fact]
        public void TheServerEnforcesTheRule_UnderTheLock_ForSowingAndPotBatch_NotJustTheUi()
        {
            var sowing = Repo("Pages", "Production", "SeedSowing", "CreateFromCutting.cshtml.cs");
            Assert.Contains("areaId => CuttingRules.CanUseAsSource(_areaAccess.CanAccessArea(User, areaId))", sowing);
            var sowingRepo = Repo("Data", "SeedSowingRepository.cs");
            Assert.Contains("canUseSource != null && !canUseSource(stockAreaId)", sowingRepo);

            var pot = Repo("Pages", "Production", "PotBatch", "Create.cshtml.cs");
            Assert.Contains("_batchRepo.CreateAsync(entry, me!.Value,", pot);
            Assert.Contains("CuttingRules.CanUseAsSource(_areaAccess.CanAccessArea(User, areaId))", pot);
            var potRepo = Repo("Data", "PotBatchRepository.cs");
            var create = potRepo.Substring(potRepo.IndexOf("public async Task<(bool Success, string? Message, int Id)> CreateAsync", StringComparison.Ordinal));
            create = create.Substring(0, create.IndexOf("public async", 20, StringComparison.Ordinal));
            Assert.Contains("FROM dbo.CuttingStock WITH (UPDLOCK, HOLDLOCK)", create);
            Assert.Contains("canUseSourceArea != null && !canUseSourceArea(poolAreaId)", create);
            // the check comes right after the pool is read and locked, before any validation or write
            Assert.True(create.IndexOf("canUseSourceArea(poolAreaId)", StringComparison.Ordinal) < create.IndexOf("SELECT IsActive, AreaType FROM dbo.Area", StringComparison.Ordinal));
        }

        [Fact]
        public void SupervisorApprovalsExtraCuttings_UseTheSameStrictRule()
        {
            var page = Repo("Pages", "Production", "ReadyConfirmation", "Confirm.cshtml.cs");
            Assert.Contains("CuttingRules.CanUseAsSource(_cuttingAreaAccess.CanAccessArea(User, areaId))", page);
            Assert.DoesNotContain("mainOfficeIds", page);
            Assert.DoesNotContain("AreaRepository", page);
        }

        [Fact]
        public void CuttingStockIndex_StillFiltersByTheUsersAreas_AndIsNotSpecialCased()
        {
            var index = Repo("Pages", "Production", "CuttingStock", "Index.cshtml.cs");
            Assert.Contains("_areaAccess.FilterByArea(User, await _cuttingStockRepo.GetAllAsync(), s => (int?)s.AreaId)", index);
            Assert.DoesNotContain("MainOffice", index);
        }

        [Fact]
        public void HistoricalRecords_AreNotReEvaluated_CancelAndReadPathsDoNotApplyTheRule()
        {
            var potRepo = Repo("Data", "PotBatchRepository.cs");
            var cancel = potRepo.Substring(potRepo.IndexOf("public async Task<(bool Success, string? Message)> CancelAsync", StringComparison.Ordinal));
            Assert.DoesNotContain("canUseSourceArea", cancel);
            var sowingRepo = Repo("Data", "SeedSowingRepository.cs");
            var sowingCancel = sowingRepo.Substring(sowingRepo.IndexOf("public async Task<(bool Success, string? Message)> CancelAsync", StringComparison.Ordinal));
            Assert.DoesNotContain("canUseSource", sowingCancel);
            // the rule is applied when a pool is chosen for NEW work only
            Assert.Equal(1, Regex.Matches(sowingRepo, @"canUseSource != null").Count);
        }

        [Fact]
        public void PagePermissions_AreUnchanged()
        {
            Assert.Equal("Sowing.Enter", FeatureAuthorizationConventions.GetRule("/Production/SeedSowing/CreateFromCutting").Read);
            Assert.Equal("PotProduction.Enter|MotherPlant.Enter|Kiran.Enter", FeatureAuthorizationConventions.GetRule("/Production/PotBatch/Create").Read);
            Assert.Equal("MotherPlant.View|MainOffice.View|PotProduction.View|Sowing.View|Kunjir.View|Kiran.View",
                FeatureAuthorizationConventions.GetRule("/Production/CuttingStock/Index").Read);
        }
    }
}
