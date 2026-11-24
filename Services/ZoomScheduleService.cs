using System;
using System.Collections.Generic;
using Microsoft.Data.SqlClient;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using System.Linq;
using Microsoft.Extensions.Configuration;
using System.Text.Json;
using System.Net.Http;

namespace UtilizationPage_ASP.Services
{
    /// <summary>
    ///     Reads the Zoom Workforce Management schedule that Zoom-REST-API syncs into SQL, and
    ///     triggers that sync on demand.
    ///
    ///     Reads go straight to the database, the same way EntryService reads the utilization
    ///     data. Only the sync itself goes over HTTP, because only the API holds the Zoom
    ///     credentials.
    /// </summary>
    public class ZoomScheduleService
    {
        private readonly string _connectionString;
        private readonly string _apiBaseUrl;
        private readonly ILogger<ZoomScheduleService> _logger;
        private readonly IConfiguration _configuration;
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly ZendeskMetricsService _zendeskMetricsService;

        public ZoomScheduleService(IConfiguration configuration, ILogger<ZoomScheduleService> logger,
            IHttpClientFactory httpClientFactory, ZendeskMetricsService zendeskMetricsService)
        {
            _configuration = configuration;
            _logger = logger;
            _httpClientFactory = httpClientFactory;
            _zendeskMetricsService = zendeskMetricsService;

            var baseConnectionString = configuration.GetConnectionString("ZoomConnection");
            if (string.IsNullOrEmpty(baseConnectionString))
            {
                _logger.LogError("ZoomConnection connection string not found in configuration.");
                throw new InvalidOperationException("ZoomConnection connection string not found in configuration.");
            }

            // Today this points at the local ZoomWFM database, which needs no password. The
            // placeholder substitution is here so that repointing at MondayUtilization later is a
            // connection string change and nothing more - same convention EntryService uses.
            if (baseConnectionString.Contains("{0}"))
            {
                var dbPassword = configuration["Readerpass"];
                if (string.IsNullOrEmpty(dbPassword))
                {
                    _logger.LogError("Database password not found in configuration for the Zoom connection.");
                    throw new InvalidOperationException("Database password not found in configuration for the Zoom connection.");
                }

                _connectionString = baseConnectionString.Replace("{0}", dbPassword);
            }
            else
            {
                _connectionString = baseConnectionString;
            }

            _apiBaseUrl = (configuration["ZoomApi:BaseUrl"] ?? "https://localhost:7159").TrimEnd('/');
            _logger.LogInformation("Zoom schedule service configured against API at {BaseUrl}", _apiBaseUrl);
        }

        /// <summary>
        ///     Label used for agents Zoom returns without a scheduling group.
        /// </summary>
        public const string UngroupedLabel = "(No pod assigned)";

        /// <summary>
        ///     Builds the schedule tree for the selected period, either agent > date > activity or
        ///     pod > agent > date > activity, optionally narrowed to a single activity.
        /// </summary>
        /// <param name="groupBy">"group" to roll up by scheduling group, anything else by agent.</param>
        /// <param name="activity">
        ///     "name:&lt;activity name&gt;" or "type:&lt;activity type&gt;" to show only that activity.
        ///     Null, empty or "all" shows everything.
        /// </param>
        public async Task<ZoomScheduleResultViewModel> GetScheduleAsync(string filter, string userEmail,
            string groupBy = "agent", string activity = null)
        {
            if (string.IsNullOrEmpty(filter))
            {
                _logger.LogError("Filter parameter is null or empty");
                throw new ArgumentException("Filter parameter is required");
            }

            GetDateRangeFromFilter(filter, out var startDate, out var endDate);

            var entries = await GetScheduleEntriesAsync(startDate, endDate, userEmail);
            _logger.LogInformation($"Received {entries.Count} schedule rows from the database");

            var result = new ZoomScheduleResultViewModel
            {
                // Built from the unfiltered rows so the dropdown keeps offering every activity in
                // the period, not just the one currently selected.
                ActivityNames = entries
                    .Where(e => !string.IsNullOrWhiteSpace(e.ActivityName))
                    .Select(e => e.ActivityName)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(n => n)
                    .ToList(),
                ActivityTypes = entries
                    .Where(e => !string.IsNullOrWhiteSpace(e.ActivityType))
                    .Select(e => e.ActivityType)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(t => t)
                    .ToList()
            };

            var filtered = ApplyActivityFilter(entries, activity, out var activityFilterApplied);

            // % To Target is a monthly figure, so a period that straddles a boundary has to pick
            // one month. The month the period ends in is what the column header names.
            var metrics = await _zendeskMetricsService.GetAgentMetricsAsync(endDate.Year, endDate.Month);
            result.MetricsMonth = endDate.ToString("MMM");

            var byPod = string.Equals(groupBy, "group", StringComparison.OrdinalIgnoreCase);
            result.Rows = byPod
                ? BuildPodNodes(filtered, activityFilterApplied, metrics)
                : BuildAgentNodes(filtered, activityFilterApplied, metrics);

            _logger.LogInformation($"Formatted {result.Rows.Count} top level rows, grouped by {(byPod ? "pod" : "agent")}, activity filter '{activity ?? "all"}'");
            return result;
        }

        /// <summary>
        ///     Narrows the rows to one activity name or activity type. Agent days with no matching
        ///     activity drop out entirely rather than showing as empty.
        /// </summary>
        private List<ZoomScheduleEntry> ApplyActivityFilter(List<ZoomScheduleEntry> entries, string activity, out bool applied)
        {
            applied = false;

            if (string.IsNullOrWhiteSpace(activity) || activity.Equals("all", StringComparison.OrdinalIgnoreCase))
            {
                return entries;
            }

            string activityName = null;
            string activityType = null;

            if (activity.StartsWith("name:", StringComparison.OrdinalIgnoreCase))
            {
                activityName = activity.Substring("name:".Length);
            }
            else if (activity.StartsWith("type:", StringComparison.OrdinalIgnoreCase))
            {
                activityType = activity.Substring("type:".Length);
            }
            else
            {
                // Tolerate a bare activity name.
                activityName = activity;
            }

            if (string.IsNullOrWhiteSpace(activityName) && string.IsNullOrWhiteSpace(activityType))
            {
                return entries;
            }

            applied = true;

            return entries
                .Where(e => !string.IsNullOrEmpty(e.ScheduleActivityId)
                    && (activityName == null || string.Equals(e.ActivityName, activityName, StringComparison.OrdinalIgnoreCase))
                    && (activityType == null || string.Equals(e.ActivityType, activityType, StringComparison.OrdinalIgnoreCase)))
                .ToList();
        }

        /// <summary>
        ///     Rolls the agents up under their Zoom scheduling group - the pod names already shown
        ///     in the Group column when the whole team is listed.
        /// </summary>
        private List<ZoomScheduleRowViewModel> BuildPodNodes(List<ZoomScheduleEntry> entries, bool useActivityTotals,
            Dictionary<string, ZendeskAgentMetric> metrics)
        {
            return entries
                .GroupBy(e => string.IsNullOrWhiteSpace(e.SchedulingGroupName) ? UngroupedLabel : e.SchedulingGroupName)
                // Real pods first, alphabetically; anyone without one falls to the bottom.
                .OrderBy(pod => pod.Key == UngroupedLabel ? 1 : 0)
                .ThenBy(pod => pod.Key)
                .Select(pod =>
                {
                    var agents = BuildAgentNodes(pod.ToList(), useActivityTotals, metrics);
                    var podScheduled = agents.Sum(a => a.ScheduledMinutes);
                    var podPaid = agents.Sum(a => a.PaidMinutes);

                    return new ZoomScheduleRowViewModel
                    {
                        Agent = $"{pod.Key} ({agents.Count} agent{(agents.Count == 1 ? string.Empty : "s")})",
                        Scheduled = FormatMinutes(podScheduled),
                        Paid = FormatMinutes(podPaid),
                        ScheduledMinutes = podScheduled,
                        PaidMinutes = podPaid,
                        Children = agents
                    };
                })
                .ToList();
        }

        /// <summary>
        ///     Builds the agent > date > activity levels.
        /// </summary>
        /// <param name="useActivityTotals">
        ///     True when an activity filter is in play. Zoom's per day totals cover the whole day,
        ///     so once the view is narrowed to one activity they would overstate what is on screen -
        ///     add the visible activities up instead.
        /// </param>
        private List<ZoomScheduleRowViewModel> BuildAgentNodes(List<ZoomScheduleEntry> entries, bool useActivityTotals,
            Dictionary<string, ZendeskAgentMetric> metrics)
        {
            return entries
                .GroupBy(e => new { e.UserId, e.DisplayName })
                .Select(agent =>
                {
                    var days = agent.GroupBy(e => e.ScheduleDate).ToList();

                    var dayNodes = days
                        .OrderBy(d => d.Key)
                        .Select(day =>
                        {
                            var first = day.First();

                            var activityNodes = day
                                // A day with no activities still returns one row, with the
                                // activity columns null. Do not render it as a blank child.
                                .Where(a => !string.IsNullOrEmpty(a.ScheduleActivityId))
                                .OrderBy(a => a.StartTime)
                                .Select(activity =>
                                {
                                    var activityMinutes = GetDurationMinutes(activity.StartTime, activity.EndTime);

                                    return new ZoomScheduleRowViewModel
                                    {
                                        Agent = activity.ActivityName,
                                        ScheduleDate = day.Key.ToString("yyyy-MM-dd"),
                                        Activity = activity.ActivityName,
                                        ActivityType = activity.ActivityType,
                                        TimeRange = FormatTimeRange(activity.StartTime, activity.EndTime),
                                        Scheduled = FormatMinutes(activityMinutes),
                                        Paid = activity.IsPaid ? "Yes" : "No",
                                        ScheduledMinutes = activityMinutes,
                                        PaidMinutes = activity.IsPaid ? activityMinutes : 0
                                    };
                                }).ToList();

                            // Zoom reports its daily totals on the agent, repeated on every joined
                            // activity row, so take them from one row rather than summing.
                            var dayScheduled = useActivityTotals
                                ? activityNodes.Sum(a => a.ScheduledMinutes)
                                : first.TotalScheduledTime;
                            var dayPaid = useActivityTotals
                                ? activityNodes.Sum(a => a.PaidMinutes)
                                : first.TotalPaidTime;

                            return new ZoomScheduleRowViewModel
                            {
                                Agent = day.Key.ToString("dddd MM-dd"),
                                ScheduleDate = day.Key.ToString("yyyy-MM-dd"),
                                Scheduled = FormatMinutes(dayScheduled),
                                Paid = FormatMinutes(dayPaid),
                                ScheduledMinutes = dayScheduled,
                                PaidMinutes = dayPaid,
                                SchedulingGroup = first.SchedulingGroupName,
                                Children = activityNodes
                            };
                        }).ToList();

                    var agentScheduled = dayNodes.Sum(d => d.ScheduledMinutes);
                    var agentPaid = dayNodes.Sum(d => d.PaidMinutes);

                    // Joined on email rather than display name: Zendesk and Zoom do not always
                    // spell an agent the same way.
                    var agentEmail = agent.First().UserEmail;
                    string percentToTarget = null;
                    if (!string.IsNullOrEmpty(agentEmail)
                        && metrics != null
                        && metrics.TryGetValue(agentEmail, out var metric)
                        && metric.PercentToTarget.HasValue)
                    {
                        percentToTarget = (metric.PercentToTarget.Value * 100m).ToString("0.0") + "%";
                    }

                    return new ZoomScheduleRowViewModel
                    {
                        Agent = agent.Key.DisplayName,
                        Scheduled = FormatMinutes(agentScheduled),
                        Paid = FormatMinutes(agentPaid),
                        ScheduledMinutes = agentScheduled,
                        PaidMinutes = agentPaid,
                        SchedulingGroup = agent.First().SchedulingGroupName,
                        PercentToTarget = percentToTarget,
                        Children = dayNodes
                    };
                })
                .OrderBy(a => a.Agent)
                .ToList();
        }

        /// <summary>
        ///     Zoom WFM activities that count toward the team's OTG total. Matched ignoring case,
        ///     spaces and punctuation, so "OTG Billable" also matches Zoom's "OTG - Billable".
        /// </summary>
        private static readonly string[] OtgActivityNames =
        {
            "Instructing",
            "Mentoring",
            "Professional Dev",
            "Project",
            "KCS",
            "OTG",
            "OTG Billable"
        };

        private static readonly HashSet<string> NormalizedOtgActivityNames =
            new HashSet<string>(OtgActivityNames.Select(NormalizeActivityName));

        private static string NormalizeActivityName(string name) =>
            new string((name ?? string.Empty).Where(char.IsLetterOrDigit).ToArray()).ToUpperInvariant();

        /// <summary>
        ///     Minutes of OTG time scheduled across the whole team today: the activities in
        ///     <see cref="OtgActivityNames"/>, using the same "today" as the Today filter.
        /// </summary>
        public async Task<int> GetOtgMinutesTodayAsync()
        {
            var today = DateTime.Now.Date;
            var entries = await GetScheduleEntriesAsync(today, today, null);

            return entries
                .Where(e => !string.IsNullOrEmpty(e.ScheduleActivityId)
                            && NormalizedOtgActivityNames.Contains(NormalizeActivityName(e.ActivityName)))
                .Sum(e => GetDurationMinutes(e.StartTime, e.EndTime));
        }

        public async Task<List<ZoomScheduleEntry>> GetScheduleEntriesAsync(DateTime startDate, DateTime endDate, string userEmail)
        {
            var entries = new List<ZoomScheduleEntry>();
            _logger.LogInformation($"GetScheduleEntriesAsync called with startDate: {startDate:yyyy-MM-dd}, endDate: {endDate:yyyy-MM-dd}, userEmail: {userEmail}");

            try
            {
                using (var connection = new SqlConnection(_connectionString))
                {
                    await connection.OpenAsync();
                    _logger.LogInformation("Zoom schedule database connection successful.");

                    using (var command = new SqlCommand("GetZoomSchedule", connection))
                    {
                        command.CommandType = System.Data.CommandType.StoredProcedure;
                        command.Parameters.AddWithValue("@StartDate", startDate.Date);
                        command.Parameters.AddWithValue("@EndDate", endDate.Date);
                        command.Parameters.AddWithValue("@UserEmail",
                            !string.IsNullOrEmpty(userEmail) ? (object)userEmail : DBNull.Value);

                        using (var reader = await command.ExecuteReaderAsync())
                        {
                            while (await reader.ReadAsync())
                            {
                                try
                                {
                                    entries.Add(new ZoomScheduleEntry
                                    {
                                        ScheduleDate = reader.GetDateTime(reader.GetOrdinal("ScheduleDate")),
                                        UserId = GetNullableString(reader, "UserId"),
                                        DisplayName = GetNullableString(reader, "DisplayName"),
                                        UserEmail = GetNullableString(reader, "UserEmail"),
                                        SchedulingGroupName = GetNullableString(reader, "SchedulingGroupName"),
                                        EmployeeId = GetNullableString(reader, "EmployeeId"),
                                        TotalScheduledTime = reader.GetInt32(reader.GetOrdinal("TotalScheduledTime")),
                                        TotalPaidTime = reader.GetInt32(reader.GetOrdinal("TotalPaidTime")),
                                        Timezone = GetNullableString(reader, "Timezone"),
                                        ScheduleActivityId = GetNullableString(reader, "ScheduleActivityId"),
                                        ActivityName = GetNullableString(reader, "ActivityName"),
                                        ActivityType = GetNullableString(reader, "ActivityType"),
                                        StartTime = GetNullableDateTimeOffset(reader, "StartTime"),
                                        EndTime = GetNullableDateTimeOffset(reader, "EndTime"),
                                        IsPaid = !reader.IsDBNull(reader.GetOrdinal("IsPaid")) && reader.GetBoolean(reader.GetOrdinal("IsPaid"))
                                    });
                                }
                                catch (Exception ex)
                                {
                                    _logger.LogError($"Error reading schedule row: {ex.Message}");
                                }
                            }
                        }
                    }
                }
                _logger.LogInformation($"Retrieved {entries.Count} schedule rows from database");
            }
            catch (Exception ex)
            {
                _logger.LogError($"Zoom schedule database connection error: {ex.Message}");
            }

            return entries;
        }

        /// <summary>
        ///     When the schedule was last synced from Zoom, in UTC.
        /// </summary>
        public async Task<DateTime?> GetLastSyncAsync()
        {
            try
            {
                using (var connection = new SqlConnection(_connectionString))
                {
                    await connection.OpenAsync();
                    using (var command = new SqlCommand("GetZoomScheduleLastSync", connection))
                    {
                        command.CommandType = System.Data.CommandType.StoredProcedure;
                        using (var reader = await command.ExecuteReaderAsync())
                        {
                            if (await reader.ReadAsync())
                            {
                                var ordinal = reader.GetOrdinal("LastSyncTime");
                                if (!reader.IsDBNull(ordinal))
                                {
                                    return reader.GetDateTime(ordinal);
                                }
                            }
                        }
                    }
                }
                return null;
            }
            catch (Exception ex)
            {
                _logger.LogError($"Error getting last Zoom sync time: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        ///     Asks Zoom-REST-API to pull the schedule from Zoom and write it to SQL. The Zoom
        ///     credentials live in that service, so the page never holds them.
        /// </summary>
        public async Task<ZoomSyncResultViewModel> SyncAsync(DateTime? from = null, DateTime? to = null)
        {
            var query = new List<string>();
            if (from.HasValue) { query.Add($"from={from.Value:yyyy-MM-dd}"); }
            if (to.HasValue) { query.Add($"to={to.Value:yyyy-MM-dd}"); }

            var url = $"{_apiBaseUrl}/Zoom/SyncSchedule";
            if (query.Count > 0) { url += "?" + string.Join("&", query); }

            _logger.LogInformation("Requesting Zoom schedule sync: {Url}", url);

            try
            {
                var client = _httpClientFactory.CreateClient();

                // A wide date range is many sequential Zoom calls, ten agents at a time.
                client.Timeout = TimeSpan.FromMinutes(10);

                var response = await client.PostAsync(url, null);
                var content = await response.Content.ReadAsStringAsync();

                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogError("Zoom sync failed with {StatusCode}: {Body}", (int)response.StatusCode, content);
                    return new ZoomSyncResultViewModel
                    {
                        Success = false,
                        Message = $"The sync service returned {(int)response.StatusCode}."
                    };
                }

                using var document = JsonDocument.Parse(content);
                var root = document.RootElement;

                var result = new ZoomSyncResultViewModel
                {
                    Success = ReadBoolean(root, "success"),
                    HasErrors = ReadBoolean(root, "hasErrors"),
                    StartDate = ReadString(root, "startDate"),
                    EndDate = ReadString(root, "endDate"),
                    DayCount = ReadInt32(root, "dayCount"),
                    AgentCount = ReadInt32(root, "agentCount"),
                    ActivityCount = ReadInt32(root, "activityCount")
                };

                result.Message = result.Success
                    ? $"Synced {result.DayCount} day(s): {result.AgentCount} agent record(s), {result.ActivityCount} activity record(s)."
                      + (result.HasErrors ? " Some dates failed - see the API logs." : string.Empty)
                    : "The sync did not complete. Check the API logs.";

                _logger.LogInformation("Zoom sync result: {Message}", result.Message);
                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError($"Error running the Zoom schedule sync: {ex.Message}");
                return new ZoomSyncResultViewModel
                {
                    Success = false,
                    Message = "Could not reach the Zoom sync service."
                };
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

        /// <summary>
        ///     Zoom reports schedule durations in minutes; the rest of the page reads in hours and
        ///     minutes, so match that.
        /// </summary>
        private string FormatMinutes(int minutes)
        {
            if (minutes <= 0)
            {
                return "0m";
            }

            var hours = minutes / 60;
            var remainder = minutes % 60;

            if (hours == 0)
            {
                return $"{remainder}m";
            }

            return remainder == 0 ? $"{hours}h" : $"{hours}h {remainder}m";
        }

        private int GetDurationMinutes(DateTimeOffset? start, DateTimeOffset? end)
        {
            if (!start.HasValue || !end.HasValue)
            {
                return 0;
            }

            return (int)Math.Round((end.Value - start.Value).TotalMinutes);
        }

        private string FormatTimeRange(DateTimeOffset? start, DateTimeOffset? end)
        {
            if (!start.HasValue || !end.HasValue)
            {
                return string.Empty;
            }

            // Zoom returns each time with the schedule's own offset, so render it as stored rather
            // than converting to the web server's local zone.
            return $"{start.Value:h:mm tt} - {end.Value:h:mm tt}";
        }

        private static string GetNullableString(SqlDataReader reader, string column)
        {
            var ordinal = reader.GetOrdinal(column);
            return reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
        }

        private static DateTimeOffset? GetNullableDateTimeOffset(SqlDataReader reader, string column)
        {
            var ordinal = reader.GetOrdinal(column);
            return reader.IsDBNull(ordinal) ? (DateTimeOffset?)null : reader.GetDateTimeOffset(ordinal);
        }

        private static bool ReadBoolean(JsonElement root, string property)
        {
            return root.TryGetProperty(property, out var value)
                && (value.ValueKind == JsonValueKind.True || value.ValueKind == JsonValueKind.False)
                && value.GetBoolean();
        }

        private static int ReadInt32(JsonElement root, string property)
        {
            return root.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Number
                ? value.GetInt32()
                : 0;
        }

        private static string ReadString(JsonElement root, string property)
        {
            return root.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;
        }
    }
}
