using System.Reflection;
using Microsoft.AspNetCore.Mvc;
using PlantStockManager.Authorization;
using PlantStockManager.Services;

namespace PlantStockManager.Tests
{
    // Pot Production -> Ready confirmation -> PottedPlantStock already existed
    // in full (Data/PotBatchRepository.ConfirmReadyAsync, unit-tested by
    // PotBatchRulesTests). The one real gap was Area -> Polyhouse tracking,
    // which did not exist anywhere in this pipeline. This adds an optional
    // PolyhouseId to PotProductionBatches only (never to PottedPlantStock,
    // which stays pooled by (SpeciesId, PotSize, AreaId) so Booking/Dispatch/
    // Outlet Sales behavior is unchanged), validated at batch creation by
    // reusing the SAME DirectSowingRules.ResolveGrowingLocation cross-check
    // every other Area -> Polyhouse cascade in this app already uses.
    public class PotBatchPolyhouseTests
    {
        [Fact]
        public void PolyhouseMismatch_IsRejected_ByTheSharedRule()
        {
            // Batch's own Area is 10; the chosen Polyhouse belongs to Area 20.
            var (ok, _, _, error) = DirectSowingRules.ResolveGrowingLocation(
                seedLotAreaId: 10, requestedAreaId: 10, polyhouseId: 99, polyhouseAreaId: 20);
            Assert.False(ok);
            Assert.Contains("different Area", error);
        }

        [Fact]
        public void PolyhouseMatchingArea_IsAccepted()
        {
            var (ok, areaId, polyhouseId, error) = DirectSowingRules.ResolveGrowingLocation(
                seedLotAreaId: 10, requestedAreaId: 10, polyhouseId: 99, polyhouseAreaId: 10);
            Assert.True(ok, error);
            Assert.Equal(10, areaId);
            Assert.Equal(99, polyhouseId);
        }

        [Fact]
        public void NoPolyhouseChosen_IsAccepted()
        {
            // Polyhouse is optional -- a batch with none chosen must still save.
            var (ok, areaId, polyhouseId, error) = DirectSowingRules.ResolveGrowingLocation(
                seedLotAreaId: 10, requestedAreaId: 10, polyhouseId: null, polyhouseAreaId: null);
            Assert.True(ok, error);
            Assert.Equal(10, areaId);
            Assert.Null(polyhouseId);
        }

        [Fact]
        public void Model_HasPolyhouseFields()
        {
            var type = typeof(PlantStockManager.Models.PotProductionBatch);
            Assert.NotNull(type.GetProperty("PolyhouseId"));
            Assert.Equal(typeof(int?), type.GetProperty("PolyhouseId")!.PropertyType);
            Assert.NotNull(type.GetProperty("PolyhouseName"));
        }

        [Fact]
        public void CreatePage_HasPolyhouseIdAndPolyhousesHandler()
        {
            var type = typeof(PlantStockManager.Pages.Production.PotBatch.CreateModel);
            var property = type.GetProperty("PolyhouseId");
            Assert.NotNull(property);
            Assert.Equal(typeof(int?), property!.PropertyType);
            Assert.True(property.GetCustomAttribute<BindPropertyAttribute>() != null);

            var handler = type.GetMethod("OnGetPolyhousesAsync");
            Assert.NotNull(handler);
            Assert.Contains("areaId", handler!.GetParameters().Select(p => p.Name));
        }

        [Fact]
        public void ExistingPotBatchPermissions_AreUnchanged()
        {
            // No new permission was introduced: the same Enter/View codes
            // still gate the confirm-READY action (the assigned-supervisor
            // business rule, not a permission code, is what enforces
            // segregation of duties -- unchanged by this addition).
            Assert.True(FeatureAuthorizationConventions.IsMapped("/Production/PotBatch/Create"));
            Assert.Contains("PotProduction.Enter", FeatureAuthorizationConventions.GetRule("/Production/PotBatch/Create").Read);
            Assert.True(FeatureAuthorizationConventions.IsMapped("/Production/PotBatch/Details"));
            Assert.Contains("PotProduction.Enter", FeatureAuthorizationConventions.GetRule("/Production/PotBatch/Details").Write);
        }
    }
}
