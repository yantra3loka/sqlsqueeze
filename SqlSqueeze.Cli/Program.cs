/*
	Copyright 2009 Clay Lenhart <clay@lenharts.net>


	This file is part of MSSQL Compressed Backup.

    MSSQL Compressed Backup is free software: you can redistribute it and/or modify
    it under the terms of the GNU General Public License as published by
    the Free Software Foundation, either version 3 of the License, or
    (at your option) any later version.

    Foobar is distributed in the hope that it will be useful,
    but WITHOUT ANY WARRANTY; without even the implied warranty of
    MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
    GNU General Public License for more details.

    You should have received a copy of the GNU General Public License
    along with Foobar.  If not, see <http://www.gnu.org/licenses/>.
*/



using System;
using System.Collections.Generic;
using System.Text;
using System.IO;
using System.Reflection;
using System.Diagnostics;

using SqlSqueeze.Plugins;
using SqlSqueeze.Core;

namespace SqlSqueeze.Cli
{
    class Program
    {
        static int Main(string[] args)
        {

#if DEBUG
            Debugger.Launch();
#endif

            // Scripting switch: a trailing --json makes backup/restore print
            // one JSON result object instead of human text. Exit codes stay
            // 0 on success, -1 on any failure.
            //
            // Ctrl-C: the first press aborts the in-flight VDI operation so the
            // normal failure path runs (cleanup, multi-user rollback); a second
            // press kills immediately, as does a press with nothing in flight.
            OperationCanceller.Reset();
            Console.CancelKeyPress += (sender, e) =>
            {
                e.Cancel = OperationCanceller.OnCancelKeyPress();
                if (e.Cancel)
                {
                    Console.Error.WriteLine("Cancelling... press Ctrl-C again to force exit.");
                }
            };
            bool json = false;
            if (args.Length > 0 && args[args.Length - 1].Equals("--json", StringComparison.OrdinalIgnoreCase))
            {
                json = true;
                string[] stripped = new string[args.Length - 1];
                Array.Copy(args, stripped, args.Length - 1);
                args = stripped;
            }

            try
            {


                Dictionary<string, Type> pipelineComponents = BackupPipeSystem.LoadTransformComponents();
                Dictionary<string, Type> databaseComponents = BackupPipeSystem.LoadDatabaseComponents();
                Dictionary<string, Type> storageComponents = BackupPipeSystem.LoadStorageComponents();


                if (args.Length == 0)
                {
                    Console.WriteLine("For help, type 'sqlsqueeze help'");
                    return 0;
                }
                else
                {
                    switch (args[0].ToLowerInvariant())
                    {
                        case "help":
                            if (args.Length == 1)
                            {
                                PrintUsage();
                            }
                            else
                            {
                                switch (args[1].ToLowerInvariant())
                                {
                                    case "backup":
                                        PrintBackupUsage();
                                        break;
                                    case "restore":
                                        PrintRestoreUsage();
                                        break;
                                    case "prune":
                                        PrintPruneUsage();
                                        break;
                                    case "verify":
                                        PrintVerifyUsage();
                                        break;
                                    case "backup-all":
                                        PrintBackupAllUsage();
                                        break;
                                    case "test-restore":
                                        PrintTestRestoreUsage();
                                        break;
                                    case "unpack":
                                        PrintUnpackUsage();
                                        break;
                                    case "restore-chain":
                                        PrintRestoreChainUsage();
                                        break;
                                    case "copy":
                                        PrintCopyUsage();
                                        break;
                                    case "listplugins":
                                        Console.WriteLine("Lists the plugins available.  Go on, try it.");
                                        break;
                                    case "helpplugin":
                                        Console.WriteLine("Displays a plugin's help text. For example:");
                                        Console.WriteLine("\tsqlsqueeze helpplugin gzip");
                                        break;
                                    case "version":
                                        Console.WriteLine("Displays the version number.");
                                        break;
                                    default:
                                        Console.WriteLine(string.Format("Command doesn't exist: {0}", args[1]));
                                        PrintUsage();
                                        return -1;
                                }

                            }
                            return 0;

                        case "backup":
                            {
                                try
                                {

                                    ConfigPair storageConfig;
                                    ConfigPair databaseConfig;
                                    bool isBackup = true;

                                    List<string> backupArgs = CopySubArgs(args);
                                    List<ConfigPair> copyTo = ExtractCopyTo(backupArgs, storageComponents, out CopyOptions copyOptions);
                                    bool verify = backupArgs.RemoveAll(a => a.Equals("--verify", StringComparison.OrdinalIgnoreCase)) > 0;

                                    List<ConfigPair> pipelineConfig = ParseBackupOrRestoreArgs(backupArgs, isBackup, pipelineComponents, databaseComponents, storageComponents, out databaseConfig, out storageConfig);

                                    CommandLineNotifier notifier = new CommandLineNotifier(true, !json);

                                    BackupStats stats = BackupPipeSystem.Backup(databaseConfig, pipelineConfig, storageConfig, notifier, copyTo, copyOptions);
                                    VerifyResult verified = verify ? BackupPipeSystem.Verify(storageConfig, pipelineConfig) : null;
                                    PrintSuccess("backup", stats, json, verified);
                                    return stats.CopyProblems.Count == 0 && (verified == null || verified.Ok) ? 0 : -1;
                                }
                                catch (ParallelExecutionException ee)
                                {
                                    HandleExecutionExceptions(ee, true, json);
                                    return -1;
                                }
                                catch (Exception e)
                                {
                                    HandleException(e, true, json);
                                    return -1;
                                }
                            }

                        case "restore":
                            {
                                try
                                {
                                    ConfigPair storageConfig;
                                    ConfigPair databaseConfig;

                                    bool isBackup = false;

                                    List<string> restoreArgs = CopySubArgs(args);
                                    bool fromManifest = restoreArgs.RemoveAll(a => a.Equals("--from-manifest", StringComparison.OrdinalIgnoreCase)) > 0;
                                    bool killConnections = restoreArgs.RemoveAll(a => a.Equals("--kill-connections", StringComparison.OrdinalIgnoreCase)) > 0;

                                    List<ConfigPair> pipelineConfig = ParseBackupOrRestoreArgs(restoreArgs, isBackup, pipelineComponents, databaseComponents, storageComponents, out databaseConfig, out storageConfig);
                                    if (fromManifest)
                                    {
                                        pipelineConfig = PipelineFromManifest(storageConfig, pipelineConfig, pipelineComponents);
                                    }
                                    if (killConnections)
                                    {
                                        databaseConfig.Parameters["KILL_CONNECTIONS"] = new List<string> { null };
                                    }

                                    CommandLineNotifier notifier = new CommandLineNotifier(false, !json);

                                    BackupStats stats = BackupPipeSystem.Restore(storageConfig, pipelineConfig, databaseConfig, notifier);
                                    PrintSuccess("restore", stats, json);
                                    return 0;
                                }
                                catch (ParallelExecutionException ee)
                                {
                                    HandleExecutionExceptions(ee, false, json);
                                    return -1;
                                }
                                catch (Exception e)
                                {
                                    HandleException(e, false, json);
                                    return -1;
                                }
                            }
                        case "copy":
                            {
                                try
                                {
                                    return Copy(args, json, storageComponents);
                                }
                                catch (Exception e)
                                {
                                    if (json)
                                    {
                                        PrintJsonError("copy", InnermostMessage(e));
                                    }
                                    else
                                    {
                                        HandleException(e, true, false);
                                    }
                                    return -1;
                                }
                            }
                        case "restore-chain":
                            {
                                try
                                {
                                    return RestoreChain(args, json, pipelineComponents, databaseComponents);
                                }
                                catch (Exception e)
                                {
                                    if (json)
                                    {
                                        PrintJsonError("restore-chain", InnermostMessage(e));
                                    }
                                    else
                                    {
                                        HandleException(e, false, false);
                                    }
                                    return -1;
                                }
                            }
                        case "unpack":
                            {
                                try
                                {
                                    return Unpack(args, json, pipelineComponents, storageComponents);
                                }
                                catch (Exception e)
                                {
                                    if (json)
                                    {
                                        PrintJsonError("unpack", InnermostMessage(e));
                                    }
                                    else
                                    {
                                        HandleException(e, false, false);
                                    }
                                    return -1;
                                }
                            }
                        case "test-restore":
                            {
                                try
                                {
                                    return TestRestore(args, json, pipelineComponents, databaseComponents, storageComponents);
                                }
                                catch (Exception e)
                                {
                                    if (json)
                                    {
                                        PrintJsonError("test-restore", InnermostMessage(e));
                                    }
                                    else
                                    {
                                        HandleException(e, false, false);
                                    }
                                    return -1;
                                }
                            }
                        case "backup-all":
                            {
                                try
                                {
                                    return BackupAll(args, json, pipelineComponents, databaseComponents, storageComponents);
                                }
                                catch (Exception e)
                                {
                                    if (json)
                                    {
                                        PrintJsonError("backup-all", InnermostMessage(e));
                                    }
                                    else
                                    {
                                        HandleException(e, true, false);
                                    }
                                    return -1;
                                }
                            }
                        case "verify":
                            {
                                try
                                {
                                    if (args.Length < 2)
                                    {
                                        throw new ArgumentException("Usage: sqlsqueeze verify <storage> [pipeline...] [--json]");
                                    }
                                    // verify <storage> [pipeline...] [--sql "db(user=sa;passwordEnv=PW)"]
                                    string sqlSpec = null;
                                    bool fromManifest = false;
                                    List<string> positional = new List<string>();
                                    for (int i = 1; i < args.Length; i++)
                                    {
                                        if (args[i].Equals("--sql", StringComparison.OrdinalIgnoreCase))
                                        {
                                            sqlSpec = NextArg(args, ref i, "--sql");
                                        }
                                        else if (args[i].Equals("--from-manifest", StringComparison.OrdinalIgnoreCase))
                                        {
                                            fromManifest = true;
                                        }
                                        else
                                        {
                                            positional.Add(args[i]);
                                        }
                                    }
                                    if (positional.Count < 1)
                                    {
                                        throw new ArgumentException("Usage: sqlsqueeze verify <storage> [pipeline...] [--sql \"db(user=sa;passwordEnv=PW)\"] [--json]");
                                    }
                                    string storageArg = StorageArgToPlugin(positional[0]);
                                    ConfigPair storageConfig = ConfigUtil.ParseComponentConfig(storageComponents, storageArg);
                                    List<ConfigPair> pipelineConfig = BuildPipelineFromString(positional.GetRange(1, positional.Count - 1), pipelineComponents);
                                    if (fromManifest)
                                    {
                                        pipelineConfig = PipelineFromManifest(storageConfig, pipelineConfig, pipelineComponents);
                                    }
                                    ConfigPair sqlConfig = sqlSpec == null ? null : ConfigUtil.ParseComponentConfig(databaseComponents, sqlSpec);

                                    VerifyResult verified = BackupPipeSystem.Verify(storageConfig, pipelineConfig, sqlConfig, new CommandLineNotifier(false, !json));
                                    PrintVerifyResult(verified, json);
                                    return verified.Ok ? 0 : -1;
                                }
                                catch (Exception e)
                                {
                                    if (json)
                                    {
                                        PrintJsonError("verify", InnermostMessage(e));
                                    }
                                    else
                                    {
                                        HandleException(e, false, false);
                                    }
                                    return -1;
                                }
                            }
                        case "prune":
                            {
                                try
                                {
                                    if (args.Length != 2)
                                    {
                                        throw new ArgumentException("Usage: sqlsqueeze prune \"local(dir=<folder>;pattern=<glob>;olderThanDays=<n>)\" [--json]");
                                    }
                                    ConfigPair storageConfig = ConfigUtil.ParseComponentConfig(storageComponents, args[1]);
                                    object plugin = Activator.CreateInstance(storageConfig.TransformationType);
                                    if (!(plugin is IPrunableStorage prunable))
                                    {
                                        throw new ArgumentException(string.Format("The {0} storage plugin does not support prune.", storageConfig.TransformationType.Name));
                                    }
                                    PruneResult pruned = prunable.PruneOldFiles(storageConfig.Parameters);
                                    if (json)
                                    {
                                        Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new
                                        {
                                            status = "ok",
                                            operation = "prune",
                                            deletedFiles = pruned.DeletedFiles,
                                            bytesReclaimed = pruned.BytesReclaimed,
                                        }));
                                    }
                                    else
                                    {
                                        foreach (string deleted in pruned.DeletedPaths)
                                        {
                                            Console.WriteLine(string.Format("deleted: {0}", deleted));
                                        }
                                        Console.WriteLine(string.Format("Pruned {0} file(s), reclaimed {1} bytes.", pruned.DeletedFiles, pruned.BytesReclaimed));
                                    }
                                    return 0;
                                }
                                catch (Exception e)
                                {
                                    if (json)
                                    {
                                        PrintJsonError("prune", InnermostMessage(e));
                                    }
                                    else
                                    {
                                        HandleException(e, true, false);
                                    }
                                    return -1;
                                }
                            }
                        case "listplugins":
                            PrintPlugins(pipelineComponents, databaseComponents, storageComponents);
                            return 0;
                        case "helpplugin":
                            if (args.Length < 2)
                            {
                                Console.WriteLine("Please give a plugin name, like sqlsqueeze helpplugin <plugin>");
                                return -1;
                            }
                            else
                            {
                                return PrintPluginHelp(args[1], pipelineComponents, databaseComponents, storageComponents);
                            }

                        case "version":
                            Version version = Assembly.GetEntryAssembly().GetName().Version;
                            Console.WriteLine(string.Format("sqlsqueeze v{0} ({1}, {2})",
                                version,
                                System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
                                System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture));
                            return 0;
                        default:
                            Console.WriteLine(string.Format("Unknown command: {0}", args[0]));
                            PrintUsage();
                            return -1;
                    }
                }
            }

            catch (Exception e)
            {

                if (json)
                {
                    PrintJsonError(args.Length > 0 ? args[0].ToLowerInvariant() : "cli", InnermostMessage(e));
                    return -1;
                }

                Util.WriteError(e);

                Exception ie = e;
                while (ie.InnerException != null)
                {
                    ie = ie.InnerException;
                }
                if (!ie.Equals(e))
                {
                    Console.WriteLine(ie.Message);
                }

                PrintUsage();

                return -1;
            }


        }


        // --copy-to <storage> (repeatable), --copy-max-hours, --copy-backoff-seconds: taken out of the argument
        // list, wherever they are, before the positional arguments are read.
        private static List<ConfigPair> ExtractCopyTo(List<string> args, Dictionary<string, Type> storageComponents, out CopyOptions options)
        {
            List<ConfigPair> destinations = new List<ConfigPair>();
            double maxHours = 24;
            double backoffSeconds = 5;
            for (int i = 0; i < args.Count; )
            {
                string flag = args[i].ToLowerInvariant();
                if (flag != "--copy-to" && flag != "--copy-max-hours" && flag != "--copy-backoff-seconds")
                {
                    i++;
                    continue;
                }
                if (i + 1 >= args.Count)
                {
                    throw new ArgumentException(string.Format("{0} needs a value.", args[i]));
                }
                string value = args[i + 1];
                if (flag == "--copy-to")
                {
                    destinations.Add(ConfigUtil.ParseComponentConfig(storageComponents, StorageArgToPlugin(value)));
                }
                else if (flag == "--copy-max-hours")
                {
                    maxHours = ParseNumber(value, "--copy-max-hours");
                }
                else
                {
                    backoffSeconds = ParseNumber(value, "--copy-backoff-seconds");
                }
                args.RemoveRange(i, 2);
            }
            options = new CopyOptions { MaxDuration = TimeSpan.FromHours(maxHours), BackoffBase = TimeSpan.FromSeconds(backoffSeconds) };
            return destinations;
        }

        private static object CopyJson(CopyOutcome c)
        {
            return new
            {
                destination = c.Destination,
                status = c.Ok ? "ok" : "error",
                attempts = c.Attempts,
                bytesCopied = c.BytesCopied,
                bytesSkipped = c.BytesSkipped,
                problems = c.Problems,
            };
        }

        private static void PrintCopies(BackupStats stats)
        {
            foreach (CopyOutcome copy in stats.Copies)
            {
                if (copy.Ok)
                {
                    Console.WriteLine(string.Format("copied to {0}: {1} bytes ({2} attempt(s)){3}", copy.Destination, copy.BytesCopied, copy.Attempts,
                        copy.BytesSkipped > 0 ? string.Format(", {0} bytes already there were not sent again", copy.BytesSkipped) : string.Empty));
                }
                else
                {
                    Console.WriteLine(string.Format("copy to {0} FAILED: {1}", copy.Destination, string.Join("; ", copy.Problems)));
                }
            }
            if (stats.CopyProblems.Count > 0)
            {
                Console.WriteLine("The backup itself succeeded. Run 'sqlsqueeze copy <source> <destination>' to retry the copy.");
            }
        }

        private static void PrintSuccess(string operation, BackupStats stats, bool json, VerifyResult verified = null)
        {
            if (json)
            {
                Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new
                {
                    status = verified == null || verified.Ok ? "ok" : "error",
                    operation = operation,
                    bytesProcessed = stats.BytesProcessed,
                    estimatedBytes = stats.EstimatedBytes,
                    elapsedSeconds = stats.Elapsed.TotalSeconds,
                    mbPerSecond = stats.MegaBytesPerSecond,
                    warnings = stats.Warnings,
                    copies = stats.Copies.ConvertAll(CopyJson),
                    copyProblems = stats.CopyProblems,
                    verify = verified == null ? null : new
                    {
                        ok = verified.Ok,
                        manifestFound = verified.ManifestFound,
                        storedBytes = verified.StoredBytes,
                        rawBytes = verified.RawBytes,
                        problems = verified.Problems,
                    },
                }));
            }
            else
            {
                Console.WriteLine(string.Format("Completed Successfully. {0}", stats.Elapsed));
                Console.WriteLine(string.Format("{0} bytes processed ({1:0.00} MB/s).",
                    stats.BytesProcessed, stats.MegaBytesPerSecond));
                foreach (string warning in stats.Warnings)
                {
                    Console.WriteLine("warning: " + warning);
                }
                PrintCopies(stats);
                if (verified != null)
                {
                    PrintVerifyResult(verified, false);
                }
            }
        }

        // file:///x is shorthand for local(path=/x).
        private static string StorageArgToPlugin(string storageArg)
        {
            if (storageArg.StartsWith("file://"))
            {
                return string.Format("local(path={0})", new Uri(storageArg).LocalPath.Replace(";", ";;"));
            }
            return storageArg;
        }

        // sqlsqueeze backup-all <databases> [pipeline...] <storage> [--db "db(...)"] [--type t] [--no-fallback]
        private static int BackupAll(string[] args, bool json, Dictionary<string, Type> pipelineComponents, Dictionary<string, Type> databaseComponents, Dictionary<string, Type> storageComponents)
        {
            string dbSpec = "db";
            string type = "full";
            bool changeBackupType = true;
            bool verify = false;
            List<string> allArgs = new List<string>(args);
            allArgs.RemoveAt(0);
            List<ConfigPair> copyTo = ExtractCopyTo(allArgs, storageComponents, out CopyOptions copyOptions);
            args = System.Linq.Enumerable.ToArray(System.Linq.Enumerable.Concat(new[] { args[0] }, allArgs));
            List<string> positional = new List<string>();
            for (int i = 1; i < args.Length; i++)
            {
                switch (args[i].ToLowerInvariant())
                {
                    case "--db":
                        dbSpec = NextArg(args, ref i, "--db");
                        break;
                    case "--type":
                        type = NextArg(args, ref i, "--type").ToLowerInvariant();
                        if (type == "diff")
                        {
                            type = "differential";
                        }
                        break;
                    case "--no-fallback":
                        changeBackupType = false;
                        break;
                    case "--verify":
                        verify = true;
                        break;
                    default:
                        positional.Add(args[i]);
                        break;
                }
            }
            if (positional.Count < 2)
            {
                throw new ArgumentException("Usage: sqlsqueeze backup-all <databases> [pipeline...] <storage> [--db \"db(user=sa;passwordEnv=PW)\"] [--type full|diff|log] [--no-fallback] [--json]");
            }

            ConfigPair databaseConfig = ConfigUtil.ParseComponentConfig(databaseComponents, dbSpec);
            ConfigPair storageConfig = ConfigUtil.ParseComponentConfig(storageComponents, StorageArgToPlugin(positional[positional.Count - 1]));
            List<ConfigPair> pipelineConfig = BuildPipelineFromString(positional.GetRange(1, positional.Count - 2), pipelineComponents);

            BackupAllResult result = BackupAllRunner.Run(new SqlDatabaseCatalog(databaseConfig), positional[0], type, changeBackupType,
                databaseConfig, pipelineConfig, storageConfig,
                (db, pipe, storage) =>
                {
                    string database = db.Parameters["database"][0];
                    string backupType = db.Parameters["backuptype"][0];
                    if (!json)
                    {
                        Console.WriteLine(string.Format("=== {0} ({1})", database, backupType));
                    }
                    List<ConfigPair> copies = copyTo.ConvertAll(c => BackupAllRunner.ExpandTokens(c, database, backupType));
                    BackupStats stats = BackupPipeSystem.Backup(db, pipe, storage, new CommandLineNotifier(true, !json), copies, copyOptions);
                    if (verify)
                    {
                        VerifyResult verified = BackupPipeSystem.Verify(storage, pipe);
                        if (!json)
                        {
                            PrintVerifyResult(verified, false);
                        }
                        if (!verified.Ok)
                        {
                            throw new InvalidOperationException(string.Format("verify failed for {0}: {1}", database, string.Join("; ", verified.Problems.ToArray())));
                        }
                    }
                    if (!json)
                    {
                        PrintCopies(stats);
                    }
                    return stats;
                }, copyTo);
            PrintBackupAllResult(result, json);
            return result.AllOk ? 0 : -1;
        }

        // sqlsqueeze copy <source-storage> <destination-storage> [--max-hours H] [--backoff-seconds S]
        private static int Copy(string[] args, bool json, Dictionary<string, Type> storageComponents)
        {
            double maxHours = 24;
            double backoffSeconds = 5;
            List<string> positional = new List<string>();
            for (int i = 1; i < args.Length; i++)
            {
                switch (args[i].ToLowerInvariant())
                {
                    case "--max-hours":
                        maxHours = ParseNumber(NextArg(args, ref i, "--max-hours"), "--max-hours");
                        break;
                    case "--backoff-seconds":
                        backoffSeconds = ParseNumber(NextArg(args, ref i, "--backoff-seconds"), "--backoff-seconds");
                        break;
                    default:
                        positional.Add(args[i]);
                        break;
                }
            }
            if (positional.Count != 2)
            {
                throw new ArgumentException("Usage: sqlsqueeze copy <source-storage> <destination-storage> [--max-hours H] [--backoff-seconds S] [--json]");
            }

            ConfigPair source = ConfigUtil.ParseComponentConfig(storageComponents, StorageArgToPlugin(positional[0]));
            ConfigPair destination = ConfigUtil.ParseComponentConfig(storageComponents, StorageArgToPlugin(positional[1]));
            CopyOptions options = new CopyOptions
            {
                MaxDuration = TimeSpan.FromHours(maxHours),
                BackoffBase = TimeSpan.FromSeconds(backoffSeconds),
                OnAttemptFailed = (attempt, e) =>
                {
                    if (!json)
                    {
                        Console.WriteLine(string.Format("attempt {0} failed: {1}", attempt, InnermostMessage(e)));
                    }
                },
            };

            DateTime started = DateTime.UtcNow;
            CopyResult result = BackupPipeSystem.Copy(source, destination, options);
            double seconds = (DateTime.UtcNow - started).TotalSeconds;

            if (json)
            {
                Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new
                {
                    status = result.Ok ? "ok" : "error",
                    operation = "copy",
                    attempts = result.Attempts,
                    bytesCopied = result.BytesCopied,
                    bytesSkipped = result.BytesSkipped,
                    elapsedSeconds = seconds,
                    warnings = result.Warnings,
                    problems = result.Problems,
                }));
            }
            else
            {
                foreach (string warning in result.Warnings)
                {
                    Console.WriteLine("warning: " + warning);
                }
                foreach (string problem in result.Problems)
                {
                    Console.WriteLine("problem: " + problem);
                }
                Console.WriteLine(result.Ok
                    ? string.Format("Copied {0} bytes in {1:0.0}s ({2} attempt(s)).", result.BytesCopied, seconds, result.Attempts)
                    : "Copy FAILED. Nothing is lost: run the same command again to retry.");
                if (result.BytesSkipped > 0)
                {
                    Console.WriteLine(string.Format("{0} of those bytes were already at the destination from an earlier attempt and were not sent again.", result.BytesSkipped));
                }
            }
            return result.Ok ? 0 : -1;
        }

        private static double ParseNumber(string text, string flag)
        {
            double value;
            if (!double.TryParse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out value) || value <= 0)
            {
                throw new ArgumentException(string.Format("{0} needs a positive number, not: {1}", flag, text));
            }
            return value;
        }

        private static void PrintCopyUsage()
        {
            Console.WriteLine("To copy a finished backup, with its manifest, to a second place (another disk or share,");
            Console.WriteLine("S3, Azure), as stored, with no SQL Server needed:");
            Console.WriteLine("\tsqlsqueeze copy file:///backups/app.bak.zst \"s3(endpoint=...;bucket=b;key=app.bak.zst;accesskey=...;secretkeyenv=SK)\"");
            Console.WriteLine("It retries through outages with a growing wait (5s doubling to 5 minutes) for up to 24 hours");
            Console.WriteLine("(--max-hours, --backoff-seconds), checks what it read against the manifest so a damaged");
            Console.WriteLine("source is not copied as if it were good, and writes the manifest last, so a destination");
            Console.WriteLine("with a backup but no manifest is an unfinished copy. Copying to S3 or Azure resumes: a retry,");
            Console.WriteLine("or running the command again later, continues the unfinished upload and does not send again the");
            Console.WriteLine("parts the server already holds with identical content (it still reads the whole source, to check");
            Console.WriteLine("it against the manifest). A copy that is given up, or fails for a reason retrying cannot fix, is");
            Console.WriteLine("aborted, never left as a truncated object. Both storages need the same number of files.");
            Console.WriteLine("Append --json for one machine-readable result line.");
        }

        // sqlsqueeze restore-chain <folder> <database> --db "db(...)" [--as name] [--stopat time] [--norecovery | --standby file] [--replace] [--kill-connections] [--dry-run]
        private static int RestoreChain(string[] args, bool json, Dictionary<string, Type> pipelineComponents, Dictionary<string, Type> databaseComponents)
        {
            string dbSpec = null;
            string asName = null;
            string stopAt = null;
            string standby = null;
            bool noRecovery = false;
            bool replace = false;
            bool kill = false;
            bool dryRun = false;
            List<string> positional = new List<string>();
            for (int i = 1; i < args.Length; i++)
            {
                switch (args[i].ToLowerInvariant())
                {
                    case "--db": dbSpec = NextArg(args, ref i, "--db"); break;
                    case "--as": asName = NextArg(args, ref i, "--as"); break;
                    case "--stopat": stopAt = NextArg(args, ref i, "--stopat"); break;
                    case "--standby": standby = NextArg(args, ref i, "--standby"); break;
                    case "--norecovery": noRecovery = true; break;
                    case "--replace": replace = true; break;
                    case "--kill-connections": kill = true; break;
                    case "--dry-run": dryRun = true; break;
                    default: positional.Add(args[i]); break;
                }
            }
            if (positional.Count != 2 || dbSpec == null)
            {
                throw new ArgumentException("Usage: sqlsqueeze restore-chain <folder> <database> --db \"db(user=sa;passwordEnv=PW)\" [--as name] [--stopat time] [--norecovery | --standby file] [--replace] [--kill-connections] [--dry-run] [--json]");
            }
            if (noRecovery && standby != null)
            {
                throw new ArgumentException("Use --norecovery or --standby, not both.");
            }

            DateTime? stopAtUtc = null;
            if (stopAt != null)
            {
                DateTime parsed;
                if (!DateTime.TryParse(stopAt, out parsed))
                {
                    throw new ArgumentException(string.Format("--stopat could not be understood as a date and time: {0}", stopAt));
                }
                stopAtUtc = parsed.ToUniversalTime();
            }

            ChainOptions options = new ChainOptions
            {
                TargetDatabase = asName,
                Mode = standby != null ? ChainMode.Standby : (noRecovery ? ChainMode.NoRecovery : ChainMode.Recovery),
                StandbyFile = standby,
                Replace = replace,
                KillConnections = kill,
                StopAt = stopAt,
            };
            ConfigPair databaseConfig = ConfigUtil.ParseComponentConfig(databaseComponents, dbSpec);

            ChainRun run = BackupPipeSystem.RestoreChain(positional[0], positional[1], databaseConfig, pipelineComponents, options, stopAtUtc, dryRun, new CommandLineNotifier(false, !json));

            string status = run.DryRun ? "planned" : (run.Result.Ok ? "ok" : "error");
            if (json)
            {
                Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new
                {
                    status = status == "planned" ? "ok" : status,
                    operation = "restore-chain",
                    dryRun = run.DryRun,
                    database = options.TargetDatabase,
                    bytesProcessed = run.Result.BytesProcessed,
                    steps = run.Result.Steps.ConvertAll(r => new { file = r.FileName, backupType = r.BackupType, status = r.Status, message = r.Message, bytesProcessed = r.BytesProcessed }),
                    warnings = run.Warnings,
                    problems = run.Result.Problems,
                }));
            }
            else
            {
                Console.WriteLine();
                foreach (string warning in run.Warnings)
                {
                    Console.WriteLine("warning: " + warning);
                }
                Console.WriteLine(string.Format("{0} {1} ({2} step(s)):", run.DryRun ? "Plan to restore" : "Restore of", options.TargetDatabase, run.Result.Steps.Count));
                foreach (ChainStepResult step in run.Result.Steps)
                {
                    Console.WriteLine(string.Format("  {0,-8} {1,-12} {2}{3}", step.Status.ToUpperInvariant(), step.BackupType, step.FileName,
                        string.IsNullOrEmpty(step.Message) ? string.Empty : ": " + step.Message));
                }
                if (!run.DryRun)
                {
                    Console.WriteLine(run.Result.Ok ? "Chain restored." : "Chain restore FAILED; the database is left restoring so it can be continued once the cause is fixed.");
                }
            }
            return run.Result.Ok ? 0 : -1;
        }

        private static void PrintRestoreChainUsage()
        {
            Console.WriteLine("To restore the latest full, differential and log backups of a database from a folder:");
            Console.WriteLine("\tsqlsqueeze restore-chain /backups app --db \"db(user=sa;passwordEnv=SQL_PW)\"");
            Console.WriteLine("It reads the manifests under the folder (subfolders included, so backup-all's layout works),");
            Console.WriteLine("takes the latest full, the latest differential after it and every log after that, and");
            Console.WriteLine("restores them in order with the pipeline each manifest records (NORECOVERY until the last");
            Console.WriteLine("step). Copy-only backups are not part of a chain. Local folders only for now.");
            Console.WriteLine("--stopat <time> restores what was current at that time (point in time, server local time).");
            Console.WriteLine("--as <name> restores under another name, placing the files in the server's default folders.");
            Console.WriteLine("--norecovery leaves the database restoring; --standby <file> leaves it readable between steps.");
            Console.WriteLine("--replace and --kill-connections apply to the first step. --dry-run only prints the plan.");
            Console.WriteLine("Selection goes by when each backup was taken; SQL Server still checks the log sequence");
            Console.WriteLine("numbers, so a gap fails at that step instead of restoring wrongly. Append --json for one result line.");
        }

        // --from-manifest: the pipeline comes from the manifest next to the backup, so it must not also be typed.
        private static List<ConfigPair> PipelineFromManifest(ConfigPair storageConfig, List<ConfigPair> typedPipeline, Dictionary<string, Type> pipelineComponents)
        {
            if (typedPipeline.Count > 0)
            {
                throw new ArgumentException("Give the pipeline on the command line or use --from-manifest, not both.");
            }
            BackupManifest manifest = BackupPipeSystem.ReadManifest(storageConfig);
            if (manifest == null)
            {
                throw new ArgumentException("--from-manifest needs the manifest written next to the backup, and there is none here. Give the pipeline on the command line instead.");
            }
            return BuildPipelineFromString(manifest.PipelineSpecsForRestore(), pipelineComponents);
        }

        // sqlsqueeze unpack <storage> [pipeline...] <native-output> [--from-manifest]
        private static int Unpack(string[] args, bool json, Dictionary<string, Type> pipelineComponents, Dictionary<string, Type> storageComponents)
        {
            bool fromManifest = false;
            List<string> positional = new List<string>();
            for (int i = 1; i < args.Length; i++)
            {
                if (args[i].Equals("--from-manifest", StringComparison.OrdinalIgnoreCase))
                {
                    fromManifest = true;
                }
                else
                {
                    positional.Add(args[i]);
                }
            }
            if (positional.Count < 2)
            {
                throw new ArgumentException("Usage: sqlsqueeze unpack <storage> [pipeline...] <native-output> [--from-manifest] [--json]");
            }

            ConfigPair source = ConfigUtil.ParseComponentConfig(storageComponents, StorageArgToPlugin(positional[0]));
            ConfigPair destination = ConfigUtil.ParseComponentConfig(storageComponents, StorageArgToPlugin(positional[positional.Count - 1]));
            List<ConfigPair> pipelineConfig = BuildPipelineFromString(positional.GetRange(1, positional.Count - 2), pipelineComponents);
            if (fromManifest)
            {
                pipelineConfig = PipelineFromManifest(source, pipelineConfig, pipelineComponents);
            }

            UnpackResult result = BackupPipeSystem.Unpack(source, pipelineConfig, destination);
            if (json)
            {
                Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new
                {
                    status = result.Ok ? "ok" : "error",
                    operation = "unpack",
                    manifestFound = result.ManifestFound,
                    bytesWritten = result.BytesWritten,
                    problems = result.Problems,
                }));
            }
            else
            {
                foreach (string problem in result.Problems)
                {
                    Console.WriteLine("problem: " + problem);
                }
                if (!result.Ok)
                {
                    Console.WriteLine("Unpack FAILED; the output was removed.");
                }
                else if (result.ManifestFound)
                {
                    Console.WriteLine(string.Format("Unpacked {0} bytes; sizes and sha256 of the stored file and of the native output match the manifest.", result.BytesWritten));
                }
                else
                {
                    Console.WriteLine(string.Format("Unpacked {0} bytes. There was no manifest, so the output could not be compared with what was backed up.", result.BytesWritten));
                }
            }
            return result.Ok ? 0 : -1;
        }

        // sqlsqueeze test-restore <storage> [pipeline...] --db "db(...)" [--keep] [--physical-only]
        private static int TestRestore(string[] args, bool json, Dictionary<string, Type> pipelineComponents, Dictionary<string, Type> databaseComponents, Dictionary<string, Type> storageComponents)
        {
            string dbSpec = null;
            bool keep = false;
            bool physicalOnly = false;
            bool fromManifest = false;
            List<string> positional = new List<string>();
            for (int i = 1; i < args.Length; i++)
            {
                switch (args[i].ToLowerInvariant())
                {
                    case "--db":
                        dbSpec = NextArg(args, ref i, "--db");
                        break;
                    case "--keep":
                        keep = true;
                        break;
                    case "--physical-only":
                        physicalOnly = true;
                        break;
                    case "--from-manifest":
                        fromManifest = true;
                        break;
                    default:
                        positional.Add(args[i]);
                        break;
                }
            }
            if (positional.Count < 1 || dbSpec == null)
            {
                throw new ArgumentException("Usage: sqlsqueeze test-restore <storage> [pipeline...] --db \"db(user=sa;passwordEnv=PW)\" [--keep] [--physical-only] [--json]");
            }

            ConfigPair storageConfig = ConfigUtil.ParseComponentConfig(storageComponents, StorageArgToPlugin(positional[0]));
            List<ConfigPair> pipelineConfig = BuildPipelineFromString(positional.GetRange(1, positional.Count - 1), pipelineComponents);
            if (fromManifest)
            {
                pipelineConfig = PipelineFromManifest(storageConfig, pipelineConfig, pipelineComponents);
            }
            ConfigPair databaseConfig = ConfigUtil.ParseComponentConfig(databaseComponents, dbSpec);

            DateTime started = DateTime.UtcNow;
            TestRestoreResult result = BackupPipeSystem.TestRestore(storageConfig, pipelineConfig, databaseConfig, keep, physicalOnly, new CommandLineNotifier(false, !json));
            double seconds = (DateTime.UtcNow - started).TotalSeconds;

            if (json)
            {
                Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new
                {
                    status = result.Ok ? "ok" : "error",
                    operation = "test-restore",
                    scratchDatabase = result.ScratchDatabase,
                    restoredBytes = result.RestoredBytes,
                    dropped = result.Dropped,
                    elapsedSeconds = seconds,
                    problems = result.Problems,
                }));
            }
            else
            {
                Console.WriteLine();
                foreach (string problem in result.Problems)
                {
                    Console.WriteLine("problem: " + problem);
                }
                if (result.Ok)
                {
                    Console.WriteLine(string.Format("Test restore passed: {0} bytes restored into {1} and DBCC CHECKDB found no errors ({2:0.0}s).", result.RestoredBytes, result.ScratchDatabase, seconds));
                }
                else
                {
                    Console.WriteLine("Test restore FAILED.");
                }
                Console.WriteLine(result.Dropped ? string.Format("Dropped {0}.", result.ScratchDatabase) : string.Format("{0} was NOT dropped; remove it when you are done.", result.ScratchDatabase));
            }
            return result.Ok ? 0 : -1;
        }

        private static string NextArg(string[] args, ref int i, string flag)
        {
            if (i + 1 >= args.Length)
            {
                throw new ArgumentException(string.Format("{0} needs a value.", flag));
            }
            i++;
            return args[i];
        }

        private static void PrintBackupAllResult(BackupAllResult result, bool json)
        {
            if (json)
            {
                Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new
                {
                    status = result.AllOk ? "ok" : "error",
                    operation = "backup-all",
                    results = result.Items.ConvertAll(i => new
                    {
                        database = i.Database,
                        backupType = i.BackupType,
                        status = i.Status,
                        message = i.Message,
                        bytesProcessed = i.Stats == null ? (long?)null : i.Stats.BytesProcessed,
                        elapsedSeconds = i.Stats == null ? (double?)null : i.Stats.Elapsed.TotalSeconds,
                        copies = i.Stats == null ? null : i.Stats.Copies.ConvertAll(CopyJson),
                        copyProblems = i.CopyProblems,
                    }),
                }));
                return;
            }

            Console.WriteLine();
            foreach (BackupAllItem item in result.Items)
            {
                Console.WriteLine(string.Format("{0,-8} {1} ({2}){3}{4}", item.Status.ToUpperInvariant(), item.Database, item.BackupType,
                    string.IsNullOrEmpty(item.Message) ? string.Empty : ": " + item.Message,
                    item.CopyProblems.Count == 0 ? string.Empty : " -- COPY FAILED: " + string.Join("; ", item.CopyProblems)));
            }
            Console.WriteLine(result.AllOk ? "All requested backups completed." : "Some backups FAILED.");
        }

        private static void PrintVerifyResult(VerifyResult verified, bool json)
        {
            if (json)
            {
                Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new
                {
                    status = verified.Ok ? "ok" : "error",
                    operation = "verify",
                    manifestFound = verified.ManifestFound,
                    storedBytes = verified.StoredBytes,
                    rawBytes = verified.RawBytes,
                    sqlChecked = verified.SqlChecked,
                    sqlMessage = verified.SqlMessage,
                    problems = verified.Problems,
                }));
                return;
            }

            foreach (string problem in verified.Problems)
            {
                Console.WriteLine("problem: " + problem);
            }
            if (!verified.Ok)
            {
                Console.WriteLine("Verification FAILED.");
            }
            else if (verified.ManifestFound)
            {
                Console.WriteLine(string.Format("Verified: {0} bytes stored, {1} bytes decoded; sizes and sha256 match the manifest.", verified.StoredBytes, verified.RawBytes));
                if (verified.SqlChecked)
                {
                    Console.WriteLine(verified.SqlMessage);
                }
            }
            else
            {
                Console.WriteLine(string.Format("Decoded {0} bytes without error, but there is no manifest to compare against, so corruption that still decodes cleanly would not be caught.", verified.RawBytes));
                if (verified.SqlChecked)
                {
                    Console.WriteLine(verified.SqlMessage);
                }
            }
        }

        private static void PrintJsonError(string operation, string message)
        {
            Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new
            {
                status = "error",
                operation = operation,
                message = message,
            }));
        }

        private static string InnermostMessage(Exception e)
        {
            while (e.InnerException != null)
            {
                e = e.InnerException;
            }
            return e.Message;
        }

        private static void HandleExecutionExceptions(ParallelExecutionException ee, bool isBackup, bool json)
        {
            string operation = isBackup ? "backup" : "restore";
            if (json)
            {
                PrintJsonError(operation, InnermostMessage(ee.Exceptions.Count > 0 ? ee.Exceptions[0] : ee));
                return;
            }

            int i = 1;
            foreach (Exception e in ee.Exceptions)
            {
                Console.WriteLine("------------------------");
                Console.WriteLine(string.Format("Exception #{0}", i));
                Util.WriteError(e);
                Console.WriteLine();
                i++;
            }

            Console.WriteLine();
            Console.WriteLine(string.Format("The {0} failed.", operation));


            PrintUsage();
        }


        private static void HandleException(Exception e, bool isBackup, bool json)
        {
            string operation = isBackup ? "backup" : "restore";
            if (json)
            {
                PrintJsonError(operation, InnermostMessage(e));
                return;
            }

            Util.WriteError(e);

            Exception ie = e;
            while (ie.InnerException != null)
            {
                ie = ie.InnerException;
            }
            if (!ie.Equals(e))
            {
                Console.WriteLine(ie.Message);
            }

            Console.WriteLine();
            Console.WriteLine(string.Format("The {0} failed.", operation));

            PrintUsage();
        }

        private static List<string> CopySubArgs(string[] args)
        {
            List<string> result = new List<string>(args.Length - 2);
            for (int i = 1; i < args.Length; i++)
            {
                result.Add(args[i]);
            }
            return result;
        }



        private static List<ConfigPair> ParseBackupOrRestoreArgs(List<string> args, bool isBackup, Dictionary<string, Type> pipelineComponents, Dictionary<string, Type> databaseComponents, Dictionary<string, Type> storageComponents, out ConfigPair databaseConfig, out ConfigPair storageConfig)
        {
            if (args.Count < 2)
            {
                throw new ArgumentException("Please provide both the database and storage plugins after the backup subcommand.");
            }


            string databaseArg;
            string storageArg;

            if (isBackup)
            {
                databaseArg = args[0];
                storageArg = args[args.Count - 1];
            }
            else
            {
                databaseArg = args[args.Count - 1];
                storageArg = args[0];
            }


            if (databaseArg.Contains("://"))
            {
                throw new ArgumentException("The first sub argument must be the name of the database.");
            }

            if (databaseArg[0] == '[' && databaseArg[databaseArg.Length - 1] == ']')
            {
                databaseArg = string.Format("db(database={0})", databaseArg.Substring(1, databaseArg.Length - 2).Replace(";", ";;"));
            }

            databaseConfig = ConfigUtil.ParseComponentConfig(databaseComponents, databaseArg);



            if (storageArg[0] == '[' && storageArg[databaseArg.Length - 1] == ']')
            {
                throw new ArgumentException("The last sub argument must be a storage plugin.");
            }

            if (storageArg.StartsWith("file://"))
            {
                Uri uri = new Uri(storageArg);
                storageArg = string.Format("local(path={0})", uri.LocalPath.Replace(";", ";;"));
            }


            storageConfig = ConfigUtil.ParseComponentConfig(storageComponents, storageArg);



            List<string> pipelineArgs = new List<string>();
            for (int i = 1; i < args.Count - 1; i++)
            {
                pipelineArgs.Add(args[i]);
            }


            List<ConfigPair> pipeline = BuildPipelineFromString(pipelineArgs, pipelineComponents);


            return pipeline;
        }





        private static List<ConfigPair> BuildPipelineFromString(List<string> pipelineArgs, Dictionary<string, Type> pipelineComponents)
        {

            for (int i = 0; i < pipelineArgs.Count; i++)
            {
                pipelineArgs[i] = pipelineArgs[i].Trim();
            }

            List<ConfigPair> results = new List<ConfigPair>(pipelineArgs.Count);

            foreach (string componentString in pipelineArgs)
            {
                ConfigPair config = ConfigUtil.ParseComponentConfig(pipelineComponents, componentString);

                results.Add(config);
            }


            return results;
        }




        private static void PrintUsage()
        {
            Console.WriteLine("Below are the commands for sqlsqueeze:");
            Console.WriteLine("\tsqlsqueeze help");
            Console.WriteLine("\tsqlsqueeze backup");
            Console.WriteLine("\tsqlsqueeze restore");
            Console.WriteLine("\tsqlsqueeze prune");
            Console.WriteLine("\tsqlsqueeze backup-all");
            Console.WriteLine("\tsqlsqueeze verify");
            Console.WriteLine("\tsqlsqueeze test-restore");
            Console.WriteLine("\tsqlsqueeze unpack");
            Console.WriteLine("\tsqlsqueeze restore-chain");
            Console.WriteLine("\tsqlsqueeze copy");
            Console.WriteLine("\tsqlsqueeze listplugins");
            Console.WriteLine("\tsqlsqueeze helpplugin");
            Console.WriteLine("\tsqlsqueeze version");
            Console.WriteLine("");
            Console.WriteLine("For more information, type sqlsqueeze help <command>");
        }

        private static void PrintBackupUsage()
        {
            Console.WriteLine("To backup a database, the first parameter must be the database in brackets, and the last parameter must be the file.  The middle parameters can modify the data, for example compressing it.");
            Console.WriteLine("To backup to a standard *.bak file:");
            Console.WriteLine("\tsqlsqueeze backup [model] file:///c:\\model.bak");
            Console.WriteLine("To compress the backup file using gzip:");
            Console.WriteLine("\tsqlsqueeze backup [model] gzip file:///c:\\model.bak.gz");
            Console.WriteLine("");
            Console.WriteLine("To copy the finished backup (and its manifest) elsewhere straight after, add one or more");
            Console.WriteLine("--copy-to <storage>, for example a second disk, S3 or Azure:");
            Console.WriteLine("\tsqlsqueeze backup [model] zstd file:///backups/model_{yyyyMMdd}.bak.zst --copy-to \"s3(...)\"");
            Console.WriteLine("The copy retries through outages like 'sqlsqueeze copy' (--copy-max-hours, --copy-backoff-seconds).");
            Console.WriteLine("If a copy fails the backup still stands, the failure is reported and the exit code is -1.");
            Console.WriteLine("");
            Console.WriteLine("For more information on the different pipline options, type sqlsqueeze listplugins");
            Console.WriteLine("--verify checks the finished backup against its manifest right away (hashes,");
            Console.WriteLine("no SQL Server needed); a failed verify fails the backup with exit code -1.");
            Console.WriteLine("Append --json for one machine-readable result line (exit code still 0/-1).");
        }

        private static void PrintRestoreUsage()
        {
            Console.WriteLine("To restore a database, the first parameter must be the file, and the last parameter must be the database in brackets.  The middle parameters can modify the data, for example uncompressing it.");
            Console.WriteLine("To restore to a standard *.bak file:");
            Console.WriteLine("\tsqlsqueeze restore file:///c:\\model.bak [model]");
            Console.WriteLine("--kill-connections rolls back other sessions first, so a restore over a database that is");
            Console.WriteLine("in use can get exclusive access (it puts the database back to multi-user if the restore");
            Console.WriteLine("fails). --from-manifest takes the pipeline from the manifest next to the backup.");
            Console.WriteLine("After a restore that leaves the database usable, users whose login does not exist on this");
            Console.WriteLine("server are reported as a warning (orphaned users).");
            Console.WriteLine("To compress the backup file using gzip:");
            Console.WriteLine("\tsqlsqueeze restore file:///c:\\model.bak.gz gzip [model]");
            Console.WriteLine("");
            Console.WriteLine("For more information on the different pipline options, type sqlsqueeze listplugins");
            Console.WriteLine("Append --json for one machine-readable result line (exit code still 0/-1).");
        }


        private static void PrintPruneUsage()
        {
            Console.WriteLine("To delete aged backup files (retention):");
            Console.WriteLine("\tsqlsqueeze prune \"local(dir=c:\\backups;pattern=*.bak.gz;olderThanDays=30)\"");
            Console.WriteLine("Deletes files in dir matching pattern (default *) whose last-write");
            Console.WriteLine("time is older than olderThanDays (minimum 1). Subfolders are skipped.");
            Console.WriteLine("Append --json for one machine-readable result line.");
        }

        private static void PrintUnpackUsage()
        {
            Console.WriteLine("To turn a SqlSqueeze backup back into the plain native .bak SQL Server wrote (no SQL");
            Console.WriteLine("Server needed, and no lock-in to this tool):");
            Console.WriteLine("\tsqlsqueeze unpack file:///backups/app.bak.zst.aes zstd \"aes(passwordEnv=KEY)\" file:///restore/app.bak");
            Console.WriteLine("\tsqlsqueeze unpack file:///backups/app.bak.zst.aes file:///restore/app.bak --from-manifest");
            Console.WriteLine("The storage comes first, the native output last, the same pipeline as restore between them");
            Console.WriteLine("(or --from-manifest to take it from the manifest). Striped backups need one output file per");
            Console.WriteLine("stripe. The output is checked against the manifest as it is written, and removed if");
            Console.WriteLine("anything differs. It never overwrites an existing file or the source.");
            Console.WriteLine("Append --json for one machine-readable result line.");
        }

        private static void PrintTestRestoreUsage()
        {
            Console.WriteLine("To prove a full backup restores to a healthy database:");
            Console.WriteLine("\tsqlsqueeze test-restore file:///backups/app/app_full.bak.zst zstd \\");
            Console.WriteLine("\t    --db \"db(user=sa;passwordEnv=SQL_PW)\"");
            Console.WriteLine("It restores the backup into a scratch database named sqs_testrestore_<db>_<time> in the");
            Console.WriteLine("server's default data and log folders (the original database is never touched), runs");
            Console.WriteLine("DBCC CHECKDB on it, and drops it again. Needs the manifest next to the backup (for the");
            Console.WriteLine("database name and file list), the SQL Server machine (on Linux the mssql user), and free");
            Console.WriteLine("disk space for a copy of the database. Only full backups. It refuses to start if a");
            Console.WriteLine("database with the scratch name exists, and only ever drops the one it created.");
            Console.WriteLine("--from-manifest takes the pipeline from the manifest instead of the command line.");
            Console.WriteLine("--keep leaves the scratch database in place. --physical-only runs the faster");
            Console.WriteLine("DBCC CHECKDB ... PHYSICAL_ONLY. Exit code 0 when it restored and CHECKDB was clean.");
            Console.WriteLine("Append --json for one machine-readable result line.");
        }

        private static void PrintBackupAllUsage()
        {
            Console.WriteLine("To back up several databases, one backup each:");
            Console.WriteLine("\tsqlsqueeze backup-all USER_DATABASES,-scratch* zstd \\");
            Console.WriteLine("\t    \"local(path=/backups/{database}/{database}_{backuptype}_{yyyyMMdd_HHmmss}.bak.zst)\" \\");
            Console.WriteLine("\t    --db \"db(user=sa;passwordEnv=SQL_PW)\"");
            Console.WriteLine("The first argument picks the databases: USER_DATABASES, SYSTEM_DATABASES, ALL_DATABASES,");
            Console.WriteLine("names, * and ? wildcards, comma separated; a leading - excludes. tempdb is never backed up.");
            Console.WriteLine("The last argument is the storage; {database} and {backuptype} in it are replaced per");
            Console.WriteLine("database ({database} is required when more than one database is selected).");
            Console.WriteLine("--db gives the db plugin's login/instance options (no database=). --type full|diff|log");
            Console.WriteLine("(default full). --verify checks each finished backup against its manifest;");
            Console.WriteLine("a failed verify fails that database without stopping the rest.");
            Console.WriteLine("A diff or log backup that cannot be taken yet (no full backup to build on)");
            Console.WriteLine("becomes a full backup unless --no-fallback; log backups of SIMPLE-recovery databases and");
            Console.WriteLine("--copy-to <storage> (repeatable) copies each backup elsewhere right after it is taken; put");
            Console.WriteLine("{database} in its path when several databases are selected. A failed copy is reported and the");
            Console.WriteLine("exit code is -1, the backup itself stands.");
            Console.WriteLine("databases that are not online are skipped and reported. One failing database does not");
            Console.WriteLine("stop the rest; the exit code is -1 if any failed. Append --json for one result line.");
        }

        private static void PrintVerifyUsage()
        {
            Console.WriteLine("To check a stored backup without SQL Server (any machine that has the file):");
            Console.WriteLine("\tsqlsqueeze verify file:///backups/model.bak.zst zstd");
            Console.WriteLine("Give the storage first, then the same pipeline you restore with.");
            Console.WriteLine("The file is read back through the pipeline (decrypt, decompress) and its size and");
            Console.WriteLine("sha256, and those of the decoded stream, are compared with the manifest that");
            Console.WriteLine("backup wrote next to it (<file>.manifest.json). Without a manifest it can only");
            Console.WriteLine("confirm the file decodes. Add --sql \"db(user=sa;passwordEnv=PW)\" to also have SQL Server");
            Console.WriteLine("read the whole backup (RESTORE VERIFYONLY through a virtual device, so it needs the");
            Console.WriteLine("SQL Server machine and, on Linux, the mssql user); skipped if the file already failed.");
            Console.WriteLine("--from-manifest takes the pipeline from the manifest instead of the command line.");
            Console.WriteLine("Exit code 0 when verified, -1 otherwise.");
            Console.WriteLine("Append --json for one machine-readable result line.");
        }

        private static void PrintPlugins(Dictionary<string, Type> pipelineComponents, Dictionary<string, Type> databaseComponents, Dictionary<string, Type> storageComponents)
        {
            Console.WriteLine("Database plugins:");
            PrintComponents(databaseComponents);
            Console.WriteLine("Pipeline plugins:");
            PrintComponents(pipelineComponents);
            Console.WriteLine("Storage plugins:");
            PrintComponents(storageComponents);

            Console.WriteLine("");
            Console.WriteLine("To find more information about a plugin, type sqlsqueeze helpplugin <plugin>");
        }

        private static void PrintComponents(Dictionary<string, Type> components)
        {
            foreach (string key in components.Keys)
            {
                IBackupPlugin db = components[key].GetConstructor(new Type[0]).Invoke(new object[0]) as IBackupPlugin;
                Console.WriteLine("\t" + db.Name);
            }
        }

        private static int PrintPluginHelp(string pluginName, Dictionary<string, Type> pipelineComponents, Dictionary<string, Type> databaseComponents, Dictionary<string, Type> storageComponents)
        {
            PrintPluginHelp(pluginName, databaseComponents);
            PrintPluginHelp(pluginName, pipelineComponents);
            PrintPluginHelp(pluginName, storageComponents);
            return 0;
        }

        private static void PrintPluginHelp(string pluginName, Dictionary<string, Type> components)
        {
            if (components.ContainsKey(pluginName))
            {
                IBackupPlugin db = components[pluginName].GetConstructor(new Type[0]).Invoke(new object[0]) as IBackupPlugin;
                Console.WriteLine(db.CommandLineHelp);
            }
        }

    }
}
