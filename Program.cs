using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
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
// After adding your other repos…
builder.Services.AddScoped<SeedBankRepository>();
builder.Services.AddScoped<TransactionRepository>();


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

builder.Services.AddAuthorization();

// Razor Pages with auth support
builder.Services.AddRazorPages(options =>
{
    // Protect CRUD folders
    options.Conventions.AuthorizeFolder("/SeedEntry");
    options.Conventions.AuthorizeFolder("/Admin");
    options.Conventions.AuthorizeFolder("/Bookings");

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
