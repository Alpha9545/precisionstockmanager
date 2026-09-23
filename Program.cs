using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using PlantStockManager.Authorization;
using PlantStockManager.Data;
using PlantStockManager.Models;
using PlantStockManager.Services;
using Microsoft.AspNetCore.HttpOverrides;


var builder = WebApplication.CreateBuilder(args);

builder.Services.AddDataProtection()
    .PersistKeysToFileSystem(new DirectoryInfo("/var/aspnet-keys"))
    .SetApplicationName("PlantStockManager");



builder.Services.AddSingleton<DatabaseHelper>();
builder.Services.AddScoped<SeedSourcesRepository>();
builder.Services.AddScoped<PolyhouseRepository>();
builder.Services.AddScoped<PlantTypeRepository>();
builder.Services.AddScoped<PlantSpeciesRepository>();
builder.Services.AddScoped<SeedEntryRepository>();
builder.Services.AddScoped<InventoryRepository>();
builder.Services.AddScoped<BookingRepository>();
builder.Services.AddScoped<VendorPurchaseRepository>();
builder.Services.AddScoped<TransactionRepository>();
builder.Services.AddScoped<EmployeeRepository>();
builder.Services.AddScoped<InventoryTransactionRepository>();
// After adding your other repos�
builder.Services.AddScoped<SeedBankRepository>();
builder.Services.AddScoped<TransactionRepository>();

// Production module: Mother Plant / Cutting workflow
builder.Services.AddScoped<AreaRepository>();
builder.Services.AddScoped<BatchNumberRepository>();
builder.Services.AddScoped<MotherPlantRepository>();
builder.Services.AddScoped<CuttingPlanRepository>(); // Phase 3
builder.Services.AddScoped<ActualCuttingRepository>(); // Phase 4
builder.Services.AddScoped<CuttingDeliveryRepository>(); // Phase 5
builder.Services.AddScoped<PropagationBatchRepository>(); // Phase 6
builder.Services.AddScoped<EmptyPotInventoryRepository>(); // Phase 7
builder.Services.AddScoped<PottedPlantStockRepository>(); // Phase 7
builder.Services.AddScoped<PotProductionRepository>(); // Phase 7
builder.Services.AddScoped<InternalTransferRepository>(); // Phase 8
builder.Services.AddScoped<PottedPlantBookingRepository>(); // Phase 9
builder.Services.AddScoped<DispatchRepository>(); // Phase 10
builder.Services.AddScoped<VendorRepository>(); // Phase 11
builder.Services.AddScoped<PurchaseOrderRepository>(); // Phase 11
builder.Services.AddScoped<LabRequestRepository>(); // Phase 12
builder.Services.AddScoped<LabourLogRepository>(); // Phase 13

// Role/permission foundation (Phase 14)
builder.Services.AddScoped<RoleRepository>();
builder.Services.AddScoped<PermissionRepository>();
builder.Services.AddScoped<UserRoleRepository>();

// Mother Plant workflow: location-scoped raw cutting stock (Phase 15)
builder.Services.AddScoped<CuttingStockRepository>();

// Growing Partner foundation (Phase 17) -- deliberately separate from
// VendorRepository/dbo.Vendors (Phase 11), see GrowingPartner.cs.
builder.Services.AddScoped<GrowingPartnerRepository>();

// Growing Partner Access & Responsibilities (Phase 17/B): reusable
// Area-scope check, reads the RoleName/AreaAccess claims stamped at
// login. Stateless, but Scoped to match every other service's lifetime
// in this project.
builder.Services.AddScoped<AreaAccessService>();

// Main Office -> Polyhouse/Growing Area Seed Issue (Phase 22/Phase H):
// a wholly new, dedicated Seed Stock domain (Physical/InTransit +
// ledger) and its own Issue header table -- deliberately NOT a new
// StockType on InternalTransferRepository. See
// Database/Phase22_MainOfficeSeedIssue.sql for the full reasoning.
builder.Services.AddScoped<SeedStockRepository>();
builder.Services.AddScoped<SeedIssueRepository>();

// Seed Stock -> Sowing (Phase 23/Phase I): a single-actor production
// event that consumes dbo.SeedStock, mirroring PotProductionRepository's
// shape rather than a StockType/transfer workflow. See
// Database/Phase23_SeedSowing.sql for the full reasoning.
builder.Services.AddScoped<SeedSowingRepository>();
builder.Services.AddScoped<ReadyStockRepository>(); // Phase 25 (Phase K)
builder.Services.AddScoped<ReadyConfirmationRepository>(); // Phase 25 (Phase K)

// Management Dashboard (Phase 26/Phase L): a dedicated, READ-ONLY
// reporting repository -- never a duplicate of any operational
// repository above, see Data/ManagementDashboardRepository.cs.
builder.Services.AddScoped<ManagementDashboardRepository>();


builder.Services.AddScoped<IPasswordHasher<User>, PasswordHasher<User>>();


//builder.Services.AddScoped<IPasswordHasher<ApplicationUser>, ApplicationPasswordHasher>();
//builder.Services.AddScoped<IUserClaimsPrincipalFactory<ApplicationUser>, ApplicationClaimsPrincipalFactory>();

// Cookie-based Authentication
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.LoginPath = "/Account/Login"; // Redirect to login page
        options.AccessDeniedPath = "/Account/AccessDenied"; // Optional
        options.ExpireTimeSpan = TimeSpan.FromDays(7);
    });

// Permission-code authorization (Phase 14). Any [Authorize(Policy = "...")]
// whose policy name matches a dbo.Permissions.Code is satisfied by a
// "Permission" claim of the same value, granted at login time -- see
// Authorization/PermissionAuthorizationPolicyProvider.cs and
// Authorization/MinimumAuthorizationLevelHandler.cs. This does not remove
// or change any of the existing folder-level AuthorizeFolder checks below;
// it is an additional, opt-in layer that specific pages can request.
builder.Services.AddSingleton<IAuthorizationPolicyProvider, PermissionAuthorizationPolicyProvider>();
builder.Services.AddScoped<IAuthorizationHandler, MinimumAuthorizationLevelHandler>();
builder.Services.AddAuthorization();

// Razor Pages with auth support
builder.Services.AddRazorPages(options =>
{
    // Protect CRUD folders
    options.Conventions.AuthorizeFolder("/SeedEntry");
    options.Conventions.AuthorizeFolder("/Admin");
    options.Conventions.AuthorizeFolder("/Bookings");
    options.Conventions.AuthorizeFolder("/Production"); // Mother Plant, and later Cutting/Delivery/etc.

    // Phase 26 (Phase L): baseline "must be authenticated" layer for the
    // new Management Dashboard folder -- exactly mirroring how "/Admin"
    // is both AuthorizeFolder-protected here AND carries its own
    // stricter [Authorize(Policy = "Admin.ManageRoles"/"Admin.ManageUsers")]
    // attributes on individual pages (Phase 14). The actual
    // Administrator-only gate is the [Authorize(Policy = "Admin.ManageAreas")]
    // attribute directly on Pages/ManagementDashboard/Index.cshtml.cs; this
    // folder convention is a second, independent layer, not a substitute
    // for it -- without it, a new top-level folder with no matching
    // AuthorizeFolder/AllowAnonymous entry falls through to fully
    // anonymous access, since no global authenticated-user fallback
    // policy is configured anywhere in this app (see AddAuthorization()
    // above).
    options.Conventions.AuthorizeFolder("/ManagementDashboard");

    // Allow anonymous access to these
    options.Conventions.AllowAnonymousToPage("/Account/Login");
    options.Conventions.AllowAnonymousToFolder("/Data"); // View-only folder
});



// Add services to the container.
//builder.Services.AddRazorPages();




var app = builder.Build();


//app.Use(async (context, next) =>
//{
//    context.Response.Headers["Cache-Control"] = "no-cache, no-store, must-revalidate";
//    context.Response.Headers["Pragma"] = "no-cache";
//    context.Response.Headers["Expires"] = "0";
//    await next();
//});

app.UseForwardedHeaders(new ForwardedHeadersOptions
{
    ForwardedHeaders =
        ForwardedHeaders.XForwardedFor |
        ForwardedHeaders.XForwardedProto
});


// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
    app.UseHsts();
}

app.Use(async (context, next) =>
{
    context.Response.Headers["X-Frame-Options"] = "SAMEORIGIN";
    context.Response.Headers["X-Content-Type-Options"] = "nosniff";
    context.Response.Headers["X-XSS-Protection"] = "1; mode=block";
    await next();
});

//app.UseHttpsRedirection();
app.UseStaticFiles();

app.UseRouting();

app.UseAuthentication();

app.UseAuthorization();

app.MapRazorPages();

app.Run();
