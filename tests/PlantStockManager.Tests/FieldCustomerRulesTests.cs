using PlantStockManager.Services.FieldWork;
using static PlantStockManager.Services.FieldWork.FieldCustomerRules;

namespace PlantStockManager.Tests
{
    // Field Work Phase 2 (2026-11-03): the pure customer / visit / follow-up rules (no database).
    public class FieldCustomerRulesTests
    {
        private static readonly DateTime Today = new(2026, 11, 3);

        // ---- phone / duplicates ---------------------------------------------------------------------------------------

        [Theory]
        [InlineData("9876543210", "9876543210")]
        [InlineData("+91 98765-43210", "919876543210")]
        [InlineData("098765 43210", "09876543210")]
        [InlineData("(080) 2345 6789", "08023456789")]
        public void Phone_IsStoredAsDigits(string input, string digits)
        {
            var (d, e) = NormalizePhone(input);
            Assert.Null(e);
            Assert.Equal(digits, d);
        }

        [Theory]
        [InlineData("12345")]
        [InlineData("98765432101234")]
        [InlineData("98765abc10")]
        public void Phone_Invalid_IsRefused(string input) => Assert.NotNull(NormalizePhone(input).Error);

        [Fact]
        public void Phone_Empty_IsNoValueNoError() => Assert.Equal((null, null), NormalizePhone("  "));

        [Fact]
        public void DuplicateKey_IsTheLastTenDigits_SoFormatsMatch()
        {
            Assert.Equal("9876543210", PhoneKey("919876543210"));
            Assert.Equal("9876543210", PhoneKey("09876543210"));
            Assert.Equal(PhoneKey(NormalizePhone("+91 98765 43210").Digits!), PhoneKey(NormalizePhone("9876543210").Digits!));
        }

        [Fact]
        public void MaskPhone_ShowsOnlyFirstAndLastTwo()
        {
            Assert.Equal("98xxxxxx10", MaskPhone("9876543210"));
            Assert.Equal("", MaskPhone(null));
            Assert.Equal("xxx", MaskPhone("123"));
        }

        [Fact]
        public void DuplicateWarning_NamesTheCustomerAndItsOwner()
        {
            var text = DuplicateWarning(new[] { new DuplicateMatch(1, "Rose Farm", "Hoskote", "Ravi K", true), new DuplicateMatch(2, "Old", "X", "Anu", false) });
            Assert.Contains("Rose Farm, Hoskote — assigned to Ravi K", text);
            Assert.Contains("(archived)", text);
        }

        // ---- district / customer --------------------------------------------------------------------------------------

        [Fact]
        public void District_IsTrimmed_AndReusesTheExistingSpelling_CaseInsensitively()
        {
            var existing = new[] { "Bengaluru Rural", "Kolar" };
            Assert.Equal("Bengaluru Rural", NormalizeDistrict("  bengaluru   RURAL ", existing));
            Assert.Equal("Tumakuru", NormalizeDistrict(" Tumakuru ", existing));
            Assert.Null(NormalizeDistrict("   ", existing));
        }

        [Fact]
        public void Customer_RequiredFields_AndLimits()
        {
            var ok = ValidateCustomer(new CustomerInput(" Rose Farm ", null, "98765 43210", "98765-43210", "Nursery", " Hoskote ", null, null), Array.Empty<string>());
            Assert.Empty(ok.Errors);
            Assert.Equal("Rose Farm", ok.Customer!.Name);
            Assert.Null(ok.Customer.AlternatePhone);   // the same number twice is kept once

            var bad = ValidateCustomer(new CustomerInput("", null, null, "12", "Alien", "", null, new string('x', 401)), Array.Empty<string>());
            Assert.Equal(6, bad.Errors.Count);   // name, phone, alternate phone, type, village, address
        }

        [Fact]
        public void Visibility_And_PhoneRules()
        {
            Assert.True(MaySeeCustomer(assignedUserId: 5, createdByUserId: 6, viewerUserId: 5, viewerMayManage: false));
            Assert.True(MaySeeCustomer(5, 6, 6, false));       // creator keeps seeing it after a reassignment
            Assert.False(MaySeeCustomer(5, 6, 7, false));
            Assert.True(MaySeeCustomer(5, 6, 7, true));
            Assert.True(MaySeePhone(5, 5, false));
            Assert.False(MaySeePhone(5, 6, false));            // ... but not the phone
            Assert.True(MaySeePhone(5, 7, true));
            Assert.False(MayWork(5, 6, false, true));
            Assert.False(MayWork(5, 5, false, customerActive: false));
            Assert.True(MayCorrectVisit(5, 5, false));
            Assert.False(MayCorrectVisit(5, 6, false));
        }

        // ---- position ---------------------------------------------------------------------------------------------------

        [Fact]
        public void Position_RequiredForVisits_ReasonFlags_PoorAccuracyNeedsReason()
        {
            Assert.NotEmpty(CheckPosition(null, null, null, null, 100, required: true, "Visit").Errors);
            var flagged = CheckPosition(null, null, null, "No signal", 100, true, "Visit");
            Assert.Empty(flagged.Errors);
            Assert.Contains(flagged.Flags, f => f.Contains("no GPS (No signal)"));
            Assert.NotEmpty(CheckPosition(12m, 77m, 500m, null, 100, true, "Visit").Errors);
            Assert.Contains(CheckPosition(12m, 77m, 500m, "Cold store", 100, true, "Visit").Flags, f => f.Contains("poor GPS accuracy 500 m"));
            Assert.Empty(CheckPosition(null, null, null, null, 100, required: false, "Customer").Errors);   // a manager in the office
            Assert.NotEmpty(CheckPosition(12m, null, 5m, null, 100, false, "Customer").Errors);             // incomplete
        }

        // ---- items ------------------------------------------------------------------------------------------------------

        [Fact]
        public void Items_SkipEmptyRows_ParseQuantities_AndRefuseBadOnes()
        {
            var (items, errors) = ValidateItems(new[] { "Rose sample", "", "Catalogue", null }, new[] { "10", "", null, "" });
            Assert.Empty(errors);
            Assert.Equal(new[] { new Item("Rose sample", 10m), new Item("Catalogue", null) }, items);
            Assert.NotEmpty(ValidateItems(new[] { "Rose" }, new[] { "-1" }).Errors);
            Assert.NotEmpty(ValidateItems(new[] { "Rose" }, new[] { "1.234" }).Errors);
            Assert.NotEmpty(ValidateItems(new string?[] { null }, new[] { "5" }).Errors);   // quantity without text
            Assert.NotEmpty(ValidateItems(Enumerable.Repeat("x", MaxItems + 1).ToList<string?>(), Array.Empty<string?>()).Errors);
            Assert.Equal("Rose sample × 10", ItemLabel("Rose sample", 10m));
        }

        // ---- follow-ups -------------------------------------------------------------------------------------------------

        [Fact]
        public void FollowUp_State_DueToday_Overdue_Upcoming()
        {
            Assert.Equal(FollowUpState.Overdue, StateOf(Today.AddDays(-1), Today));
            Assert.Equal(FollowUpState.Today, StateOf(Today, Today));
            Assert.Equal(FollowUpState.Upcoming, StateOf(Today.AddDays(1), Today));
        }

        [Fact]
        public void FollowUp_Date_NotInThePast_AtMostAYearAhead()
        {
            Assert.Null(ValidateFollowUp(null, null, Today));
            Assert.Null(ValidateFollowUp(Today, "Call", Today));
            Assert.NotNull(ValidateFollowUp(Today.AddDays(-1), null, Today));
            Assert.NotNull(ValidateFollowUp(Today.AddDays(MaxFollowUpDaysAhead + 1), null, Today));
            Assert.NotNull(ValidateFollowUp(Today, new string('x', 501), Today));
        }

        [Fact]
        public void FollowUp_VisitAlwaysCompletes_CallOnlyWhenTicked()
        {
            Assert.True(CompletesPending(hasPending: true, isVisit: true, completesChecked: false));
            Assert.True(CompletesPending(true, false, true));
            Assert.False(CompletesPending(true, false, false));
            Assert.False(CompletesPending(false, true, true));
        }

        [Fact]
        public void FollowUp_OnePending_NextDateNeedsTheOldOneCompleted_AskForNextDateOnlyWhenNoneSet()
        {
            Assert.NotNull(ValidateNextFollowUpSlot(hasPending: true, completesPending: false, nextDate: Today));
            Assert.Null(ValidateNextFollowUpSlot(true, true, Today));
            Assert.Null(ValidateNextFollowUpSlot(false, false, Today));
            Assert.Null(ValidateNextFollowUpSlot(true, false, null));
            Assert.True(AskForNextDate(completedPending: true, nextDate: null));
            Assert.False(AskForNextDate(true, Today));
            Assert.False(AskForNextDate(false, null));
        }

        // ---- visit requires an open Field Day ---------------------------------------------------------------------------

        [Fact]
        public void Visit_RequiresTodaysDayToBeOpen()
        {
            Assert.Null(FieldVisitService.CheckOpenDay(FieldDayRules.StatusOpen));
            Assert.Equal(FieldVisitService.NoOpenDayMessage, FieldVisitService.CheckOpenDay(null));
            Assert.Equal(FieldVisitService.DayClosedMessage, FieldVisitService.CheckOpenDay(FieldDayRules.StatusClosed));
            Assert.Equal(FieldVisitService.DayClosedMessage, FieldVisitService.CheckOpenDay(FieldDayRules.StatusAutoClosed));
        }

        [Fact]
        public void Visit_Validation_DiscussedRequired_PhotoLimitAndConsent()
        {
            var options = new FieldWorkOptions { MaxPhotosPerVisit = 2, PhotoRoot = Path.GetTempPath() };
            var store = new FieldPhotoStore(options);
            byte[] Jpeg() { var b = new byte[100]; b[0] = 0xFF; b[1] = 0xD8; b[2] = 0xFF; b[^2] = 0xFF; b[^1] = 0xD9; return b; }
            FieldVisitService.VisitRequest R(string? discussed, int photos, int? consent)
                => new(1, 12m, 77m, 10m, null, discussed, null, Array.Empty<string?>(), Array.Empty<string?>(),
                    Enumerable.Range(0, photos).Select(_ => Jpeg()).ToList(), consent, null, null, null, "t");
            Assert.Empty(FieldVisitService.Validate(R("Talked", 2, 1), options, store, Today, out _, out _));
            Assert.NotEmpty(FieldVisitService.Validate(R(" ", 0, 1), options, store, Today, out _, out _));
            Assert.NotEmpty(FieldVisitService.Validate(R("Talked", 3, 1), options, store, Today, out _, out _));
            Assert.NotEmpty(FieldVisitService.Validate(R("Talked", 1, 0), options, store, Today, out _, out _));   // reminder version changed
            Assert.Equal(FieldWorkOptions.MaxPhotosPerVisitLimit, new FieldWorkOptions { MaxPhotosPerVisit = 99 }.EffectiveMaxPhotosPerVisit);
        }

        // ---- reassign / filter ------------------------------------------------------------------------------------------

        [Fact]
        public void Reassign_NeedsCustomers_AnActiveTarget_AndADifferentEmployee()
        {
            Assert.Null(ValidateReassign(5, 6, true, 3));
            Assert.NotNull(ValidateReassign(5, 6, true, 0));
            Assert.NotNull(ValidateReassign(5, 6, false, 3));
            Assert.NotNull(ValidateReassign(5, 5, true, 3));
        }

        [Fact]
        public void ListFilter_DropsUnknownValues_AndManagerOnlyOptionsForEmployees()
        {
            var f = FieldCustomerService.NormalizeFilter(" rose ", "Hacked", "Alien", "nowhere", 99, 13, true, true, "Archived", -4,
                mayManage: false, districtOptions: new[] { "Kolar" }, employeeOptions: new[] { 99 });
            Assert.Equal("rose", f.Search);
            Assert.Null(f.Status);
            Assert.Null(f.Type);
            Assert.Null(f.District);
            Assert.Null(f.EmployeeId);        // employees cannot filter by employee
            Assert.Null(f.NoVisitDays);       // 13 is not an option
            Assert.True(f.NoFollowUp);
            Assert.False(f.NeedsReassign);    // manager only
            Assert.Equal("Active", f.Archived);
            Assert.Equal(1, f.Page);

            var m = FieldCustomerService.NormalizeFilter(null, "Interested", "Farmer", "kolar", 99, 30, false, true, "All", 3,
                mayManage: true, districtOptions: new[] { "Kolar" }, employeeOptions: new[] { 99 });
            Assert.Equal("Interested", m.Status);
            Assert.Equal("Kolar", m.District);
            Assert.Equal(99, m.EmployeeId);
            Assert.Equal(30, m.NoVisitDays);
            Assert.True(m.NeedsReassign);
            Assert.Equal("All", m.Archived);
        }

        [Fact]
        public void ProfileChanges_AreDescribed_WithoutPhoneNumbers()
        {
            var old = new Models.FieldCustomer { Name = "Rose", Phone = "9876543210", CustomerType = "Farmer", VillageCity = "Hoskote" };
            var text = FieldCustomerService.DescribeChanges(old, new CleanCustomer("Rose Farm", null, "9123456789", null, "Farmer", "Hoskote", "Kolar", null));
            Assert.Contains("Name: \"Rose\" → \"Rose Farm\"", text);
            Assert.Contains("Phone changed", text);
            Assert.DoesNotContain("9123456789", text);
            Assert.DoesNotContain("9876543210", text);
            Assert.Equal("", FieldCustomerService.DescribeChanges(old, new CleanCustomer("Rose", null, "9876543210", null, "Farmer", "Hoskote", null, null)));
        }
    }
}
