using PlantStockManager.Services.FieldWork;
using static PlantStockManager.Services.FieldWork.FieldExpenseRules;

namespace PlantStockManager.Tests
{
    // Field Work Phase 4 (2026-11-03): the pure rules of an expense (no database).
    public class FieldExpenseRulesTests
    {
        private static readonly DateTime Today = new(2026, 11, 3);

        private static IReadOnlyList<string> V(string? category = "Food", string? amount = "250", DateTime? date = null, string? note = null)
            => Validate(new Input(category, amount, date, note), Today, 7, out _);

        [Fact]
        public void Category_MustBeOneOfTheList_OtherNeedsANote()
        {
            foreach (var c in Categories.Where(c => c != CategoryOther))
                Assert.Empty(V(c));
            Assert.Contains("Choose the category.", V(null));
            Assert.Contains("Choose the category.", V("Fuel"));       // the old name is not accepted
            Assert.Contains("Write a note for an \"Other\" expense.", V("Other", note: "  "));
            Assert.Empty(V("Other", note: "Courier"));
            Assert.Contains($"The note can be at most {MaxNoteLength} characters.", V(note: new string('x', MaxNoteLength + 1)));
        }

        [Fact]
        public void Amount_PositiveAtMostTheCeiling_TwoDecimals_CommasAllowed()
        {
            Validate(new Input("Food", "1,250.50", null, null), Today, 7, out var clean);
            Assert.Equal(1250.50m, clean!.Amount);
            Assert.Contains("Enter the amount.", V(amount: " "));
            Assert.NotEmpty(V(amount: "abc"));
            Assert.NotEmpty(V(amount: "-5"));
            Assert.Contains("The amount must be more than zero.", V(amount: "0"));
            Assert.Empty(V(amount: "100000"));
            Assert.NotEmpty(V(amount: "100000.01"));
            Assert.Contains("The amount can have at most 2 decimals (paise).", V(amount: "10.005"));
        }

        [Fact]
        public void Date_DefaultsToToday_UpTo7DaysBack_NeverFuture()
        {
            Validate(new Input("Food", "10", null, null), Today, 7, out var clean);
            Assert.Equal(Today, clean!.Date);
            Assert.Empty(V(date: Today.AddDays(-7)));
            Assert.NotEmpty(V(date: Today.AddDays(-8)));
            Assert.Contains("The date cannot be in the future.", V(date: Today.AddDays(1)));
            Assert.Equal(Today.AddDays(-7), EarliestDate(Today, 7));
            Assert.Equal(Today, EarliestDate(Today, -3));   // a negative setting means today only
        }

        [Fact]
        public void BillPhoto_Required_WithTheCurrentReminderVersion()
        {
            Assert.Equal("Take a photo of the bill.", CheckBill(false, 1, 1));
            Assert.NotNull(CheckBill(true, null, 1));
            Assert.NotNull(CheckBill(true, 1, 2));
            Assert.Null(CheckBill(true, 1, 1));

            var options = new FieldWorkOptions { PhotoRoot = Path.GetTempPath() };
            var store = new FieldPhotoStore(options);
            var jpeg = new byte[64]; jpeg[0] = 0xFF; jpeg[1] = 0xD8; jpeg[2] = 0xFF; jpeg[^2] = 0xFF; jpeg[^1] = 0xD9;
            FieldExpenseService.CreateRequest R(byte[]? photo, int? v) => new("Food", "10", null, null, photo, v, "t");
            Assert.Empty(FieldExpenseService.Validate(R(jpeg, 1), options, store, Today, out _));
            Assert.Contains("Take a photo of the bill.", FieldExpenseService.Validate(R(null, 1), options, store, Today, out _));
            Assert.Contains(FieldExpenseService.Validate(R(new byte[] { 1, 2, 3, 4 }, 1), options, store, Today, out _), e => e.StartsWith("Bill photo:"));
        }

        [Fact]
        public void Cancel_OnlyTheOwner_OnlyWhilePending_WithAReason()
        {
            Assert.Null(CheckCancel(StatusSubmitted, 7, 7));
            Assert.NotNull(CheckCancel(StatusSubmitted, 7, 8));
            foreach (var s in new[] { StatusApproved, StatusRejected, StatusCancelled })
                Assert.NotNull(CheckCancel(s, 7, 7));
            Assert.NotNull(ValidateReason(" ", "cancelling"));
            Assert.NotNull(ValidateReason(new string('x', MaxReasonLength + 1), "cancelling"));
            Assert.Null(ValidateReason("Wrong amount", "cancelling"));
        }

        [Fact]
        public void Decision_ManagerOnly_NeverOwnUnlessAdministrator_OnlyPending_RejectNeedsComment()
        {
            Assert.Null(CheckDecision(StatusSubmitted, ownerId: 7, deciderId: 9, mayManage: true, isFullAccess: false));
            Assert.NotNull(CheckDecision(StatusSubmitted, 7, 9, mayManage: false, isFullAccess: false));
            Assert.Contains("own expense", CheckDecision(StatusSubmitted, 7, 7, true, false));
            Assert.Null(CheckDecision(StatusSubmitted, 7, 7, true, isFullAccess: true));
            foreach (var s in new[] { StatusApproved, StatusRejected, StatusCancelled })
                Assert.NotNull(CheckDecision(s, 7, 9, true, true));
            Assert.NotNull(ValidateComment(reject: true, " "));
            Assert.Null(ValidateComment(reject: true, "No date on the bill"));
            Assert.Null(ValidateComment(reject: false, null));
            Assert.NotNull(ValidateComment(reject: false, new string('x', MaxCommentLength + 1)));
        }

        [Fact]
        public void MonthRange_InvalidOrFutureMeansThisMonth()
        {
            Assert.Equal((new DateTime(2026, 11, 1), new DateTime(2026, 11, 30)), MonthRange(null, Today));
            Assert.Equal((new DateTime(2026, 2, 1), new DateTime(2026, 2, 28)), MonthRange("2026-02", Today));
            Assert.Equal(new DateTime(2026, 11, 1), MonthRange("2026-12", Today).From);
            Assert.Equal(new DateTime(2026, 11, 1), MonthRange("garbage", Today).From);
        }

        [Fact]
        public void ManagerFilter_DefaultsToThisMonth_DropsUnknownValues_CapsTheRange()
        {
            var f = NormalizeFilter(null, null, null, null, null, new[] { 5 }, Today);
            Assert.Equal((new DateTime(2026, 11, 1), Today), (f.From, f.To));
            f = NormalizeFilter(Today, Today.AddDays(-3), 99, "Fuel", "Paid", new[] { 5 }, Today);
            Assert.Equal((Today.AddDays(-3), Today, (int?)null, (string?)null, (string?)null), (f.From, f.To, f.UserId, f.Category, f.Status));
            f = NormalizeFilter(Today.AddYears(-3), Today, 5, "Toll", StatusApproved, new[] { 5 }, Today);
            Assert.Equal(Today.AddDays(-MaxRangeDays), f.From);
            Assert.Equal((5, "Toll", StatusApproved), (f.UserId!.Value, f.Category!, f.Status!));
        }

        [Fact]
        public void StartDayGate_LetsTheEmployeesExpensePagesThroughAfterConsent_NotTheManagerPage()
        {
            foreach (var page in new[] { "/FieldWork/Expenses/Index", "/FieldWork/Expenses/Create", "/FieldWork/Expenses/Details" })
            {
                Assert.Equal(FieldDayRules.GateResult.Consent, FieldDayRules.Gate(true, page, hasConsent: false, todayStatus: null, mayOpenDashboard: false));
                Assert.Equal(FieldDayRules.GateResult.Allow, FieldDayRules.Gate(true, page, true, null, false));            // not started
                Assert.Equal(FieldDayRules.GateResult.Allow, FieldDayRules.Gate(true, page, true, "Closed", false));        // after End Day
            }
            Assert.Equal(FieldDayRules.GateResult.StartDay, FieldDayRules.Gate(true, "/FieldWork/Expenses/Manage/Index", true, "Closed", false));
            Assert.Equal(FieldDayRules.GateResult.StartDay, FieldDayRules.Gate(true, "/FieldWork/Visits/New", true, "Closed", false));
        }
    }
}
