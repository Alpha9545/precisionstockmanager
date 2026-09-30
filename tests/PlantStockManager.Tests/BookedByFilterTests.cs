using System.Text.RegularExpressions;
using PlantStockManager.Authorization;
using PlantStockManager.Services;

namespace PlantStockManager.Tests
{
    // CORRECTION #6 -- the "Booking By" filter of the booking record lists. Pure rules and wiring here; the real database
    // behaviour (every person, combined filters, no match, BookedById / BookedByOther / not recorded, historical bookings,
    // Area security, "no filter = exactly as before") is BookedByFilterE2ETests against a scratch copy of the database.
    public class BookedByFilterTests
    {
        // ---- parsing: what may arrive in the query string --------------------------------------------------------

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        public void NothingSelected_MeansEveryone_WithNoNotice(string? raw)
        {
            var (filter, notice) = BookedByFilter.Parse(raw);
            Assert.False(filter.IsActive);
            Assert.Equal("", filter.Value);
            Assert.Null(notice);
        }

        [Fact]
        public void APerson_IsParsedAndRoundTrips()
        {
            var (filter, notice) = BookedByFilter.Parse("U:3");
            Assert.Null(notice);
            Assert.Equal(BookedByFilter.FilterKind.User, filter.Kind);
            Assert.Equal(3, filter.UserId);
            Assert.Equal("U:3", filter.Value);
            Assert.Equal(BookedByFilter.UserValue(3), filter.Value);
        }

        [Fact]
        public void AFreeTextName_IsParsedTrimmedAndRoundTrips()
        {
            var (filter, notice) = BookedByFilter.Parse("  O:  Walk-in Agent ");
            Assert.Null(notice);
            Assert.Equal(BookedByFilter.FilterKind.Other, filter.Kind);
            Assert.Equal("Walk-in Agent", filter.OtherName);
            Assert.Equal("O:Walk-in Agent", filter.Value);
        }

        [Fact]
        public void NotRecorded_IsParsed()
        {
            var (filter, notice) = BookedByFilter.Parse("X:none");
            Assert.Null(notice);
            Assert.Equal(BookedByFilter.FilterKind.NotRecorded, filter.Kind);
            Assert.Equal("X:none", filter.Value);
        }

        [Theory]
        [InlineData("bogus")]
        [InlineData("U:")]
        [InlineData("U:0")]
        [InlineData("U:-4")]
        [InlineData("U:abc")]
        [InlineData("U:3; DROP TABLE Bookings")]
        [InlineData("U:99999999999")]
        [InlineData("O:")]
        [InlineData("O:   ")]
        [InlineData("x:none")]
        [InlineData("u:3")]
        [InlineData("3")]
        public void UnknownOrMalformedValues_AreIgnored_EveryoneIsShown_WithANotice(string raw)
        {
            var (filter, notice) = BookedByFilter.Parse(raw);
            Assert.False(filter.IsActive);
            Assert.Equal("", filter.Value);
            Assert.NotNull(notice);
        }

        [Fact]
        public void ATooLongFreeTextName_IsIgnored()
        {
            var (filter, notice) = BookedByFilter.Parse("O:" + new string('x', BookedByFilter.MaxOtherLength + 1));
            Assert.False(filter.IsActive);
            Assert.NotNull(notice);
            Assert.True(BookedByFilter.Parse("O:" + new string('x', BookedByFilter.MaxOtherLength)).Filter.IsActive);
        }

        // ---- how a booking names its person ------------------------------------------------------------------

        [Fact]
        public void TheDisplayedName_IsTheUser_ElseTheFreeText_ElseAUserNumber_ElseEmpty()
        {
            Assert.Equal("Sarika Kolhe", BookedByFilter.Display(3, "Sarika Kolhe", null));
            Assert.Equal("Sarika Kolhe", BookedByFilter.Display(3, "Sarika Kolhe", "Ramesh"));      // the user wins when both are recorded
            Assert.Equal("Walk-in Agent", BookedByFilter.Display(null, null, " Walk-in Agent "));
            Assert.Equal("Walk-in Agent", BookedByFilter.Display(0, "", "Walk-in Agent"));
            Assert.Equal("User #10", BookedByFilter.Display(10, "", null));                          // the user no longer exists
            Assert.Equal("", BookedByFilter.Display(null, null, null));
            Assert.Equal("", BookedByFilter.Display(0, "", ""));
        }

        [Fact]
        public void OptionLabels_MakeInactiveAndMissingUsersRecognisable_ButKeepThemSelectable()
        {
            Assert.Equal("Sarika Kolhe", BookedByFilter.UserLabel(3, "Sarika Kolhe", true));
            Assert.Equal("Somnath (inactive)", BookedByFilter.UserLabel(5, "Somnath", false));
            Assert.Equal("User #10 (no longer in the system)", BookedByFilter.UserLabel(10, null, null));
            Assert.Equal("Walk-in Agent (other)", BookedByFilter.OtherLabel("Walk-in Agent"));
        }

        // ---- wiring (source scans) --------------------------------------------------------------------------

        private static string Repo(params string[] parts)
        {
            var dir = AppContext.BaseDirectory;
            while (dir != null && !Directory.GetFiles(dir, "*.sln").Any()) dir = Path.GetDirectoryName(dir);
            Assert.NotNull(dir);
            return File.ReadAllText(Path.Combine(new[] { dir! }.Concat(parts).ToArray()));
        }

        [Fact]
        public void TheSql_IsParameterised_AndUsesOnlyTheExistingBookedByColumns()
        {
            var sql = Repo("Data", "BookedBySql.cs");
            Assert.Contains("@ByKind IS NULL", sql);                                                  // no filter = every row, as before
            Assert.Contains("@ByKind = 'U' AND {idColumn} = @ByUserId", sql);
            Assert.Contains("@ByKind = 'O' AND LTRIM(RTRIM({otherColumn})) = @ByOther", sql);
            Assert.Contains("@ByKind = 'X' AND ({idColumn} IS NULL OR {idColumn} = 0)", sql);
            foreach (var parameter in new[] { "@ByKind", "@ByUserId", "@ByOther" })
                Assert.Contains($"cmd.Parameters.Add(\"{parameter}\"", sql);
            // user values never reach the SQL text
            Assert.DoesNotContain("filter.OtherName +", sql);
            Assert.DoesNotContain("+ filter.", sql);
            Assert.DoesNotContain("filter.UserId}", sql.Replace("filter.UserId : DBNull", ""));
        }

        [Fact]
        public void BothBookingTables_ShareTheSameFilterSql()
        {
            var seedling = Repo("Data", "BookingRepository.cs");
            Assert.Contains("BookedBySql.Clause(\"b.BookedById\", \"b.BookedByOther\")", seedling);
            Assert.Contains("BookedBySql.AddParameters(cmd, bookedBy)", seedling);
            Assert.Contains("BookedBySql.LoadOptionsAsync(conn, \"dbo.Bookings b\", \"b\", null, null)", seedling);
            var potted = Repo("Data", "PottedPlantBookingRepository.cs");
            Assert.Contains("BookedBySql.Clause(\"bk.BookedById\", \"bk.BookedByOther\")", potted);
            Assert.Contains("BookedBySql.AddParameters(cmd, bookedBy)", potted);
            Assert.Contains("BookedBySql.LoadOptionsAsync(conn, \"dbo.PottedPlantBookings bk\", \"bk\", AreaScope,", potted);
        }

        [Fact]
        public void TheSeedlingQuery_KeepsEveryOtherFilter_AndTheFilterIsOneMoreAnd()
        {
            var repo = Repo("Data", "BookingRepository.cs");
            var method = repo.Substring(repo.IndexOf("public async Task<List<Booking>> GetBookingRecords(int? plantTypeId", StringComparison.Ordinal));
            method = method.Substring(0, method.IndexOf("using var r = await cmd.ExecuteReaderAsync()", StringComparison.Ordinal));
            foreach (var kept in new[] { "WHERE YEAR(b.DeliveryDate) = @Year", "AND b.Status = @Status", "AND b.PlantId = @PlantTypeId", "AND b.SpeciesId = @SpeciesId", "AND MONTH(b.DeliveryDate) = @Month" })
                Assert.Contains(kept, method);
            Assert.Contains("BookedBySql.Clause(", method);
            Assert.DoesNotContain(".Where(", method);            // filtered by the database, never in C#
            Assert.Contains("BookedByFilter? bookedBy = null", repo);
        }

        [Fact]
        public void PottedBookings_KeepTheirAreaRuleInsideTheQuery_AndTheDropdownIsScopedToo()
        {
            var potted = Repo("Data", "PottedPlantBookingRepository.cs");
            Assert.Contains("(@Allowed IS NULL OR bk.AreaId IS NULL OR bk.AreaId IN (SELECT CAST(value AS INT) FROM STRING_SPLIT(@Allowed, ',')))", potted);
            var search = potted.Substring(potted.IndexOf("public async Task<List<PottedPlantBooking>> SearchAsync", StringComparison.Ordinal));
            search = search.Substring(0, search.IndexOf("public async Task<List<BookedByFilter.Option>>", StringComparison.Ordinal));
            Assert.Contains("+ AreaScope + BookedBySql.Clause(", search);
            var page = Repo("Pages", "Production", "PottedPlantBooking", "Index.cshtml.cs");
            Assert.Contains("_areaAccessService.HasFullAreaAccess(User) ? null : _areaAccessService.GetAccessibleAreaIds(User)", page);
            Assert.Contains("_bookingRepo.SearchAsync(bookedBy, allowedAreas)", page);
            Assert.Contains("_bookingRepo.GetBookedByOptionsAsync(allowedAreas)", page);
            Assert.Contains("_areaAccessService.CanAccessArea(User, b.AreaId)", page);              // the old check stays as a second guard
        }

        [Theory]
        [InlineData("Pages/Data/BookingsRecord.cshtml", "Pages/Data/BookingsRecord.cshtml.cs")]
        [InlineData("Pages/Bookings/EditBookingRecords.cshtml", "Pages/Bookings/EditBookingRecords.cshtml.cs")]
        [InlineData("Pages/Production/PottedPlantBooking/Index.cshtml", "Pages/Production/PottedPlantBooking/Index.cshtml.cs")]
        public void EveryBookingList_HasTheSameFilterBehaviour(string viewPath, string modelPath)
        {
            var view = Repo(viewPath.Split('/'));
            var model = Repo(modelPath.Split('/'));
            Assert.Contains("[BindProperty(SupportsGet = true)]", model);
            Assert.Contains("public string? SelectedBookedBy { get; set; }", model);
            Assert.Contains("BookedByFilter.Parse(SelectedBookedBy)", model);
            Assert.Contains("SelectedBookedBy = bookedBy.Value;", model);                            // the normalised value goes back to the form
            Assert.Contains("name=\"SelectedBookedBy\"", view);
            Assert.Contains("<option value=\"\">All</option>", view);                                // All / default
            Assert.Contains("selected=\"@(Model.SelectedBookedBy == option.Value)\"", view);        // stays selected after searching
            Assert.Contains("Clear filters", view);
            Assert.Contains("record@(Model.Bookings.Count == 1 ? \"\" : \"s\") found", view);        // "N records found"
            Assert.Contains("No records match the selected filters", view);
            Assert.Contains("@Model.Notice", view);                                                   // unknown value notice
            Assert.Contains("<form method=\"get\"", view);
        }

        [Fact]
        public void ThePdf_UsesTheSameFilterAsThePage()
        {
            var model = Repo("Pages", "Data", "BookingsRecord.cshtml.cs");
            var pdf = model.Substring(model.IndexOf("OnGetGeneratePdfAsync", StringComparison.Ordinal));
            Assert.Contains("BookedByFilter.Parse(SelectedBookedBy).Filter", pdf);
            var view = Repo("Pages", "Data", "BookingsRecord.cshtml");
            Assert.Contains("asp-route-SelectedBookedBy=\"@Model.SelectedBookedBy\"", view);
        }

        [Fact]
        public void EditPageTable_ColumnsAreUntouched_SoItsEditScriptStillLinesUp()
        {
            var view = Repo("Pages", "Bookings", "EditBookingRecords.cshtml");
            var head = view.Substring(view.IndexOf("<thead class=\"table-dark\"", StringComparison.Ordinal));
            head = head.Substring(0, head.IndexOf("</thead>", StringComparison.Ordinal));
            Assert.Equal(23, Regex.Matches(head, "<th[ >]").Count);
            Assert.Contains("row.find(\"td:eq(16)\")", view);                                        // the hidden BookedById cell the edit form reads
        }

        [Fact]
        public void Creation_Reservation_Fulfilment_AndStatus_AreNotTouched()
        {
            foreach (var (file, marker) in new[]
            {
                ("Data/SeedlingFulfilmentRepository.cs", "BookedBySql"), ("Data/DispatchRepository.cs", "BookedBySql"), ("Pages/Bookings/Book.cshtml.cs", "BookedByFilter"),
                ("Pages/Production/PottedPlantBooking/Create.cshtml.cs", "BookedByFilter"), ("Pages/Production/Dispatch/Create.cshtml.cs", "BookedByFilter")
            })
                Assert.DoesNotContain(marker, Repo(file.Split('/')));
            var insert = Repo("Data", "BookingRepository.cs");
            insert = insert.Substring(insert.IndexOf("public async Task<int> InsertBookingAsync", StringComparison.Ordinal));
            insert = insert.Substring(0, insert.IndexOf("public async", 20, StringComparison.Ordinal));
            Assert.DoesNotContain("BookedBySql", insert);
        }

        [Fact]
        public void PermissionsAreUnchanged()
        {
            Assert.Equal("Booking.View|Booking.Direct|Dispatch.View|Reports.View", FeatureAuthorizationConventions.GetRule("/Data/BookingsRecord").Read);
            Assert.Equal("Booking.Enter", FeatureAuthorizationConventions.GetRule("/Bookings/EditBookingRecords").Read);
            Assert.Equal("Booking.View|Outlet.View", FeatureAuthorizationConventions.GetRule("/Production/PottedPlantBooking/Index").Read);
        }
    }
}
