using PlantStockManager.Models;
using PlantStockManager.Services;

namespace PlantStockManager.Tests
{
    // Phase 1: who may be chosen as a supervisor (SupervisorRules). The
    // candidate rows are what UserRoleRepository.GetSupervisorCandidatesAsync
    // returns: one row per qualifying role assignment (its Area, or all Areas).
    public class SupervisorEligibilityTests
    {
        private const int AreaA = 10;
        private const int AreaB = 20;

        private static readonly SupervisorCandidate[] Candidates =
        {
            new(1, "Asha",  "Supervisor", IsActive: true,  AreaId: AreaA, IsAllAreas: false),
            new(2, "Bala",  "Supervisor", IsActive: true,  AreaId: AreaB, IsAllAreas: false),
            new(3, "Chitra","Manager",    IsActive: true,  AreaId: null,  IsAllAreas: true),   // all-Area role
            new(4, "Dev",   "Supervisor", IsActive: false, AreaId: AreaA, IsAllAreas: false),  // inactive
            new(5, "Esha",  "Supervisor", IsActive: true,  AreaId: AreaA, IsAllAreas: false),
            new(5, "Esha",  "Supervisor", IsActive: true,  AreaId: AreaB, IsAllAreas: false),  // two Areas
        };

        private static List<int> Ids(IEnumerable<Employee> list) => list.Select(e => e.EmployeeID).ToList();

        [Fact]
        public void AreaA_OffersOnlyItsSupervisorsAndAllAreaRoles()
            => Assert.Equal(new[] { 1, 3, 5 }, Ids(SupervisorRules.Eligible(Candidates, AreaA)).OrderBy(i => i));

        [Fact]
        public void OtherArea_SupervisorIsNotOffered()
            => Assert.DoesNotContain(1, Ids(SupervisorRules.Eligible(Candidates, AreaB)));

        [Fact]
        public void InactiveUser_IsNeverOffered()
        {
            Assert.DoesNotContain(4, Ids(SupervisorRules.Eligible(Candidates, AreaA)));
            Assert.DoesNotContain(4, Ids(SupervisorRules.Eligible(Candidates, null)));
        }

        [Fact]
        public void UserWithoutThePermission_IsNotACandidate()
        {
            // The repository only returns users holding the kind's permission
            // (or a full-access role); anyone else never reaches the list.
            Assert.DoesNotContain(99, Ids(SupervisorRules.Eligible(Candidates, AreaA)));
        }

        [Fact]
        public void NoArea_OrAreaScopeOff_OffersEveryActiveHolder()
        {
            var expected = new[] { 1, 2, 3, 5 };
            Assert.Equal(expected, Ids(SupervisorRules.Eligible(Candidates, null)).OrderBy(i => i));
            Assert.Equal(expected, Ids(SupervisorRules.Eligible(Candidates, AreaA, enforceArea: false)).OrderBy(i => i));
        }

        [Fact]
        public void UserWithSeveralAreas_AppearsOnce_WithAllTheirAreas()
        {
            var options = SupervisorRules.Options(Candidates);
            var esha = Assert.Single(options, o => o.EmployeeID == 5);
            Assert.Equal(new[] { AreaA, AreaB }, esha.AreaIds);
            Assert.Equal("10,20", esha.AreaAttribute);
            Assert.Equal("*", options.Single(o => o.EmployeeID == 3).AreaAttribute);
        }

        [Fact]
        public void ExcludedUsers_CreatorOrEditor_AreRemoved()
            => Assert.DoesNotContain(1, Ids(SupervisorRules.Eligible(Candidates, AreaA, excludeUserIds: new[] { 1 })));

        [Fact]
        public void TamperedId_IsRefused()
        {
            var eligible = SupervisorRules.Eligible(Candidates, AreaA);
            var (ok, error) = SupervisorRules.ValidateChoice(2, null, eligible, SupervisorKind.ProductionArea);
            Assert.False(ok);
            Assert.Contains("not an eligible supervisor", error);
            Assert.False(SupervisorRules.ValidateChoice(12345, null, eligible, SupervisorKind.ProductionArea).Ok);
        }

        [Fact]
        public void TamperedId_IsRefused_ByTheOptionOverload()
        {
            var options = SupervisorRules.Options(Candidates);
            Assert.False(SupervisorRules.ValidateChoice(2, null, options, AreaA, SupervisorKind.Outlet).Ok);
            Assert.True(SupervisorRules.ValidateChoice(2, null, options, AreaB, SupervisorKind.Outlet).Ok);
        }

        [Fact]
        public void UnchangedValue_IsAccepted_EvenIfNoLongerEligible()
            => Assert.True(SupervisorRules.ValidateChoice(4, 4, SupervisorRules.Eligible(Candidates, AreaA), SupervisorKind.ProductionArea).Ok);

        [Fact]
        public void Empty_IsAllowedUnlessRequired()
        {
            var eligible = SupervisorRules.Eligible(Candidates, AreaA);
            Assert.True(SupervisorRules.ValidateChoice(null, null, eligible, SupervisorKind.MainOffice).Ok);
            var (ok, error) = SupervisorRules.ValidateChoice(null, null, eligible, SupervisorKind.MainOffice, required: true);
            Assert.False(ok);
            Assert.StartsWith("Main Office Supervisor", error);
        }

        [Fact]
        public void IncludeCurrent_KeepsStoredSupervisorSelectable()
        {
            var options = SupervisorRules.Options(Candidates);
            var withCurrent = SupervisorRules.IncludeCurrent(options, 4, "Dev");
            Assert.Contains(withCurrent, o => o.EmployeeID == 4 && o.Name == "Dev (current)" && o.CanServe(AreaA));
            Assert.Same(options, SupervisorRules.IncludeCurrent(options, 1, "Asha"));   // already listed
            Assert.Same(options, SupervisorRules.IncludeCurrent(options, null, null));
        }

        [Theory]
        [InlineData("MotherPlant", SupervisorKind.ProductionArea)]
        [InlineData("Kunjir", SupervisorKind.ProductionArea)]
        [InlineData("Kiran", SupervisorKind.ProductionArea)]
        [InlineData("MainOffice", SupervisorKind.MainOffice)]
        [InlineData("Outlet", SupervisorKind.Outlet)]
        public void AreaType_MapsToSupervisorKind(string areaType, SupervisorKind kind)
            => Assert.Equal(kind, SupervisorRules.KindForAreaType(areaType));

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("GrowingPartner")]
        public void UnknownAreaType_HasNoKind(string? areaType)
        {
            Assert.Null(SupervisorRules.KindForAreaType(areaType));
            Assert.Null(SupervisorRules.KindKeyForAreaType(areaType));
        }

        [Theory]
        [InlineData(SupervisorKind.Sowing, "ReadyStock.Confirm")]
        [InlineData(SupervisorKind.ProductionArea, "MotherPlant.Enter")]
        [InlineData(SupervisorKind.MainOffice, "MainOffice.Confirm")]
        [InlineData(SupervisorKind.Outlet, "Outlet.Confirm")]
        public void Kind_UsesItsPermission(SupervisorKind kind, string permission)
            => Assert.Equal(permission, SupervisorRules.PermissionFor(kind));

        // D-3: an Area's own supervisor (Admin > Areas) must be eligible for
        // that Area -- a supervisor of another Area is refused, an all-Area
        // role is accepted, and the stored value is kept on edit.
        [Fact]
        public void AreaSupervisor_MustBeEligibleForThatArea()
        {
            var options = SupervisorRules.Options(Candidates);
            Assert.True(SupervisorRules.ValidateChoice(1, null, options, AreaA, SupervisorKind.ProductionArea).Ok);
            Assert.True(SupervisorRules.ValidateChoice(3, null, options, AreaA, SupervisorKind.ProductionArea).Ok);
            Assert.False(SupervisorRules.ValidateChoice(2, null, options, AreaA, SupervisorKind.ProductionArea).Ok);
            Assert.True(SupervisorRules.ValidateChoice(2, 2, options, AreaA, SupervisorKind.ProductionArea).Ok);
        }
    }
}
