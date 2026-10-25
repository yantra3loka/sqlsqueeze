/* SqlSqueeze SQL Agent template: point-in-time RESTORE chain (log shipping
 * secondary or scheduled refresh). Restores latest full + latest diff + all
 * later logs via Restore-SqlChain (tools/SqlSqueeze.psm1): everything
 * NORECOVERY except the tail, which is RECOVERY, STANDBY, or left loading.
 * Same pattern as Full-Backup-Job.sql; see it for the full notes.
 */
USE msdb;
GO

DECLARE @pwsh        nvarchar(4000) = N'pwsh';                        -- or full path
DECLARE @module      nvarchar(4000) = N'/opt/sqlsqueeze/tools/SqlSqueeze.psm1';
DECLARE @backupDir   nvarchar(4000) = N'/var/backups';
DECLARE @db          sysname        = N'MyDatabase';
DECLARE @targetDb    sysname        = N'MyDatabase_copy';
DECLARE @jobName     sysname        = N'SqlSqueeze - RESTORE CHAIN - MyDatabase_copy';
-- Tail mode: empty = RECOVERY, a path = STANDBY file, N'NORECOVERY' = stay loading.
DECLARE @tailMode    nvarchar(4000) = N'';

DECLARE @cmd nvarchar(4000) =
    N'"' + @pwsh + N'" -NoProfile -NonInteractive -Command "Import-Module ''' + @module
    + N'''; Restore-SqlChain -Directory ''' + @backupDir + N''' -Database ''' + @db
    + N''' -TargetDatabase ''' + @targetDb + N''''
    + CASE WHEN @tailMode = N'' THEN N''
           WHEN @tailMode = N'NORECOVERY' THEN N' -NoRecovery'
           ELSE N' -StandbyFile ''' + @tailMode + N'''' END + N'"';

IF EXISTS (SELECT 1 FROM dbo.sysjobs WHERE name = @jobName)
    EXEC dbo.sp_delete_job @job_name = @jobName, @delete_unused_schedule = 1;

EXEC dbo.sp_add_job @job_name = @jobName, @enabled = 1,
    @description = N'SqlSqueeze log-shipping restore chain (full -> diff -> logs).';

EXEC dbo.sp_add_jobstep @job_name = @jobName, @step_name = N'Restore chain',
    @subsystem = N'CmdExec', @command = @cmd,
    @on_success_action = 1, @on_fail_action = 2;

-- Hourly is a starting point; match it to the log-backup frequency.
EXEC dbo.sp_add_jobschedule @job_name = @jobName, @name = N'Hourly',
    @freq_type = 4, @freq_interval = 1, @freq_subday_type = 8,
    @freq_subday_interval = 1, @active_start_time = 0;
GO
