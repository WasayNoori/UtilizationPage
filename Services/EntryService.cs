using System;
using System.Collections.Generic;
using System.Data.SqlClient;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using System.Linq;
using Microsoft.Extensions.Configuration;
using System.Text.Json;

namespace UtilizationPage_ASP.Services
{
  

    public class EntryService
    {
        private readonly string _connectionString;
        private readonly ILogger<EntryService> _logger;
        private readonly IConfiguration _configuration;
       // private readonly string _connectionString = "Server=tcp:hawkridge.database.windows.net,1433;Initial Catalog=MondayUtilization;Persist Security Info=False;User ID=MondayReader;Password=HawkRidge#1!!;MultipleActiveResultSets=False;Encrypt=True;TrustServerCertificate=False;Connection Timeout=30";

        public string GetConnectionString() => _connectionString;

        public EntryService(IConfiguration configuration,ILogger<EntryService> logger)
        {
            _configuration = configuration;
            _logger = logger;

            var password = configuration["Readerpass"];

            if (string.IsNullOrEmpty(password))
            {
                _logger.LogError("Database password not found in environment variables.");
            }
            else
            {
                _logger.LogInformation($"Database password retrieved successfully. Length: {password.Length}");
            }



            _connectionString = $"Server=tcp:hawkridge.database.windows.net,1433;Initial Catalog=MondayUtilization;Persist Security Info=False;User ID=MondayReader;Password={password};MultipleActiveResultSets=False;Encrypt=True;TrustServerCertificate=False;Connection Timeout=30";

        }

        private string FormatHours(double hours)
        {
            // For weekly and monthly summaries, show hours with 2 decimal places
            return hours.ToString("0.00") + "h";
        }

        private string FormatUserFriendlyTime(double hours)
        {
            if (hours < 1)
            {
                // Convert to minutes for values less than 1 hour
                int minutes = (int)(hours * 60);
                return $"{minutes}m";
            }
            else
            {
                // For 1 hour or more, show hours and minutes
                int wholeHours = (int)hours;
                int minutes = (int)((hours - wholeHours) * 60);
                if (minutes == 0)
                    return $"{wholeHours}h";
                return $"{wholeHours}h {minutes}m";
            }
        }

        public async Task<List<TableEntryViewModel>> GetEntriesAsync(string filter, string userEmail)
        {
            if (string.IsNullOrEmpty(filter))
            {
                _logger.LogError("Filter parameter is null or empty");
                throw new ArgumentException("Filter parameter is required");
            }

            DateTime startDate, endDate;
            var today = DateTime.Today;
            
            if (filter.StartsWith("Month_"))
            {
                // Handle monthly data
                var parts = filter.Split('_');
                var year = int.Parse(parts[1]);
                var month = int.Parse(parts[2]);
                startDate = new DateTime(year, month, 1);
                endDate = startDate.AddMonths(1).AddDays(-1); // Last day of the month
                _logger.LogInformation($"Fetching data for {startDate:MMMM yyyy}");
            }
            else
            {
                // Handle regular filters (Yesterday, ThisWeek, LastWeek)
                switch (filter)
                {
                    case "Today":
                        startDate = DateTime.Now.Date;
                        endDate = startDate;
                        break;

                    case "Yesterday":
                        startDate = DateTime.Now.AddDays(-1).Date;
                        endDate = startDate;
                        break;

                    case "TwoWeeksAgo":
                        endDate = DateTime.Now.Date;
                        startDate = endDate.AddDays(-14);
                        break;

                    case "ThisWeek":
                        startDate = DateTime.Now.AddDays(-(int)DateTime.Now.DayOfWeek + 1).Date;
                        endDate = DateTime.Now.Date;
                        break;

                    case "LastWeek":
                        startDate = DateTime.Now.AddDays(-(int)DateTime.Now.DayOfWeek - 6).Date;
                        endDate = startDate.AddDays(6);
                        break;

                    default:
                        throw new ArgumentException("Invalid filter option");
                }
            }

            _logger.LogInformation($"Date range: {startDate:yyyy-MM-dd} ({startDate:ddd}) to {endDate:yyyy-MM-dd} ({endDate:ddd})");
            var userData = await GetEntriesAsync(startDate, endDate, userEmail);
            _logger.LogInformation($"Received {userData.Count} entries from service");

            var formattedData = userData
                .GroupBy(e => e.BoardName)
                .Select(board => {
                    var boardViewModel = new TableEntryViewModel
                    {
                        Task = board.Key,
                        Hours = FormatUserFriendlyTime(board.Sum(e => e.Duration)),
                        CategoryName = board.First().CategoryName,
                        Children = board.GroupBy(item => item.GroupName)
                            .Select(group => {
                                var groupViewModel = new TableEntryViewModel
                                {
                                    Task = group.Key,
                                    Hours = FormatUserFriendlyTime(group.Sum(e => e.Duration)),
                                    Children = group.Select(item => new TableEntryViewModel
                                    {
                                        Task = item.ItemName,
                                        Hours = FormatUserFriendlyTime(item.Duration),
                                        EntryDate = item.StartTime.ToString("dddd MM-dd")
                                    }).ToList()
                                };
                                _logger.LogInformation($"Group: {group.Key}, Hours: {groupViewModel.Hours}, Children: {groupViewModel.Children.Count}");
                                return groupViewModel;
                            }).ToList()
                    };
                    _logger.LogInformation($"Board: {board.Key}, Category: {boardViewModel.CategoryName}, Hours: {boardViewModel.Hours}, Groups: {boardViewModel.Children.Count}");
                    return boardViewModel;
                }).ToList();

            _logger.LogInformation($"Formatted {formattedData.Count} boards with hierarchy");
            foreach (var board in formattedData)
            {
                _logger.LogInformation($"Board: {board.Task}, Hours: {board.Hours}, Groups: {board.Children?.Count ?? 0}");
                if (board.Children != null)
                {
                    foreach (var group in board.Children)
                    {
                        _logger.LogInformation($"  Group: {group.Task}, Hours: {group.Hours}, Items: {group.Children?.Count ?? 0}");
                        if (group.Children != null)
                        {
                            foreach (var item in group.Children)
                            {
                                _logger.LogInformation($"    Item: {item.Task}, Hours: {item.Hours}");
                            }
                        }
                    }
                }
            }

            // Add serialization logging to see exact JSON structure
            var json = JsonSerializer.Serialize(formattedData, new JsonSerializerOptions { WriteIndented = true });
            _logger.LogInformation($"Serialized JSON structure:\n{json}");

            return formattedData;
        }

        public async Task<List<EntryViewModel>> GetEntriesAsync(DateTime startDate, DateTime endDate, string userEmail)
        {
            var entries = new List<EntryViewModel>();
            _logger.LogInformation($"GetEntriesAsync called with startDate: {startDate:yyyy-MM-dd}, endDate: {endDate:yyyy-MM-dd}, userEmail: {userEmail}");
            
            try
            {
                using (var connection = new SqlConnection(_connectionString))
                {
                    await connection.OpenAsync();
                    _logger.LogInformation("Database connection successful.");

                    using (var command = new SqlCommand("GetUserEntries", connection))
                    {
                        command.CommandType = System.Data.CommandType.StoredProcedure;
                        command.Parameters.AddWithValue("@StartDate", startDate);
                        command.Parameters.AddWithValue("@EndDate", endDate);
                        command.Parameters.AddWithValue("@userEmail", userEmail);

                        using (var reader = await command.ExecuteReaderAsync())
                        {
                            while (await reader.ReadAsync())
                            {
                                try
                                {
                                    var entry = new EntryViewModel
                                    {
                                        StartTime = reader.GetDateTime(1),
                                        BoardName = reader.GetString(2),
                                        ItemName = reader.GetString(3),
                                        Duration = Math.Round(reader.GetDouble(5), 2),
                                        GroupName = reader.GetString(4),
                                        CategoryName = !reader.IsDBNull(6) ? reader.GetString(6) : null
                                    };
                                    entries.Add(entry);
                                    _logger.LogInformation($"Successfully read entry: {entry.BoardName} - {entry.ItemName}");
                                }
                                catch (Exception ex)
                                {
                                    _logger.LogError($"Error reading row: {ex.Message}");
                                    _logger.LogError($"Column values: ItemID={reader.GetValue(0)}, StartTime={reader.GetValue(1)}, BoardName={reader.GetValue(2)}, ItemName={reader.GetValue(3)}, Duration={reader.GetValue(4)}, GroupName={reader.GetValue(5)}, CategoryName={reader.GetValue(6)}");
                                }
                            }
                        }
                    }
                }
                _logger.LogInformation($"Retrieved {entries.Count} entries from database");
            }
            catch (Exception ex)
            {
                _logger.LogError($"Database connection error: {ex.Message}");
            }
            return entries;
        }

        public async Task<WeeklyHoursSummary> GetWeeklyHoursSummaryAsync(string weekOption, string userEmail)
        {
            var summary = new WeeklyHoursSummary();

            try
            {
                using (var connection = new SqlConnection(_connectionString))
                {
                    await connection.OpenAsync();
                    _logger.LogInformation($"Database connection successful (weekly summary) for {weekOption} week.");

                    using (var command = new SqlCommand("GetWeeklyLoggedHoursByUser", connection))
                    {
                        command.CommandType = System.Data.CommandType.StoredProcedure;
                        command.Parameters.AddWithValue("@WeekOption", weekOption);
                        command.Parameters.AddWithValue("@UserEmail", !string.IsNullOrEmpty(userEmail) ? userEmail : DBNull.Value);

                            using (var reader = await command.ExecuteReaderAsync())
                        {
                            // Log the column names and their indices
                            for (int i = 0; i < reader.FieldCount; i++)
                            {
                                _logger.LogInformation($"Weekly Summary Column {i}: {reader.GetName(i)}");
                            }

                                if (await reader.ReadAsync())
                            {
                                try
                                {
                                    summary.UserName = reader.GetString(reader.GetOrdinal("UserName"));
                                    summary.Monday = FormatUserFriendlyTime(reader.GetDouble(reader.GetOrdinal("Mon")));
                                    summary.Tuesday = FormatUserFriendlyTime(reader.GetDouble(reader.GetOrdinal("Tue")));
                                    summary.Wednesday = FormatUserFriendlyTime(reader.GetDouble(reader.GetOrdinal("Wed")));
                                    summary.Thursday = FormatUserFriendlyTime(reader.GetDouble(reader.GetOrdinal("Thu")));
                                    summary.Friday = FormatUserFriendlyTime(reader.GetDouble(reader.GetOrdinal("Fri")));

                                    _logger.LogInformation($"Weekly summary loaded successfully - User: {summary.UserName}, Mon: {summary.Monday}, Tue: {summary.Tuesday}, Wed: {summary.Wednesday}, Thu: {summary.Thursday}, Fri: {summary.Friday}");
                                }
                                catch (Exception ex)
                                {
                                    _logger.LogError($"Error reading weekly summary row: {ex.Message}");
                                    // Log all column values to help diagnose the issue
                                    for (int i = 0; i < reader.FieldCount; i++)
                                    {
                                        _logger.LogError($"Column {i} ({reader.GetName(i)}): {reader.GetValue(i)}");
                                    }
                                }
                            }
                            else
                            {
                                _logger.LogWarning("No data returned from GetWeeklyLoggedHoursByUser stored procedure");
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError($"Error fetching weekly hours summary: {ex.Message}");
            }

            return summary;
        }

        public async Task<List<MonthlyHoursSummary>> GetMonthlyHoursComparisonAsync(string userEmail)
        {
            var monthlyData = new List<MonthlyHoursSummary>();

            try
            {
                using (var connection = new SqlConnection(_connectionString))
                {
                    await connection.OpenAsync();
                    _logger.LogInformation("Database connection successful (monthly hours comparison).");

                    using (var command = new SqlCommand("GetMonthlyHoursByUser", connection))
                    {
                        command.CommandType = System.Data.CommandType.StoredProcedure;
                        command.Parameters.AddWithValue("@UserEmail", !string.IsNullOrEmpty(userEmail) ? userEmail : DBNull.Value);

                        using (var reader = await command.ExecuteReaderAsync())
                        {
                            while (await reader.ReadAsync())
                            {
                                var summary = new MonthlyHoursSummary
                                {
                                    MonthName = reader.GetString(reader.GetOrdinal("MonthName")),
                                    TeamAverage = FormatUserFriendlyTime(reader.GetDouble(reader.GetOrdinal("TeamAverage"))),
                                    UserHours = FormatUserFriendlyTime(reader.GetDouble(reader.GetOrdinal("UserHours")))
                                };
                                monthlyData.Add(summary);
                                _logger.LogInformation($"Monthly data loaded for {summary.MonthName}: User Hours = {summary.UserHours}, Team Average = {summary.TeamAverage}");
                            }
                        }
                    }
                }
                _logger.LogInformation($"Retrieved monthly hours comparison data for {userEmail}");
            }
            catch (Exception ex)
            {
                _logger.LogError($"Error fetching monthly hours comparison: {ex.Message}");
            }

            return monthlyData;
        }

        public async Task<List<User>> GetAllUsersAsync()
        {
            var users = new List<User>();
            try
            {
                _logger.LogInformation("Starting to fetch all users");
                using (var connection = new SqlConnection(_connectionString))
                {
                    await connection.OpenAsync();
                    _logger.LogInformation("Database connection successful (get all users).");

                    // Get all users with their team information
                    using (var command = new SqlCommand(@"
                        SELECT UserName, Email, UserType, Team 
                        FROM Users 
                        ORDER BY UserName", connection))
                    {
                        using (var reader = await command.ExecuteReaderAsync())
                        {
                            while (await reader.ReadAsync())
                            {
                                var userName = reader.GetString(reader.GetOrdinal("UserName"));
                                var email = reader.GetString(reader.GetOrdinal("Email"));
                                var userType = reader.GetString(reader.GetOrdinal("UserType"));
                                var team = reader.IsDBNull(reader.GetOrdinal("Team")) ? "NULL" : reader.GetString(reader.GetOrdinal("Team"));
                                
                                // Only add users from ES or TS teams
                                if (team == "ES" || team == "TS")
                                {
                                    users.Add(new User
                                    {
                                        UserName = userName,
                                        Email = email,
                                        UserType = userType
                                    });
                                }
                            }
                        }
                    }
                }
                _logger.LogInformation($"Retrieved {users.Count} users from database (filtered by Team='ES' or Team='TS')");
            }
            catch (Exception ex)
            {
                _logger.LogError($"Error fetching users: {ex.Message}");
                _logger.LogError($"Stack trace: {ex.StackTrace}");
            }
            return users;
        }

        public async Task<User> GetUserByEmailAsync(string email)
        {
            try
            {
                _logger.LogInformation($"Attempting to get user by email: {email}");
                using (var connection = new SqlConnection(_connectionString))
                {
                    await connection.OpenAsync();
                    _logger.LogInformation("Database connection successful (get user by email).");

                    using (var command = new SqlCommand(@"
                        SELECT UserName, Email, UserType, Team 
                        FROM Users 
                        WHERE Email = @Email", connection))
                    {
                        command.Parameters.AddWithValue("@Email", email);
                        _logger.LogInformation($"Executing SQL query for email: {email}");
                        
                        using (var reader = await command.ExecuteReaderAsync())
                        {
                            if (await reader.ReadAsync())
                            {
                                var user = new User
                                {
                                    UserName = reader.GetString(reader.GetOrdinal("UserName")),
                                    Email = reader.GetString(reader.GetOrdinal("Email")),
                                    UserType = reader.GetString(reader.GetOrdinal("UserType"))
                                };
                                _logger.LogInformation($"Found user in database: {user.UserName} ({user.Email}) - Type: {user.UserType}");
                                return user;
                            }
                            else
                            {
                                _logger.LogWarning($"No user found in database with email: {email}");
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError($"Error fetching user by email: {ex.Message}");
                _logger.LogError($"Stack trace: {ex.StackTrace}");
            }
            return null;
        }

        public async Task<List<BoardDistribution>> GetBoardDistributionAsync(string filter, string userEmail)
        {
            try
            {
                // _logger.LogInformation($"Getting board distribution for filter '{filter}' and user '{userEmail}'");
                
                // Get the existing entries data
                var entries = await GetEntriesAsync(filter, userEmail);
                
                if (entries == null || !entries.Any())
                {
                    // _logger.LogInformation("No entries found for distribution calculation");
                    return new List<BoardDistribution>();
                }

                // _logger.LogInformation($"Processing {entries.Count} board entries");

                // The entries are already grouped by board and have total hours calculated
                var distribution = entries.Select(board => 
                {
                    // _logger.LogInformation($"Processing board: {board.Task} with hours: {board.Hours}");
                    return new BoardDistribution
                    {
                        BoardName = board.Task,
                        Hours = board.Hours,
                        Percentage = 0 // We'll calculate this after we have the total
                    };
                }).ToList();

                // Calculate total hours for percentage calculation
                var totalHours = 0.0;
                foreach (var board in entries)
                {
                    var hoursStr = board.Hours;
                    // Parse hours like "2h 30m" or "23h"
                    var parts = hoursStr.Split(' ');
                    var hours = 0.0;
                    
                    foreach (var part in parts)
                    {
                        if (part.EndsWith("h"))
                        {
                            hours += double.Parse(part.TrimEnd('h'));
                        }
                        else if (part.EndsWith("m"))
                        {
                            hours += double.Parse(part.TrimEnd('m')) / 60.0;
                        }
                    }
                    totalHours += hours;
                }

                // _logger.LogInformation($"Total hours across all boards: {totalHours}");

                // Calculate percentages
                foreach (var board in distribution)
                {
                    var hoursStr = board.Hours;
                    var parts = hoursStr.Split(' ');
                    var hours = 0.0;
                    
                    foreach (var part in parts)
                    {
                        if (part.EndsWith("h"))
                        {
                            hours += double.Parse(part.TrimEnd('h'));
                        }
                        else if (part.EndsWith("m"))
                        {
                            hours += double.Parse(part.TrimEnd('m')) / 60.0;
                        }
                    }
                    
                    board.Percentage = Math.Round((hours / totalHours) * 100, 1);
                    // _logger.LogInformation($"Board: {board.BoardName}, Hours: {board.Hours}, Percentage: {board.Percentage}%");
                }

                var orderedDistribution = distribution.OrderByDescending(d => d.Percentage).ToList();
                // _logger.LogInformation($"Calculated distribution for {orderedDistribution.Count} boards");
                return orderedDistribution;
            }
            catch (Exception ex)
            {
                _logger.LogError($"Error calculating board distribution: {ex.Message}");
                _logger.LogError($"Stack trace: {ex.StackTrace}");
                return new List<BoardDistribution>();
            }
        }

        private void GetDateRangeFromFilter(string filter, out DateTime startDate, out DateTime endDate)
        {
            var today = DateTime.Now.Date;
            startDate = today;
            endDate = today;

            if (filter.StartsWith("Month_"))
            {
                var parts = filter.Split('_');
                var year = int.Parse(parts[1]);
                var month = int.Parse(parts[2]);
                startDate = new DateTime(year, month, 1);
                endDate = startDate.AddMonths(1).AddDays(-1); // Last day of the month
                _logger.LogInformation($"Monthly filter: {filter} -> {startDate:yyyy-MM-dd} to {endDate:yyyy-MM-dd}");
                return;
            }

            switch (filter)
            {
                case "Today":
                    startDate = DateTime.Now.Date;
                    endDate = startDate;
                    break;

                case "Yesterday":
                    startDate = DateTime.Now.AddDays(-1).Date;
                    endDate = startDate;
                    break;

                case "TwoWeeksAgo":
                    endDate = DateTime.Now.Date;
                    startDate = endDate.AddDays(-14);
                    break;

                case "ThisWeek":
                    startDate = DateTime.Now.AddDays(-(int)DateTime.Now.DayOfWeek + 1).Date;
                    endDate = DateTime.Now.Date;
                    break;

                case "LastWeek":
                    startDate = DateTime.Now.AddDays(-(int)DateTime.Now.DayOfWeek - 6).Date;
                    endDate = startDate.AddDays(6);
                    break;

                default:
                    _logger.LogWarning($"Unknown filter: {filter}, using today's date");
                    break;
            }
            _logger.LogInformation($"Filter {filter} -> {startDate:yyyy-MM-dd} ({startDate:ddd}) to {endDate:yyyy-MM-dd} ({endDate:ddd})");
        }

        public async Task<DateTime?> GetLatestUpdateAsync()
        {
            try
            {
                using (var connection = new SqlConnection(_connectionString))
                {
                    await connection.OpenAsync();
                    using (var command = new SqlCommand("GetLatestUpdate", connection))
                    {
                        command.CommandType = System.Data.CommandType.StoredProcedure;
                        var result = await command.ExecuteScalarAsync();
                        if (result != null && result != DBNull.Value)
                        {
                            return (DateTime)result;
                        }
                    }
                }
                return null;
            }
            catch (Exception ex)
            {
                _logger.LogError($"Error getting latest update time: {ex.Message}");
                return null;
            }
        }

    }
}
