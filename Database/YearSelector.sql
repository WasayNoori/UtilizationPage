/*
    Year selector support for the Utilization page.

    Run this BEFORE deploying the matching app changes: the app now sends @Year to
    GetMonthlyHoursByUser, GetTopForBoard, GetWeeklyHoursForGraph and hoursbreakdown,
    and the old procedures would reject it.

    All new parameters are optional and default to the old behavior, so the currently
    deployed app keeps working after this script runs.
*/

SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO

/*  GetUserEntries
    Removed the YEAR(PacificTime) = YEAR(GETDATE()) filter. @StartDate/@EndDate already
    bound the rows; the extra filter hid prior-year months and cut December out of
    "Last Week" / "Last 2 Weeks" in early January. */
ALTER PROCEDURE [dbo].[GetUserEntries]
    @StartDate DATETIME,
    @EndDate DATETIME,
    @UserEmail NVARCHAR(255)
AS
BEGIN
    SET @EndDate = DATEADD(SECOND, -1, DATEADD(DAY, 1, @EndDate));

    SELECT
        e.ItemID,
        CAST(e.PacificTime AS DATE) AS EntryDate,
        e.BoardName,
        e.ItemName,
        e.[GroupName],
        SUM(e.Duration) AS TotalDuration,
        c.CategoryName
    FROM Entries e
    JOIN Users u ON u.UserID = e.UserID
    JOIN Categories c ON c.BoardID = e.BoardID AND c.ColumnID = e.ColumnID
    WHERE u.Email = @UserEmail
        AND c.CustomerFacing = 1
        AND e.PacificTime >= @StartDate
        AND e.PacificTime <= @EndDate
    GROUP BY e.ItemID, CAST(e.PacificTime AS DATE), e.BoardName, e.ItemName, e.[GroupName], c.CategoryName
    ORDER BY EntryDate, e.ItemID;
END;
GO

/*  GetMonthlyHoursByUser
    Added optional @Year (defaults to the current year). */
ALTER PROCEDURE [dbo].[GetMonthlyHoursByUser]
    @UserEmail NVARCHAR(255),
    @Year INT = NULL
AS
BEGIN
    DECLARE @TargetYear INT = COALESCE(@Year, YEAR(GETDATE()));

    SELECT
        FORMAT(e.PacificTime, 'MMM') AS MonthName,
        (SELECT AVG(MonthlyUserHours.TotalHours) FROM (
            SELECT u2.UserID, MONTH(e2.PacificTime) AS Month, SUM(e2.Duration) AS TotalHours
            FROM Entries e2
            JOIN Users u2 ON u2.UserID = e2.UserID
            JOIN Categories c2 ON c2.BoardID = e2.BoardID AND c2.ColumnID = e2.ColumnID
            WHERE YEAR(e2.PacificTime) = @TargetYear
              AND (u2.TEAM = 'TS' OR u2.TEAM = 'ES')
              AND c2.CustomerFacing = 1
            GROUP BY u2.UserID, MONTH(e2.PacificTime)
        ) AS MonthlyUserHours WHERE MonthlyUserHours.Month = MONTH(e.PacificTime) AND MonthlyUserHours.TotalHours >= 50) AS TeamAverage,
        SUM(CASE WHEN u.Email = @UserEmail THEN e.Duration ELSE 0 END) AS UserHours
    FROM Entries e
    JOIN Users u ON u.UserID = e.UserID
    JOIN Categories c ON c.BoardID = e.BoardID AND c.ColumnID = e.ColumnID
    WHERE YEAR(e.PacificTime) = @TargetYear
      AND (u.TEAM = 'TS' OR u.TEAM = 'ES')
      AND c.CustomerFacing = 1
    GROUP BY FORMAT(e.PacificTime, 'MMM'), MONTH(e.PacificTime)
    ORDER BY MONTH(e.PacificTime);
END;
GO

/*  GetTopForBoard
    Added optional @Year. When omitted, the year logic is unchanged. The app now passes
    last month's year with last month, so January looks at December of the prior year. */
ALTER PROCEDURE [dbo].[GetTopForBoard] (
    @BoardID BIGINT,
    @Month INT = NULL,
    @Year INT = NULL
)
AS
BEGIN
    SELECT Username, ItemCount
    FROM (
        SELECT
            u.Username,
            COUNT(DISTINCT e.ItemID) AS ItemCount,
            ROW_NUMBER() OVER (ORDER BY COUNT(DISTINCT e.ItemID) DESC) AS RowNum
        FROM Entries e
        INNER JOIN users u ON e.UserID = u.UserID
        WHERE e.BoardID = @BoardID
        AND (
            @Month IS NULL OR MONTH(e.StartTime) = @Month
        )
        AND YEAR(e.StartTime) = COALESCE(@Year, CASE
            WHEN @Month IS NULL THEN YEAR(DATEADD(month, -1, GETDATE()))
            ELSE YEAR(GETDATE())
        END)
        GROUP BY u.Username
    ) AS UserItemCounts
    WHERE RowNum <= 1
    ORDER BY ItemCount DESC;
END;
GO

/*  GetWeeklyHoursForGraph (Trends tab)
    Added optional @Year (defaults to the current year). */
ALTER PROCEDURE [dbo].[GetWeeklyHoursForGraph]
    @Email VARCHAR(255),
    @Year INT = NULL
AS
BEGIN
    SET NOCOUNT ON;

    DECLARE
        @ThisYear INT = COALESCE(@Year, YEAR(GETDATE())),
        @UserID   INT;

    SELECT @UserID = UserID
      FROM dbo.Users
     WHERE Email = @Email;

    IF @UserID IS NULL
    BEGIN
        SELECT 'User not found.' AS Message;
        RETURN;
    END;

    ;WITH RawUserHours AS (
        SELECT
            WeekNumber,
            SUM(Duration) AS UserHours
        FROM dbo.Entries
        WHERE UserID = @UserID
          AND YEAR(StartTime) = @ThisYear
        GROUP BY WeekNumber
    ),
    FilledUserHours AS (
        SELECT
            WeekNumber,
            CASE
                WHEN UserHours = 0
                  THEN MAX(CASE WHEN UserHours <> 0 THEN UserHours END)
                       OVER (ORDER BY WeekNumber
                             ROWS BETWEEN UNBOUNDED PRECEDING AND 1 PRECEDING)
                ELSE UserHours
            END AS UserHours
        FROM RawUserHours
    ),
    TeamWeekly AS (
        SELECT
            WeekNumber,
            AVG(WeekSum) AS AvgTeamHours
        FROM (
            SELECT
                UserID,
                WeekNumber,
                SUM(Duration) AS WeekSum
            FROM dbo.Entries
            WHERE YEAR(StartTime) = @ThisYear
            GROUP BY UserID, WeekNumber
        ) AS perUser
        GROUP BY WeekNumber
    )
    SELECT
        t.WeekNumber,
        28                  AS WeeklyIdeal,
        ISNULL(f.UserHours, 0) AS UserHours,
        t.AvgTeamHours        AS AvgTeamHours
    FROM TeamWeekly t
    LEFT JOIN FilledUserHours f
      ON t.WeekNumber = f.WeekNumber
    ORDER BY t.WeekNumber;
END;
GO

/*  hoursbreakdown (Profile tab)
    Replaced the hard-coded 2025 with optional @Year (defaults to the current year). */
ALTER PROCEDURE [dbo].[hoursbreakdown] (
    @Team VARCHAR(255) = '*',
    @Year INT = NULL
)
AS
BEGIN
    DECLARE @TargetYear INT = COALESCE(@Year, YEAR(GETDATE()));

    SELECT
        CASE
            WHEN GROUPING(c.CategoryName) = 1 THEN 'Total'
            ELSE c.CategoryName
        END AS Category,
        CASE
            WHEN GROUPING(c.CategoryName) = 1 THEN (
                SELECT SUM(e.Duration)
                FROM Entries e
                JOIN users u ON u.UserID = e.UserID
                WHERE e.[YEAR] = @TargetYear
                AND (@Team = '*' OR u.TEAM = @Team)
            )
            ELSE SUM(e.Duration)
        END AS TotalHours
    FROM
        Entries e
    JOIN
        Categories c ON e.BoardId = c.BoardId
    JOIN
        users u ON u.UserID = e.UserID
    WHERE
        c.MainCategory = 1
        AND e.[YEAR] = @TargetYear
        AND (@Team = '*' OR u.TEAM = @Team)
    GROUP BY
        ROLLUP(c.CategoryName)
    ORDER BY
        GROUPING(c.CategoryName),
        CASE
            WHEN GROUPING(c.CategoryName) = 1 THEN 0 -- Ensure 'Total' comes last
            ELSE SUM(e.Duration)
        END DESC;
END;
GO
