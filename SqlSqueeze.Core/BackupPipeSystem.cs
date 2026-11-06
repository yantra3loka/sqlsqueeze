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
using System.Reflection;
using System.IO;

using SqlSqueeze.Plugins;
using SqlSqueeze.Plugins.Storage;
using VdiNet.VirtualBackupDevice;

namespace SqlSqueeze.Core
{
    public static class BackupPipeSystem
    {
        private static readonly object sCancelLock = new object();
        private static IVirtualDeviceSet sActiveDeviceSet;

        /// <summary>
        /// Ctrl-C entry point: signal VDI abort on the in-flight operation, if any.
        /// Returns true when an abort was signalled (the caller should swallow the
        /// key press and let the normal failure path run), false when nothing is
        /// in flight (the caller should let the process die as before).
        /// </summary>
        public static bool RequestCancel()
        {
            lock (sCancelLock)
            {
                if (sActiveDeviceSet == null)
                {
                    return false;
                }
                try { sActiveDeviceSet.SignalAbort(); }
                catch (Exception) { }
                return true;
            }
        }

        internal static void TrackDeviceSet(IVirtualDeviceSet deviceSet)
        {
            lock (sCancelLock)
            {
                sActiveDeviceSet = deviceSet;
            }
        }

        internal static void UntrackDeviceSet(IVirtualDeviceSet deviceSet)
        {
            lock (sCancelLock)
            {
                if (object.ReferenceEquals(sActiveDeviceSet, deviceSet))
                {
                    sActiveDeviceSet = null;
                }
            }
        }

        /// <summary>Tracks the in-flight VDI set so Ctrl-C can abort it; untracks on dispose.</summary>
        private sealed class ActiveDeviceSet : IDisposable
        {
            private readonly IVirtualDeviceSet mSet;

            public ActiveDeviceSet(IVirtualDeviceSet set)
            {
                mSet = set;
                TrackDeviceSet(set);
            }

            public void Dispose()
            {
                UntrackDeviceSet(mSet);
            }
        }

        public static Dictionary<string, Type> LoadTransformComponents()
        {
            return LoadComponents("IBackupTransformer");
        }
        public static Dictionary<string, Type> LoadDatabaseComponents()
        {
            return LoadComponents("IBackupDatabase");
        }
        public static Dictionary<string, Type> LoadStorageComponents()
        {
            return LoadComponents("IBackupStorage");
        }



        public static BackupStats Backup(ConfigPair databaseConfig, List<ConfigPair> pipelineConfig, ConfigPair storageConfig, IUpdateNotification updateNotifier)
        {
            return BackupOrRestore(true, storageConfig, databaseConfig, pipelineConfig, updateNotifier, null, null);
        }

        /// <summary>
        /// Backs up, then copies the finished backup and its manifest to each destination (retrying
        /// as copy does). The backup stands on its own: a copy that fails is reported in
        /// stats.CopyProblems and never turns a good backup into an exception.
        /// </summary>
        public static BackupStats Backup(ConfigPair databaseConfig, List<ConfigPair> pipelineConfig, ConfigPair storageConfig, IUpdateNotification updateNotifier,
            IList<ConfigPair> copyTo, CopyOptions copyOptions)
        {
            return BackupOrRestore(true, storageConfig, databaseConfig, pipelineConfig, updateNotifier, copyTo, copyOptions);
        }


        public static BackupStats Restore(ConfigPair storageConfig, List<ConfigPair> pipelineConfig, ConfigPair databaseConfig, IUpdateNotification updateNotifier)
        {
            return BackupOrRestore(false, storageConfig, databaseConfig, pipelineConfig, updateNotifier, null, null);
        }


        /// <summary>
        /// The manifest the storage keeps next to a backup, or null when there is none
        /// (or the storage plugin does not keep manifests).
        /// </summary>
        public static BackupManifest ReadManifest(ConfigPair storageConfig)
        {
            IBackupStorage storage = storageConfig.TransformationType.GetConstructor(new Type[0]).Invoke(new object[0]) as IBackupStorage;
            IManifestStorage manifests = storage as IManifestStorage;
            if (manifests == null)
            {
                return null;
            }
            string json = manifests.ReadManifest(storageConfig.Parameters);
            return json == null ? null : BackupManifest.FromJson(json);
        }


        /// <summary>
        /// Checks a stored backup without SQL Server: reads it back through the
        /// pipeline and compares with the manifest written next to it (when the
        /// storage keeps one).
        /// </summary>
        public static VerifyResult Verify(ConfigPair storageConfig, List<ConfigPair> pipelineConfig)
        {
            IBackupStorage storage = storageConfig.TransformationType.GetConstructor(new Type[0]).Invoke(new object[0]) as IBackupStorage;
            BackupManifest manifest = ReadManifest(storageConfig);
            return BackupVerifier.Verify(storage, storageConfig.Parameters, pipelineConfig, manifest);
        }


        /// <summary>
        /// Like Verify, and then also asks SQL Server (RESTORE VERIFYONLY through a
        /// virtual device, so the pipeline is undone first) whether the backup is
        /// valid. Skipped when the stream check already failed: SQL Server would
        /// be reading a file known to be damaged.
        /// </summary>
        public static VerifyResult Verify(ConfigPair storageConfig, List<ConfigPair> pipelineConfig, ConfigPair sqlDatabaseConfig, IUpdateNotification notifier)
        {
            VerifyResult result = Verify(storageConfig, pipelineConfig);
            if (sqlDatabaseConfig == null || !result.Ok)
            {
                return result;
            }
            try
            {
                Restore(storageConfig, pipelineConfig, VerifyOnlyConfig(sqlDatabaseConfig), notifier);
                result.RecordSqlCheck(true, "SQL Server read the whole backup and accepted it (RESTORE VERIFYONLY).");
            }
            catch (Exception e)
            {
                result.RecordSqlCheck(false, ErrorText.Innermost(e));
            }
            return result;
        }


        /// <summary>
        /// Restores a full backup into a scratch database next to the real ones, runs
        /// DBCC CHECKDB on it and drops it, to prove the backup restores to a healthy
        /// database. Needs the manifest written next to the backup.
        /// </summary>
        public static TestRestoreResult TestRestore(ConfigPair storageConfig, List<ConfigPair> pipelineConfig, ConfigPair databaseConfig, bool keep, bool physicalOnly, IUpdateNotification notifier)
        {
            IBackupStorage storage = storageConfig.TransformationType.GetConstructor(new Type[0]).Invoke(new object[0]) as IBackupStorage;
            BackupManifest manifest = ReadManifest(storageConfig);
            return TestRestoreRunner.Run(manifest, new SqlTestRestoreHost(storageConfig, pipelineConfig, databaseConfig, notifier), keep, physicalOnly, DateTime.UtcNow);
        }


        /// <summary>A copy of the db options that makes the restore side run RESTORE VERIFYONLY.</summary>
        internal static ConfigPair VerifyOnlyConfig(ConfigPair databaseConfig, string databaseName = null)
        {
            Dictionary<string, List<string>> parameters = new Dictionary<string, List<string>>(databaseConfig.Parameters.Comparer);
            foreach (KeyValuePair<string, List<string>> pair in databaseConfig.Parameters)
            {
                parameters[pair.Key] = new List<string>(pair.Value);
            }
            parameters["restoretype"] = new List<string> { "verifyonly" };
            if (databaseName != null)
            {
                parameters["database"] = new List<string> { databaseName };
            }
            return new ConfigPair { TransformationType = databaseConfig.TransformationType, Parameters = parameters };
        }


        /// <summary>
        /// Converts a SqlSqueeze backup back to the plain native backup file(s) SQL Server
        /// wrote, by running it through the pipeline and writing the decoded stream to the
        /// destination storage, one destination file per source file. It is checked against
        /// the manifest as it goes; on any problem the output is removed. The destination
        /// must not be (or contain) the source, and a destination file that already exists
        /// is never overwritten.
        /// </summary>
        public static UnpackResult Unpack(ConfigPair sourceConfig, List<ConfigPair> pipelineConfig, ConfigPair destinationConfig)
        {
            IBackupStorage source = sourceConfig.TransformationType.GetConstructor(new Type[0]).Invoke(new object[0]) as IBackupStorage;
            IBackupStorage destination = destinationConfig.TransformationType.GetConstructor(new Type[0]).Invoke(new object[0]) as IBackupStorage;

            int sourceFiles = source.GetNumberOfDevices(sourceConfig.Parameters);
            int destinationFiles = destination.GetNumberOfDevices(destinationConfig.Parameters);
            if (sourceFiles != destinationFiles)
            {
                throw new ArgumentException(string.Format("The source has {0} file(s) but the destination has {1}; give one destination file for each source file.", sourceFiles, destinationFiles));
            }
            if (SamePlace(sourceConfig, destinationConfig))
            {
                throw new ArgumentException("The destination is the same as the source; unpack writes a new file and never replaces the backup it reads.");
            }
            if (destination is LocalStorage)
            {
                foreach (string path in destinationConfig.Parameters["path"])
                {
                    if (File.Exists(path))
                    {
                        throw new ArgumentException(string.Format("{0} already exists; unpack will not overwrite it. Remove it or choose another name.", path));
                    }
                }
            }

            BackupManifest manifest = ReadManifest(sourceConfig);

            Stream[] outputs = destination.GetBackupWriter(destinationConfig.Parameters);
            VerifyResult verified;
            try
            {
                verified = BackupVerifier.Verify(source, sourceConfig.Parameters, pipelineConfig, manifest, outputs);
                if (!verified.Ok)
                {
                    StreamAbort.AbortAll(outputs);
                }
            }
            catch
            {
                StreamAbort.AbortAll(outputs);
                throw;
            }
            finally
            {
                foreach (Stream output in outputs)
                {
                    try { output.Dispose(); } catch (Exception) { }
                }
            }
            if (!verified.Ok)
            {
                destination.CleanupOnAbort();
            }
            return new UnpackResult(verified);
        }

        /// <summary>
        /// Runs when a backup's streams are about to be disposed: if the backup did not complete,
        /// the file streams are aborted first, so a writer to cloud storage discards what it has
        /// instead of completing the upload and leaving a truncated object that looks like a backup.
        /// </summary>
        private sealed class AbortUnlessCompleted : IDisposable
        {
            private readonly bool mIsBackup;
            private readonly IEnumerable<Stream> mFiles;
            private readonly Func<bool> mCompleted;

            public AbortUnlessCompleted(bool isBackup, IEnumerable<Stream> files, Func<bool> completed)
            {
                mIsBackup = isBackup;
                mFiles = files;
                mCompleted = completed;
            }

            public void Dispose()
            {
                if (mIsBackup && !mCompleted())
                {
                    StreamAbort.AbortAll(mFiles);
                }
            }
        }

        private static bool SamePlace(ConfigPair a, ConfigPair b)
        {
            if (a.TransformationType != b.TransformationType)
            {
                return false;
            }
            if (a.TransformationType == typeof(LocalStorage))
            {
                HashSet<string> first = new HashSet<string>();
                foreach (string path in a.Parameters["path"])
                {
                    first.Add(Path.GetFullPath(path));
                }
                foreach (string path in b.Parameters["path"])
                {
                    if (first.Contains(Path.GetFullPath(path)))
                    {
                        return true;
                    }
                }
                return false;
            }
            return string.Join(";", Describe(a)) == string.Join(";", Describe(b));
        }

        private static List<string> Describe(ConfigPair config)
        {
            List<string> parts = new List<string>();
            foreach (KeyValuePair<string, List<string>> pair in config.Parameters)
            {
                parts.Add(pair.Key.ToLowerInvariant() + "=" + string.Join("|", pair.Value));
            }
            parts.Sort(StringComparer.Ordinal);
            return parts;
        }


        /// <summary>
        /// Restores the latest chain found in a folder: the full, differential and logs the
        /// manifests there describe, in order, each with the pipeline its manifest records.
        /// With dryRun nothing is restored and the plan is returned. Restoring under another
        /// name (options.TargetDatabase) places the files in the server's default folders.
        /// </summary>
        public static ChainRun RestoreChain(string folder, string database, ConfigPair databaseTemplate, Dictionary<string, Type> pipelineComponents,
            ChainOptions options, DateTime? stopAtUtc, bool dryRun, IUpdateNotification notifier)
        {
            ChainRun run = new ChainRun { DryRun = dryRun };
            List<ChainCandidate> candidates = RestoreChainScanner.Scan(folder, run.Warnings);
            run.Plan = RestoreChainPlanner.Plan(candidates, database, stopAtUtc);
            if (options.TargetDatabase == null)
            {
                options.TargetDatabase = database;
            }

            bool renaming = !string.Equals(options.TargetDatabase, database, StringComparison.OrdinalIgnoreCase);
            if (renaming && (options.Moves == null || options.Moves.Count == 0))
            {
                List<DatabaseFileInfo> files = run.Plan.Steps[0].Candidate.Manifest.DatabaseFiles;
                if (files == null || files.Count == 0)
                {
                    throw new ArgumentException("Restoring under another name needs the database's file list, which this backup's manifest does not have (it was taken before manifests recorded one).");
                }
                if (!dryRun)
                {
                    ServerPaths paths = new SqlDatabaseCatalog(databaseTemplate).GetDefaultPaths();
                    options.Moves = TestRestorePlanner.MoveClauses(files, options.TargetDatabase, paths.Data, paths.Log);
                }
            }

            if (dryRun)
            {
                run.Result = new ChainResult();
                foreach (ChainStep step in run.Plan.Steps)
                {
                    run.Result.Steps.Add(new ChainStepResult { FileName = step.FileName, BackupType = step.BackupType, Status = "planned" });
                }
                return run;
            }

            run.Result = RestoreChainRunner.Run(run.Plan, options, databaseTemplate.Parameters, (step, storage, stepOptions) =>
            {
                List<ConfigPair> pipeline = new List<ConfigPair>();
                foreach (string spec in step.Candidate.Manifest.PipelineSpecsForRestore())
                {
                    pipeline.Add(ConfigUtil.ParseComponentConfig(pipelineComponents, spec));
                }
                ConfigPair stepDatabase = new ConfigPair { TransformationType = databaseTemplate.TransformationType, Parameters = stepOptions };
                return Restore(storage, pipeline, stepDatabase, notifier);
            });
            return run;
        }


        /// <summary>
        /// Copies a finished backup and its manifest from one storage to another, retrying
        /// through outages until options.MaxDuration, and checking what was read against the
        /// manifest. The backup is copied as stored (no pipeline), so it stays as it was.
        /// </summary>
        public static CopyResult Copy(ConfigPair sourceConfig, ConfigPair destinationConfig, CopyOptions options)
        {
            IBackupStorage source = sourceConfig.TransformationType.GetConstructor(new Type[0]).Invoke(new object[0]) as IBackupStorage;
            IBackupStorage destination = destinationConfig.TransformationType.GetConstructor(new Type[0]).Invoke(new object[0]) as IBackupStorage;
            if (SamePlace(sourceConfig, destinationConfig))
            {
                throw new ArgumentException("The destination is the same as the source.");
            }
            return BackupCopier.Copy(source, sourceConfig.Parameters, destination, destinationConfig.Parameters, options);
        }


        /// <summary>The plugin name plus parameters of each stage, secrets redacted.</summary>
        public static List<string> DescribePipeline(List<ConfigPair> pipelineConfig)
        {
            List<string> stages = new List<string>();
            foreach (ConfigPair stage in pipelineConfig)
            {
                IBackupPlugin plugin = stage.TransformationType.GetConstructor(new Type[0]).Invoke(new object[0]) as IBackupPlugin;
                stages.Add(BackupManifest.DescribeStage(plugin.Name, stage.Parameters));
            }
            return stages;
        }




        private static BackupStats BackupOrRestore(bool isBackup, ConfigPair storageConfig, ConfigPair databaseConfig, List<ConfigPair> pipelineConfig, IUpdateNotification updateNotifier,
            IList<ConfigPair> copyTo, CopyOptions copyOptions)
        {
            System.Diagnostics.Stopwatch elapsed = System.Diagnostics.Stopwatch.StartNew();
            BackupStats stats = new BackupStats();

            string deviceSetName = Guid.NewGuid().ToString();

            IBackupStorage storage = storageConfig.TransformationType.GetConstructor(new Type[0]).Invoke(new object[0]) as IBackupStorage;
            IBackupDatabase databaseComp = databaseConfig.TransformationType.GetConstructor(new Type[0]).Invoke(new object[0]) as IBackupDatabase;
            ManifestRecorder recorder = isBackup && storage is IManifestStorage ? new ManifestRecorder() : null;


            try
            {

                int numDevices = storage.GetNumberOfDevices(storageConfig.Parameters);

                string instanceName = databaseComp.GetInstanceName(databaseConfig.Parameters);
                string clusterNetworkName = databaseComp.GetClusterNetworkName(databaseConfig.Parameters);


                SqlPlatform sqlPlatform = VirtualBackupDeviceFactory.DiscoverSqlPlatform(instanceName,
                                                                                         clusterNetworkName);


                //NotifyWhenReady notifyWhenReady = new NotifyWhenReady(deviceName, isBackup);))
                using (IVirtualDeviceSet deviceSet = VirtualBackupDeviceFactory.NewVirtualDeviceSet(sqlPlatform))
                {
                    using (new ActiveDeviceSet(deviceSet))
                    using (SqlThread sql = new SqlThread())
                    {
                        bool sqlStarted = false;
                        bool sqlFinished = false;
                        ParallelExecutionException exceptions = new ParallelExecutionException();

                        try
                        {
                            InternalStreamNotification streamNotification = new InternalStreamNotification(updateNotifier);
                            long estimatedTotalBytes;
                            bool streamsCompleted = false;
                            List<string> deviceNames = sql.PreConnect(clusterNetworkName, instanceName, deviceSetName, numDevices, databaseComp, databaseConfig.Parameters, isBackup, updateNotifier, out estimatedTotalBytes);

                            using (DisposableList<Stream> fileStreams = new DisposableList<Stream>(isBackup ? storage.GetBackupWriter(storageConfig.Parameters) : storage.GetRestoreReader(storageConfig.Parameters, out estimatedTotalBytes)))
                            using (DisposableList<Stream> topOfPilelines = new DisposableList<Stream>(CreatePipeline(pipelineConfig, fileStreams, isBackup, streamNotification, estimatedTotalBytes, recorder)))
                            using (new AbortUnlessCompleted(isBackup, fileStreams, () => streamsCompleted))
                            {

                                VirtualDeviceSetConfig config = new VirtualDeviceSetConfig();
                                config.Features = FeatureSet.PipeLike;
                                config.DeviceCount = (uint)topOfPilelines.Count;
                                deviceSet.CreateEx(instanceName, deviceSetName, config);
                                sql.BeginExecute();
                                sqlStarted = true;
                                deviceSet.GetConfiguration(TimeSpan.FromMinutes(1));
                                List<IVirtualDevice> devices = new List<IVirtualDevice>();

                                foreach (string devName in deviceNames)
                                {
                                    devices.Add(deviceSet.OpenDevice(devName));
                                }

                                using (DisposableList<DeviceThread> threads = new DisposableList<DeviceThread>(devices.Count))
                                {
                                    for (int i = 0; i < devices.Count; i++)
                                    {
                                        DeviceThread dThread = new DeviceThread();
                                        threads.Add(dThread);
                                        dThread.Initialize(isBackup, topOfPilelines[i], devices[i], deviceSet);
                                    }
                                    foreach (DeviceThread dThread in threads)
                                    {
                                        dThread.BeginCopy();
                                    }

                                    updateNotifier.OnStart();
                                    //Console.WriteLine(string.Format("{0} started", isBackup ? "Backup" : "Restore"));

                                    Exception sqlE = sql.EndExecute();
                                    sqlFinished = true;

                                    if (sqlE != null)
                                    {
                                        exceptions.Exceptions.Add(sqlE);
                                    }

                                    foreach (DeviceThread dThread in threads)
                                    {
                                        Exception devE = dThread.EndCopy();
                                        if (devE != null)
                                        {
                                            exceptions.Exceptions.Add(devE);
                                        }
                                    }

                                    long transferred = 0;
                                    foreach (DeviceThread done in threads)
                                    {
                                        transferred += done.TotalBytesTransferred;
                                    }
                                    stats.BytesProcessed = transferred;
                                    stats.EstimatedBytes = estimatedTotalBytes;
                                    streamsCompleted = !exceptions.HasExceptions;


                                }
                            }
                        }
                        catch (Exception e)
                        {
                            exceptions.Exceptions.Add(e);
                        }
                        finally
                        {
                            if (sqlStarted && !sqlFinished)
                            {
                                Exception sqlE = sql.EndExecute();
                                sqlFinished = true;
                                if (sqlE != null)
                                {
                                    exceptions.Exceptions.Add(sqlE);
                                }
                            }
                        }

                        if (exceptions.HasExceptions)
                        {
                            throw exceptions;
                        }

                        if (recorder != null)
                        {
                            WriteManifest(recorder, storage, storageConfig, databaseConfig, pipelineConfig);
                        }

                        if (!isBackup && RestorePreparation.ShouldReportOrphans(databaseConfig.Parameters))
                        {
                            AddOrphanedUserWarning(stats, databaseConfig);
                        }

                        if (isBackup && copyTo != null && copyTo.Count > 0)
                        {
                            CopyBackupTo(storage, storageConfig, copyTo, copyOptions ?? new CopyOptions(), stats);
                        }

                        stats.Elapsed = elapsed.Elapsed;
                    }
                }
            }
            catch
            {
                storage.CleanupOnAbort();
                throw;
            }

            return stats;

        }



        /// <summary>
        /// Copies the backup just taken, found by the names its storage really used (date tokens
        /// already expanded), and its manifest, to each destination. Each destination is tried
        /// whatever happens to the others, and nothing here throws: the backup already succeeded.
        /// </summary>
        internal static void CopyBackupTo(IBackupStorage usedStorage, ConfigPair storageConfig, IList<ConfigPair> copyTo, CopyOptions options, BackupStats stats)
        {
            if (copyTo == null || copyTo.Count == 0)
            {
                return;
            }

            IManifestStorage manifests = usedStorage as IManifestStorage;
            ConfigPair source = new ConfigPair
            {
                TransformationType = storageConfig.TransformationType,
                Parameters = manifests != null ? manifests.ResolveConfig(storageConfig.Parameters) : storageConfig.Parameters,
            };

            foreach (ConfigPair destination in copyTo)
            {
                CopyOutcome outcome = new CopyOutcome { Destination = DescribeDestination(destination) };
                stats.Copies.Add(outcome);
                try
                {
                    CopyResult result = Copy(source, destination, options);
                    outcome.Ok = result.Ok;
                    outcome.Attempts = result.Attempts;
                    outcome.BytesCopied = result.BytesCopied;
                    outcome.BytesSkipped = result.BytesSkipped;
                    outcome.Problems.AddRange(result.Problems);
                }
                catch (Exception e)
                {
                    outcome.Ok = false;
                    outcome.Problems.Add(ErrorText.Innermost(e));
                }
                foreach (string problem in outcome.Problems)
                {
                    stats.CopyProblems.Add(string.Format("copy to {0}: {1}", outcome.Destination, problem));
                }
            }
        }

        /// <summary>The plugin name and where it points (path, bucket and key, container and blob), never a secret.</summary>
        internal static string DescribeDestination(ConfigPair storage)
        {
            IBackupPlugin plugin = storage.TransformationType.GetConstructor(new Type[0]).Invoke(new object[0]) as IBackupPlugin;
            List<string> parts = new List<string>();
            foreach (string key in new[] { "endpoint", "bucket", "container", "path", "key", "blob" })
            {
                List<string> values;
                if (storage.Parameters.TryGetValue(key, out values))
                {
                    parts.Add(key + "=" + string.Join(",", values));
                }
            }
            return plugin.Name + "(" + string.Join(";", parts) + ")";
        }

        // The restore itself worked, so a failed check is a warning, never a failure.
        private static void AddOrphanedUserWarning(BackupStats stats, ConfigPair databaseConfig)
        {
            string database = FirstValue(databaseConfig.Parameters, "database", string.Empty);
            try
            {
                List<string> orphans = new SqlDatabaseCatalog(databaseConfig).ListOrphanedUsers(database);
                if (orphans.Count > 0)
                {
                    stats.Warnings.Add(string.Format(
                        "{0} has {1} orphaned user(s) that no login on this server matches (logins are matched by SID, not by name): {2}. Fix each with ALTER USER <user> WITH LOGIN = <login> in {0}; the login must exist.",
                        database, orphans.Count, string.Join(", ", orphans)));
                }
            }
            catch (Exception e)
            {
                stats.Warnings.Add("could not check for orphaned users: " + e.Message);
            }
        }

        // A finished backup is already safely stored, so a manifest that cannot
        // be written is reported on stderr (stdout carries the --json result)
        // and does not fail the job.
        private static void WriteManifest(ManifestRecorder recorder, IBackupStorage storage, ConfigPair storageConfig, ConfigPair databaseConfig, List<ConfigPair> pipelineConfig)
        {
            try
            {
                IManifestStorage manifests = (IManifestStorage)storage;
                string database = FirstValue(databaseConfig.Parameters, "database", string.Empty);
                string backupType = FirstValue(databaseConfig.Parameters, "backuptype", "full");
                BackupManifest manifest = recorder.Build(database, backupType, ((IBackupPlugin)storage).Name,
                    DescribePipeline(pipelineConfig), manifests.GetFileNames(storageConfig.Parameters));
                manifest.CopyOnly = databaseConfig.Parameters.ContainsKey("copy_only");
                try
                {
                    manifest.DatabaseFiles = new SqlDatabaseCatalog(databaseConfig).ListFiles(database);
                }
                catch (Exception e)
                {
                    // Only a later test restore needs the file list; the manifest is still worth writing without it.
                    Console.Error.WriteLine("warning: the database's file list could not be read for the manifest: " + e.Message);
                }
                manifests.WriteManifest(storageConfig.Parameters, manifest.ToJson());
            }
            catch (Exception e)
            {
                Console.Error.WriteLine("warning: the backup succeeded but its manifest was not written: " + e.Message);
            }
        }

        private static string FirstValue(Dictionary<string, List<string>> parameters, string name, string fallback)
        {
            List<string> values;
            if (parameters.TryGetValue(name, out values) && values.Count > 0)
            {
                return values[0];
            }
            return fallback;
        }


        internal static Stream[] CreatePipeline(List<ConfigPair> pipelineConfig, IList<Stream> fileStreams, bool isBackup, IStreamNotification streamNotification, long estimatedTotalBytes, ManifestRecorder recorder = null)
        {
            streamNotification.EstimatedBytes = estimatedTotalBytes;

            List<Stream> result = new List<Stream>(fileStreams.Count);

            foreach (Stream fileStream in fileStreams)
            {
                Stream topStream = fileStream;
                if (recorder != null)
                {
                    topStream = recorder.WrapStored(topStream);
                }
                if (!isBackup)
                {
                    topStream = new TrackingStream(topStream, streamNotification);
                }

                for (int i = pipelineConfig.Count - 1; i >= 0; i--)
                {
                    ConfigPair config = pipelineConfig[i];

                    IBackupTransformer tran = config.TransformationType.GetConstructor(new Type[0]).Invoke(new object[0]) as IBackupTransformer;
                    if (tran == null)
                    {
                        throw new ArgumentException(string.Format("Unable to create pipe component: {0}", config.TransformationType.Name));
                    }
                    topStream = isBackup ? tran.GetBackupWriter(config.Parameters, topStream) : tran.GetRestoreReader(config.Parameters, topStream);
                }

                if (isBackup)
                {
                    topStream = new TrackingStream(topStream, streamNotification);
                }
                if (recorder != null)
                {
                    topStream = recorder.WrapRaw(topStream);
                }
                result.Add(topStream);
            }
            return result.ToArray();
        }



        private static Dictionary<string, Type> LoadComponents(string interfaceName)
        {
            Dictionary<string, Type> result = new Dictionary<string, Type>(StringComparer.InvariantCultureIgnoreCase);

            DirectoryInfo binDir = new FileInfo(Assembly.GetEntryAssembly().Location).Directory;

            foreach (FileInfo file in binDir.GetFiles("*.dll"))
            {

                Assembly dll = null;
                try
                {
                    dll = Assembly.LoadFrom(file.FullName);
                }
                catch (Exception)
                { }
                if (dll != null)
                {
                    FindPlugins(dll, result, interfaceName);
                }
            }

            return result;
        }



        private static void FindPlugins(Assembly dll, Dictionary<string, Type> result, string interfaceName)
        {
            foreach (Type t in dll.GetTypes())
            {
                try
                {
                    if (t.IsPublic)
                    {
                        if (!((t.Attributes & TypeAttributes.Abstract) == TypeAttributes.Abstract))
                        {
                            if (t.GetInterface(interfaceName) != null)
                            {
                                object o = t.GetConstructor(new Type[0]).Invoke(new object[0]);


                                IBackupPlugin test = o as IBackupPlugin;

                                if (test != null)
                                {
                                    string name = test.Name.ToLowerInvariant();

                                    if (name.Contains("|") || name.Contains("("))
                                    {
                                        throw new ArgumentException(string.Format("The name of the plugin, {0}, cannot contain these characters: |, (", name));
                                    }

                                    if (result.ContainsKey(name))
                                    {
                                        throw new ArgumentException(string.Format("plugin found twice: {0}", name));
                                    }
                                    result.Add(name, t);
                                }
                            }
                        }
                    }
                }
                catch (Exception e)
                {
                    throw new Exception(string.Format("Warning: Plugin not loaded due to error: {0}", e.Message), e);
                }
            }
        }

    }
}
