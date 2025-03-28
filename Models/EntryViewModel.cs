namespace UtilizationPage_ASP.Services
{

    
        public class EntryViewModel
        {
            public string BoardName { get; set; }
            public string ItemName { get; set; }
            public double Duration { get; set; }
        public string GroupName { get; set; }
            public DateTime StartTime { get; set; }
            public string CategoryName { get; set; }
        }

    public class TableEntryViewModel
    {
        public string Task { get; set; }
        public string Hours { get; set; }
        public string EntryDate { get; set; }
        public string CategoryName { get; set; }
        public List<TableEntryViewModel> Children { get; set; }
    }

    public class WeeklyHoursSummary
    {
        public string UserName { get; set; }
        public string Monday { get; set; }
        public string Tuesday { get; set; }
        public string Wednesday { get; set; }
        public string Thursday { get; set; }
        public string Friday { get; set; }
    }

    public class AvgSummary
    {
        public double RollingAvg { get; set; }
        public double TeamAverage { get; set; }
    }

    public class MonthlyHoursSummary
    {
        public string MonthName { get; set; }
        public string TeamAverage { get; set; }
        public string UserHours { get; set; }
    }

    public class BoardDistribution
    {
        public string BoardName { get; set; }
        public double Percentage { get; set; }
        public string Hours { get; set; }
    }

    public class User
    {
        public string UserName { get; set; }
        public string Email { get; set; }
        public string UserType { get; set; }
    }
}
    

