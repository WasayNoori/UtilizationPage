using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Identity.Web;
using Microsoft.Identity.Web.UI;
using UtilizationPage_ASP.Services;

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
    // Azure AD authentication
    builder.Services.AddAuthentication(OpenIdConnectDefaults.AuthenticationScheme)
        .AddMicrosoftIdentityWebApp(builder.Configuration.GetSection("AzureAd"));

    builder.Services.AddAuthorization(options =>
    {
        options.FallbackPolicy = options.DefaultPolicy;
    });
}

builder.Services.AddAuthorization(options => options.FallbackPolicy = options.DefaultPolicy);

builder.Services.AddRazorPages().AddMicrosoftIdentityUI();
builder.Services.AddScoped<EntryService>();
builder.Logging.AddAzureWebAppDiagnostics();
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

Console.WriteLine(app.Environment.EnvironmentName);

app.UseRouting();
app.UseAuthentication();
app.UseAuthorization();
app.MapRazorPages();
app.Run();