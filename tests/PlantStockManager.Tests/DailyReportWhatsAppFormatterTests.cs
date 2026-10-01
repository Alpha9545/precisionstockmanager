using PlantStockManager.Models;
using PlantStockManager.Services;

namespace PlantStockManager.Tests
{
    // Step 8E: DailyReport -> the 16 approved Sendvise template variables.
    // Pure, no DB, no HTTP -- FormatVariables never touches SendviseOptions
    // or an API key at all, so there is nothing to accidentally leak.
    public class DailyReportWhatsAppFormatterTests
    {
        private static EmployeeLoginActivity Employee(int userId, string name, bool loggedIn, bool hasActivityToday = false)
            => new()
            {
                UserId = userId,
                Name = name,
                FirstLoginAtToday = loggedIn ? ReportTime : null,
                HasActivityToday = hasActivityToday,
            };

        // 15 logged in (12 active + 3 no-activity, matching ActiveUserCount/
        // NoActivityRecordedCount below) and 5 not logged in. The formatter
        // never emits any of these names -- only the counts -- but the lists
        // are kept populated here to prove names never leak into the template
        // regardless.
        private static List<EmployeeLoginActivity> SampleLoggedIn() => Enumerable.Range(1, 12)
            .Select(i => Employee(i, $"Active Employee {i}", loggedIn: true, hasActivityToday: true))
            .Append(Employee(101, "Amit Patil", loggedIn: true, hasActivityToday: false))
            .Append(Employee(102, "Rahul Shinde", loggedIn: true, hasActivityToday: false))
            .Append(Employee(103, "Sneha Joshi", loggedIn: true, hasActivityToday: false))
            .ToList();

        private static List<EmployeeLoginActivity> SampleNotLoggedIn() => new()
        {
            Employee(201, "Kavita Nair", loggedIn: false),
            Employee(202, "Rohan Deshpande", loggedIn: false),
            Employee(203, "Not LoggedIn 3", loggedIn: false),
            Employee(204, "Not LoggedIn 4", loggedIn: false),
            Employee(205, "Not LoggedIn 5", loggedIn: false),
        };

        // All 4 Plant Types, in the database's own PlantTypeId order -- the
        // formatter itself picks the top 2 BY QUANTITY from this (SEASONAL
        // VARITIES 1430, then MERI GOLD 1250), so the two chosen here are
        // deliberately NOT simply "the first two in the list" -- proving the
        // selection is quantity-driven, not position-driven.
        private static List<ReadyStockPlantTypeQuantity> SampleReadyStockByPlantType() => new()
        {
            new("MERI GOLD", 1250),
            new("CHRYSANTHEMUM", 850),
            new("ZINNIA", 620),
            new("SEASONAL VARITIES", 1430),
        };

        private static DailyReport SampleReport() => new()
        {
            ReportDate = new DateTime(2026, 9, 30),
            SowingQuantity = 3480,
            ReadyStockOverdueCount = 2,
            ReadyStockOverdueQuantity = 150,
            ReadyStockReadyTodayCount = 1,
            ReadyStockReadyTodayQuantity = 80,
            ReadyStockReadySoonCount = 3,
            ReadyStockReadySoonQuantity = 200,
            ReadyStockMeriGoldQuantity = 1250,
            ReadyStockChrysanthemumQuantity = 850,
            ReadyStockZinniaQuantity = 620,
            ReadyStockSeasonalVaritiesQuantity = 1430,
            ReadyStockByPlantType = SampleReadyStockByPlantType(),
            SeedlingBookingsCount = 5,
            SeedlingBookingsQuantity = 500,
            PottedPlantBookingsCount = 2,
            SeedlingDispatchCount = 4,
            SeedlingDispatchQuantity = 400,
            PottedPlantDispatchCount = 1,
            PottedPlantDispatchQuantity = 10,
            OutletSalesCount = 3,
            FertilizerUsageQuantity = 45.5m,
            WastageQuantity = 120,
            TotalActiveEmployees = 20,
            LoggedInEmployeeCount = 15,
            ActiveUserCount = 12,
            NoActivityRecordedCount = 3,
            NotLoggedInEmployeeCount = 5,
            EmployeesLoggedInToday = SampleLoggedIn(),
            EmployeesNotLoggedInToday = SampleNotLoggedIn(),
        };

        private static readonly DateTime ReportTime = new(2026, 9, 30, 10, 30, 0);

        // ---- Exactly 16 variables produced -----------------------------

        [Fact]
        public void FormatVariables_ProducesExactly16Values()
        {
            var variables = DailyReportWhatsAppFormatter.FormatVariables(SampleReport(), ReportTime);
            Assert.Equal(16, variables.Count);
            Assert.Equal(DailyReportWhatsAppFormatter.ExpectedVariableCount, variables.Count);
        }

        // ---- Exact variable positions {{1}} through {{16}} -------------

        [Fact]
        public void FormatVariables_OrderMatchesApprovedTemplate_ExactPositions()
        {
            var variables = DailyReportWhatsAppFormatter.FormatVariables(SampleReport(), ReportTime);

            Assert.Equal("2026-09-30", variables[0]);                  // {{1}} Date
            Assert.Equal("3,480", variables[1]);                       // {{2}} Sowing
            Assert.Equal("SEASONAL VARITIES", variables[2]);           // {{3}} Ready Stock name #1 (highest quantity)
            Assert.Equal("1,430", variables[3]);                       // {{4}} Ready Stock qty #1
            Assert.Equal("MERI GOLD", variables[4]);                   // {{5}} Ready Stock name #2 (2nd highest)
            Assert.Equal("1,250", variables[5]);                       // {{6}} Ready Stock qty #2
            Assert.Equal("Seedling: 5, Potted: 2", variables[6]);      // {{7}} Bookings
            Assert.Equal("Seedling: 4, Potted: 1", variables[7]);      // {{8}} Dispatch
            Assert.Equal("3", variables[8]);                           // {{9}} Outlet Sales
            Assert.Equal("45.5", variables[9]);                        // {{10}} Fertilizer Usage
            Assert.Equal("120", variables[10]);                        // {{11}} Wastage
            Assert.Equal("20", variables[11]);                         // {{12}} Total Employees
            Assert.Equal("15", variables[12]);                         // {{13}} Logged In
            Assert.Equal("12", variables[13]);                         // {{14}} Active Users
            Assert.Equal("Amit Patil, Rahul Shinde, Sneha Joshi", variables[14]);  // {{15}} No Activity Recorded (names)
            Assert.Equal("Kavita Nair, Rohan Deshpande, Not LoggedIn 3, Not LoggedIn 4, Not LoggedIn 5", variables[15]); // {{16}} Not Logged In (names)
        }

        // ---- The two shown Plant Types are chosen by QUANTITY, not position/name ----

        [Fact]
        public void FormatVariables_ReadyStock_SelectsTop2ByQuantity_NotByListPosition()
        {
            // Deliberately put the two HIGHEST-quantity types in positions 2
            // and 3 of the source list (not first) to prove selection is
            // quantity-driven, never "the first two entries."
            var report = SampleReport();
            report.ReadyStockByPlantType = new List<ReadyStockPlantTypeQuantity>
            {
                new("MERI GOLD", 100),
                new("CHRYSANTHEMUM", 9000),   // highest
                new("ZINNIA", 8000),          // 2nd highest
                new("SEASONAL VARITIES", 50),
            };

            var variables = DailyReportWhatsAppFormatter.FormatVariables(report, ReportTime);

            Assert.Equal("CHRYSANTHEMUM", variables[2]);
            Assert.Equal("9,000", variables[3]);
            Assert.Equal("ZINNIA", variables[4]);
            Assert.Equal("8,000", variables[5]);
            Assert.DoesNotContain("MERI GOLD", variables);
            Assert.DoesNotContain("SEASONAL VARITIES", variables);
        }

        [Fact]
        public void FormatVariables_ReadyStock_Tie_BrokenByExistingPlantTypeIdOrder()
        {
            // Equal quantities: OrderByDescending is a stable sort, and the
            // source list already arrives in PlantTypeId order, so a tie
            // keeps that order -- ZINNIA (Id 3) before SEASONAL VARITIES (Id 4).
            var report = SampleReport();
            report.ReadyStockByPlantType = new List<ReadyStockPlantTypeQuantity>
            {
                new("MERI GOLD", 10),
                new("CHRYSANTHEMUM", 10),
                new("ZINNIA", 500),
                new("SEASONAL VARITIES", 500),
            };

            var variables = DailyReportWhatsAppFormatter.FormatVariables(report, ReportTime);

            Assert.Equal("ZINNIA", variables[2]);
            Assert.Equal("SEASONAL VARITIES", variables[4]);
        }

        // ---- Zero Ready Stock still displays as 0 -------------------------

        [Fact]
        public void FormatVariables_ReadyStock_ZeroQuantity_StillDisplayedAsZero_WhenSelected()
        {
            var report = SampleReport();
            report.ReadyStockByPlantType = new List<ReadyStockPlantTypeQuantity>
            {
                new("MERI GOLD", 0),
                new("CHRYSANTHEMUM", 0),
                new("ZINNIA", 0),
                new("SEASONAL VARITIES", 0),
            };

            var variables = DailyReportWhatsAppFormatter.FormatVariables(report, ReportTime);

            Assert.Equal("MERI GOLD", variables[2]);
            Assert.Equal("0", variables[3]);
            Assert.Equal("CHRYSANTHEMUM", variables[4]);
            Assert.Equal("0", variables[5]);
            Assert.DoesNotContain("N/A", variables);
            Assert.DoesNotContain("null", variables);
        }

        // ---- Names come from DailyReport, never hard-coded in the formatter ----

        [Fact]
        public void FormatVariables_ReadyStockNames_ComeFromDailyReport_NotHardCoded()
        {
            var report = SampleReport();
            report.ReadyStockByPlantType = new List<ReadyStockPlantTypeQuantity>
            {
                new("Renamed Type A", 10),
                new("Renamed Type B", 999),
            };

            var variables = DailyReportWhatsAppFormatter.FormatVariables(report, ReportTime);

            Assert.Equal("Renamed Type B", variables[2]); // higher quantity -> first
            Assert.Equal("999", variables[3]);
            Assert.Equal("Renamed Type A", variables[4]);
            Assert.Equal("10", variables[5]);
        }

        [Fact]
        public void FormatVariables_ReadyStockByPlantType_FewerThanTwoEntries_NeverThrows()
        {
            var report = SampleReport();
            report.ReadyStockByPlantType = new List<ReadyStockPlantTypeQuantity> { new("MERI GOLD", 1250) };

            var variables = DailyReportWhatsAppFormatter.FormatVariables(report, ReportTime);

            Assert.Equal(16, variables.Count);
            Assert.Equal("MERI GOLD", variables[2]);
            Assert.Equal("1,250", variables[3]);
            Assert.Equal("", variables[4]);
            Assert.Equal("0", variables[5]);
        }

        // ---- No positions for Plant Types #3/#4 remain anywhere -----------

        [Fact]
        public void FormatVariables_OnlyTwoPlantTypesAppear_NeverThreeOrFour()
        {
            var variables = DailyReportWhatsAppFormatter.FormatVariables(SampleReport(), ReportTime);
            // Exactly 2 of the 4 known names may appear (as {{3}}/{{5}}); the
            // other 2 must not appear anywhere in the 16 variables at all.
            var allNames = new[] { "MERI GOLD", "CHRYSANTHEMUM", "ZINNIA", "SEASONAL VARITIES" };
            var appearances = allNames.Count(n => variables.Contains(n));
            Assert.Equal(2, appearances);
        }

        // ---- Date formatting -----------------------------------------

        [Theory]
        [InlineData(2026, 1, 5, "2026-01-05")]
        [InlineData(2026, 12, 31, "2026-12-31")]
        public void FormatVariables_DateFormat_IsIsoYyyyMmDd(int year, int month, int day, string expected)
        {
            var report = SampleReport();
            report.ReportDate = new DateTime(year, month, day);
            var variables = DailyReportWhatsAppFormatter.FormatVariables(report, ReportTime);
            Assert.Equal(expected, variables[0]);
        }

        // ---- Booking / Dispatch / Outlet / Fertilizer / Wastage formatting ----

        [Fact]
        public void FormatVariables_Bookings_SeedlingAndPottedBothShown_QuantityExcluded()
        {
            var report = SampleReport();
            report.SeedlingBookingsCount = 9;
            report.SeedlingBookingsQuantity = 12345; // must NOT appear
            report.PottedPlantBookingsCount = 0;
            var variables = DailyReportWhatsAppFormatter.FormatVariables(report, ReportTime);
            Assert.Equal("Seedling: 9, Potted: 0", variables[6]);
            Assert.DoesNotContain("12345", variables[6]);
        }

        [Fact]
        public void FormatVariables_Dispatch_SeedlingAndPottedBothShown_QuantityExcluded()
        {
            var report = SampleReport();
            report.SeedlingDispatchCount = 6;
            report.SeedlingDispatchQuantity = 54321; // must NOT appear
            report.PottedPlantDispatchCount = 2;
            var variables = DailyReportWhatsAppFormatter.FormatVariables(report, ReportTime);
            Assert.Equal("Seedling: 6, Potted: 2", variables[7]);
            Assert.DoesNotContain("54321", variables[7]);
        }

        [Fact]
        public void FormatVariables_OutletSalesFertilizerWastage_RemainCorrect()
        {
            var report = SampleReport();
            report.OutletSalesCount = 7;
            report.FertilizerUsageQuantity = 12.25m;
            report.WastageQuantity = 88;
            var variables = DailyReportWhatsAppFormatter.FormatVariables(report, ReportTime);
            Assert.Equal("7", variables[8]);
            Assert.Equal("12.3", variables[9]);
            Assert.Equal("88", variables[10]);
        }

        // ---- {{12}}-{{14}} remain pure numeric counts; names never leak in ----

        [Fact]
        public void FormatVariables_EmployeeCounts_MapToCorrectPositions_RemainNumeric_NamesExcluded()
        {
            var report = SampleReport();
            report.TotalActiveEmployees = 25;
            report.LoggedInEmployeeCount = 18;
            report.ActiveUserCount = 14;
            report.EmployeesLoggedInToday = new List<EmployeeLoginActivity>
            {
                new() { UserId = 1, Name = "Rohit Shinde", FirstLoginAtToday = ReportTime, HasActivityToday = true }
            };
            report.EmployeesNotLoggedInToday = new List<EmployeeLoginActivity>
            {
                new() { UserId = 2, Name = "Reshma Sayyad" }
            };

            var variables = DailyReportWhatsAppFormatter.FormatVariables(report, ReportTime);

            Assert.Equal("25", variables[11]);
            Assert.Equal("18", variables[12]);
            Assert.Equal("14", variables[13]);

            foreach (var i in new[] { 11, 12, 13 })
            {
                Assert.True(int.TryParse(variables[i], out _), $"variables[{i}] must be numeric-only: '{variables[i]}'");
                Assert.DoesNotContain("Rohit", variables[i]);
                Assert.DoesNotContain("Reshma", variables[i]);
            }
        }

        // ---- {{15}} No Activity Recorded = names of logged-in employees with no activity ----

        [Fact]
        public void FormatVariables_NoActivityRecorded_OneEmployee_IsJustThatName()
        {
            var report = SampleReport();
            report.EmployeesLoggedInToday = new List<EmployeeLoginActivity>
            {
                new() { UserId = 1, Name = "Rohit Shinde", FirstLoginAtToday = ReportTime, HasActivityToday = true },  // active -- excluded
                new() { UserId = 2, Name = "Sneha Joshi", FirstLoginAtToday = ReportTime, HasActivityToday = false }, // no activity -- included
            };

            var variables = DailyReportWhatsAppFormatter.FormatVariables(report, ReportTime);

            Assert.Equal("Sneha Joshi", variables[14]);
            Assert.DoesNotContain("Rohit", variables[14]);
        }

        [Fact]
        public void FormatVariables_NoActivityRecorded_MultipleEmployees_AllAppearExactlyOnce_CommaSeparated()
        {
            var report = SampleReport();
            report.EmployeesLoggedInToday = new List<EmployeeLoginActivity>
            {
                new() { UserId = 1, Name = "Amit Patil", FirstLoginAtToday = ReportTime, HasActivityToday = false },
                new() { UserId = 2, Name = "Rahul Shinde", FirstLoginAtToday = ReportTime, HasActivityToday = false },
                new() { UserId = 3, Name = "Sneha Joshi", FirstLoginAtToday = ReportTime, HasActivityToday = false },
            };

            var variables = DailyReportWhatsAppFormatter.FormatVariables(report, ReportTime);

            Assert.Equal("Amit Patil, Rahul Shinde, Sneha Joshi", variables[14]);
        }

        [Fact]
        public void FormatVariables_NoActivityRecorded_ZeroEmployees_IsNone()
        {
            var report = SampleReport();
            report.EmployeesLoggedInToday = new List<EmployeeLoginActivity>
            {
                new() { UserId = 1, Name = "Rohit Shinde", FirstLoginAtToday = ReportTime, HasActivityToday = true },
            };

            var variables = DailyReportWhatsAppFormatter.FormatVariables(report, ReportTime);

            Assert.Equal("None", variables[14]);
        }

        [Fact]
        public void FormatVariables_NoActivityRecorded_NoLoggedInEmployeesAtAll_IsNone()
        {
            var report = SampleReport();
            report.EmployeesLoggedInToday = new List<EmployeeLoginActivity>();

            var variables = DailyReportWhatsAppFormatter.FormatVariables(report, ReportTime);

            Assert.Equal("None", variables[14]);
        }

        [Fact]
        public void FormatVariables_NoActivityRecorded_ActiveUsersAreExcluded()
        {
            var report = SampleReport();
            report.EmployeesLoggedInToday = new List<EmployeeLoginActivity>
            {
                new() { UserId = 1, Name = "Active Employee", FirstLoginAtToday = ReportTime, HasActivityToday = true },
                new() { UserId = 2, Name = "Idle Employee", FirstLoginAtToday = ReportTime, HasActivityToday = false },
            };

            var variables = DailyReportWhatsAppFormatter.FormatVariables(report, ReportTime);

            Assert.Equal("Idle Employee", variables[14]);
            Assert.DoesNotContain("Active Employee", variables[14]);
        }

        // ---- {{16}} Not Logged In = names of employees who did not log in -------------

        [Fact]
        public void FormatVariables_NotLoggedIn_OneEmployee_IsJustThatName()
        {
            var report = SampleReport();
            report.EmployeesNotLoggedInToday = new List<EmployeeLoginActivity>
            {
                new() { UserId = 3, Name = "Kavita Nair" },
            };

            var variables = DailyReportWhatsAppFormatter.FormatVariables(report, ReportTime);

            Assert.Equal("Kavita Nair", variables[15]);
        }

        [Fact]
        public void FormatVariables_NotLoggedIn_MultipleEmployees_CommaSeparated()
        {
            var report = SampleReport();
            report.EmployeesNotLoggedInToday = new List<EmployeeLoginActivity>
            {
                new() { UserId = 3, Name = "Kavita Nair" },
                new() { UserId = 4, Name = "Rohan Deshpande" },
            };

            var variables = DailyReportWhatsAppFormatter.FormatVariables(report, ReportTime);

            Assert.Equal("Kavita Nair, Rohan Deshpande", variables[15]);
        }

        [Fact]
        public void FormatVariables_NotLoggedIn_ZeroEmployees_IsNone()
        {
            var report = SampleReport();
            report.EmployeesNotLoggedInToday = new List<EmployeeLoginActivity>();

            var variables = DailyReportWhatsAppFormatter.FormatVariables(report, ReportTime);

            Assert.Equal("None", variables[15]);
        }

        [Fact]
        public void FormatVariables_NotLoggedIn_ActiveUsersNeverAppear()
        {
            // An active user is, by definition, logged in -- so they belong
            // in EmployeesLoggedInToday, never in EmployeesNotLoggedInToday.
            // This asserts that separation holds through to the formatted
            // output too (rule 11: {{15}} and {{16}} employees never overlap).
            var report = SampleReport();
            report.EmployeesLoggedInToday = new List<EmployeeLoginActivity>
            {
                new() { UserId = 1, Name = "Rohit Shinde", FirstLoginAtToday = ReportTime, HasActivityToday = true },
            };
            report.EmployeesNotLoggedInToday = new List<EmployeeLoginActivity>
            {
                new() { UserId = 2, Name = "Kavita Nair" },
            };

            var variables = DailyReportWhatsAppFormatter.FormatVariables(report, ReportTime);

            Assert.DoesNotContain("Rohit", variables[15]);
            Assert.Equal("Kavita Nair", variables[15]);
        }

        [Fact]
        public void FormatVariables_NoActivityEmployees_NeverAppearInNotLoggedIn_AndViceVersa()
        {
            var report = SampleReport();
            report.EmployeesLoggedInToday = new List<EmployeeLoginActivity>
            {
                new() { UserId = 1, Name = "Idle One", FirstLoginAtToday = ReportTime, HasActivityToday = false },
                new() { UserId = 2, Name = "Idle Two", FirstLoginAtToday = ReportTime, HasActivityToday = false },
            };
            report.EmployeesNotLoggedInToday = new List<EmployeeLoginActivity>
            {
                new() { UserId = 3, Name = "Absent One" },
                new() { UserId = 4, Name = "Absent Two" },
            };

            var variables = DailyReportWhatsAppFormatter.FormatVariables(report, ReportTime);

            Assert.Equal("Idle One, Idle Two", variables[14]);
            Assert.Equal("Absent One, Absent Two", variables[15]);
            Assert.DoesNotContain("Idle", variables[15]);
            Assert.DoesNotContain("Absent", variables[14]);
        }

        // ---- Names preserved verbatim (spaces, initials, apostrophes) ----------------

        [Theory]
        [InlineData("A. B. Patil")]
        [InlineData("Mary Jane O'Brien")]
        [InlineData("Jean-Pierre")]
        public void FormatVariables_NamesWithSpacesOrInitials_AppearVerbatim(string name)
        {
            var report = SampleReport();
            report.EmployeesLoggedInToday = new List<EmployeeLoginActivity>
            {
                new() { UserId = 1, Name = name, FirstLoginAtToday = ReportTime, HasActivityToday = false },
            };

            var variables = DailyReportWhatsAppFormatter.FormatVariables(report, ReportTime);

            Assert.Equal(name, variables[14]);
        }

        [Fact]
        public void FormatVariables_RepositoryGuaranteesOneRowPerEmployee_NameNeverDuplicated()
        {
            // UserLoginHistoryRepository.GetTodaysActivityAsync selects FROM
            // dbo.IMSUsers u -- one row per Id -- so the input list can never
            // actually contain the same employee twice. This documents that
            // guarantee holds through to the formatted output (not a
            // defensive Distinct() in the formatter, which could wrongly
            // merge two different employees who share a name).
            var report = SampleReport();
            report.EmployeesLoggedInToday = new List<EmployeeLoginActivity>
            {
                new() { UserId = 1, Name = "Sneha Joshi", FirstLoginAtToday = ReportTime, HasActivityToday = false },
            };

            var variables = DailyReportWhatsAppFormatter.FormatVariables(report, ReportTime);

            Assert.Equal("Sneha Joshi", variables[14]);
        }

        // ---- {{15}}/{{16}} must never be numeric-only -------------------------------

        [Fact]
        public void FormatVariables_NoActivityAndNotLoggedIn_NeverContainNumericCountsOnly()
        {
            var report = SampleReport();
            report.EmployeesLoggedInToday = new List<EmployeeLoginActivity>();
            report.EmployeesNotLoggedInToday = new List<EmployeeLoginActivity>();

            var variables = DailyReportWhatsAppFormatter.FormatVariables(report, ReportTime);

            // Both empty -> "None" (not a count like "0").
            Assert.False(int.TryParse(variables[14], out _));
            Assert.False(int.TryParse(variables[15], out _));
            Assert.Equal("None", variables[14]);
            Assert.Equal("None", variables[15]);
        }

        // ---- No API key appears in formatted output ---------------------

        [Fact]
        public void FormatVariables_NeverContainsAnythingResemblingAnApiKey()
        {
            var variables = DailyReportWhatsAppFormatter.FormatVariables(SampleReport(), ReportTime);
            Assert.All(variables, v => Assert.DoesNotContain("sv_", v, StringComparison.OrdinalIgnoreCase));
            Assert.All(variables, v => Assert.DoesNotContain("apikey", v, StringComparison.OrdinalIgnoreCase));
            Assert.All(variables, v => Assert.DoesNotContain("x-api-key", v, StringComparison.OrdinalIgnoreCase));
        }

        // ---- Zero report values format cleanly, no exceptions ---------------------

        [Fact]
        public void FormatVariables_ZeroValues_FormatCleanly_NoExceptions()
        {
            var report = new DailyReport
            {
                ReportDate = new DateTime(2026, 9, 30),
                ReadyStockByPlantType = new List<ReadyStockPlantTypeQuantity>
                {
                    new("MERI GOLD", 0),
                    new("CHRYSANTHEMUM", 0),
                    new("ZINNIA", 0),
                    new("SEASONAL VARITIES", 0),
                },
            };
            var variables = DailyReportWhatsAppFormatter.FormatVariables(report, ReportTime);
            Assert.Equal(16, variables.Count);
            Assert.Equal("0", variables[1]);                 // Sowing
            Assert.Equal("MERI GOLD", variables[2]);
            Assert.Equal("0", variables[3]);
            Assert.Equal("CHRYSANTHEMUM", variables[4]);
            Assert.Equal("0", variables[5]);
            Assert.Equal("Seedling: 0, Potted: 0", variables[6]);
            Assert.Equal("Seedling: 0, Potted: 0", variables[7]);
            Assert.Equal("0", variables[8]);
            Assert.Equal("0.0", variables[9]);
            Assert.Equal("0", variables[10]);
            Assert.Equal("0", variables[11]);
            Assert.Equal("0", variables[12]);
            Assert.Equal("0", variables[13]);
            // {{15}}/{{16}} are names, not counts: a default-constructed
            // DailyReport has empty EmployeesLoggedInToday/NotLoggedInToday
            // lists -- "None", never "0" (a bare "0" would misleadingly read
            // as a count on a name-only template field).
            Assert.Equal("None", variables[14]);
            Assert.Equal("None", variables[15]);
        }
    }
}
