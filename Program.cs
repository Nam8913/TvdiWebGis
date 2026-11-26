using System.Security.Claims;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using TvdiWebGis.Components;
using TvdiWebGis.Components.Account;
using TvdiWebGis.Data;
using TvdiWebGis.Endpoints;
using TvdiWebGis.Forecasting;
using TvdiWebGis.Services;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

builder.Services.AddCascadingAuthenticationState();
builder.Services.AddAuthorization();
builder.Services.AddScoped<IdentityRedirectManager>();
builder.Services.AddScoped<AuthenticationStateProvider, IdentityRevalidatingAuthenticationStateProvider>();

builder.Services.AddAuthentication(options =>
    {
        options.DefaultScheme = IdentityConstants.ApplicationScheme;
        options.DefaultSignInScheme = IdentityConstants.ExternalScheme;
    })
    .AddIdentityCookies();

var connectionString = builder.Configuration.GetConnectionString("DefaultConnection") ?? throw new InvalidOperationException("Configure the SQL Server connection with ConnectionStrings__DefaultConnection.");
builder.Services.AddDbContextFactory<ApplicationDbContext>(options => options.UseSqlServer(connectionString));
builder.Services.AddScoped(sp => sp.GetRequiredService<IDbContextFactory<ApplicationDbContext>>().CreateDbContext()); // cho Identity

builder.Services.AddDatabaseDeveloperPageExceptionFilter();

builder.Services.AddIdentityCore<ApplicationUser>(o => o.SignIn.RequireConfirmedAccount = false)
    .AddRoles<IdentityRole>()                                   // ← thêm dòng này
    .AddEntityFrameworkStores<ApplicationDbContext>()
    .AddSignInManager()
    .AddDefaultTokenProviders();

builder.Services.AddSingleton<IEmailSender<ApplicationUser>, IdentityNoOpEmailSender>();
builder.Services.AddSingleton<IForecastModel, LinearRegressionModel>();
builder.Services.AddSingleton<IForecastModel, RandomForestModel>();

builder.Services.AddSingleton<GeoTiffService>();
builder.Services.AddScoped<RasterFileService>();
builder.Services.AddScoped<SettingsService>();
builder.Services.AddScoped<ForecastService>();
builder.Services.AddScoped<TvdiAnalysisService>();
builder.Services.AddSingleton(new SemaphoreSlim(1, 1));
builder.Services.AddMemoryCache();
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddPolicy("map-api", context => RateLimitPartition.GetFixedWindowLimiter(
        context.User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "anonymous",
        _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = 120,
            Window = TimeSpan.FromMinutes(1)
        }));
    options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
    {
        if (context.Request.Path.StartsWithSegments("/api", StringComparison.OrdinalIgnoreCase))
        {
            return RateLimitPartition.GetConcurrencyLimiter("api",
                _ => new ConcurrencyLimiterOptions { PermitLimit = 4, QueueLimit = 0 });
        }

        if (HttpMethods.IsPost(context.Request.Method) &&
            (context.Request.Path.StartsWithSegments("/Account/Login", StringComparison.OrdinalIgnoreCase) ||
             context.Request.Path.StartsWithSegments("/Account/Register", StringComparison.OrdinalIgnoreCase)))
        {
            return RateLimitPartition.GetFixedWindowLimiter(
                context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = 20,
                    Window = TimeSpan.FromMinutes(1)
                });
        }

        return RateLimitPartition.GetNoLimiter("other");
    });
});

builder.Services.AddSingleton<WardZonalService>();

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>().Database.MigrateAsync();
    await DbSeeder.SeedAsync(scope.ServiceProvider, app.Configuration);
}

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.UseMigrationsEndPoint();
}
else
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
    app.UseHsts();
}
app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseHttpsRedirection();

app.Use(async (context, next) =>
{
    context.Response.Headers["X-Content-Type-Options"] = "nosniff";
    context.Response.Headers["Referrer-Policy"] = "strict-origin-when-cross-origin";
    context.Response.Headers["X-Frame-Options"] = "DENY";
    await next();
});
app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();
app.UseAntiforgery();

app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.MapRasterEndpoints();
app.MapWardEndpoints(); 

// Add additional endpoints required by the Identity /Account Razor components.
app.MapAdditionalIdentityEndpoints();

app.Run();
