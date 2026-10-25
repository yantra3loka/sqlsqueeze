/* SqlSqueeze SQL Agent template: alert operator + backup failure e-mail.
 *
 * EDIT the four variables below, then run in SSMS (or sqlcmd) on the
 * instance that owns the database. Creates the msdb operator that gets
 * notified when a backup job fails, and wires it into the FULL, DIFF,
 * LOG and VERIFY jobs created by the templates in this folder.
 *
 * Notes:
 *   - E-mail needs Database Mail configured on the instance (a mail
 *     profile whose account matches @emailAddress). This script only
 *     tells Agent who to notify; SQL Server does the sending.
 *   - The job side uses sp_update_job, so no sp_add_jobnotification is
 *     required - that procedure is not present in msdb on SQL Server
 *     on Linux.
 *   - notify levels: 0 never, 1 on success, 2 on failure, 3 always.
 *   - The operator is replaced, not updated: sp_update_operator is
 *     blocked on SQL Server on Linux (Agent XPs is not available
 *     there), so an existing operator is deleted and re-added. The
 *     operator id changes, so the jobs are re-wired afterwards.
 *   - Idempotent: re-running replaces the operator and re-wires the
 *     jobs. Jobs that do not exist yet are reported, not created - run
 *     the job templates in this folder first.
 */
USE msdb;
GO

DECLARE @operatorName  sysname        = N'SqlSqueeze Alerts';
DECLARE @emailAddress  nvarchar(256)  = N'dba@example.com';  -- must match a Database Mail profile account
DECLARE @pagerAddress  nvarchar(256)  = N'';                  -- optional; leave empty to skip paging
DECLARE @db            sysname        = N'MyDatabase';

DECLARE @categoryName  sysname        = N'SqlSqueeze';
DECLARE @fullJob       sysname        = N'SqlSqueeze - FULL - ' + @db;
DECLARE @diffJob       sysname        = N'SqlSqueeze - DIFF - ' + @db;
DECLARE @logJob        sysname        = N'SqlSqueeze - LOG - ' + @db;
DECLARE @verifyJob     sysname        = N'SqlSqueeze - VERIFY - ' + @db;

-- Operator category (sp_add_operator fails on a duplicate).
IF NOT EXISTS (SELECT 1 FROM dbo.syscategories WHERE name = @categoryName)
    EXEC dbo.sp_add_category @class = 'OPERATOR', @type = 'NONE', @name = @categoryName;

-- Replace the operator: sp_update_operator is blocked on SQL Server on
-- Linux (no Agent XPs), so an existing operator is deleted and re-added.
IF EXISTS (SELECT 1 FROM dbo.sysoperators WHERE name = @operatorName)
    EXEC dbo.sp_delete_operator @name = @operatorName;

EXEC dbo.sp_add_operator @name = @operatorName, @enabled = 1,
    @email_address = @emailAddress,
    @pager_address = @pagerAddress,
    @category_name = @categoryName;

-- Wire failure notifications into the four backup jobs.
DECLARE @jobs TABLE (name sysname);
INSERT INTO @jobs (name) VALUES (@fullJob), (@diffJob), (@logJob), (@verifyJob);

DECLARE @jobName sysname;
DECLARE job_cursor CURSOR LOCAL FAST_FORWARD FOR SELECT name FROM @jobs;
OPEN job_cursor;
FETCH NEXT FROM job_cursor INTO @jobName;

WHILE @@FETCH_STATUS = 0
BEGIN
    IF EXISTS (SELECT 1 FROM dbo.sysjobs WHERE name = @jobName)
    BEGIN
        EXEC dbo.sp_update_job @job_name = @jobName,
            @notify_level_email = 2,               -- on failure
            @notify_email_operator_name = @operatorName,
            @notify_level_eventlog = 2;            -- on failure

        IF LEN(@pagerAddress) > 0
            EXEC dbo.sp_update_job @job_name = @jobName,
                @notify_level_page = 2,            -- on failure
                @notify_page_operator_name = @operatorName;

        PRINT N'Wired failure notification: ' + @jobName;
    END
    ELSE
        PRINT N'Skipped (job not found): ' + @jobName;

    FETCH NEXT FROM job_cursor INTO @jobName;
END

CLOSE job_cursor;
DEALLOCATE job_cursor;
GO
