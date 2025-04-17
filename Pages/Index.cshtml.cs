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

    private string GetUserEmail(ClaimsPrincipal user) => user.FindFirstValue(ClaimTypes.Email) ?? user.FindFirstValue("preferred_username");

    private string GetSelectedUserEmail(string userEmail = null)
    {
        bool isDevelopment = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT") == "Development";
        return !string.IsNullOrEmpty(userEmail) ? userEmail : 
               isDevelopment ? GetDevelopmentUserEmail() : 
               GetUserEmail(User);
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
            var currentUserEmail = GetSelectedUserEmail();
            var currentUser = await _entryService.GetUserByEmailAsync(currentUserEmail);
            
            if (currentUser == null)
            {
                return new JsonResult(new { success = false, message = "User not found" });
            }

            var users = await _entryService.GetAllUsersAsync();
            return new JsonResult(new { success = true, data = users });
        }
        catch (Exception ex)
        {
            _logger.LogError($"Error in OnGetUsers: {ex.Message}");
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
}