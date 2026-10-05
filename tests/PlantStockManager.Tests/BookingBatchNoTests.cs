using System.IO;
using PlantStockManager.Models;
using PlantStockManager.Services;

namespace PlantStockManager.Tests
{
    // Booking / Allocation batch number (month letter + two-digit sowing day)
    // and the Allocate Batch double-submit guard -- pure rules and wiring.
    // Database behaviour (search, filters, paging, allocation from the right
    // ReadyStock row, duplicates) is BookingBatchAllocationE2ETests.
    public class BookingBatchNoTests
    {
        private static string ReadSource(params string[] parts)
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "PlantStockManager.sln")))
                dir = dir.Parent;
            Assert.NotNull(dir);
            return File.ReadAllText(Path.Combine(new[] { dir!.FullName }.Concat(parts).ToArray()));
        }

        // ---- format: month letter + "-" + two-digit day ----------------------------------------------

        [Theory]
        [InlineData(2026, 10, 1, "J-01")]
        [InlineData(2026, 10, 2, "J-02")]
        [InlineData(2026, 10, 9, "J-09")]
        [InlineData(2026, 10, 10, "J-10")]
        [InlineData(2026, 10, 29, "J-29")]
        [InlineData(2026, 10, 31, "J-31")]
        [InlineData(2026, 9, 29, "I-29")]
        [InlineData(2026, 1, 1, "A-01")]
        [InlineData(2026, 1, 9, "A-09")]
        [InlineData(2026, 1, 10, "A-10")]
        [InlineData(2026, 12, 25, "L-25")]
        public void Format_IsMonthLetterDashTwoDigitDay(int year, int month, int day, string expected)
            => Assert.Equal(expected, SowingBatchNo.Format(new DateTime(year, month, day)));

        [Fact]
        public void Format_IgnoresTheTimeOfDay_AndTheYear()
        {
            Assert.Equal("J-10", SowingBatchNo.Format(new DateTime(2026, 10, 10, 23, 59, 0)));
            Assert.Equal("J-10", SowingBatchNo.Format(new DateTime(2027, 10, 10)));
        }

        [Fact]
        public void SameSowingDate_GivesTheSameBatchNo_NeverIncremented()
        {
            var labels = Enumerable.Range(0, 3).Select(_ => SowingBatchNo.Format(new DateTime(2026, 10, 10))).ToList();
            Assert.All(labels, l => Assert.Equal("J-10", l));
        }

        [Fact]
        public void Format_UsesTheSameMonthLetterAsTheFullSowingCode()
        {
            var date = new DateTime(2026, 10, 2);
            var full = PlantStockManager.Data.BatchNumberRepository.FormatSowingBatchNumber(date, 19);   // 2026-10-02-J-019
            Assert.Equal("2026-10-02-J-019", full);
            Assert.StartsWith(full[11..12], SowingBatchNo.Format(date));                               // both "J"
        }

        // ---- search parsing ("J-10" typed in the search box) ----------------------------------------

        [Theory]
        [InlineData("J-10", 10, 10)]
        [InlineData("j-10", 10, 10)]
        [InlineData(" J10 ", 10, 10)]
        [InlineData("J-01", 10, 1)]
        [InlineData("J-1", 10, 1)]
        [InlineData("I-29", 9, 29)]
        [InlineData("A-09", 1, 9)]
        public void TryParse_RecognisesBatchNumbers(string text, int month, int day)
        {
            Assert.True(SowingBatchNo.TryParse(text, out var m, out var d));
            Assert.Equal(month, m);
            Assert.Equal(day, d);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("Petunia")]
        [InlineData("M-10")]          // no month 13
        [InlineData("J-00")]
        [InlineData("J-32")]
        [InlineData("2026-10-02-J-019")]
        public void TryParse_RejectsAnythingElse(string? text)
            => Assert.False(SowingBatchNo.TryParse(text, out _, out _));

        [Fact]
        public void Models_ExposeTheShortBatchNo_AndKeepTheirOwnIds()
        {
            var a = new ReadyBatchOption { ReadyStockId = 71, SeedSowingId = 971, SowingDate = new DateTime(2026, 10, 10), BatchCode = "2026-10-10-J-001" };
            var b = new ReadyBatchOption { ReadyStockId = 72, SeedSowingId = 972, SowingDate = new DateTime(2026, 10, 10), BatchCode = "2026-10-10-J-002" };
            Assert.Equal(a.BatchNo, b.BatchNo);                 // both "J-10"
            Assert.NotEqual(a.ReadyStockId, b.ReadyStockId);    // still two separate records
            Assert.Equal("J-10", new BookingBatchAllocation { SowingDate = new DateTime(2026, 10, 10) }.BatchNo);
            Assert.Equal("J-10", new SeedlingDispatchLine { SowingDate = new DateTime(2026, 10, 10) }.BatchNo);
        }

        // ---- double-submit guard ---------------------------------------------------------------------

        [Fact]
        public void TokenGuard_AcceptsEachTokenOnce()
        {
            var guard = new SubmissionTokenGuard();
            var token = SubmissionTokenGuard.NewToken();
            Assert.True(guard.TryConsume("scope", token));
            Assert.False(guard.TryConsume("scope", token));
            Assert.True(guard.TryConsume("other-scope", token));
            Assert.False(guard.TryConsume("scope", null));
            Assert.False(guard.TryConsume("scope", ""));
        }

        [Fact]
        public async Task TokenGuard_UnderSimultaneousSubmits_ExactlyOneWins()
        {
            var guard = new SubmissionTokenGuard();
            var token = SubmissionTokenGuard.NewToken();
            var results = await Task.WhenAll(Enumerable.Range(0, 50).Select(_ => Task.Run(() => guard.TryConsume("scope", token))));
            Assert.Equal(1, results.Count(r => r));
        }

        // ---- wiring ------------------------------------------------------------------------------------

        [Fact]
        public void AllocateTable_PostsTheReadyStockId_NeverTheBatchNo()
        {
            var html = ReadSource("Pages", "Bookings", "SeedlingDispatch.cshtml");
            Assert.Contains("name=\"AllocateReadyStockId\" value=\"@o.ReadyStockId\"", html);
            Assert.DoesNotContain("<select asp-for=\"AllocateReadyStockId\"", html);    // the large dropdown is gone
            foreach (var column in new[] { "Select", "Batch No", "Variety", "Sowing Date", "Available", "Cavity", "Area", "Polyhouse", "Allocation Type", "Ref" })
                Assert.Contains($"<th{(column == "Available" ? " class=\"text-end\"" : "")}>{column}</th>", html);
            Assert.Contains("asp-for=\"AllocateToken\"", html);

            var page = ReadSource("Pages", "Bookings", "SeedlingDispatch.cshtml.cs");
            Assert.Contains("_tokens.TryConsume(AllocateTokenScope, AllocateToken)", page);
            Assert.Contains("_repo.AllocateBatchAsync(Id, AllocateReadyStockId, AllocateQuantity, SubstitutionReason, Actor, CanAccessArea)", page);
        }

        [Fact]
        public void NothingLooksUpOrStoresTheShortBatchNo()
        {
            var repo = ReadSource("Data", "SeedlingFulfilmentRepository.cs");
            // No SQL ever selects a batch by its code or batch number (only LIKE searches; identity is the Id).
            Assert.DoesNotContain("SowingCode = @", repo);
            Assert.DoesNotContain("BatchNo = @", repo);
            // The full, unique SowingCode generation is unchanged.
            Assert.Contains("FormatSowingBatchNumber(sowingDate, sequence)", ReadSource("Data", "BatchNumberRepository.cs"));
        }
    }
}
