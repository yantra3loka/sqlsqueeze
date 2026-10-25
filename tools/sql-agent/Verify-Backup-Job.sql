/* SqlSqueeze SQL Agent template: nightly RESTORE VERIFYONLY of native backups.
 *
 * EDIT the four variables below, then run in SSMS (or sqlcmd) on the
 * instance that owns the database. The verification runs as a CmdExec
 * step, so it works on Windows and Linux Agent.
 *
 * WHAT IT VERIFIES
 *   The most recent native-format FULL backup that SQL Server recorded
 *   for @db in msdb (backupset.type 'D', device_type 2 = disk file).
 *   RESTORE VERIFYONLY reads the backup set without restoring it, so the
 *   file on disk is checked for readability and corruption.
 *
 * WHY NOT THE COMPRESSED OUTPUTS
 *   RESTORE VERIFYONLY rejects compressed/virtual-device backups by
 *   design - same as Redgate .sqb files. sqlsqueeze backups (.bak.zst,
 *   .bak.gz.aes, VDI device_type 7) and URL backups (device_type 9)
 *   cannot be VERIFYONLY'd, so this job targets NATIVE-format outputs
 *   (BACKUP DATABASE ... TO DISK, with or without native compression)
 *   and fails loudly when the newest backup of @db is not native.
 *   Verify compressed files by restoring them to a scratch database and
 *   comparing checksums - see tools/SqlSqueeze.psm1 (Restore-SqlDatabase).
 *
 * SAMPLE SCHEDULES (see the other templates in this folder)
 *   FULL   02:00 nightly    (Full-Backup-Job.sql)
 *   VERIFY 03:00 nightly    (this file) - after the nightly full
 *   DIFF   every 4 hours    (Diff-Backup-Job.sql)
 *   LOG    every 15 minutes (Log-Backup-Job.sql)
 */
USE msdb;
GO

DECLARE @client      nvarchar(4000) = N'sqlcmd -S localhost -E -b'; -- sqlcmd + connection flags (Windows: -E integrated; Linux: -U sa, password from the SQLCMDPASSWORD env var - never inline it here)
DECLARE @db          sysname        = N'MyDatabase';
DECLARE @maxAgeHours int            = 26;                          -- 0 = no age limit
DECLARE @jobName     sysname        = N'SqlSqueeze - VERIFY - MyDatabase';

/* The step runs one sqlcmd batch: find the newest FULL backup of @db in
 * msdb, then RESTORE VERIFYONLY that file. __DB__ / __HOURS__ are
 * substituted below. The batch stays comment-free, percent-free (cmd.exe
 * would expand %VAR% inside the quoted -Q argument) and is flattened to
 * a single line before it goes into the job step. */
DECLARE @sql nvarchar(max) = N'
SET NOCOUNT ON;
DECLARE @db sysname = N''__DB__'';
DECLARE @maxAgeHours int = __HOURS__;
DECLARE @path nvarchar(4000), @when datetime2(3), @device int, @damaged bit, @found int, @kind nvarchar(30), @msg nvarchar(2048);
SELECT TOP (1) @found = 1, @path = f.physical_device_name, @when = s.backup_finish_date, @device = f.device_type, @damaged = s.is_damaged, @kind = CASE f.device_type WHEN 2 THEN ''disk'' WHEN 7 THEN ''virtual device (VDI)'' WHEN 9 THEN ''URL'' ELSE ''other'' END FROM msdb.dbo.backupset AS s JOIN msdb.dbo.backupmediafamily AS f ON f.media_set_id = s.media_set_id WHERE s.database_name = @db AND s.type = ''D'' ORDER BY s.backup_finish_date DESC;
IF @found IS NULL BEGIN SET @msg = CONCAT(''SqlSqueeze verify: no FULL backup of '', @db, '' found in msdb.''); THROW 50000, @msg, 1; END;
IF @damaged = 1 BEGIN SET @msg = CONCAT(''SqlSqueeze verify: latest backup of '', @db, '' is marked damaged in msdb.''); THROW 50000, @msg, 1; END;
IF @device <> 2 BEGIN SET @msg = CONCAT(''SqlSqueeze verify: latest backup of '', @db, '' is '', @kind, '' (device_type '', @device, ''). RESTORE VERIFYONLY rejects compressed/virtual-device backups by design - point this job at native-format outputs.''); THROW 50000, @msg, 1; END;
IF @maxAgeHours > 0 AND DATEDIFF(hour, @when, SYSDATETIME()) > @maxAgeHours BEGIN SET @msg = CONCAT(''SqlSqueeze verify: latest native full backup of '', @db, '' finished '', CONVERT(nvarchar(30), @when, 121), '' - older than '', @maxAgeHours, '' hours.''); THROW 50000, @msg, 1; END;
DECLARE @stmt nvarchar(max) = N''RESTORE VERIFYONLY FROM DISK = N'' + CHAR(39) + REPLACE(@path, CHAR(39), CHAR(39) + CHAR(39)) + CHAR(39);
EXEC sys.sp_executesql @stmt;
PRINT ''SqlSqueeze verify: VERIFYONLY OK - '' + @path;
';

SET @sql = REPLACE(REPLACE(@sql, N'__DB__', REPLACE(@db, N'''', N'''''')),
                   N'__HOURS__', CONVERT(nvarchar(10), @maxAgeHours));

/* One line, double quotes around the batch, no double quotes inside. */
DECLARE @cmd nvarchar(4000) =
    @client + N' -Q "' + REPLACE(REPLACE(@sql, NCHAR(13), N' '), NCHAR(10), N' ') + N'"';

IF EXISTS (SELECT 1 FROM dbo.sysjobs WHERE name = @jobName)
    EXEC dbo.sp_delete_job @job_name = @jobName, @delete_unused_schedule = 1;

EXEC dbo.sp_add_job @job_name = @jobName, @enabled = 1,
    @description = N'SqlSqueeze nightly RESTORE VERIFYONLY of the latest native FULL backup.';

EXEC dbo.sp_add_jobstep @job_name = @jobName, @step_name = N'Verify latest native backup',
    @subsystem = N'CmdExec', @command = @cmd,
    @on_success_action = 1, @on_fail_action = 2; -- quit w/ failure

-- Nightly 03:00, after the 02:00 full backup.
EXEC dbo.sp_add_jobschedule @job_name = @jobName, @name = N'Nightly 03:00',
    @freq_type = 4, @freq_interval = 1, @active_start_time = 030000;
GO
