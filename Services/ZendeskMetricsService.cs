using System;
using System.Collections.Generic;
using System.Data;
using System.Threading.Tasks;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace UtilizationPage_ASP.Services
{
    /// <summary>
    ///     Reads the imported Zendesk metrics for display. Keyed by the resolved Zoom agent
    ///     email so the WFM schedule rows can be joined without relying on display names.
    /// </summary>
    public class ZendeskMetricsService
    {
        private readonly string _connectionString;
        private readonly ILogger<ZendeskMetricsService> _logger;

        public ZendeskMetricsService(IConfiguration configuration, ILogger<ZendeskMetricsService> logger)
        {
            _logger = logger;

            var baseConnectionString = configuration.GetConnectionString("DefaultConnection");
            if (string.IsNullOrEmpty(baseConnectionString))
            {
                throw new InvalidOperationException("DefaultConnection connection string not found in configuration.");
            }

            if (baseConnectionString.Contains("{0}"))
            {
                var dbPassword = configuration["Readerpass"];
                if (string.IsNullOrEmpty(dbPassword))
                {
                    throw new InvalidOperationException("Readerpass not found in configuration.");
                }

                _connectionString = baseConnectionString.Replace("{0}", dbPassword);
            }
            else
            {
                _connectionString = baseConnectionString;
            }
        }

        /// <summary>
        ///     Metrics for one month, keyed by lower-cased resolved email. Agents that resolve to
        ///     no Zoom account are skipped - their rows stay in the database and start appearing
        ///     as soon as an alias is added.
        /// </summary>
        public async Task<Dictionary<string, ZendeskAgentMetric>> GetAgentMetricsAsync(int year, int month)
        {
            var metrics = new Dictionary<string, ZendeskAgentMetric>(StringComparer.OrdinalIgnoreCase);

            try
            {
                using (var connection = new SqlConnection(_connectionString))
                {
                    await connection.OpenAsync();

                    using (var command = new SqlCommand("GetZendeskAgentMetrics", connection))
                    {
                        command.CommandType = CommandType.StoredProcedure;
                        command.Parameters.AddWithValue("@MetricYear", year);
                        command.Parameters.AddWithValue("@MetricMonth", (byte)month);

                        using (var reader = await command.ExecuteReaderAsync())
                        {
                            while (await reader.ReadAsync())
                            {
                                var email = GetNullableString(reader, "ResolvedEmail");
                                if (string.IsNullOrEmpty(email)) { continue; }

                                metrics[email] = new ZendeskAgentMetric
                                {
                                    AgentName = GetNullableString(reader, "AgentName"),
                                    ResolvedEmail = email,
                                    PercentToTarget = GetNullableDecimal(reader, "PercentToTarget"),
                                    PercentToTargetYtd = GetNullableDecimal(reader, "PercentToTargetYtd"),
                                    CsatPct = GetNullableDecimal(reader, "CsatPct"),
                                    CsatPctYtd = GetNullableDecimal(reader, "CsatPctYtd"),
                                    Solves = GetNullableInt(reader, "Solves"),
                                    SolvesYtd = GetNullableInt(reader, "SolvesYtd"),
                                    FrtPct = GetNullableDecimal(reader, "FrtPct"),
                                    FrtPctYtd = GetNullableDecimal(reader, "FrtPctYtd"),
                                    AwtPct = GetNullableDecimal(reader, "AwtPct"),
                                    AwtPctYtd = GetNullableDecimal(reader, "AwtPctYtd"),
                                    RwtPct = GetNullableDecimal(reader, "RwtPct"),
                                    RwtPctYtd = GetNullableDecimal(reader, "RwtPctYtd"),
                                    ImportedAt = reader.GetDateTime(reader.GetOrdinal("ImportedAt"))
                                };
                            }
                        }
                    }
                }

                _logger.LogInformation($"Loaded {metrics.Count} Zendesk agent metric rows for {year}-{month:00}");
            }
            catch (Exception ex)
            {
                // A metrics failure must not take the schedule down with it.
                _logger.LogError($"Error loading Zendesk agent metrics: {ex.Message}");
            }

            return metrics;
        }

        /// <summary>
        ///     The most recent month with imported data, for the staleness indicator.
        /// </summary>
        public async Task<(int Year, int Month, DateTime ImportedAt)?> GetLatestImportAsync()
        {
            try
            {
                using (var connection = new SqlConnection(_connectionString))
                {
                    await connection.OpenAsync();
                    using (var command = new SqlCommand("GetZendeskLatestImport", connection))
                    {
                        command.CommandType = CommandType.StoredProcedure;
                        using (var reader = await command.ExecuteReaderAsync())
                        {
                            if (await reader.ReadAsync())
                            {
                                return (reader.GetInt32(0), Convert.ToInt32(reader.GetValue(1)), reader.GetDateTime(2));
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError($"Error reading the latest Zendesk import: {ex.Message}");
            }

            return null;
        }

        private static string GetNullableString(SqlDataReader reader, string column)
        {
            var ordinal = reader.GetOrdinal(column);
            return reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
        }

        private static decimal? GetNullableDecimal(SqlDataReader reader, string column)
        {
            var ordinal = reader.GetOrdinal(column);
            return reader.IsDBNull(ordinal) ? (decimal?)null : reader.GetDecimal(ordinal);
        }

        private static int? GetNullableInt(SqlDataReader reader, string column)
        {
            var ordinal = reader.GetOrdinal(column);
            return reader.IsDBNull(ordinal) ? (int?)null : reader.GetInt32(ordinal);
        }
    }
}
