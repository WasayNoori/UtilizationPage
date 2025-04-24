// Razor Page Model (Index.cshtml.cs)
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using UtilizationPage_ASP.Services;
using System.Security.Claims;
using Microsoft.Extensions.Configuration;
using System.Data.SqlClient;


[Authorize]
public class IndexModel : PageModel
{
    private readonly EntryService _entryService;
    private readonly ILogger<IndexModel> _logger;
    private readonly IConfiguration _configuration;
    public WeeklyHoursSummary WeeklyTotals { get; set; }
    public string UserName { get; set; }

    public IndexModel(EntryService entryService, ILogger<IndexModel> logger, IConfiguration configuration)
    {
        _entryService = entryService;
        _logger = logger;
        _configuration = configuration;
    }

    public async Task OnGetAsync()
    {
        try
        {
            var userEmail = GetSelectedUserEmail();
            var user = await _entryService.GetUserByEmailAsync(userEmail);
            UserName = user?.UserName ?? "User";
        }
        catch (Exception ex)
        {
            _logger.LogError($"Error getting user info: {ex.Message}");
            UserName = "User";
        }
    }

    private string GetDevelopmentUserEmail() => _configuration["TestUser:Email"] ?? "wasay@hawkridgesys.com";

    private string GetUserEmail(ClaimsPrincipal user)
    {
        _logger.LogInformation("GetUserEmail called - Environment: {Env}", 
            Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT") ?? "Not Set");

        // Log all available claims for debugging
        var allClaims = user.Claims.Select(c => $"{c.Type}: {c.Value}").ToList();
        _logger.LogInformation("All available claims: {Claims}", string.Join(Environment.NewLine, allClaims));

        // Try all possible claim types for email in order of preference
        var email = user.FindFirstValue("preferred_username") // Azure AD preferred_username
            ?? user.FindFirstValue("http://schemas.xmlsoap.org/ws/2005/05/identity/claims/emailaddress") // Standard email claim
            ?? user.FindFirstValue("email") // Azure AD email
            ?? user.FindFirstValue(ClaimTypes.Email) // Standard .NET email claim
            ?? user.FindFirstValue(ClaimTypes.Upn); // UPN claim often contains email

        _logger.LogInformation("Retrieved user email from claims: {Email}", email ?? "not found");
        
        if (string.IsNullOrEmpty(email))
        {
            _logger.LogWarning("No email found in claims. Identity details:");
            _logger.LogWarning("- IsAuthenticated: {IsAuthenticated}", user.Identity?.IsAuthenticated);
            _logger.LogWarning("- AuthenticationType: {AuthType}", user.Identity?.AuthenticationType);
            _logger.LogWarning("- Name from Identity: {Name}", user.Identity?.Name);
        }

        return email;
    }

    private string GetSelectedUserEmail(string userEmail = null)
    {
        bool isDevelopment = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT") == "Development";
        _logger.LogInformation("GetSelectedUserEmail - IsDevelopment: {IsDev}, ProvidedEmail: {Email}", 
            isDevelopment, userEmail ?? "none");

        var selectedEmail = !string.IsNullOrEmpty(userEmail) ? userEmail : 
                          isDevelopment ? GetDevelopmentUserEmail() : 
                          GetUserEmail(User);

        _logger.LogInformation("Selected email result: {Email}", selectedEmail ?? "none");
        return selectedEmail;
    }

    public async Task<IActionResult> OnGetEntriesAsync(string filter, string userEmail = null)
    {
        try
        {
            var selectedUserEmail = GetSelectedUserEmail(userEmail);
            var entries = await _entryService.GetEntriesAsync(filter, selectedUserEmail);
            return new JsonResult(new { success = true, data = entries });
        }
        catch (Exception ex)
        {
            _logger.LogError($"Error getting entries: {ex.Message}");
            return new JsonResult(new { success = false, error = ex.Message });
        }
    }

    public async Task<IActionResult> OnGetWeeklySummaryAsync(string weekOption, string userEmail = null)
    {
        try
        {
            var selectedUserEmail = GetSelectedUserEmail(userEmail);
            var data = await _entryService.GetWeeklyHoursSummaryAsync(weekOption, selectedUserEmail);
            return new JsonResult(new { success = true, data });
        }
        catch (Exception ex)
        {
            _logger.LogError($"Error in OnGetWeeklySummaryAsync: {ex.Message}");
            return new JsonResult(new { success = false, error = ex.Message });
        }
    }

    public async Task<IActionResult> OnGetMonthlyComparisonAsync(string userEmail = null)
    {
        try
        {
            var selectedUserEmail = GetSelectedUserEmail(userEmail);
            var data = await _entryService.GetMonthlyHoursComparisonAsync(selectedUserEmail);
            return new JsonResult(new { success = true, data });
        }
        catch (Exception ex)
        {
            _logger.LogError($"Error in OnGetMonthlyComparisonAsync: {ex.Message}");
            return new JsonResult(new { success = false, error = ex.Message });
        }
    }

    public async Task<IActionResult> OnGetUsers()
    {
        try
        {
            _logger.LogInformation("OnGetUsers endpoint called - Starting execution");
            var currentUserEmail = GetSelectedUserEmail();
            _logger.LogInformation("OnGetUsers - Current user email: {Email}", currentUserEmail);

            var currentUser = await _entryService.GetUserByEmailAsync(currentUserEmail);
            _logger.LogInformation("OnGetUsers - User lookup result: {Result}", currentUser != null ? "Found" : "Not Found");
            
            if (currentUser == null)
            {
                _logger.LogWarning("OnGetUsers - User not found in database for email: {Email}", currentUserEmail);
                return new JsonResult(new { success = false, message = "User not found" });
            }

            _logger.LogInformation("OnGetUsers - About to call GetAllUsersAsync");
            var users = await _entryService.GetAllUsersAsync();
            _logger.LogInformation("OnGetUsers - Retrieved {Count} users from database", users?.Count ?? 0);
            
            if (users != null && users.Any())
            {
                _logger.LogInformation("OnGetUsers - First few users retrieved: {Users}", 
                    string.Join(", ", users.Take(3).Select(u => $"{u.UserName} ({u.Email})")));
            }
            
            return new JsonResult(new { success = true, data = users });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in OnGetUsers");
            return new JsonResult(new { success = false, message = ex.Message });
        }
    }

    public async Task<IActionResult> OnGetUserInfoAsync(string email)
    {
        try
        {
            var user = await _entryService.GetUserByEmailAsync(email);
            return user == null 
                ? new JsonResult(new { success = false, message = "User not found" })
                : new JsonResult(new { success = true, data = user });
        }
        catch (Exception ex)
        {
            _logger.LogError($"Error in OnGetUserInfoAsync: {ex.Message}");
            return new JsonResult(new { success = false, message = "Error retrieving user information" });
        }
    }

    // Function to format hours properly
    string FormatHours(double duration)
    {
        if (duration < 1)
        {
            int minutes = (int)Math.Round(duration * 60);  // Convert fraction of an hour to minutes
            return $"{minutes} min";
        }
        else
        {
            int hours = (int)duration;
            int minutes = (int)Math.Round((duration - hours) * 60);
            if (minutes == 0)
                return $"{hours} hrs";  // Only hours
            return $"{hours} hrs {minutes} min";  // Hours and minutes
        }
    }

    public async Task<IActionResult> OnGetBoardDistributionAsync(string filter, string userEmail = null)
    {
        try
        {
            var selectedUserEmail = GetSelectedUserEmail(userEmail);
            var data = await _entryService.GetBoardDistributionAsync(filter, selectedUserEmail);
            return new JsonResult(new { success = true, data });
        }
        catch (Exception ex)
        {
            _logger.LogError($"Error in OnGetBoardDistributionAsync: {ex.Message}");
            return new JsonResult(new { success = false, error = ex.Message });
        }
    }

    public async Task<IActionResult> OnGetLatestUpdateAsync()
    {
        try
        {
            var updateTime = await _entryService.GetLatestUpdateAsync();
            return updateTime.HasValue
                ? new JsonResult(new { success = true, timestamp = updateTime.Value })
                : new JsonResult(new { success = false, message = "No update time found" });
        }
        catch (Exception ex)
        {
            _logger.LogError($"Error getting latest update time: {ex.Message}");
            return new JsonResult(new { success = false, message = "Error retrieving update time" });
        }
    }

    public async Task<IActionResult> OnGetTotalHoursTodayAsync()
    {
        try
        {
            var totalHours = await _entryService.GetTotalHoursTodayAsync();
            return new JsonResult(new { success = true, totalHours });
        }
        catch (Exception ex)
        {
            _logger.LogError($"Error getting total hours for today: {ex.Message}");
            return new JsonResult(new { success = false, message = "Error retrieving total hours" });
        }
    }

    public async Task<IActionResult> OnGetWeekendHoursAsync(string userEmail = null)
    {
        try
        {
            var selectedUserEmail = GetSelectedUserEmail(userEmail);
            var weekendHours = await _entryService.GetWeekendHoursAsync(selectedUserEmail);
            return new JsonResult(new { success = true, data = weekendHours });
        }
        catch (Exception ex)
        {
            _logger.LogError($"Error getting weekend hours: {ex.Message}");
            return new JsonResult(new { success = false, error = "Error retrieving weekend hours" });
        }
    }

    public async Task<IActionResult> OnGetTimeBreakdownAsync(string team = "All")
    {
        try
        {
            var breakdownData = await _entryService.GetTimeBreakdown(team);
            return new JsonResult(new { success = true, data = breakdownData });
        }
        catch (Exception ex)
        {
            _logger.LogError($"Error getting time breakdown: {ex.Message}");
            return new JsonResult(new { success = false, error = "Error retrieving time breakdown data" });
        }
    }

    public async Task<IActionResult> OnGetWeeklyVisualizationDataAsync(string userEmail = null)
    {
        try
        {
            var selectedUserEmail = GetSelectedUserEmail(userEmail);
            var visualizationData = await _entryService.GetWeeklyVisualizationDataAsync(selectedUserEmail);
            return new JsonResult(new { success = true, data = visualizationData });
        }
        catch (Exception ex)
        {
            _logger.LogError($"Error getting weekly visualization data: {ex.Message}");
            return new JsonResult(new { success = false, error = "Error retrieving visualization data" });
        }
    }

    public async Task<IActionResult> OnGetMVPOverall()
    {
        try
        {
            var mvpData = await _entryService.GetMVPOverall();
            return new JsonResult(mvpData);
        }
        catch (Exception ex)
        {
            _logger.LogError($"Error fetching MVP data: {ex.Message}");
            return new JsonResult(new { error = "Failed to fetch MVP data" }) { StatusCode = 500 };
        }
    }

    public async Task<IActionResult> OnGetMVPLastMonth()
    {
        try
        {
            var mvpData = await _entryService.GetMVPLastMonth();
            return new JsonResult(mvpData);
        }
        catch (Exception ex)
        {
            _logger.LogError($"Error fetching last month MVP data: {ex.Message}");
            return new JsonResult(new { error = "Failed to fetch last month MVP data" }) { StatusCode = 500 };
        }
    }

    public async Task<IActionResult> OnGetTopPerformances()
    {
        try
        {
            var performanceData = await _entryService.GeTopPerformances();
            return new JsonResult(performanceData);
        }
        catch (Exception ex)
        {
            _logger.LogError($"Error fetching top performances data: {ex.Message}");
            return new JsonResult(new { error = "Failed to fetch top performances data" }) { StatusCode = 500 };
        }
    }

    public async Task<IActionResult> OnPostAddReviewAsync([FromBody] ReviewRequest request)
    {
        try
        {
            var userEmail = GetSelectedUserEmail();
            if (string.IsNullOrEmpty(userEmail))
            {
                return BadRequest("User not authenticated");
            }

            await _entryService.AddReview(userEmail, request.Stars, request.Comments);
            return new JsonResult(new { success = true });
        }
        catch (Exception ex)
        {
            _logger.LogError($"Error adding review: {ex.Message}");
            return BadRequest("Error submitting feedback");
        }
    }

    public async Task<IActionResult> OnGetAverageRatingAsync()
    {
        try
        {
            var rating = await _entryService.GetAverageRating();
            return new JsonResult(new { success = true, averageRating = rating });
        }
        catch (Exception ex)
        {
            _logger.LogError($"Error getting average rating: {ex.Message}");
            return new JsonResult(new { success = false, error = ex.Message });
        }
    }

    [ValidateAntiForgeryToken]
    public async Task<IActionResult> OnGetReviewsAsync()
    {
        try
        {
            var reviews = await _entryService.GetReviews();
            return new JsonResult(new { success = true, data = reviews });
        }
        catch (Exception ex)
        {
            _logger.LogError($"Error getting reviews: {ex.Message}");
            return new JsonResult(new { success = false, error = ex.Message });
        }
    }

  
}