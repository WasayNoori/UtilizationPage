using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Identity.Web;
using Microsoft.Identity.Web.UI;
using UtilizationPage_ASP.Services;
using Microsoft.AspNetCore.Authentication;
using System.Security.Claims;

var builder = WebApplication.CreateBuilder(args);

if (builder.Environment.IsDevelopment())
{
    // Local authentication with cookie-based auth
    builder.Services.AddAuthentication("LocalScheme")
        .AddCookie("LocalScheme", options => {
            options.LoginPath = "/Account/Login";
            options.Cookie.Name = "LocalAuthCookie";
            options.ExpireTimeSpan = TimeSpan.FromHours(24);
        });

    builder.Services.AddAuthorization(options => {
        options.FallbackPolicy = options.DefaultPolicy;
    });
}
else
{
    // Azure AD authentication with claims transformation
    builder.Services.AddAuthentication(OpenIdConnectDefaults.AuthenticationScheme)
        .AddMicrosoftIdentityWebApp(options =>
        {
            builder.Configuration.GetSection("AzureAd").Bind(options);
            options.Events = new OpenIdConnectEvents
            {
                OnTokenValidated = async context =>
                {
                    var email = context.Principal.FindFirstValue("preferred_username") ??
                               context.Principal.FindFirstValue(ClaimTypes.Email) ??
                               context.Principal.FindFirstValue("email");

                    if (!string.IsNullOrEmpty(email))
                    {
                        var claims = new List<Claim>
                        {
                            new Claim(ClaimTypes.Email, email),
                            new Claim("preferred_username", email)
                        };

                        var appIdentity = new ClaimsIdentity(claims);
                        context.Principal.AddIdentity(appIdentity);
                    }
                }
            };
        });

    builder.Services.AddAuthorization(options =>
    {
        options.FallbackPolicy = options.DefaultPolicy;
    });
}

builder.Services.AddRazorPages().AddMicrosoftIdentityUI();
builder.Services.AddScoped<EntryService>();
builder.Logging.AddAzureWebAppDiagnostics();

// Add session support
builder.Services.AddSession(options =>
{
    options.IdleTimeout = TimeSpan.FromMinutes(30);
    options.Cookie.HttpOnly = true;
    options.Cookie.IsEssential = true;
});

var app = builder.Build();

// Configure error handling based on environment
if (app.Environment.IsDevelopment())
{
    app.UseDeveloperExceptionPage();
}
else
{
    app.UseExceptionHandler("/Error");
    app.UseStatusCodePagesWithReExecute("/Error", "?statusCode={0}");
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseStaticFiles();
app.UseSession();

Console.WriteLine($"Current environment: {app.Environment.EnvironmentName}");

app.UseRouting();
app.UseAuthentication();
app.UseAuthorization();
app.MapRazorPages();

// Add middleware to log claims for debugging
app.Use(async (context, next) =>
{
    if (context.User.Identity.IsAuthenticated)
    {
        var logger = context.RequestServices.GetRequiredService<ILogger<Program>>();
        logger.LogInformation("User Claims:");
        foreach (var claim in context.User.Claims)
        {
            logger.LogInformation("Claim: {Type} = {Value}", claim.Type, claim.Value);
        }
    }
    await next();
});

app.Run();