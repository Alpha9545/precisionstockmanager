using PlantStockManager.Data;
using PlantStockManager.Models;
using PlantStockManager.Services;

namespace PlantStockManager.Tests
{
    // 2026-10-22: the Variety / Sowing Date filter values of Bed Transplanting and Sowing Approvals (SowingListFilter) -- pure
    // rules, no database. The pages' end-to-end behaviour is in SowingListFiltersE2ETests.
    public class SowingListFilterTests
    {
        private static readonly int[] Allowed = { 5, 9 };

        [Fact]
        public void Unset_IsNoRestriction()
        {
            var (f, note) = SowingListFilter.Normalize(null, null, Allowed);
            Assert.False(f.IsActive);
            Assert.Null(f.SpeciesId);
            Assert.Null(f.SowingDate);
            Assert.Null(f.DayStart);
            Assert.Null(note);
            Assert.Equal(f, SowingListFilter.None);
        }

        [Fact]
        public void Variety_KeptOnlyWhenItIsOneOfThePagesOwnOptions()
        {
            Assert.Equal(9, SowingListFilter.Normalize(9, null, Allowed).Filter.SpeciesId);
            // an id outside the user's scope (or forged) is dropped, never applied
            var (f, note) = SowingListFilter.Normalize(77, null, Allowed);
            Assert.Null(f.SpeciesId);
            Assert.False(f.IsActive);
            Assert.Equal(SowingListFilter.VarietyClearedMessage, note);
            Assert.Null(SowingListFilter.Normalize(0, null, Allowed).Filter.SpeciesId);
            Assert.Null(SowingListFilter.Normalize(-3, null, Allowed).Filter.SpeciesId);
            Assert.Null(SowingListFilter.Normalize(0, null, Allowed).Note);
        }

        [Fact]
        public void SowingDate_IsTheWholeCalendarDay_TimeIgnored()
        {
            var (f, note) = SowingListFilter.Normalize(null, new DateTime(2026, 10, 5, 17, 45, 12), Allowed);
            Assert.Null(note);
            Assert.True(f.IsActive);
            Assert.Equal(new DateTime(2026, 10, 5), f.SowingDate);
            Assert.Equal(new DateTime(2026, 10, 5), f.DayStart);
            Assert.Equal(new DateTime(2026, 10, 6), f.DayEnd);                 // exclusive upper bound: 23:59:59.9999999 is inside
            Assert.Equal("2026-10-05", f.SowingDateValue);
        }

        [Fact]
        public void SowingDate_OutOfRange_IsIgnoredWithANote()
        {
            foreach (var bad in new[] { DateTime.MinValue, new DateTime(1999, 12, 31), new DateTime(2101, 1, 1), DateTime.MaxValue })
            {
                var (f, note) = SowingListFilter.Normalize(5, bad, Allowed);
                Assert.Null(f.SowingDate);
                Assert.Equal(5, f.SpeciesId);
                Assert.Equal(SowingListFilter.InvalidDateMessage, note);
            }
        }

        [Fact]
        public void BothFilters_AreKeptTogether()
        {
            var (f, note) = SowingListFilter.Normalize(5, new DateTime(2026, 9, 1), Allowed);
            Assert.Equal((5, new DateTime(2026, 9, 1)), (f.SpeciesId!.Value, f.SowingDate!.Value));
            Assert.Null(note);
        }

        [Fact]
        public void VarietiesOf_TheQueue_DistinctAndSortedByName()
        {
            SowingApprovalQueueRepository.Item Item(int id, int species, string name, string type) => new(
                new SeedSowing { Id = id, SpeciesId = species, SpeciesName = name, PlantTypeName = type }, SowingApprovalRules.CategorySowing,
                null, new SowingApprovalRules.Eligibility(true, null, "x", null), null, null);
            var options = SowingApprovalQueueRepository.VarietiesOf(new[]
            {
                Item(1, 7, "Tomato ", "Vegetable"), Item(2, 3, "aster", "Flower"), Item(3, 7, "Tomato", "Vegetable")
            });
            Assert.Equal(new[] { 3, 7 }, options.Select(o => o.Id));
            Assert.Equal("Tomato (Vegetable)", options[1].Label);
            Assert.Equal("aster (Flower)", options[0].Label);
            Assert.Empty(SowingApprovalQueueRepository.VarietiesOf(Array.Empty<SowingApprovalQueueRepository.Item>()));
        }
    }
}
