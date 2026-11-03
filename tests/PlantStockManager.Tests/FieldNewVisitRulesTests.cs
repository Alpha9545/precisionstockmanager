using PlantStockManager.Services.FieldWork;
using static PlantStockManager.Services.FieldWork.FieldCustomerRules;

namespace PlantStockManager.Tests
{
    // Field Work follow-up (2026-11-03): the pure rules of the one-step New Visit form (no database).
    public class FieldNewVisitRulesTests
    {
        [Fact]
        public void Mode_MustBeChosen_ExistingNeedsACustomer()
        {
            Assert.Null(FieldVisitService.CheckMode(FieldVisitService.ModeNew, null));
            Assert.Null(FieldVisitService.CheckMode(FieldVisitService.ModeExisting, 12));
            Assert.NotNull(FieldVisitService.CheckMode(FieldVisitService.ModeExisting, null));
            Assert.NotNull(FieldVisitService.CheckMode(FieldVisitService.ModeExisting, 0));
            Assert.NotNull(FieldVisitService.CheckMode(null, 12));
            Assert.NotNull(FieldVisitService.CheckMode("other", null));
        }

        [Fact]
        public void FirstPhoto_BecomesTheProfilePhoto_OnlyWhenTakenAndTheCustomerHasNone()
        {
            Assert.True(UseFirstPhotoAsProfile(photoCount: 1, customerHasPhoto: false));
            Assert.True(UseFirstPhotoAsProfile(3, false));
            Assert.False(UseFirstPhotoAsProfile(0, false));   // no photo: no profile photo, no error
            Assert.False(UseFirstPhotoAsProfile(2, true));    // never replaced
        }

        [Fact]
        public void UseThisExistingCustomer_OnlyForAnActiveCustomerAssignedToTheUser()
        {
            Assert.True(DuplicateUsable(new DuplicateMatch(1, "A", "X", "Me", true, AssignedUserId: 7), 7));
            Assert.False(DuplicateUsable(new DuplicateMatch(1, "A", "X", "Other", true, AssignedUserId: 8), 7));   // someone else's: would be 404
            Assert.False(DuplicateUsable(new DuplicateMatch(1, "A", "X", "Me", false, AssignedUserId: 7), 7));     // archived
            Assert.False(DuplicateUsable(new DuplicateMatch(1, "A", "X", "Me", true, AssignedUserId: 7), null));
        }

        [Fact]
        public void Visit_WithZeroPhotos_IsValid_AndPhotosStillNeedTheConsentVersion()
        {
            var options = new FieldWorkOptions { PhotoRoot = Path.GetTempPath() };
            var store = new FieldPhotoStore(options);
            FieldVisitService.VisitRequest R(int photos, int? consent) => new(0, 12m, 77m, 10m, null, "Talked", null, Array.Empty<string?>(), Array.Empty<string?>(),
                Enumerable.Range(0, photos).Select(_ => { var b = new byte[64]; b[0] = 0xFF; b[1] = 0xD8; b[2] = 0xFF; b[^2] = 0xFF; b[^1] = 0xD9; return b; }).ToList(),
                consent, null, null, null, "t");
            var today = new DateTime(2026, 11, 3);
            Assert.Empty(FieldVisitService.Validate(R(0, null), options, store, today, out _, out _));   // no photo, no consent version: fine
            Assert.NotEmpty(FieldVisitService.Validate(R(1, null), options, store, today, out _, out _));
            Assert.Empty(FieldVisitService.Validate(R(1, 1), options, store, today, out _, out _));
        }
    }
}
