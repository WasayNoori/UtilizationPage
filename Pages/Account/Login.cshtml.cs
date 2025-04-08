using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using System.Security.Claims;
using Microsoft.Extensions.Configuration;

namespace UtilizationPage_ASP.Pages.Account
{
    [AllowAnonymous]
    public class LoginModel : PageModel
    {
        private readonly IConfiguration _configuration;

        public LoginModel(IConfiguration configuration)
        {
            _configuration = configuration;
        }

        public async Task<IActionResult> OnPostAsync()
        {
            Console.WriteLine("POST Async");

            // Prevent infinite loop by checking if user is already authenticated
            if (User.Identity != null && User.Identity.IsAuthenticated)
            {
                Console.WriteLine("User already authenticated, redirecting to home.");
                return Redirect("/");
            }

            var testUserEmail = _configuration["TestUser:Email"] ;
            
            var claims = new List<Claim>
            {
                new Claim(ClaimTypes.Name, "LocalUser"),
                new Claim(ClaimTypes.Email, testUserEmail)
            };

            var identity = new ClaimsIdentity(claims, "LocalScheme");
            var principal = new ClaimsPrincipal(identity);

            try
            {
                await HttpContext.SignInAsync("LocalScheme", principal);
            }
            catch (Exception ex)
            {
                Console.WriteLine("Error during sign-in: " + ex.ToString());
            }

            return Redirect("/"); // Redirect to the home page
        }

        public void OnGet()
        {
            Console.WriteLine("HIT");
        }
    }
}