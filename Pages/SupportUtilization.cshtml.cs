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
//using System.Data.SqlClient;
using Microsoft.Data.SqlClient;



[Authorize]
public class SupportUtilizationModel : PageModel
{
    private readonly EntryService _entryService;
    private readonly ZoomScheduleService _zoomScheduleService;
    private readonly ZendeskImportService _zendeskImportService;
    private readonly ZendeskMetricsService _zendeskMetricsService;
    private readonly IAuthorizationService _authorizationService;
    private readonly ILogger<IndexModel> _logger;
    private readonly IConfiguration _configuration;
    public WeeklyHoursSummary WeeklyTotals { get; set; }
    public string UserName { get; set; }

    /// <summary>
    ///     Whether the signed-in user may import Zendesk metrics. Drives whether the import
    ///     control renders; the handler re-checks the same policy, because hiding a control is
    ///     not a restriction.
    /// </summary>
    public bool CanImportMetrics { get; set; }

    public SupportUtilizationModel(EntryService entryService, ZoomScheduleService zoomScheduleService,
        ZendeskImportService zendeskImportService, ZendeskMetricsService zendeskMetricsService,
        IAuthorizationService authorizationService,
        ILogger<IndexModel> logger, IConfiguration configuration)
    {
        _entryService = entryService;
        _zoomScheduleService = zoomScheduleService;
        _zendeskImportService = zendeskImportService;
        _zendeskMetricsService = zendeskMetricsService;
        _authorizationService = authorizationService;
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

        CanImportMetrics = await IsImportAllowedAsync();
    }

    private async Task<bool> IsImportAllowedAsync()
    {
        var result = await _authorizationService.AuthorizeAsync(User, "CanImportMetrics");
        return result.Succeeded;
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

    #region Zoom WFM schedule

    public async Task<IActionResult> OnGetZoomScheduleAsync(string filter, string userEmail = null,
        string groupBy = "agent", string activity = null)
    {
        try
        {
            // An empty userEmail means "everyone on the schedule" here, unlike the utilization
            // table which always resolves to a single person.
            var schedule = await _zoomScheduleService.GetScheduleAsync(filter, userEmail, groupBy, activity);
            return new JsonResult(new
            {
                success = true,
                data = schedule.Rows,
                activityNames = schedule.ActivityNames,
                activityTypes = schedule.ActivityTypes,
                metricsMonth = schedule.MetricsMonth
            });
        }
        catch (Exception ex)
        {
            _logger.LogError($"Error getting Zoom schedule: {ex.Message}");
            return new JsonResult(new { success = false, error = ex.Message });
        }
    }

    public async Task<IActionResult> OnGetZoomLastSyncAsync()
    {
        try
        {
            var lastSync = await _zoomScheduleService.GetLastSyncAsync();
            return lastSync.HasValue
                ? new JsonResult(new { success = true, timestamp = lastSync.Value })
                : new JsonResult(new { success = false, message = "The schedule has never been synced." });
        }
        catch (Exception ex)
        {
            _logger.LogError($"Error getting the last Zoom sync time: {ex.Message}");
            return new JsonResult(new { success = false, message = "Error retrieving the last sync time" });
        }
    }

    public async Task<IActionResult> OnPostSyncZoomScheduleAsync()
    {
        try
        {
            var result = await _zoomScheduleService.SyncAsync();
            return new JsonResult(new
            {
                success = result.Success,
                hasErrors = result.HasErrors,
                startDate = result.StartDate,
                endDate = result.EndDate,
                dayCount = result.DayCount,
                agentCount = result.AgentCount,
                activityCount = result.ActivityCount,
                message = result.Message
            });
        }
        catch (Exception ex)
        {
            _logger.LogError($"Error running the Zoom schedule sync: {ex.Message}");
            return new JsonResult(new { success = false, message = "Error running the schedule sync" });
        }
    }

    /// <summary>
    ///     Imports a Zendesk Explore CSV export.
    ///
    ///     The policy is checked here rather than with an [Authorize] attribute because Razor
    ///     Pages ignores authorization attributes on individual handler methods - only the page
    ///     model is inspected. Hiding the button in the view is cosmetic; this check is the
    ///     actual control.
    /// </summary>
    public async Task<IActionResult> OnPostImportZendeskAsync(IFormFile csvFile, int year, int month)
    {
        if (!await IsImportAllowedAsync())
        {
            _logger.LogWarning("Rejected a Zendesk import attempt from {Email}",
                User.FindFirstValue(ClaimTypes.Email) ?? "unknown");
            return new JsonResult(new { success = false, message = "You are not permitted to import metrics." })
            {
                StatusCode = 403
            };
        }

        const long maxUploadBytes = 10 * 1024 * 1024;

        if (csvFile == null || csvFile.Length == 0)
        {
            return new JsonResult(new { success = false, message = "Choose a CSV file to import." });
        }

        if (csvFile.Length > maxUploadBytes)
        {
            return new JsonResult(new { success = false, message = "That file is larger than 10 MB - it is probably not an Explore export." });
        }

        if (month < 1 || month > 12)
        {
            return new JsonResult(new { success = false, message = "Select a valid month." });
        }

        try
        {
            var importedBy = User.FindFirstValue(ClaimTypes.Email)
                             ?? User.FindFirstValue("preferred_username")
                             ?? "unknown";

            ZendeskImportResult result;
            using (var stream = csvFile.OpenReadStream())
            {
                result = await _zendeskImportService.ImportAsync(stream, csvFile.FileName, year, month, importedBy);
            }

            return new JsonResult(new
            {
                success = result.Success,
                fileKind = result.FileKind,
                rowsParsed = result.RowsParsed,
                rowsUpserted = result.RowsUpserted,
                monthsCovered = result.MonthsCovered,
                unmatchedAgents = result.UnmatchedAgents,
                nothingChanged = result.NothingChanged,
                message = result.Message
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Zendesk import failed");
            return new JsonResult(new { success = false, message = $"Import failed: {ex.Message}" });
        }
    }

    #endregion
}