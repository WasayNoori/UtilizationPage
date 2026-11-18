/*
    Zendesk Explore agent metrics, imported from the dashboard's CSV exports.

    Run against MondayUtilization. Azure SQL compatible: connect to the target database
    with -d, there is no USE statement here.

    Re-runnable: tables are only created when missing, so a re-run never discards imported
    rows. Types are dropped and recreated, which means their dependent procedures are
    dropped first and recreated below.

    Two CSVs feed the same row, keyed (MetricYear, MetricMonth, AgentName):
      - "Agent % To Target By Month" supplies PercentToTarget for many months
      - "Agent Table"                supplies the component columns and the YTD figures
    The upsert only overwrites columns the incoming file actually provides, so the two
    imports compose instead of clobbering each other.
*/

/* ------------------------------------------------------------------ Tables */

IF OBJECT_ID('dbo.ZendeskAgentMetrics', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.ZendeskAgentMetrics
    (
        MetricYear          INT             NOT NULL,
        MetricMonth         TINYINT         NOT NULL,
        AgentName           NVARCHAR(255)   NOT NULL,

        -- Ratios are stored at full source precision. Explore exports 20 decimal places and
        -- rounding here would make reconciliation against the dashboard impossible.
        PercentToTarget     DECIMAL(28,18)  NULL,
        PercentToTargetYtd  DECIMAL(28,18)  NULL,
        CsatPct             DECIMAL(28,18)  NULL,
        CsatPctYtd          DECIMAL(28,18)  NULL,
        Solves              INT             NULL,
        SolvesYtd           INT             NULL,
        FrtPct              DECIMAL(28,18)  NULL,
        FrtPctYtd           DECIMAL(28,18)  NULL,
        AwtPct              DECIMAL(28,18)  NULL,
        AwtPctYtd           DECIMAL(28,18)  NULL,
        RwtPct              DECIMAL(28,18)  NULL,
        RwtPctYtd           DECIMAL(28,18)  NULL,
        GoodSurveys         INT             NULL,
        BadSurveys          INT             NULL,
        InPodPct            DECIMAL(28,18)  NULL,
        InPodPctYtd         DECIMAL(28,18)  NULL,
        OttPctYtd           DECIMAL(28,18)  NULL,
        AvgFirstReply       DECIMAL(28,18)  NULL,
        AvgReqWait          DECIMAL(28,18)  NULL,
        AvgOnHold           DECIMAL(28,18)  NULL,
        AvgWorkTime         DECIMAL(28,18)  NULL,
        AvgResolution       DECIMAL(28,18)  NULL,

        ImportedAt          DATETIME2(0)    NOT NULL CONSTRAINT DF_ZendeskAgentMetrics_ImportedAt DEFAULT (SYSUTCDATETIME()),
        ImportedBy          NVARCHAR(255)   NULL,
        SourceFile          NVARCHAR(255)   NULL,
        CONSTRAINT PK_ZendeskAgentMetrics PRIMARY KEY CLUSTERED (MetricYear, MetricMonth, AgentName)
    );

    CREATE INDEX IX_ZendeskAgentMetrics_AgentName ON dbo.ZendeskAgentMetrics (AgentName);
END
GO

-- Zendesk reports "Assignee name", Zoom reports a display name, and they do not always
-- agree. Ignored = 1 marks a Zendesk agent who is legitimately absent from the Zoom
-- schedule, so the import summary stops reporting them as a surprise.
IF OBJECT_ID('dbo.ZendeskAgentAlias', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.ZendeskAgentAlias
    (
        ZendeskName     NVARCHAR(255)   NOT NULL,
        ZoomUserEmail   NVARCHAR(255)   NULL,
        Ignored         BIT             NOT NULL CONSTRAINT DF_ZendeskAgentAlias_Ignored DEFAULT (0),
        Note            NVARCHAR(255)   NULL,
        CONSTRAINT PK_ZendeskAgentAlias PRIMARY KEY CLUSTERED (ZendeskName)
    );
END
GO

IF NOT EXISTS (SELECT 1 FROM dbo.ZendeskAgentAlias WHERE ZendeskName = 'Joseph Luers')
BEGIN
    INSERT INTO dbo.ZendeskAgentAlias (ZendeskName, ZoomUserEmail, Ignored, Note)
    VALUES ('Joseph Luers', 'josephl@hawkridgesys.com', 0, 'Zendesk uses the full name, Zoom uses Joe');
END
GO

IF OBJECT_ID('dbo.ZendeskImports', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.ZendeskImports
    (
        Id              INT IDENTITY(1,1)   NOT NULL CONSTRAINT PK_ZendeskImports PRIMARY KEY,
        FileName        NVARCHAR(255)       NULL,
        FileKind        NVARCHAR(32)        NULL,
        MetricYear      INT                 NULL,
        MetricMonth     TINYINT             NULL,
        RowsParsed      INT                 NOT NULL CONSTRAINT DF_ZendeskImports_Parsed DEFAULT (0),
        RowsUpserted    INT                 NOT NULL CONSTRAINT DF_ZendeskImports_Upserted DEFAULT (0),
        UnmatchedAgents NVARCHAR(MAX)       NULL,
        ImportedBy      NVARCHAR(255)       NULL,
        ImportedAt      DATETIME2(0)        NOT NULL CONSTRAINT DF_ZendeskImports_At DEFAULT (SYSUTCDATETIME())
    );
END
GO

/* ------------------------------------------------------- Table valued type */

-- Column ORDER matters: Dapper's AsTableValuedParameter binds a DataTable by position, so
-- this must stay in step with ZendeskImportService.ToMetricTable.
IF TYPE_ID('dbo.ZendeskAgentMetricType') IS NOT NULL
BEGIN
    DROP PROCEDURE IF EXISTS dbo.sp_UpsertZendeskAgentMetrics;
    DROP TYPE dbo.ZendeskAgentMetricType;
END
GO

CREATE TYPE dbo.ZendeskAgentMetricType AS TABLE
(
    MetricYear          INT             NOT NULL,
    MetricMonth         TINYINT         NOT NULL,
    AgentName           NVARCHAR(255)   NOT NULL,
    PercentToTarget     DECIMAL(28,18)  NULL,
    PercentToTargetYtd  DECIMAL(28,18)  NULL,
    CsatPct             DECIMAL(28,18)  NULL,
    CsatPctYtd          DECIMAL(28,18)  NULL,
    Solves              INT             NULL,
    SolvesYtd           INT             NULL,
    FrtPct              DECIMAL(28,18)  NULL,
    FrtPctYtd           DECIMAL(28,18)  NULL,
    AwtPct              DECIMAL(28,18)  NULL,
    AwtPctYtd           DECIMAL(28,18)  NULL,
    RwtPct              DECIMAL(28,18)  NULL,
    RwtPctYtd           DECIMAL(28,18)  NULL,
    GoodSurveys         INT             NULL,
    BadSurveys          INT             NULL,
    InPodPct            DECIMAL(28,18)  NULL,
    InPodPctYtd         DECIMAL(28,18)  NULL,
    OttPctYtd           DECIMAL(28,18)  NULL,
    AvgFirstReply       DECIMAL(28,18)  NULL,
    AvgReqWait          DECIMAL(28,18)  NULL,
    AvgOnHold           DECIMAL(28,18)  NULL,
    AvgWorkTime         DECIMAL(28,18)  NULL,
    AvgResolution       DECIMAL(28,18)  NULL
);
GO

/* --------------------------------------------------------- Write procedure */

-- Each file is authoritative for the columns it carries, and only those.
--
-- The earlier version used ISNULL(src, tgt) on every column so the two CSVs could stack. That
-- also meant a blank cell could never clear a stale value: re-importing a corrected export
-- left the old number in place wherever the new file happened to be empty. Branching on
-- @FileKind keeps the stacking - a By-Month file still leaves the Agent Table's columns alone -
-- while letting a file blank out its own columns.
CREATE OR ALTER PROCEDURE dbo.sp_UpsertZendeskAgentMetrics
    @Metrics    dbo.ZendeskAgentMetricType READONLY,
    @ImportedBy NVARCHAR(255) = NULL,
    @SourceFile NVARCHAR(255) = NULL,
    @FileKind   NVARCHAR(32)  = NULL
AS
BEGIN
    SET NOCOUNT ON;

    -- Captures what the merge actually did, so the caller can report "28 values changed"
    -- rather than just "400 rows upserted". Re-importing the same file reports zero changes,
    -- which is the signal that the wrong export was picked.
    DECLARE @changes TABLE
    (
        MetricMonth TINYINT        NOT NULL,
        Action      NVARCHAR(10)   NOT NULL,
        OldPct      DECIMAL(28,18) NULL,
        NewPct      DECIMAL(28,18) NULL
    );

    MERGE dbo.ZendeskAgentMetrics AS tgt
    USING @Metrics AS src
        ON  tgt.MetricYear  = src.MetricYear
        AND tgt.MetricMonth = src.MetricMonth
        AND tgt.AgentName   = src.AgentName

    -- MERGE permits only one WHEN MATCHED ... UPDATE, so the branch is per column. Both files
    -- carry PercentToTarget, which is therefore always taken from the incoming row - including
    -- when it is blank. Every other column belongs to the Agent Table export and is left alone
    -- when a By-Month file is being imported.
    WHEN MATCHED THEN UPDATE SET
        PercentToTarget    = src.PercentToTarget,
        PercentToTargetYtd = CASE WHEN @FileKind = 'ByMonth' THEN tgt.PercentToTargetYtd ELSE src.PercentToTargetYtd END,
        CsatPct            = CASE WHEN @FileKind = 'ByMonth' THEN tgt.CsatPct            ELSE src.CsatPct            END,
        CsatPctYtd         = CASE WHEN @FileKind = 'ByMonth' THEN tgt.CsatPctYtd         ELSE src.CsatPctYtd         END,
        Solves             = CASE WHEN @FileKind = 'ByMonth' THEN tgt.Solves             ELSE src.Solves             END,
        SolvesYtd          = CASE WHEN @FileKind = 'ByMonth' THEN tgt.SolvesYtd          ELSE src.SolvesYtd          END,
        FrtPct             = CASE WHEN @FileKind = 'ByMonth' THEN tgt.FrtPct             ELSE src.FrtPct             END,
        FrtPctYtd          = CASE WHEN @FileKind = 'ByMonth' THEN tgt.FrtPctYtd          ELSE src.FrtPctYtd          END,
        AwtPct             = CASE WHEN @FileKind = 'ByMonth' THEN tgt.AwtPct             ELSE src.AwtPct             END,
        AwtPctYtd          = CASE WHEN @FileKind = 'ByMonth' THEN tgt.AwtPctYtd          ELSE src.AwtPctYtd          END,
        RwtPct             = CASE WHEN @FileKind = 'ByMonth' THEN tgt.RwtPct             ELSE src.RwtPct             END,
        RwtPctYtd          = CASE WHEN @FileKind = 'ByMonth' THEN tgt.RwtPctYtd          ELSE src.RwtPctYtd          END,
        GoodSurveys        = CASE WHEN @FileKind = 'ByMonth' THEN tgt.GoodSurveys        ELSE src.GoodSurveys        END,
        BadSurveys         = CASE WHEN @FileKind = 'ByMonth' THEN tgt.BadSurveys         ELSE src.BadSurveys         END,
        InPodPct           = CASE WHEN @FileKind = 'ByMonth' THEN tgt.InPodPct           ELSE src.InPodPct           END,
        InPodPctYtd        = CASE WHEN @FileKind = 'ByMonth' THEN tgt.InPodPctYtd        ELSE src.InPodPctYtd        END,
        OttPctYtd          = CASE WHEN @FileKind = 'ByMonth' THEN tgt.OttPctYtd          ELSE src.OttPctYtd          END,
        AvgFirstReply      = CASE WHEN @FileKind = 'ByMonth' THEN tgt.AvgFirstReply      ELSE src.AvgFirstReply      END,
        AvgReqWait         = CASE WHEN @FileKind = 'ByMonth' THEN tgt.AvgReqWait         ELSE src.AvgReqWait         END,
        AvgOnHold          = CASE WHEN @FileKind = 'ByMonth' THEN tgt.AvgOnHold          ELSE src.AvgOnHold          END,
        AvgWorkTime        = CASE WHEN @FileKind = 'ByMonth' THEN tgt.AvgWorkTime        ELSE src.AvgWorkTime        END,
        AvgResolution      = CASE WHEN @FileKind = 'ByMonth' THEN tgt.AvgResolution      ELSE src.AvgResolution      END,
        ImportedAt         = SYSUTCDATETIME(),
        ImportedBy         = ISNULL(@ImportedBy, tgt.ImportedBy),
        SourceFile         = ISNULL(@SourceFile, tgt.SourceFile)
    WHEN NOT MATCHED THEN INSERT
        (MetricYear, MetricMonth, AgentName,
         PercentToTarget, PercentToTargetYtd, CsatPct, CsatPctYtd, Solves, SolvesYtd,
         FrtPct, FrtPctYtd, AwtPct, AwtPctYtd, RwtPct, RwtPctYtd,
         GoodSurveys, BadSurveys, InPodPct, InPodPctYtd, OttPctYtd,
         AvgFirstReply, AvgReqWait, AvgOnHold, AvgWorkTime, AvgResolution,
         ImportedBy, SourceFile)
    VALUES
        (src.MetricYear, src.MetricMonth, src.AgentName,
         src.PercentToTarget, src.PercentToTargetYtd, src.CsatPct, src.CsatPctYtd, src.Solves, src.SolvesYtd,
         src.FrtPct, src.FrtPctYtd, src.AwtPct, src.AwtPctYtd, src.RwtPct, src.RwtPctYtd,
         src.GoodSurveys, src.BadSurveys, src.InPodPct, src.InPodPctYtd, src.OttPctYtd,
         src.AvgFirstReply, src.AvgReqWait, src.AvgOnHold, src.AvgWorkTime, src.AvgResolution,
         @ImportedBy, @SourceFile)

    OUTPUT $action, inserted.MetricMonth, deleted.PercentToTarget, inserted.PercentToTarget
    INTO @changes (Action, MetricMonth, OldPct, NewPct);

    -- Counts are for % To Target specifically - it is the headline metric and the only one
    -- both exports carry, so it is the honest thing to report a change count against.
    SELECT
        MetricMonth,
        SUM(CASE WHEN Action = 'INSERT' THEN 1 ELSE 0 END) AS Inserted,
        SUM(CASE WHEN Action = 'UPDATE'
                  AND (   (OldPct IS NULL     AND NewPct IS NOT NULL)
                       OR (OldPct IS NOT NULL AND NewPct IS NULL)
                       OR (OldPct <> NewPct))
                 THEN 1 ELSE 0 END) AS Changed,
        SUM(CASE WHEN Action = 'UPDATE'
                  AND (   (OldPct IS NULL AND NewPct IS NULL)
                       OR (OldPct = NewPct))
                 THEN 1 ELSE 0 END) AS Unchanged
    FROM @changes
    GROUP BY MetricMonth
    ORDER BY MetricMonth;
END
GO

CREATE OR ALTER PROCEDURE dbo.sp_LogZendeskImport
    @FileName        NVARCHAR(255),
    @FileKind        NVARCHAR(32),
    @MetricYear      INT           = NULL,
    @MetricMonth     TINYINT       = NULL,
    @RowsParsed      INT,
    @RowsUpserted    INT,
    @UnmatchedAgents NVARCHAR(MAX) = NULL,
    @ImportedBy      NVARCHAR(255) = NULL
AS
BEGIN
    SET NOCOUNT ON;

    INSERT INTO dbo.ZendeskImports
        (FileName, FileKind, MetricYear, MetricMonth, RowsParsed, RowsUpserted, UnmatchedAgents, ImportedBy)
    VALUES
        (@FileName, @FileKind, @MetricYear, @MetricMonth, @RowsParsed, @RowsUpserted, @UnmatchedAgents, @ImportedBy);
END
GO

/* ---------------------------------------------------------- Resolution view */

-- Exact display-name match wins; the alias table covers the rest. An agent who resolves to
-- nothing comes back with ResolvedEmail NULL and simply does not render on the page -
-- the metrics row is still stored, so adding an alias later fixes history retroactively.
CREATE OR ALTER VIEW dbo.vZendeskAgentMetricsResolved
AS
SELECT
    m.*,
    COALESCE(a.ZoomUserEmail, z.UserEmail) AS ResolvedEmail,
    ISNULL(a.Ignored, 0)                   AS Ignored
FROM dbo.ZendeskAgentMetrics AS m
LEFT JOIN dbo.ZendeskAgentAlias AS a
    ON a.ZendeskName = m.AgentName
LEFT JOIN (SELECT DisplayName, MIN(UserEmail) AS UserEmail
           FROM dbo.ZoomScheduleAgents
           GROUP BY DisplayName) AS z
    ON z.DisplayName = m.AgentName;
GO

/* --------------------------------------------------------- Read procedures */

CREATE OR ALTER PROCEDURE dbo.GetZendeskAgentMetrics
    @MetricYear  INT,
    @MetricMonth TINYINT
AS
BEGIN
    SET NOCOUNT ON;

    SELECT
        AgentName, ResolvedEmail,
        PercentToTarget, PercentToTargetYtd,
        CsatPct, CsatPctYtd, Solves, SolvesYtd,
        FrtPct, FrtPctYtd, AwtPct, AwtPctYtd, RwtPct, RwtPctYtd,
        ImportedAt
    FROM dbo.vZendeskAgentMetricsResolved
    WHERE MetricYear = @MetricYear
      AND MetricMonth = @MetricMonth;
END
GO

-- Names that landed in the database but map to no Zoom agent. Drives the import summary.
CREATE OR ALTER PROCEDURE dbo.GetZendeskUnmatchedAgents
    @MetricYear  INT,
    @MetricMonth TINYINT
AS
BEGIN
    SET NOCOUNT ON;

    SELECT DISTINCT AgentName
    FROM dbo.vZendeskAgentMetricsResolved
    WHERE MetricYear = @MetricYear
      AND MetricMonth = @MetricMonth
      AND ResolvedEmail IS NULL
      AND Ignored = 0
    ORDER BY AgentName;
END
GO

CREATE OR ALTER PROCEDURE dbo.GetZendeskLatestImport
AS
BEGIN
    SET NOCOUNT ON;

    SELECT TOP (1) MetricYear, MetricMonth, MAX(ImportedAt) AS ImportedAt
    FROM dbo.ZendeskAgentMetrics
    GROUP BY MetricYear, MetricMonth
    ORDER BY MetricYear DESC, MetricMonth DESC;
END
GO

/* ------------------------------------------------------------------ Grants */

-- The page signs in as MondayReader. Ownership chaining lets these procedures write to the
-- tables without granting MondayReader any direct table access; executing a procedure that
-- takes a table valued parameter also needs EXECUTE on the type itself.
GRANT EXECUTE ON TYPE::dbo.ZendeskAgentMetricType TO MondayReader;
GRANT EXECUTE ON dbo.sp_UpsertZendeskAgentMetrics TO MondayReader;
GRANT EXECUTE ON dbo.sp_LogZendeskImport          TO MondayReader;
GRANT EXECUTE ON dbo.GetZendeskAgentMetrics       TO MondayReader;
GRANT EXECUTE ON dbo.GetZendeskUnmatchedAgents    TO MondayReader;
GRANT EXECUTE ON dbo.GetZendeskLatestImport       TO MondayReader;
GO

PRINT 'Zendesk metrics objects are up to date.';
GO
