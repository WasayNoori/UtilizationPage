using System;
using System.Collections.Generic;

namespace UtilizationPage_ASP.Services
{
    /// <summary>
    ///     One flattened agent + activity row as read back from the ZoomWFM database.
    /// </summary>
    public class ZoomScheduleEntry
    {
        public DateTime ScheduleDate { get; set; }
        public string UserId { get; set; }
        public string DisplayName { get; set; }
        public string UserEmail { get; set; }
        public string SchedulingGroupName { get; set; }
        public string EmployeeId { get; set; }
        public int TotalScheduledTime { get; set; }
        public int TotalPaidTime { get; set; }
        public string Timezone { get; set; }
        public string ScheduleActivityId { get; set; }
        public string ActivityName { get; set; }
        public string ActivityType { get; set; }
        public DateTimeOffset? StartTime { get; set; }
        public DateTimeOffset? EndTime { get; set; }
        public bool IsPaid { get; set; }
    }

    /// <summary>
    ///     A node in the agent > date > activity tree the WFM Schedule tab renders. Mirrors
    ///     TableEntryViewModel, which drives the utilization table on the same page.
    /// </summary>
    public class ZoomScheduleRowViewModel
    {
        public string Agent { get; set; }
        public string ScheduleDate { get; set; }
        public string Activity { get; set; }
        public string ActivityType { get; set; }
        public string TimeRange { get; set; }
        public string Scheduled { get; set; }
        public string Paid { get; set; }

        // Raw minutes alongside the formatted strings, so the page can total a column without
        // parsing "8h 30m" back apart.
        public int ScheduledMinutes { get; set; }
        public int PaidMinutes { get; set; }

        public string SchedulingGroup { get; set; }
        public List<ZoomScheduleRowViewModel> Children { get; set; }
    }

    /// <summary>
    ///     The schedule tree plus the activity choices available for the selected period. The
    ///     choices are worked out before any activity filter is applied, so picking one does not
    ///     collapse the list down to itself.
    /// </summary>
    public class ZoomScheduleResultViewModel
    {
        public List<ZoomScheduleRowViewModel> Rows { get; set; } = new List<ZoomScheduleRowViewModel>();
        public List<string> ActivityNames { get; set; } = new List<string>();
        public List<string> ActivityTypes { get; set; } = new List<string>();
    }

    /// <summary>
    ///     What the Zoom REST API reported back from a manual sync.
    /// </summary>
    public class ZoomSyncResultViewModel
    {
        public bool Success { get; set; }
        public bool HasErrors { get; set; }
        public string StartDate { get; set; }
        public string EndDate { get; set; }
        public int DayCount { get; set; }
        public int AgentCount { get; set; }
        public int ActivityCount { get; set; }
        public string Message { get; set; }
    }
}
