using PlantStockManager.Services;

namespace PlantStockManager.Tests
{
    // Seedling booking ORDERS ("Add Another Species" = one order with several variety
    // lines, linked by Bookings.OrderBookingId): grouping, labels, new-order validation,
    // and the code paths that must treat the order -- not a line -- as the booking.
    // Database behaviour: BookingOrdersE2ETests.
    public class BookingOrdersTests
    {
        private static string ReadSource(params string[] parts)
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "PlantStockManager.sln")))
                dir = dir.Parent;
            Assert.NotNull(dir);
            return File.ReadAllText(Path.Combine(new[] { dir!.FullName }.Concat(parts).ToArray())).Replace("\r\n", "\n");
        }

        private sealed record Line(int Id, int OrderId, string Variety);

        [Fact]
        public void Group_KeepsOrdersInFirstAppearance_AndLinesById()
        {
            var lines = new[]
            {
                new Line(5052, 5050, "Impatiens"), new Line(4000, 4000, "Basil"), new Line(5050, 5050, "Petunia"),
                new Line(5051, 5050, "Marigold"), new Line(3000, 3000, "Zinnia")
            };
            var groups = BookingOrders.Group(lines, l => l.OrderId, l => l.Id);
            Assert.Equal(new[] { 5050, 4000, 3000 }, groups.Select(g => g.OrderId));
            Assert.Equal(new[] { "Petunia", "Marigold", "Impatiens" }, groups[0].Lines.Select(l => l.Variety));
            Assert.Equal(5050, groups[0].First.Id);
            Assert.Single(groups[1].Lines);
            Assert.Equal(lines.Length, groups.Sum(g => g.Lines.Count));           // nothing lost, nothing merged across orders
        }

        [Fact]
        public void LineLabel_IsBookingNumberAndVariety()
        {
            Assert.Equal("Booking #5050 – Marigold", BookingOrders.LineLabel(5050, "Marigold"));
            Assert.Equal("Booking #5050 – Marigold", BookingOrders.LineLabel(5050, "  Marigold "));
            Assert.Equal("Booking #5050", BookingOrders.LineLabel(5050, null));
            Assert.DoesNotContain("line", BookingOrders.LineLabel(5050, "Marigold"), StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void EffectiveOrderSql_IsIsNullOrderBookingIdId()
            => Assert.Equal("ISNULL(b.OrderBookingId, b.Id)", BookingOrders.EffectiveOrderSql("b"));

        private static readonly DateTime D = new(2026, 10, 10);

        [Fact]
        public void ValidateNewOrder_ThreeGoodLines_IsValid()
            => Assert.True(BookingOrders.ValidateNewOrder(new[]
            {
                new BookingOrders.NewLine(1, 10, 500, D), new BookingOrders.NewLine(2, 20, 300, D.AddDays(5)), new BookingOrders.NewLine(3, 30, 200, D)
            }).Ok);

        [Theory]
        [InlineData(0, 10, 5, true, "Species is required (variety 2).")]
        [InlineData(1, 0, 5, true, "Variety is required (variety 2).")]
        [InlineData(1, 10, 0, true, "Quantity must be at least 1 (variety 2).")]
        [InlineData(1, 10, -3, true, "Quantity must be at least 1 (variety 2).")]
        [InlineData(1, 10, 5, false, "Delivery date is required (variety 2).")]
        public void ValidateNewOrder_OneBadLine_RejectsTheWholeOrder(int plant, int species, int qty, bool hasDate, string error)
        {
            var lines = new[] { new BookingOrders.NewLine(1, 10, 500, D), new BookingOrders.NewLine(plant, species, qty, hasDate ? D : null) };
            var (ok, message) = BookingOrders.ValidateNewOrder(lines);
            Assert.False(ok);
            Assert.Equal(error, message);
        }

        [Fact]
        public void ValidateNewOrder_EmptyOrTooMany_IsRejected()
        {
            Assert.False(BookingOrders.ValidateNewOrder(Array.Empty<BookingOrders.NewLine>()).Ok);
            Assert.False(BookingOrders.ValidateNewOrder(null).Ok);
            Assert.False(BookingOrders.ValidateNewOrder(Enumerable.Range(0, BookingOrders.MaxLinesPerOrder + 1)
                .Select(_ => new BookingOrders.NewLine(1, 1, 1, D)).ToList()).Ok);
        }

        // ---- code paths ---------------------------------------------------------------------------------

        [Fact]
        public void BookPage_SendsTheWholeOrderInOneRequest()
        {
            var view = ReadSource("Pages", "Bookings", "Book.cshtml");
            Assert.DoesNotContain("lines.map(", view);                      // no longer one POST per line
            Assert.DoesNotContain("$.when.apply", view);
            Assert.Contains("payload[\"Lines[\" + i + \"].SpeciesId\"]", view);
            var model = ReadSource("Pages", "Bookings", "Book.cshtml.cs");
            Assert.Contains("_bookingRepo.InsertOrderAsync(Booking, lines)", model);
        }

        [Fact]
        public void InsertOrder_IsOneTransaction_FirstLineIsTheBookingNumber()
        {
            var repo = ReadSource("Data", "BookingRepository.cs");
            var body = repo[repo.IndexOf("InsertOrderAsync(\n", StringComparison.Ordinal)..];
            Assert.Contains("conn.BeginTransaction()", body);
            Assert.Contains("UPDATE Bookings SET OrderBookingId = Id WHERE Id = @Id", body);
            Assert.Contains("ids.Count == 0 ? DBNull.Value : ids[0]", body);
            Assert.Contains("transaction.Rollback()", body);
        }

        [Fact]
        public void OrderLevelCounts_CountUniqueOrders()
        {
            Assert.Contains("COUNT(DISTINCT ISNULL(OrderBookingId, Id))", ReadSource("Data", "BookingRepository.cs"));          // daily report / WhatsApp
            // Management Dashboard (the home page "/" no longer shows booking counts): every
            // open line carries its order id, and orders are counted DISTINCT.
            var dashboard = ReadSource("Data", "ManagementDashboardRepository.cs");
            Assert.Contains("SELECT ISNULL(b.OrderBookingId, b.Id), b.SpeciesId", dashboard);
            Assert.Contains("SELECT COUNT(DISTINCT OrderId), COUNT(*)", dashboard);
            Assert.DoesNotContain("dbo.Bookings", ReadSource("Pages", "Index.cshtml.cs"));
        }

        [Fact]
        public void ReviseAddVariety_JoinsTheSameOrder()
        {
            var repo = ReadSource("Data", "SeedlingFulfilmentRepository.cs");
            Assert.Contains("UPDATE dbo.Bookings SET OrderBookingId = Id WHERE Id = @ParentId AND OrderBookingId IS NULL", repo);
            Assert.Contains("p.Id, p.OrderBookingId\nFROM dbo.Bookings p WHERE p.Id = @ParentId;", repo);
        }

        [Fact]
        public void EditBooking_AppliesCustomerDetailsToTheWholeOrder()
        {
            var repo = ReadSource("Data", "BookingRepository.cs");
            Assert.Contains("WHERE OrderBookingId = @O AND Id <> @BookingId;", repo);
            Assert.Contains("BookedById = @BookedById, BookedByOther = NULL, StateId = @StateId, DistrictId = @DistrictId,", repo);
        }

        [Fact]
        public void Migrations_AreGuarded_DryRunCommit_AndServerChecked()
        {
            foreach (var file in new[] { "2026-10-05_BookingOrderLink_Schema.sql", "2026-10-05_BookingOrderLink_Backfill.sql", "2026-10-05_BookingOrderLink_Rollback.sql" })
            {
                var sql = ReadSource("Database", "Migrations", file);
                Assert.Contains("DB_NAME() <> N'PlantsIMS2_Test' AND DB_NAME() NOT LIKE N'PlantsIMS2[_]Scratch[_]%'", sql);
                Assert.Contains("N'$(MODE)' NOT IN (N'DRYRUN', N'COMMIT')", sql);
                Assert.Contains("@@SERVERNAME <> N'$(TARGET_SERVER)'", sql);
                Assert.Contains("ROLLBACK TRANSACTION", sql);
            }
            var backfill = ReadSource("Database", "Migrations", "2026-10-05_BookingOrderLink_Backfill.sql");
            Assert.Contains("WHERE b.OrderBookingId IS NULL", backfill);                        // idempotent: only unlinked rows
            Assert.Contains("THROW 52108", backfill);                                          // nothing but OrderBookingId may change
            foreach (var reason in new[] { "SPAN", "NEAR", "MULTI_ADVANCE", "MIXED_LOCATION", "INTERLEAVED", "DUPLICATE_LINE" })
                Assert.Contains($"N'{reason}'", backfill);
        }

        [Fact]
        public void PottedPlantBookings_AreUntouched()
        {
            Assert.DoesNotContain("OrderBookingId", ReadSource("Data", "PottedPlantBookingRepository.cs"));
            Assert.DoesNotContain("OrderBookingId", ReadSource("Data", "DispatchRepository.cs"));
        }
    }
}
