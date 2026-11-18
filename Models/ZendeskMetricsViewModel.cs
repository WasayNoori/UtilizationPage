using System;
using System.Collections.Generic;
using System.Linq;

namespace UtilizationPage_ASP.Services
{
    /// <summary>
    ///     One parsed CSV row, ready for the table valued parameter. Every metric is nullable:
    ///     Explore leaves cells blank when an agent had no qualifying tickets, and a blank must
    ///     stay a blank rather than becoming a zero.
    /// </summary>
    public class ZendeskMetricRow
    {
        public int MetricYear { get; set; }
        public byte MetricMonth { get; set; }
        public string AgentName { get; set; }

        public decimal? PercentToTarget { get; set; }
        public decimal? PercentToTargetYtd { get; set; }
        public decimal? CsatPct { get; set; }
        public decimal? CsatPctYtd { get; set; }
        public int? Solves { get; set; }
        public int? SolvesYtd { get; set; }
        public decimal? FrtPct { get; set; }
        public decimal? FrtPctYtd { get; set; }
        public decimal? AwtPct { get; set; }
        public decimal? AwtPctYtd { get; set; }
        public decimal? RwtPct { get; set; }
        public decimal? RwtPctYtd { get; set; }
        public int? GoodSurveys { get; set; }
        public int? BadSurveys { get; set; }
        public decimal? InPodPct { get; set; }
        public decimal? InPodPctYtd { get; set; }
        public decimal? OttPctYtd { get; set; }
        public decimal? AvgFirstReply { get; set; }
        public decimal? AvgReqWait { get; set; }
        public decimal? AvgOnHold { get; set; }
        public decimal? AvgWorkTime { get; set; }
        public decimal? AvgResolution { get; set; }
    }

    /// <summary>
    ///     What one month's worth of an import actually did. Counts are for % To Target, the
    ///     only metric both exports carry.
    /// </summary>
    public class ZendeskMonthChange
    {
        public int Month { get; set; }
        public string MonthName { get; set; }
        public int Inserted { get; set; }
        public int Changed { get; set; }
        public int Unchanged { get; set; }

        public int Total => Inserted + Changed + Unchanged;
    }

    /// <summary>
    ///     What an import produced, shown back on the upload page.
    /// </summary>
    public class ZendeskImportResult
    {
        public bool Success { get; set; }
        public string FileKind { get; set; }
        public string FileName { get; set; }
        public int RowsParsed { get; set; }
        public int RowsUpserted { get; set; }
        public List<string> MonthsCovered { get; set; } = new List<string>();
        public List<string> UnmatchedAgents { get; set; } = new List<string>();
        public List<ZendeskMonthChange> MonthChanges { get; set; } = new List<ZendeskMonthChange>();
        public string Message { get; set; }

        /// <summary>
        ///     True when the file moved no numbers at all - the signal that the same export has
        ///     been imported twice.
        /// </summary>
        public bool NothingChanged => MonthChanges.Count > 0
                                      && MonthChanges.All(m => m.Inserted == 0 && m.Changed == 0);
    }

    /// <summary>
    ///     A stored metrics row read back for display, already resolved to a Zoom agent.
    /// </summary>
    public class ZendeskAgentMetric
    {
        public string AgentName { get; set; }
        public string ResolvedEmail { get; set; }
        public decimal? PercentToTarget { get; set; }
        public decimal? PercentToTargetYtd { get; set; }
        public decimal? CsatPct { get; set; }
        public decimal? CsatPctYtd { get; set; }
        public int? Solves { get; set; }
        public int? SolvesYtd { get; set; }
        public decimal? FrtPct { get; set; }
        public decimal? FrtPctYtd { get; set; }
        public decimal? AwtPct { get; set; }
        public decimal? AwtPctYtd { get; set; }
        public decimal? RwtPct { get; set; }
        public decimal? RwtPctYtd { get; set; }
        public DateTime ImportedAt { get; set; }
    }
}
