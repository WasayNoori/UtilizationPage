using System;
using System.Collections.Generic;
using System.Data;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace UtilizationPage_ASP.Services
{
    /// <summary>
    ///     Parses the two Zendesk Explore CSV exports and upserts them into
    ///     dbo.ZendeskAgentMetrics.
    ///
    ///     Both files are semicolon-delimited with quoted fields and values carrying 20 decimal
    ///     places, so every number is parsed with InvariantCulture - a comma-decimal locale
    ///     would silently mangle them.
    /// </summary>
    public class ZendeskImportService
    {
        public const string FileKindByMonth = "ByMonth";
        public const string FileKindAgentTable = "AgentTable";

        private readonly string _connectionString;
        private readonly ILogger<ZendeskImportService> _logger;

        public ZendeskImportService(IConfiguration configuration, ILogger<ZendeskImportService> logger)
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

        public async Task<ZendeskImportResult> ImportAsync(Stream csvStream, string fileName, int year, int month, string importedBy)
        {
            var result = new ZendeskImportResult { FileName = fileName };

            List<string> lines;
            using (var reader = new StreamReader(csvStream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true))
            {
                var content = await reader.ReadToEndAsync();
                lines = content
                    .Replace("\r\n", "\n")
                    .Split('\n')
                    .Where(l => !string.IsNullOrWhiteSpace(l))
                    .ToList();
            }

            if (lines.Count < 2)
            {
                result.Message = "The file has no data rows.";
                return result;
            }

            var headers = SplitCsvLine(lines[0]);
            result.FileKind = DetectFileKind(headers);

            if (result.FileKind == null)
            {
                result.Message = "Unrecognised file. Expected the \"Agent % To Target By Month\" or \"Agent Table\" export.";
                return result;
            }

            var rows = result.FileKind == FileKindByMonth
                ? ParseByMonth(headers, lines, year)
                : ParseAgentTable(headers, lines, year, month);

            result.RowsParsed = rows.Count;
            result.MonthsCovered = rows
                .Select(r => new DateTime(r.MetricYear, r.MetricMonth, 1).ToString("MMM yyyy", CultureInfo.InvariantCulture))
                .Distinct()
                .ToList();

            if (rows.Count == 0)
            {
                result.Message = "No usable rows found in the file.";
                return result;
            }

            result.MonthChanges = await UpsertAsync(rows, importedBy, fileName, result.FileKind);
            result.RowsUpserted = result.MonthChanges.Sum(m => m.Total);

            // Report unmatched names for every month the file touched, so a new agent or a
            // changed name surfaces at import time rather than as a blank column weeks later.
            foreach (var (y, m) in rows.Select(r => (r.MetricYear, (int)r.MetricMonth)).Distinct())
            {
                result.UnmatchedAgents.AddRange(await GetUnmatchedAsync(y, m));
            }
            result.UnmatchedAgents = result.UnmatchedAgents.Distinct().OrderBy(n => n).ToList();

            await LogImportAsync(result, year, month, importedBy);

            result.Success = true;
            result.Message = BuildChangeSummary(result);
            _logger.LogInformation("Zendesk import: {Message}", result.Message);

            return result;
        }

        /// <summary>
        ///     Reports what moved, not just what was read. "400 rows upserted" looks identical
        ///     whether the file was new or a duplicate of the last one; a changed count does not.
        /// </summary>
        private static string BuildChangeSummary(ZendeskImportResult result)
        {
            var totalNew = result.MonthChanges.Sum(m => m.Inserted);
            var totalChanged = result.MonthChanges.Sum(m => m.Changed);

            if (result.NothingChanged)
            {
                return $"Read {result.RowsParsed} row(s), but no % To Target value changed. "
                     + "This looks like a file that has already been imported - check you picked the newest export.";
            }

            var detail = result.MonthChanges
                .Where(m => m.Inserted > 0 || m.Changed > 0)
                .Select(m =>
                {
                    var parts = new List<string>();
                    if (m.Changed > 0) { parts.Add($"{m.Changed} changed"); }
                    if (m.Inserted > 0) { parts.Add($"{m.Inserted} new"); }
                    return $"{m.MonthName}: {string.Join(", ", parts)}";
                })
                .ToList();

            var headline = $"Imported {result.RowsParsed} row(s) covering {string.Join(", ", result.MonthsCovered)}. "
                         + $"{totalChanged} % To Target value(s) changed, {totalNew} added.";

            return detail.Count > 0 ? $"{headline}  ({string.Join("; ", detail)})" : headline;
        }

        private static string DetectFileKind(List<string> headers)
        {
            if (headers.Any(h => h.Equals("Ticket solved - Month", StringComparison.OrdinalIgnoreCase)))
            {
                return FileKindByMonth;
            }

            if (headers.Any(h => h.Equals("% To Target, MTD", StringComparison.OrdinalIgnoreCase)))
            {
                return FileKindAgentTable;
            }

            return null;
        }

        /// <summary>
        ///     "Agent % To Target By Month": one % To Target per agent per month. The month comes
        ///     from the row itself, so only the year is taken from the upload form. Quarterly AVG
        ///     rows are skipped - Explore computes those from quarter-level aggregates, not by
        ///     averaging the months, so they are not a value we can store per month.
        /// </summary>
        private List<ZendeskMetricRow> ParseByMonth(List<string> headers, List<string> lines, int year)
        {
            var rows = new List<ZendeskMetricRow>();

            int nameIdx = IndexOf(headers, "Assignee name");
            int monthIdx = IndexOf(headers, "Ticket solved - Month");
            int valueIdx = IndexOf(headers, "% To Target");

            if (nameIdx < 0 || monthIdx < 0 || valueIdx < 0)
            {
                return rows;
            }

            foreach (var line in lines.Skip(1))
            {
                var fields = SplitCsvLine(line);
                if (fields.Count <= Math.Max(nameIdx, Math.Max(monthIdx, valueIdx))) { continue; }

                var monthName = fields[monthIdx];
                if (monthName.Equals("AVG", StringComparison.OrdinalIgnoreCase)) { continue; }

                var month = ParseMonthName(monthName);
                if (month == null) { continue; }

                // A blank month is emitted as null rather than skipped. Skipping it would mean a
                // corrected export could never clear a value written by an earlier, premature
                // upload - the row would simply not be in the batch.
                rows.Add(new ZendeskMetricRow
                {
                    MetricYear = year,
                    MetricMonth = month.Value,
                    AgentName = fields[nameIdx].Trim(),
                    PercentToTarget = ParseDecimal(fields[valueIdx])
                });
            }

            return rows;
        }

        /// <summary>
        ///     "Agent Table": a snapshot of every component metric, MTD and YTD, for one month.
        ///     The month comes from the upload form because the file does not carry it.
        /// </summary>
        private List<ZendeskMetricRow> ParseAgentTable(List<string> headers, List<string> lines, int year, int month)
        {
            var rows = new List<ZendeskMetricRow>();
            int nameIdx = IndexOf(headers, "Assignee name");
            if (nameIdx < 0) { return rows; }

            foreach (var line in lines.Skip(1))
            {
                var fields = SplitCsvLine(line);
                if (fields.Count <= nameIdx) { continue; }

                var name = fields[nameIdx].Trim();
                if (string.IsNullOrEmpty(name)) { continue; }

                rows.Add(new ZendeskMetricRow
                {
                    MetricYear = year,
                    MetricMonth = (byte)month,
                    AgentName = name,
                    PercentToTarget    = Dec(headers, fields, "% To Target, MTD"),
                    PercentToTargetYtd = Dec(headers, fields, "% To Target, YTD"),
                    CsatPct            = Dec(headers, fields, "CSAT, MTD"),
                    CsatPctYtd         = Dec(headers, fields, "CSAT, YTD"),
                    Solves             = Int(headers, fields, "Solves, MTD"),
                    SolvesYtd          = Int(headers, fields, "Solves, YTD"),
                    FrtPct             = Dec(headers, fields, "FRT %, MTD"),
                    FrtPctYtd          = Dec(headers, fields, "FRT %, YTD"),
                    AwtPct             = Dec(headers, fields, "AWT %, MTD"),
                    AwtPctYtd          = Dec(headers, fields, "AWT %, YTD"),
                    RwtPct             = Dec(headers, fields, "RWT %, MTD"),
                    RwtPctYtd          = Dec(headers, fields, "RWT %, YTD"),
                    GoodSurveys        = Int(headers, fields, "Good Surveys"),
                    BadSurveys         = Int(headers, fields, "Bad Surveys"),
                    InPodPct           = Dec(headers, fields, "In Pod, MTD"),
                    InPodPctYtd        = Dec(headers, fields, "In Pod, YTD"),
                    OttPctYtd          = Dec(headers, fields, "OTT, YTD"),
                    AvgFirstReply      = Dec(headers, fields, "Avg First Reply"),
                    AvgReqWait         = Dec(headers, fields, "Avg Req Wait"),
                    AvgOnHold          = Dec(headers, fields, "Avg On-Hold"),
                    AvgWorkTime        = Dec(headers, fields, "Avg Work Time"),
                    AvgResolution      = Dec(headers, fields, "Avg Resolution")
                });
            }

            return rows;
        }

        private async Task<List<ZendeskMonthChange>> UpsertAsync(List<ZendeskMetricRow> rows, string importedBy,
            string fileName, string fileKind)
        {
            var changes = new List<ZendeskMonthChange>();

            using (var connection = new SqlConnection(_connectionString))
            {
                await connection.OpenAsync();

                using (var command = new SqlCommand("sp_UpsertZendeskAgentMetrics", connection))
                {
                    command.CommandType = CommandType.StoredProcedure;
                    command.CommandTimeout = 180;

                    var tvp = command.Parameters.AddWithValue("@Metrics", ToMetricTable(rows));
                    tvp.SqlDbType = SqlDbType.Structured;
                    tvp.TypeName = "dbo.ZendeskAgentMetricType";

                    command.Parameters.AddWithValue("@ImportedBy", (object)importedBy ?? DBNull.Value);
                    command.Parameters.AddWithValue("@SourceFile", (object)fileName ?? DBNull.Value);
                    command.Parameters.AddWithValue("@FileKind", (object)fileKind ?? DBNull.Value);

                    using (var reader = await command.ExecuteReaderAsync())
                    {
                        while (await reader.ReadAsync())
                        {
                            var month = Convert.ToInt32(reader["MetricMonth"]);
                            changes.Add(new ZendeskMonthChange
                            {
                                Month = month,
                                MonthName = new DateTime(2000, month, 1).ToString("MMM", CultureInfo.InvariantCulture),
                                Inserted = Convert.ToInt32(reader["Inserted"]),
                                Changed = Convert.ToInt32(reader["Changed"]),
                                Unchanged = Convert.ToInt32(reader["Unchanged"])
                            });
                        }
                    }
                }
            }

            return changes;
        }

        private async Task<List<string>> GetUnmatchedAsync(int year, int month)
        {
            var names = new List<string>();

            using (var connection = new SqlConnection(_connectionString))
            {
                await connection.OpenAsync();
                using (var command = new SqlCommand("GetZendeskUnmatchedAgents", connection))
                {
                    command.CommandType = CommandType.StoredProcedure;
                    command.Parameters.AddWithValue("@MetricYear", year);
                    command.Parameters.AddWithValue("@MetricMonth", (byte)month);

                    using (var reader = await command.ExecuteReaderAsync())
                    {
                        while (await reader.ReadAsync())
                        {
                            names.Add(reader.GetString(0));
                        }
                    }
                }
            }

            return names;
        }

        private async Task LogImportAsync(ZendeskImportResult result, int year, int month, string importedBy)
        {
            try
            {
                using (var connection = new SqlConnection(_connectionString))
                {
                    await connection.OpenAsync();
                    using (var command = new SqlCommand("sp_LogZendeskImport", connection))
                    {
                        command.CommandType = CommandType.StoredProcedure;
                        command.Parameters.AddWithValue("@FileName", (object)result.FileName ?? DBNull.Value);
                        command.Parameters.AddWithValue("@FileKind", (object)result.FileKind ?? DBNull.Value);
                        command.Parameters.AddWithValue("@MetricYear", year);
                        command.Parameters.AddWithValue("@MetricMonth", (byte)month);
                        command.Parameters.AddWithValue("@RowsParsed", result.RowsParsed);
                        command.Parameters.AddWithValue("@RowsUpserted", result.RowsUpserted);
                        command.Parameters.AddWithValue("@UnmatchedAgents",
                            result.UnmatchedAgents.Count > 0 ? (object)string.Join(", ", result.UnmatchedAgents) : DBNull.Value);
                        command.Parameters.AddWithValue("@ImportedBy", (object)importedBy ?? DBNull.Value);

                        await command.ExecuteNonQueryAsync();
                    }
                }
            }
            catch (Exception ex)
            {
                // An audit row failing must not fail the import itself.
                _logger.LogError($"Could not write the Zendesk import audit row: {ex.Message}");
            }
        }

        // Column ORDER must match dbo.ZendeskAgentMetricType in ZendeskMetrics.sql - a table
        // valued parameter binds by position.
        private DataTable ToMetricTable(IEnumerable<ZendeskMetricRow> rows)
        {
            var table = new DataTable();
            table.Columns.Add("MetricYear", typeof(int));
            table.Columns.Add("MetricMonth", typeof(byte));
            table.Columns.Add("AgentName", typeof(string));
            table.Columns.Add("PercentToTarget", typeof(decimal));
            table.Columns.Add("PercentToTargetYtd", typeof(decimal));
            table.Columns.Add("CsatPct", typeof(decimal));
            table.Columns.Add("CsatPctYtd", typeof(decimal));
            table.Columns.Add("Solves", typeof(int));
            table.Columns.Add("SolvesYtd", typeof(int));
            table.Columns.Add("FrtPct", typeof(decimal));
            table.Columns.Add("FrtPctYtd", typeof(decimal));
            table.Columns.Add("AwtPct", typeof(decimal));
            table.Columns.Add("AwtPctYtd", typeof(decimal));
            table.Columns.Add("RwtPct", typeof(decimal));
            table.Columns.Add("RwtPctYtd", typeof(decimal));
            table.Columns.Add("GoodSurveys", typeof(int));
            table.Columns.Add("BadSurveys", typeof(int));
            table.Columns.Add("InPodPct", typeof(decimal));
            table.Columns.Add("InPodPctYtd", typeof(decimal));
            table.Columns.Add("OttPctYtd", typeof(decimal));
            table.Columns.Add("AvgFirstReply", typeof(decimal));
            table.Columns.Add("AvgReqWait", typeof(decimal));
            table.Columns.Add("AvgOnHold", typeof(decimal));
            table.Columns.Add("AvgWorkTime", typeof(decimal));
            table.Columns.Add("AvgResolution", typeof(decimal));

            foreach (var r in rows)
            {
                table.Rows.Add(
                    r.MetricYear, r.MetricMonth, r.AgentName,
                    N(r.PercentToTarget), N(r.PercentToTargetYtd), N(r.CsatPct), N(r.CsatPctYtd),
                    N(r.Solves), N(r.SolvesYtd),
                    N(r.FrtPct), N(r.FrtPctYtd), N(r.AwtPct), N(r.AwtPctYtd), N(r.RwtPct), N(r.RwtPctYtd),
                    N(r.GoodSurveys), N(r.BadSurveys),
                    N(r.InPodPct), N(r.InPodPctYtd), N(r.OttPctYtd),
                    N(r.AvgFirstReply), N(r.AvgReqWait), N(r.AvgOnHold), N(r.AvgWorkTime), N(r.AvgResolution));
            }

            return table;
        }

        private static object N<T>(T? value) where T : struct
            => value.HasValue ? (object)value.Value : DBNull.Value;

        private static int IndexOf(List<string> headers, string name)
            => headers.FindIndex(h => h.Equals(name, StringComparison.OrdinalIgnoreCase));

        private static decimal? Dec(List<string> headers, List<string> fields, string header)
        {
            int idx = IndexOf(headers, header);
            return idx >= 0 && idx < fields.Count ? ParseDecimal(fields[idx]) : null;
        }

        private static int? Int(List<string> headers, List<string> fields, string header)
        {
            var value = Dec(headers, fields, header);
            return value.HasValue ? (int?)Math.Round(value.Value, MidpointRounding.AwayFromZero) : null;
        }

        /// <summary>
        ///     Blank stays null rather than becoming zero. Explore's own dashboard treats a blank
        ///     component as zero when averaging, which drags low-volume agents down; we keep the
        ///     honest value and let the display decide.
        /// </summary>
        private static decimal? ParseDecimal(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) { return null; }

            return decimal.TryParse(raw.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
                ? value
                : (decimal?)null;
        }

        private static byte? ParseMonthName(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) { return null; }

            if (DateTime.TryParseExact(raw.Trim(), "MMMM", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed) ||
                DateTime.TryParseExact(raw.Trim(), "MMM", CultureInfo.InvariantCulture, DateTimeStyles.None, out parsed))
            {
                return (byte)parsed.Month;
            }

            return null;
        }

        /// <summary>
        ///     Splits one semicolon-delimited line, honouring quoted fields and doubled quotes.
        /// </summary>
        private static List<string> SplitCsvLine(string line)
        {
            var fields = new List<string>();
            var current = new StringBuilder();
            bool inQuotes = false;

            for (int i = 0; i < line.Length; i++)
            {
                char c = line[i];

                if (c == '"')
                {
                    if (inQuotes && i + 1 < line.Length && line[i + 1] == '"')
                    {
                        current.Append('"');
                        i++;
                    }
                    else
                    {
                        inQuotes = !inQuotes;
                    }
                }
                else if (c == ';' && !inQuotes)
                {
                    fields.Add(current.ToString());
                    current.Clear();
                }
                else
                {
                    current.Append(c);
                }
            }

            fields.Add(current.ToString());
            return fields;
        }
    }
}
