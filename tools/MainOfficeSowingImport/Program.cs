// Controlled import: Main Office Direct Seed Sowing, 01-10-2026 .. 12-10-2026 (owner decisions 14-10-2026).
// Database/DataEntry/2026-10_MainOfficeSowing holds the data (entry_rows.csv), the owner's variety decisions
// (variety_mapping.csv), the read-only preflight and verification SQL, and the report.
//
// It drives the application's OWN page models (the same validation, authorization, repositories, triggers and
// ledgers as the screens) as the recorder, whose principal is built from the database by the app's
// UserClaimsFactory -- no password, exactly like the project's E2E tests. It never writes a table row itself.
//
//   check : READ-ONLY. Target, identities, permissions, mapping, growing days, cavities, stock, idempotency state
//           and the full plan. Writes nothing.
//   run   : A varieties (NEW lines) -> B seed receipts -> C empty-tray receipts -> D Cocopeat receipt -> E 79 sowings.
//           Stops at the first failure, never retries. A rerun skips what this batch already saved (deterministic
//           submission tokens / the batch tag), so nothing is entered twice.
//
// Connection: environment variable PSM_IMPORT_CONNECTION (never printed). The target is decided by what the connected
// server reports about itself, not by the database name (ImportTargetGuard.cs): on the local development server only
// PlantsIMS2_Test or a PlantsIMS2_Scratch_* copy; any other server -- live on the VPS is ALSO named PlantsIMS2_Test --
// only with --allow-live-target "<server>/<database>" (owner's decision, after a verified backup and after every
// migration this code needs is on live). Re-verified on the application's own connection before the first write.
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using PlantStockManager.Authorization;
using PlantStockManager.Data;
using PlantStockManager.Services;
using PlantPage = PlantStockManager.Pages.Admin.PlantModel;
using ReceiptPage = PlantStockManager.Pages.Production.SeedStock.AddSeedStockModel;
using MaterialPage = PlantStockManager.Pages.Production.MaterialStock.AddModel;
using SowingPage = PlantStockManager.Pages.Production.SeedSowing.CreateModel;
using SowingEntry = PlantStockManager.Models.SeedSowing;
using MainOfficeSowingImport;

// ---- owner decisions (14-10-2026) --------------------------------------------------------------------------------
const string Tag = "MAIN-OFFICE-SOWING-2026-10";
const string RecorderUsername = "Prajwal";                       // records the sowings (user 21 in PlantsIMS2_Test)
const string SupervisorUsername = "Maya", SupervisorName = "Maya Shinde";   // assigned supervisor / approver
const string AreaName = "Main-Office-Areas", PolyhouseName = "Facility 1 Main office";
const string MaterialCode = "COCOPEAT";
// Receipts (seed lots, empty trays) dated 30-09-2026 09:00 IST = 03:30 UTC (UTC kind: independent of the machine's zone).
var receiptUtc = new DateTime(2026, 9, 30, 3, 30, 0, DateTimeKind.Utc);
const string ReceiptNote = Tag + " | Reconstructed receipt for the October 2026 sowing entry; quantity = what the sowings use "
    + "(owner decision 14-10-2026), not a verified purchase/supplier receipt.";

var mode = args.FirstOrDefault() ?? "";
if (mode is not ("check" or "run"))
{
    Console.WriteLine($"usage: MainOfficeSowingImport check|run [--data <folder>] [--mapping <csv>] [{ImportTargetGuard.LiveFlag} \"<server>/<database>\"]");
    return 2;
}
if (args.Contains(ImportTargetGuard.RetiredLiveFlag))
{
    Console.WriteLine($"{ImportTargetGuard.RetiredLiveFlag} is no longer accepted: name the exact target with {ImportTargetGuard.LiveFlag} \"<server>/<database>\".");
    return 2;
}
string Arg(string name, string fallback) { var i = Array.IndexOf(args, name); return i >= 0 && i + 1 < args.Length ? args[i + 1] : fallback; }
var repoRoot = FindRepoRoot();
var dataDir = Arg("--data", Path.Combine(repoRoot, "Database", "DataEntry", "2026-10_MainOfficeSowing"));
var mappingPath = Arg("--mapping", Path.Combine(dataDir, "variety_mapping.csv"));
string? liveAuthorization = args.Contains(ImportTargetGuard.LiveFlag) ? Arg(ImportTargetGuard.LiveFlag, "") : null;
var cs = Environment.GetEnvironmentVariable("PSM_IMPORT_CONNECTION");
if (string.IsNullOrWhiteSpace(cs)) { Console.WriteLine("Set PSM_IMPORT_CONNECTION to the target database's connection string."); return 2; }

// ---- source rows (entry_rows.csv: row 35 already 150 Cavity / 900 by owner decision) -------------------------------
var rows = Csv.Read(Path.Combine(dataDir, "entry_rows.csv")).Select(r => new Row(
    int.Parse(r["RowNo"]), DateTime.ParseExact(r["SowingDate"], "yyyy-MM-dd", null), r["SourceVariety"], r["CavityType"],
    int.Parse(r["Trays"]), int.Parse(r["Seedlings"]), r["OwnerDecision"])).OrderBy(r => r.RowNo).ToList();
Check(rows.Count == 79 && rows.Select(r => r.RowNo).Distinct().Count() == 79, "79 entry rows, numbered once each");
Check(rows.Sum(r => r.Trays) == 6939 && rows.Sum(r => r.Seedlings) == 1_026_098, "totals 6,939 trays / 1,026,098 seedlings");
foreach (var r in rows)
{
    var cavity = DirectSowingRules.CavityCount(r.Cavity);
    if (cavity is not > 0 || cavity * r.Trays != r.Seedlings) Fail($"row {r.RowNo}: {r.Trays} x {r.Cavity} <> {r.Seedlings}");
    if (r.Date < new DateTime(2026, 10, 1) || r.Date > new DateTime(2026, 10, 12)) Fail($"row {r.RowNo}: date {r.Date:dd-MM-yyyy} outside 01..12-10-2026");
}
Console.WriteLine("  ok: every row is complete trays (trays x cavity = seedlings), dated 01..12-10-2026");

// ---- owner's variety decisions --------------------------------------------------------------------------------------
var mapping = Csv.Read(mappingPath).ToDictionary(m => m["SourceVariety"], m => m);
var unresolved = rows.Select(r => r.Source).Distinct().Where(s => !mapping.TryGetValue(s, out var m) || m["Decision"].Trim() is not ("EXISTING" or "NEW")).ToList();

// ---- services, exactly as the app ----------------------------------------------------------------------------------
var config = new ConfigurationBuilder()
    .AddJsonFile(Path.Combine(repoRoot, "appsettings.json"), optional: false)
    .AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:DefaultConnection"] = cs })
    .Build();
var services = new ServiceCollection();
services.AddSingleton<IConfiguration>(config);
services.AddLogging();
services.AddSingleton<DatabaseHelper>();
services.Configure<SecurityOptions>(config.GetSection(SecurityOptions.SectionName));
services.Configure<SeedlingWorkflowOptions>(config.GetSection(SeedlingWorkflowOptions.SectionName));
services.AddScoped<AreaAccessService>();
services.AddScoped<SeedlingAreaScope>();
services.AddScoped<UserClaimsFactory>();
services.AddSingleton<IAuthorizationPolicyProvider, PermissionAuthorizationPolicyProvider>();
services.AddScoped<IAuthorizationHandler, MinimumAuthorizationLevelHandler>();
services.AddAuthorization();
services.AddScoped<FeatureAccessService>();
foreach (var t in typeof(DatabaseHelper).Assembly.GetTypes().Where(t => t.Namespace == "PlantStockManager.Data" && t.IsClass && !t.IsAbstract && t.Name.EndsWith("Repository")))
    services.AddScoped(t);
using var provider = services.BuildServiceProvider();
using var scope = provider.CreateScope();
var sp = scope.ServiceProvider;

// ---- 1. target -----------------------------------------------------------------------------------------------------
ImportTarget target;
using (var conn = new SqlConnection(cs)) { await conn.OpenAsync(); target = await ReadTargetAsync(conn); }
Console.WriteLine($"  target: {target.ServerName} / {target.Database} (database_id {await Scalar<int>("SELECT DB_ID()")})");
var (targetAllowed, targetIsLive, targetReason) = ImportTargetGuard.Evaluate(target, liveAuthorization);
Check(targetAllowed, "target allowed: " + targetReason);
Check(await Scalar<int>(WorkflowCorrectionsSchema.Cavity70ProbeSql) == 1 || rows.All(r => r.Cavity != DirectSowingRules.Cavity70),
    "70 Cavity is enabled in this database (2026-10-19_Cavity70.sql)");
Check(await Scalar<int>("SELECT CASE WHEN OBJECT_ID('dbo.MaterialConsumptionRules','U') IS NOT NULL THEN 1 ELSE 0 END") == 1,
    "Material Stock is set up (2026-10-08_MaterialStock.sql)");

// ---- 2. locations and people ---------------------------------------------------------------------------------------
var areaId = await Single("SELECT Id FROM dbo.Area WHERE Name = @N AND AreaType = N'MainOffice' AND IsActive = 1", ("@N", AreaName));
Check(areaId > 0, $"Area '{AreaName}' (active, MainOffice) = Id {areaId}");
var polyhouseId = await Single("SELECT Id FROM dbo.Polyhouses WHERE Name = @N AND AreaId = @A", ("@N", PolyhouseName), ("@A", areaId));
Check(polyhouseId > 0, $"Polyhouse '{PolyhouseName}' in that Area = Id {polyhouseId}");
var supervisorId = await Single("SELECT Id FROM dbo.IMSUsers WHERE Username = @U AND Name = @N AND ISNULL(IsActive, 0) = 1", ("@U", SupervisorUsername), ("@N", SupervisorName));
Check(supervisorId > 0, $"supervisor {SupervisorName} ({SupervisorUsername}) = active user {supervisorId}");
var recorderId = await Single("SELECT Id FROM dbo.IMSUsers WHERE Username = @U AND ISNULL(IsActive, 0) = 1", ("@U", RecorderUsername));
Check(recorderId > 0 && recorderId != supervisorId, $"recorder {RecorderUsername} = active user {recorderId}, not the supervisor");
var approvers = (await sp.GetRequiredService<UserRoleRepository>().GetSowingApproversAsync()).Select(a => a.EmployeeID).ToList();
Check(approvers.Contains(supervisorId), $"{SupervisorName} is an active Sowing Supervisor (may be assigned and approve)");
var materialId = await Single("SELECT Id FROM dbo.MaterialTypes WHERE Code = @C AND IsActive = 1", ("@C", MaterialCode));
var factor = await Scalar<decimal>("SELECT ConversionFactor FROM dbo.MaterialTypes WHERE Id = @M", ("@M", materialId));
Check(materialId > 0 && factor > 0, $"growing material {MaterialCode} = Id {materialId}, conversion factor {factor}");

var factory = sp.GetRequiredService<UserClaimsFactory>();
var recorder = await factory.GetActiveUserAsync(recorderId);
Check(recorder != null, "recorder can sign in (active, with roles)");
var principal = await factory.CreatePrincipalAsync(recorder!);
var access = sp.GetRequiredService<FeatureAccessService>();
var newLines = mapping.Values.Where(m => m["Decision"].Trim() == "NEW" && rows.Any(r => r.Source == m["SourceVariety"])).ToList();
foreach (var page in new[] { "/Production/SeedStock/AddSeedStock", "/Production/MaterialStock/Add", "/Production/SeedSowing/Create" }
             .Concat(newLines.Count > 0 ? new[] { "/Admin/Plant" } : Array.Empty<string>()))
    Check(await access.CanAccessPageAsync(principal, page), $"recorder may use {page}");
Check(principal.CanAddTrays() && principal.CanAddMaterials(), "recorder may add trays and growing material");
Check(sp.GetRequiredService<AreaAccessService>().CanAccessArea(principal, areaId), "recorder may work in the Main Office Area");

// ---- 3. varieties --------------------------------------------------------------------------------------------------
if (unresolved.Count > 0)
{
    Console.WriteLine($"  UNRESOLVED variety lines ({unresolved.Count}): fill Decision = EXISTING or NEW in {Path.GetFileName(mappingPath)}:");
    foreach (var u in unresolved) Console.WriteLine($"    - {u}");
    Fail("variety mapping incomplete -- nothing was written");
}
var typeIds = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
foreach (var (id, name) in await Rows2<int, string>("SELECT Id, LTRIM(RTRIM(Name)) FROM dbo.PlantTypes")) typeIds[name] = id;
var plan = new Dictionary<string, VarietyPlan>();   // by source name
foreach (var source in rows.Select(r => r.Source).Distinct())
{
    var m = mapping[source];
    var name = m["VarietyName"].Trim();
    if (name.Length == 0) Fail($"'{source}': VarietyName is required");
    var found = await Rows2<int, int?>(SpeciesByNameSql, ("@N", name));
    if (m["Decision"].Trim() == "EXISTING")
    {
        if (found.Count != 1) Fail($"'{source}': EXISTING variety '{name}' found {found.Count} times in {target.Database}");
        if (int.TryParse(m["ExpectSpeciesId"], out var expect) && expect != found[0].Item1) Fail($"'{source}': '{name}' is Id {found[0].Item1} here, worksheet expects {expect}");
        if (found[0].Item2 is not > 0) Fail($"'{source}': '{name}' has no growing days -- set them in Plant Master first");
        plan[source] = new VarietyPlan(name, found[0].Item1, null, null);
    }
    else
    {
        if (!typeIds.TryGetValue(m["NewPlantType"].Trim(), out var typeId)) Fail($"'{source}': NewPlantType '{m["NewPlantType"]}' is not a plant type here");
        if (!int.TryParse(m["NewReadyStockDays"], out var days) || days <= 0) Fail($"'{source}': NewReadyStockDays must be a positive number");
        if (found.Count > 1) Fail($"'{source}': NEW variety '{name}' already exists {found.Count} times");
        plan[source] = new VarietyPlan(name, found.Count == 1 ? found[0].Item1 : 0, typeId, days);
    }
}
foreach (var g in plan.Values.Where(p => p.NewTypeId != null).GroupBy(p => p.Name.ToLowerInvariant()))
    if (g.Select(p => (p.NewTypeId, p.NewDays)).Distinct().Count() > 1) Fail($"NEW variety '{g.First().Name}' is given different types/days on different lines");
var toCreate = plan.Values.Where(p => p.NewTypeId != null && p.SpeciesId == 0).GroupBy(p => p.Name.ToLowerInvariant()).Select(g => g.First()).ToList();
Console.WriteLine($"  ok: {plan.Count} source names resolved -> {plan.Values.Select(p => p.Name.ToLowerInvariant()).Distinct().Count()} varieties ({toCreate.Count} to create)");

// ---- 4. the plan ---------------------------------------------------------------------------------------------------
var lotPlan = rows.GroupBy(r => plan[r.Source].Name.ToLowerInvariant())
    .Select(g => (Name: plan[g.First().Source].Name, Seeds: (decimal)g.Sum(r => r.Seedlings), FirstDate: g.Min(r => r.Date))).ToList();
var trayPlan = rows.GroupBy(r => r.Cavity).Select(g => (Cavity: g.Key, Trays: g.Sum(r => (int)DirectSowingRules.PhysicalTraysRequired(r.Seedlings, r.Cavity)))).OrderBy(t => t.Cavity).ToList();
var perTray = new Dictionary<string, decimal>();
foreach (var t in trayPlan)
{
    var q = await Scalar<decimal?>("SELECT QuantityPerTray FROM dbo.MaterialConsumptionRules WHERE MaterialTypeId = @M AND CavityType = @C AND IsActive = 1", ("@M", materialId), ("@C", t.Cavity));
    if (q is not > 0) Fail($"no active {MaterialCode} rule for {t.Cavity}");
    perTray[t.Cavity] = q!.Value;
}
var kgNeeded = rows.Sum(r => MaterialStockRules.RequiredFor(DirectSowingRules.PhysicalTraysRequired(r.Seedlings, r.Cavity), perTray[r.Cavity]));
var (materialInput, kgReceived) = MaterialInputFor(kgNeeded, factor);
Console.WriteLine($"  plan: {lotPlan.Count} seed receipts = {lotPlan.Sum(l => l.Seeds):N0} seeds (1 seed = 1 seedling, owner decision)");
foreach (var t in trayPlan) Console.WriteLine($"  plan: {t.Trays:N0} empty {t.Cavity} trays ({perTray[t.Cavity]:0.###} kg {MaterialCode} each)");
Console.WriteLine($"  plan: {MaterialCode} {kgNeeded:N3} kg needed -> enter {materialInput:0.###} x factor {factor} = {kgReceived:N3} kg" + (kgReceived != kgNeeded ? $" ({kgReceived - kgNeeded:N3} kg more: the factor cannot give the exact figure)" : " (exact)"));
foreach (var d in rows.GroupBy(r => r.Date)) Console.WriteLine($"  plan {d.Key:dd-MM-yyyy}: {d.Count()} sowings, {d.Sum(r => r.Trays):N0} trays, {d.Sum(r => r.Seedlings):N0} seedlings");

// ---- 5. current state (idempotency) --------------------------------------------------------------------------------
var sowingsDone = 0;
foreach (var r in rows) sowingsDone += await Scalar<int>("SELECT COUNT(*) FROM dbo.SeedSowings WHERE SubmissionToken = @T", ("@T", SowingToken(r.RowNo)));
var receiptsDone = await Scalar<int>("SELECT COUNT(*) FROM dbo.SeedStockTransactions WHERE TransactionType = N'StockIn' AND Remarks LIKE @T", ("@T", Tag + " |%"));
Console.WriteLine($"  state: varieties to create {toCreate.Count}, this batch's seed receipts present {receiptsDone}/{lotPlan.Count}, sowings present {sowingsDone}/79");
foreach (var l in lotPlan)
{
    var sid = plan.Values.First(p => p.Name.Equals(l.Name, StringComparison.OrdinalIgnoreCase)).SpeciesId;
    if (sid == 0) continue;
    var lot = await Scalar<int?>("SELECT Id FROM dbo.SeedStock WHERE SpeciesId = @S AND AreaId = @A AND BatchNo = N''", ("@S", sid), ("@A", areaId));
    if (lot is not int lotId) continue;
    var tagged = await Scalar<int>("SELECT COUNT(*) FROM dbo.SeedStockTransactions WHERE SeedStockId = @L AND TransactionType = N'StockIn' AND Remarks LIKE @T", ("@L", lotId), ("@T", Tag + " |%"));
    var available = await Scalar<decimal>("SELECT PhysicalQuantity - InTransitQuantity FROM dbo.SeedStock WHERE Id = @L", ("@L", lotId));
    if (tagged == 0 && available != 0)
        Fail($"'{l.Name}': its blank-lot Main Office seed lot {lotId} already holds {available:N0} seeds -- owner decision needed before mixing them");
}
Console.WriteLine("  ok: no existing blank-lot seed lot of these varieties holds other seeds");

if (mode == "check")
{
    Console.WriteLine("CHECK PASSED -- nothing was written.");
    return 0;
}

// ==== RUN ===========================================================================================================
// The pages write through the application's own DatabaseHelper: verify THAT connection reaches the target just checked.
using (var conn = sp.GetRequiredService<DatabaseHelper>().GetConnection())
{
    await conn.OpenAsync();
    var effective = await ReadTargetAsync(conn);
    Check(effective == target && ImportTargetGuard.Evaluate(effective, liveAuthorization).Allowed,
        $"the application's connection writes to the checked target {target.ServerName} / {target.Database}{(targetIsLive ? " (LIVE)" : "")}");
}
// A. varieties
foreach (var v in toCreate)
{
    var page = Page<PlantPage>();
    page.NewSpeciesTypeId = v.NewTypeId!.Value; page.NewSpeciesName = v.Name; page.NewSpeciesScientificName = ""; page.NewSpeciesReadyStockDays = v.NewDays; page.NewSpeciesColor = null;
    await page.OnPostAddSpeciesAsync();
    if (page.TempData["Error"] is string err) Fail($"variety '{v.Name}': {err}");
    var saved = await Rows2<int, int?>(SpeciesByNameSql, ("@N", v.Name));
    if (saved.Count != 1 || saved[0].Item2 != v.NewDays) Fail($"variety '{v.Name}' not saved exactly once with {v.NewDays} days");
    Console.WriteLine($"  A created variety {saved[0].Item1}: {v.Name} ({v.NewDays} days)");
}
foreach (var key in plan.Keys.ToList())
    if (plan[key].SpeciesId == 0) plan[key] = plan[key] with { SpeciesId = (await Rows2<int, int?>(SpeciesByNameSql, ("@N", plan[key].Name)))[0].Item1 };

// B. seed receipts (one blank-lot Main Office lot per variety)
var lotIds = new Dictionary<int, int>();   // SpeciesId -> SeedStockId
foreach (var l in lotPlan)
{
    var speciesId = plan.Values.First(p => p.Name.Equals(l.Name, StringComparison.OrdinalIgnoreCase)).SpeciesId;
    var lot = await Scalar<int?>("SELECT Id FROM dbo.SeedStock WHERE SpeciesId = @S AND AreaId = @A AND BatchNo = N''", ("@S", speciesId), ("@A", areaId));
    if (lot is int existing && await Scalar<int>("SELECT COUNT(*) FROM dbo.SeedStockTransactions WHERE SeedStockId = @L AND TransactionType = N'StockIn' AND Remarks LIKE @T", ("@L", existing), ("@T", Tag + " |%")) > 0)
    {
        var got = await Scalar<decimal>("SELECT SUM(Quantity) FROM dbo.SeedStockTransactions WHERE SeedStockId = @L AND TransactionType = N'StockIn' AND Remarks LIKE @T", ("@L", existing), ("@T", Tag + " |%"));
        if (got != l.Seeds) Fail($"'{l.Name}': lot {existing} already received {got:N0} for this batch, plan is {l.Seeds:N0}");
        lotIds[speciesId] = existing;
        Console.WriteLine($"  B '{l.Name}': already received (lot {existing}) -- skipped");
        continue;
    }
    var page = Page<ReceiptPage>();
    page.SpeciesId = speciesId; page.AreaId = areaId; page.SeedSourceId = null; page.BatchNo = null; page.Unit = "Seeds";
    page.Quantity = l.Seeds; page.ReceivedOn = receiptUtc; page.Notes = ReceiptNote;
    if (await page.OnPostAsync() is not RedirectToPageResult) Fail($"'{l.Name}': seed receipt refused -- {Errors(page)}");
    lotIds[speciesId] = (await Scalar<int?>("SELECT Id FROM dbo.SeedStock WHERE SpeciesId = @S AND AreaId = @A AND BatchNo = N''", ("@S", speciesId), ("@A", areaId)))!.Value;
    Console.WriteLine($"  B '{l.Name}': lot {lotIds[speciesId]} +{l.Seeds:N0} seeds");
}

// C. empty trays at Facility 1 (one receipt per cavity; the token makes a rerun a no-op)
foreach (var t in trayPlan)
{
    var page = Page<MaterialPage>();
    page.Tray.AreaId = areaId; page.Tray.PolyhouseId = polyhouseId; page.Tray.TraySize = t.Cavity; page.Tray.Quantity = t.Trays;
    page.Tray.TransactionDate = receiptUtc; page.Tray.Remarks = ReceiptNote; page.Tray.SubmissionToken = TrayToken(t.Cavity);
    if (await page.OnPostTrayAsync() is not RedirectToPageResult) Fail($"{t.Cavity} trays refused -- {Errors(page)}");
    Console.WriteLine($"  C {t.Cavity}: {page.TempData["Success"]}");
}

// D. growing material
{
    var page = Page<MaterialPage>(new Dictionary<string, string> { ["MaterialTypeId"] = materialId.ToString() });
    page.AreaId = areaId; page.PolyhouseId = polyhouseId; page.MaterialTypeId = materialId; page.InputQuantity = materialInput;
    page.Remarks = ReceiptNote; page.SubmissionToken = MaterialToken();
    if (await page.OnPostAsync() is not RedirectToPageResult) Fail($"{MaterialCode} receipt refused -- {Errors(page)}");
    Console.WriteLine($"  D {page.TempData["Success"]}");
}

// E. the 79 Direct Seed Sowings, in date then row order
foreach (var r in rows.OrderBy(r => r.Date).ThenBy(r => r.RowNo))
{
    var v = plan[r.Source];
    var page = Page<SowingPage>();
    page.PlantTypeId = await Scalar<int>("SELECT PlantTypeId FROM dbo.PlantSpecies WHERE Id = @S", ("@S", v.SpeciesId));
    page.SeedSowing = new SowingEntry
    {
        SowingDate = r.Date, SpeciesId = v.SpeciesId, SourceSeedStockId = lotIds[v.SpeciesId], PolyhouseId = polyhouseId,
        CavityType = r.Cavity, SeedQuantity = r.Seedlings, SupervisorId = supervisorId, GrowingMaterialTypeId = materialId,
        SubmissionToken = SowingToken(r.RowNo),
        Remarks = $"{Tag} | row {r.RowNo}: {r.Source}, {r.Trays} x {r.Cavity}" + (r.Decision.Length > 0 ? $" ({r.Decision})" : ""),
    };
    if (await page.OnPostAsync() is not RedirectToPageResult) Fail($"row {r.RowNo} {r.Date:dd-MM-yyyy} {r.Source}: refused -- {Errors(page)}");
    // Read back what the database holds for this row's token (a repeat returns only the existing sowing's Id).
    var stored = await Rows2<string, string>(@"SELECT SowingCode, CONCAT(NumberOfTrays, '|', CAST(QuantitySown AS INT), '|', CavityType, '|', SpeciesId, '|',
        CONVERT(CHAR(10), SowingDate, 23), '|', SupervisorId, '|', PolyhouseId) FROM dbo.SeedSowings WHERE SubmissionToken = @T", ("@T", SowingToken(r.RowNo)));
    var expected = $"{r.Trays}|{r.Seedlings}|{r.Cavity}|{v.SpeciesId}|{r.Date:yyyy-MM-dd}|{supervisorId}|{polyhouseId}";
    if (stored.Count != 1 || stored[0].Item2 != expected) Fail($"row {r.RowNo}: stored [{(stored.Count == 1 ? stored[0].Item2 : stored.Count + " rows")}], expected [{expected}]");
    Console.WriteLine($"  E row {r.RowNo} {r.Date:dd-MM-yyyy} {stored[0].Item1}{(page.SeedSowing.IsDuplicateSubmission ? " (already saved -- not duplicated)" : "")}: {r.Source} -> {v.Name}, {r.Trays} x {r.Cavity}");
}
Console.WriteLine("RUN COMPLETED -- now run 03_Verify_ReadOnly.sql.");
return 0;

// ---- helpers -------------------------------------------------------------------------------------------------------
T Page<T>(Dictionary<string, string>? form = null) where T : PageModel
{
    var http = new DefaultHttpContext { User = principal, RequestServices = sp };
    http.Request.ContentType = "application/x-www-form-urlencoded";
    http.Request.Form = new FormCollection((form ?? new()).ToDictionary(kv => kv.Key, kv => new Microsoft.Extensions.Primitives.StringValues(kv.Value)));
    var page = ActivatorUtilities.CreateInstance<T>(sp);
    page.PageContext = new PageContext(new ActionContext(http, new Microsoft.AspNetCore.Routing.RouteData(), new CompiledPageActionDescriptor()));
    page.TempData = new TempDataDictionary(http, new NullTempData());
    return page;
}
static string Errors(PageModel p) => string.Join(" | ", p.ModelState.Values.SelectMany(v => v.Errors).Select(e => e.ErrorMessage));
static Guid Hash(string s) => new(MD5.HashData(Encoding.UTF8.GetBytes(s)));
static Guid SowingToken(int rowNo) => Hash($"{Tag}|sowing|{rowNo}");
static Guid MaterialToken() => Hash($"{Tag}|material|{MaterialCode}");
static int TrayToken(string cavity) => (BitConverter.ToInt32(MD5.HashData(Encoding.UTF8.GetBytes($"{Tag}|trays|{cavity}")), 0) & 0x3FFFFFFF) + 1;
// The smallest input (3 decimals) whose converted stock is at least the kg needed -- exact when the factor allows it.
static (decimal Input, decimal Kg) MaterialInputFor(decimal kg, decimal factor)
{
    var input = Math.Round(kg / factor, MaterialStockRules.Decimals, MidpointRounding.ToZero);
    while (input > 0.001m && MaterialStockRules.StockQuantityFor(input - 0.001m, factor) >= kg) input -= 0.001m;
    while (MaterialStockRules.StockQuantityFor(input, factor) < kg) input += 0.001m;
    return (input, MaterialStockRules.StockQuantityFor(input, factor));
}
async Task<T> Scalar<T>(string sql, params (string Name, object? Value)[] ps)
{
    using var conn = new SqlConnection(cs);
    await conn.OpenAsync();
    using var cmd = new SqlCommand(sql, conn);
    foreach (var (n, v) in ps) cmd.Parameters.AddWithValue(n, v ?? DBNull.Value);
    var r = await cmd.ExecuteScalarAsync();
    if (r == null || r == DBNull.Value) return default!;
    var t = Nullable.GetUnderlyingType(typeof(T)) ?? typeof(T);
    return (T)Convert.ChangeType(r, t);
}
async Task<int> Single(string sql, params (string Name, object? Value)[] ps)
{
    var found = await Rows2<int, int>(sql.Replace("SELECT Id", "SELECT Id, 0"), ps);
    return found.Count == 1 ? found[0].Item1 : 0;
}
async Task<List<(T1, T2)>> Rows2<T1, T2>(string sql, params (string Name, object? Value)[] ps)
{
    using var conn = new SqlConnection(cs);
    await conn.OpenAsync();
    using var cmd = new SqlCommand(sql, conn);
    foreach (var (n, v) in ps) cmd.Parameters.AddWithValue(n, v ?? DBNull.Value);
    var list = new List<(T1, T2)>();
    using var rd = await cmd.ExecuteReaderAsync();
    while (await rd.ReadAsync())
        list.Add(((T1)rd.GetValue(0), rd.IsDBNull(1) ? default! : (T2)rd.GetValue(1)));
    return list;
}
static async Task<ImportTarget> ReadTargetAsync(SqlConnection conn)
{
    using var cmd = new SqlCommand("SELECT @@SERVERNAME, CAST(SERVERPROPERTY('ServerName') AS nvarchar(256)), DB_NAME()", conn);
    using var rd = await cmd.ExecuteReaderAsync();
    if (!await rd.ReadAsync()) return new ImportTarget(null, null, null);
    return new ImportTarget(rd.IsDBNull(0) ? null : rd.GetString(0), rd.IsDBNull(1) ? null : rd.GetString(1), rd.IsDBNull(2) ? null : rd.GetString(2));
}
static void Check(bool ok, string what) { if (!ok) Fail("check failed: " + what); Console.WriteLine("  ok: " + what); }
static void Fail(string message) { Console.WriteLine("STOPPED: " + message); Environment.Exit(1); }
static string FindRepoRoot()
{
    for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
        if (File.Exists(Path.Combine(d.FullName, "PlantStockManager.csproj"))) return d.FullName;
    throw new InvalidOperationException("PlantStockManager.csproj not found above the runner.");
}

partial class Program
{
    // Exact name, ignoring case, surrounding spaces and the stray line breaks some master names carry.
    const string SpeciesByNameSql = "SELECT Id, ReadyStockDays FROM dbo.PlantSpecies WHERE LOWER(LTRIM(RTRIM(REPLACE(REPLACE(Name, CHAR(13), ''), CHAR(10), '')))) = LOWER(LTRIM(RTRIM(@N)))";
}
record Row(int RowNo, DateTime Date, string Source, string Cavity, int Trays, int Seedlings, string Decision);
record VarietyPlan(string Name, int SpeciesId, int? NewTypeId, int? NewDays);
sealed class NullTempData : ITempDataProvider
{
    public IDictionary<string, object> LoadTempData(HttpContext context) => new Dictionary<string, object>();
    public void SaveTempData(HttpContext context, IDictionary<string, object> values) { }
}
static class Csv
{
    // RFC 4180: quoted fields, doubled quotes, commas inside quotes; a UTF-8 BOM is ignored.
    public static List<Dictionary<string, string>> Read(string path)
    {
        var text = File.ReadAllText(path, Encoding.UTF8).TrimStart('﻿');
        var records = new List<List<string>>();
        var field = new StringBuilder(); var record = new List<string>(); var quoted = false;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (quoted)
            {
                if (c == '"' && i + 1 < text.Length && text[i + 1] == '"') { field.Append('"'); i++; }
                else if (c == '"') quoted = false;
                else field.Append(c);
            }
            else if (c == '"') quoted = true;
            else if (c == ',') { record.Add(field.ToString()); field.Clear(); }
            else if (c == '\n' || c == '\r')
            {
                if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++;
                record.Add(field.ToString()); field.Clear();
                if (record.Any(f => f.Length > 0)) records.Add(record);
                record = new List<string>();
            }
            else field.Append(c);
        }
        record.Add(field.ToString());
        if (record.Any(f => f.Length > 0)) records.Add(record);
        var header = records[0];
        return records.Skip(1).Select(r => header.Select((h, i) => (h, v: i < r.Count ? r[i] : "")).ToDictionary(x => x.h, x => x.v)).ToList();
    }
}
