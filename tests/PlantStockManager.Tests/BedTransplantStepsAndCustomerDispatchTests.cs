using System.Reflection;
using Microsoft.AspNetCore.Mvc;
using PlantStockManager.Authorization;
using PlantStockManager.Data;
using PlantStockManager.Models;
using PlantStockManager.Services;
using MovePage = PlantStockManager.Pages.Production.ReadyStock.MoveModel;
using TransplantPage = PlantStockManager.Pages.Production.BedSowing.TransplantModel;

namespace PlantStockManager.Tests
{
    // 2026-10-19: wiring of (1) Bed Transplanting in steps (choose bed -> details -> server review -> confirm) and (2) the
    // Customer destination of Ready Stock > Send (Office Coordinator, ReadyStock.DispatchToCustomer). No database needed;
    // the behaviour itself is covered end to end in BedTransplantStepsE2ETests / ReadyStockCustomerDispatchE2ETests.
    public class BedTransplantStepsAndCustomerDispatchTests
    {
        private static string ReadSource(params string[] parts)
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "PlantStockManager.sln")))
                dir = dir.Parent;
            Assert.NotNull(dir);
            return File.ReadAllText(Path.Combine(new[] { dir!.FullName }.Concat(parts).ToArray()));
        }

        // ---- Bed Transplanting ---------------------------------------------------------------------------------------

        [Fact]
        public void BedTransplant_PermissionUnchanged_ReviewAndEditHandlersAdded_SaveHandlerKept()
        {
            Assert.Equal(BedSowingRules.TransplantPermission, FeatureAuthorizationConventions.GetRule("/Production/BedSowing/Transplant").Read);
            var t = typeof(TransplantPage);
            foreach (var handler in new[] { "OnGetAsync", "OnPostReviewAsync", "OnPostEditAsync", "OnPostTransplantAsync" })
                Assert.NotNull(t.GetMethod(handler, BindingFlags.Public | BindingFlags.Instance));
            // the posted values are still exactly the transplant request (+ the bed id, + 2026-10-22 the list's Variety / Sowing
            // Date filters); no Area, stock or plan is bound
            var bound = t.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(p => p.GetCustomAttribute<BindPropertyAttribute>() != null).Select(p => p.Name).OrderBy(n => n).ToArray();
            Assert.Equal(new[] { "BedId", "Entry", "SowingDate", "Variety" }, bound);
            Assert.Equal(typeof(int?), t.GetProperty("Variety")!.PropertyType);
            Assert.Equal(typeof(DateTime?), t.GetProperty("SowingDate")!.PropertyType);
            Assert.Equal(typeof(BedTransplantRequest), t.GetProperty("Entry")!.PropertyType);
        }

        [Fact]
        public void BedTransplant_Page_DetailsPostToReview_ConfirmPostsToTheSave_BackKeepsTheValues()
        {
            var page = ReadSource("Pages", "Production", "BedSowing", "Transplant.cshtml");
            Assert.Contains(@"<form method=""post"" asp-page-handler=""Review"" id=""transplantForm"">", page);
            Assert.Contains(@"asp-page-handler=""Transplant"" class=""btn btn-primary"" id=""confirmTransplant"">Confirm Transplant</button>", page);
            Assert.Contains(@"asp-page-handler=""Edit"" formnovalidate", page);
            // the confirmation re-posts the same one-time token (duplicate submits are recorded once by the repository)
            Assert.Contains(@"name=""Entry.SubmissionToken"" value=""@e.SubmissionToken""", page);
            // the bed list is step 1 only; the chosen bed is shown on top of steps 2 and 3
            Assert.Contains(@"id=""bedList""", page);
            Assert.Contains(@"id=""sourceBed""", page);
            // the existing fields keep their names
            foreach (var field in new[] { "Entry.TargetPolyhouseId", "Entry.TransplantDate", "Entry.CavityType", "Entry.Quantity", "Entry.OutputTrays", "Entry.CloseAsWaste", "Entry.WasteReason", "Entry.Remarks" })
                Assert.Contains(field, page);
        }

        // ---- Customer dispatch ---------------------------------------------------------------------------------------

        [Fact]
        public void ReadyStockMove_CustomerDestination_HasItsOwnPermission_TransferPolicyUnchanged()
        {
            Assert.Equal("ReadyStock.DispatchToCustomer", MovePage.CustomerDispatchPolicy);
            Assert.Equal("InternalTransfer.Enter|MainOffice.Confirm", MovePage.TransferPolicy);
            Assert.DoesNotContain(MovePage.CustomerDispatchPolicy, MovePage.TransferPolicy);
            var read = FeatureAuthorizationConventions.GetRule("/Production/ReadyStock/Move").Read.Split('|');
            Assert.Equal(new[] { "InternalTransfer.Enter", "MainOffice.Confirm", "ReadyStock.DispatchToCustomer" }, read);
            // the Mother Plant direct sale keeps its own permission
            Assert.Equal("Outlet.Sell", FeatureAuthorizationConventions.GetRule("/Production/MotherPlant/SellTrays").Read);
            Assert.Equal("Customer", MovePage.ToCustomer);
        }

        [Fact]
        public void ReadyStockMove_CustomerPath_UsesTheDirectSale_WithTheStrictAreaRule()
        {
            var model = ReadSource("Pages", "Production", "ReadyStock", "Move.cshtml.cs");
            Assert.Contains("_fulfilment.DirectTraySaleAsync(", model);
            Assert.Contains("FromReadySeedlingStock: true", model);
            Assert.Contains("areaId => _strictArea.CanAccessArea(User, areaId)", model);
            // the request flag defaults to the unchanged Mother Plant-only sale
            var flag = typeof(SeedlingFulfilmentRepository.DirectTraySaleRequest).GetConstructors().Single().GetParameters().Last();
            Assert.Equal(("FromReadySeedlingStock", true, (object?)false), (flag.Name, flag.HasDefaultValue, flag.DefaultValue));
        }

        [Fact]
        public void Migration_GrantsOnlyTheNewPermission_OnlyToOfficeCoordinator_Guarded_WithRollback()
        {
            var sql = ReadSource("Database", "Migrations", "2026-10-23_ReadyStockDispatchToCustomer.sql");
            var rollback = ReadSource("Database", "Migrations", "2026-10-23_ReadyStockDispatchToCustomer_Rollback.sql");
            foreach (var script in new[] { sql, rollback })
            {
                Assert.Contains("DB_NAME() <> N'PlantsIMS2_Test' AND DB_NAME() NOT LIKE N'PlantsIMS2[_]Scratch[_]%'", script);
                Assert.Contains("IF @@SERVERNAME <> N'$(TARGET_SERVER)'", script);
                Assert.Contains("IF N'$(MODE)' = N'COMMIT'", script);
                Assert.Contains("DECLARE @RoleName NVARCHAR(100) = N'Office Coordinator';", script);
                Assert.Contains("DECLARE @Code NVARCHAR(100) = N'ReadyStock.DispatchToCustomer';", script);
            }
            // exactly one grant, of the new code, logged; the Area guard; no transfer permission is granted
            Assert.Single(System.Text.RegularExpressions.Regex.Matches(sql, @"INSERT INTO dbo\.RolePermissions"));
            Assert.Contains("INSERT INTO dbo.RolePermissions (RoleId, PermissionId) VALUES (@Role, @Perm);", sql);
            Assert.Contains("INSERT INTO dbo.RolePermissionChangeLog", sql);
            Assert.Contains("WHERE RoleId = @Role AND AreaId IS NULL", sql);
            Assert.DoesNotContain("N'InternalTransfer.Enter'", sql);
            Assert.DoesNotContain("N'MainOffice.Confirm'", sql);
            Assert.Contains("DELETE FROM dbo.RolePermissions WHERE RoleId = @Role AND PermissionId = @Perm;", rollback);
        }

        // ---- migration safety (review 2026-10-19): target identity and rollback ownership ----------------------------

        private static readonly string[] TargetVariables = { "MODE", "TARGET_SERVER", "TARGET_MACHINE", "TARGET_DB", "TARGET_DB_CREATED" };

        private static (string Guards, string Body) SplitAtFirstGo(string script)
        {
            var m = System.Text.RegularExpressions.Regex.Match(script, @"^GO\s*$", System.Text.RegularExpressions.RegexOptions.Multiline);
            Assert.True(m.Success);
            return (script[..m.Index], script[m.Index..]);
        }

        [Fact]
        public void MigrationAndRollback_VerifyServerHostDatabaseAndCopy_BeforeAnythingElse_NoServerNameBuiltIn()
        {
            foreach (var file in new[] { "2026-10-23_ReadyStockDispatchToCustomer.sql", "2026-10-23_ReadyStockDispatchToCustomer_Rollback.sql" })
            {
                var (guards, body) = SplitAtFirstGo(ReadSource("Database", "Migrations", file));
                // the first batch is guards only: stop on error, every identity check, and no read / write of application data
                Assert.Contains(":on error exit", guards);
                Assert.Contains("DB_NAME() <> N'PlantsIMS2_Test' AND DB_NAME() NOT LIKE N'PlantsIMS2[_]Scratch[_]%'", guards);
                Assert.Contains("IF N'$(MODE)' NOT IN (N'DRYRUN', N'COMMIT')", guards);
                Assert.Contains("IF @@SERVERNAME <> N'$(TARGET_SERVER)'", guards);
                Assert.Contains("CAST(SERVERPROPERTY('ServerName') AS NVARCHAR(128)) <> @@SERVERNAME", guards);
                Assert.Contains("CAST(SERVERPROPERTY('MachineName') AS NVARCHAR(128)) <> N'$(TARGET_MACHINE)'", guards);
                Assert.Contains("IF DB_NAME() <> N'$(TARGET_DB)'", guards);
                Assert.Contains("CONVERT(CHAR(19), create_date, 126) = N'$(TARGET_DB_CREATED)'", guards);
                Assert.DoesNotMatch(@"(?i)\b(INSERT|UPDATE|DELETE|MERGE|BEGIN\s+TRAN)", guards.Split('\n')
                    .Where(l => !l.TrimStart().StartsWith("--")).Aggregate("", (a, l) => a + l + "\n"));
                Assert.Equal(7, System.Text.RegularExpressions.Regex.Matches(guards, @"^\s*THROW 526\d\d,", System.Text.RegularExpressions.RegexOptions.Multiline).Count);
                // every target variable is required (sqlcmd stops when one is not supplied); nothing names a real server
                foreach (var v in TargetVariables)
                    Assert.Contains($"$({v})", guards);
                foreach (var hardCoded in new[] { "LAPTOP-", "69.62.75.87", "MSSQLSERVER02", "precisionagritech" })
                    Assert.DoesNotContain(hardCoded, guards + body);
                // the operator checklist is part of the script
                Assert.Contains("OPERATOR CHECKLIST", guards);
                Assert.Contains("Before COMMIT", guards);
                Assert.Contains("BEGIN TRANSACTION", body);
            }
        }

        [Fact]
        public void Migration_MarksOnlyACodeItCreates_AndRecordsWhichCaseInTheGrantLog()
        {
            var sql = ReadSource("Database", "Migrations", "2026-10-23_ReadyStockDispatchToCustomer.sql");
            var rollback = ReadSource("Database", "Migrations", "2026-10-23_ReadyStockDispatchToCustomer_Rollback.sql");
            var owned = System.Text.RegularExpressions.Regex.Match(sql, @"DECLARE @OwnedDescription NVARCHAR\(200\) =\s*N'([^']+)';");
            Assert.True(owned.Success);
            Assert.EndsWith("[created by 2026-10-23_ReadyStockDispatchToCustomer.sql]", owned.Groups[1].Value);
            Assert.True(owned.Groups[1].Value.Length <= 200);
            Assert.Contains(owned.Value, rollback);                                     // the same marker in both scripts
            // the marker is written only when the code is missing; an existing code is used as it is (never updated)
            Assert.Contains("IF NOT EXISTS (SELECT 1 FROM dbo.Permissions WITH (UPDLOCK, HOLDLOCK) WHERE Code = @Code)", sql);
            Assert.Contains("INSERT INTO dbo.Permissions (Code, Description) VALUES (@Code, @OwnedDescription);", sql);
            Assert.DoesNotMatch(@"UPDATE\s+dbo\.Permissions", sql);
            Assert.Contains("[code created by this script; PermissionId=", sql);
            Assert.Contains("[code pre-existed; PermissionId=", sql);
        }

        [Fact]
        public void Rollback_DeletesTheCodeOnlyWhenOwned_GrantGone_AndUnreferenced_NeverTouchesHistory()
        {
            var rollback = ReadSource("Database", "Migrations", "2026-10-23_ReadyStockDispatchToCustomer_Rollback.sql");
            // the grant: only when the LATEST change for role + code is the migration's 'Added'
            Assert.Contains("ORDER BY Id DESC", rollback);
            Assert.Contains("IF ISNULL(@LastChange, N'') <> N'Added' OR ISNULL(@LastScript, N'') <> @Script", rollback);
            // the code: all three conditions, then a delete pinned to the owned row, exactly once
            Assert.Contains("AND Description = @OwnedDescription", rollback);
            Assert.Contains("Reason LIKE N'%[[]code created by this script; PermissionId=' + @PermText + N']'", rollback);
            Assert.Contains("IF @CreatedByMigration = 0", rollback);
            Assert.Contains("ELSE IF @GrantGone = 0", rollback);
            Assert.Contains("ELSE IF @Referenced = 1", rollback);
            Assert.Contains("DELETE FROM dbo.Permissions WHERE Id = @Perm AND Code = @Code AND Description = @OwnedDescription;", rollback);
            Assert.Contains("IF @@ROWCOUNT <> 1", rollback);
            Assert.Single(System.Text.RegularExpressions.Regex.Matches(rollback, @"DELETE FROM dbo\.Permissions"));
            Assert.Single(System.Text.RegularExpressions.Regex.Matches(rollback, @"DELETE FROM dbo\.RolePermissions"));
            // audit history is append-only: never updated or deleted
            Assert.DoesNotMatch(@"(UPDATE|DELETE\s+FROM)\s+dbo\.RolePermissionChangeLog", rollback);
            Assert.Contains("N'Removed', @RollbackScript", rollback);
        }


        [Theory]
        [InlineData(480, 24, 20, ReadyStock.OriginSowing)]
        [InlineData(47, 24, 1, ReadyStock.OriginSowing)]
        [InlineData(23, 24, 0, ReadyStock.OriginTransfer)]
        // 2026-10-31: a SOWING batch under one tray can only be a partial-tray cutting sowing -- one tray of its whole quantity
        [InlineData(23, 24, 1, ReadyStock.OriginSowing)]
        public void CustomerDispatch_OffersWholeTraysOnly(decimal available, int cavity, int trays, string origin)
        {
            var b = new ReadyStock { Quantity = available, CavityType = $"{cavity} Cavity", OriginType = origin };
            Assert.Equal(trays, AreaReadyStockActionRules.AvailableCompleteTrays(b));
            Assert.Equal(trays > 0, AreaReadyStockActionRules.SellToCustomer(b, true, false, true).Available);
        }
    }
}
