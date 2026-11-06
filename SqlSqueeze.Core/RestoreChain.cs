/*
    This file is part of SqlSqueeze.

    SqlSqueeze is free software: you can redistribute it and/or modify
    it under the terms of the GNU General Public License as published by
    the Free Software Foundation, either version 3 of the License, or
    (at your option) any later version.

    SqlSqueeze is distributed in the hope that it will be useful,
    but WITHOUT ANY WARRANTY; without even the implied warranty of
    MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
    GNU General Public License for more details.

    You should have received a copy of the GNU General Public License
    along with SqlSqueeze.  If not, see <http://www.gnu.org/licenses/>.
*/

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using SqlSqueeze.Plugins.Storage;

namespace SqlSqueeze.Core
{
    /// <summary>One backup found in a folder: its manifest and the files it is stored in.</summary>
    public class ChainCandidate
    {
        public string ManifestPath { get; set; }
        public BackupManifest Manifest { get; set; }

        /// <summary>The backup's files in stripe order, as full paths.</summary>
        public List<string> FilePaths { get; set; }
    }

    public class ChainStep
    {
        public ChainCandidate Candidate { get; set; }
        public bool IsLast { get; set; }

        public string BackupType { get { return Candidate.Manifest.BackupType; } }
        public string FileName { get { return Path.GetFileName(Candidate.FilePaths[0]); } }
    }

    public class ChainPlan
    {
        public List<ChainStep> Steps { get; } = new List<ChainStep>();
    }

    public enum ChainMode
    {
        /// <summary>Restore in steps and bring the database online after the last one.</summary>
        Recovery,

        /// <summary>Leave the database restoring after every step, for more logs to be applied later.</summary>
        NoRecovery,

        /// <summary>Leave the database readable between steps (log shipping), using a standby file.</summary>
        Standby,
    }

    public class ChainOptions
    {
        public string TargetDatabase { get; set; }
        public ChainMode Mode { get; set; }
        public string StandbyFile { get; set; }
        public bool Replace { get; set; }
        public bool KillConnections { get; set; }

        /// <summary>A point in time to stop at, as SQL Server's STOPAT takes it (server local time).</summary>
        public string StopAt { get; set; }

        /// <summary>MOVE clauses for the first step, when restoring under another name or place.</summary>
        public List<string> Moves { get; set; } = new List<string>();
    }

    /// <summary>
    /// Picks the backups that make up the latest chain from the manifests in a folder:
    /// the latest full, the latest differential after it, and the logs after that, or
    /// what was current at a given time. Copy-only backups are not part of a chain.
    /// Ordering is by when each backup was taken; SQL Server still checks the log
    /// sequence numbers as it restores, so a gap fails rather than restoring wrongly.
    /// </summary>
    public static class RestoreChainPlanner
    {
        public static ChainPlan Plan(IList<ChainCandidate> candidates, string database, DateTime? stopAtUtc)
        {
            List<ChainCandidate> mine = candidates
                .Where(c => string.Equals(c.Manifest.Database, database, StringComparison.OrdinalIgnoreCase))
                .ToList();
            DateTime until = stopAtUtc ?? DateTime.MaxValue;

            ChainCandidate full = mine
                .Where(c => c.Manifest.BackupType == "full" && !c.Manifest.CopyOnly && c.Manifest.CreatedUtc <= until)
                .OrderBy(c => c.Manifest.CreatedUtc).LastOrDefault();
            if (full == null)
            {
                throw new ArgumentException(stopAtUtc == null
                    ? string.Format("There is no full backup of {0} here to start a chain from.", database)
                    : string.Format("There is no full backup of {0} at or before the stop time to start a chain from.", database));
            }

            ChainCandidate diff = mine
                .Where(c => c.Manifest.BackupType == "differential" && !c.Manifest.CopyOnly && c.Manifest.CreatedUtc > full.Manifest.CreatedUtc && c.Manifest.CreatedUtc <= until)
                .OrderBy(c => c.Manifest.CreatedUtc).LastOrDefault();
            ChainCandidate start = diff ?? full;

            List<ChainCandidate> logs = mine
                .Where(c => c.Manifest.BackupType == "log" && !c.Manifest.CopyOnly && c.Manifest.CreatedUtc > start.Manifest.CreatedUtc)
                .OrderBy(c => c.Manifest.CreatedUtc).ToList();
            if (stopAtUtc != null)
            {
                // everything taken before the stop time, plus the first log taken after it (it covers the stop time)
                List<ChainCandidate> kept = logs.Where(c => c.Manifest.CreatedUtc < until).ToList();
                ChainCandidate covering = logs.FirstOrDefault(c => c.Manifest.CreatedUtc >= until);
                if (covering != null)
                {
                    kept.Add(covering);
                }
                logs = kept;
            }

            ChainPlan plan = new ChainPlan();
            plan.Steps.Add(new ChainStep { Candidate = full });
            if (diff != null)
            {
                plan.Steps.Add(new ChainStep { Candidate = diff });
            }
            foreach (ChainCandidate log in logs)
            {
                plan.Steps.Add(new ChainStep { Candidate = log });
            }
            plan.Steps[plan.Steps.Count - 1].IsLast = true;
            return plan;
        }
    }

    public static class RestoreChainScanner
    {
        /// <summary>
        /// The backups described by the manifests under a folder (subfolders included, which
        /// is how backup-all lays them out). A manifest that cannot be read, or whose backup
        /// file is missing, is skipped and reported in warnings rather than ending the scan.
        /// </summary>
        public static List<ChainCandidate> Scan(string folder, List<string> warnings)
        {
            List<ChainCandidate> found = new List<ChainCandidate>();
            foreach (string manifestPath in Directory.GetFiles(folder, "*.manifest.json", SearchOption.AllDirectories).OrderBy(p => p, StringComparer.Ordinal))
            {
                BackupManifest manifest;
                try
                {
                    manifest = BackupManifest.FromJson(File.ReadAllText(manifestPath));
                }
                catch (Exception e)
                {
                    warnings.Add(string.Format("{0}: could not read the manifest ({1}); skipped", manifestPath, ErrorText.Innermost(e)));
                    continue;
                }

                string dir = Path.GetDirectoryName(manifestPath);
                List<string> files = manifest.Files.Select(f => Path.Combine(dir, f.Name)).ToList();
                string missing = files.FirstOrDefault(f => !File.Exists(f));
                if (files.Count == 0 || missing != null)
                {
                    warnings.Add(string.Format("{0}: the backup file {1} is missing; skipped", manifestPath, missing == null ? "(none listed)" : Path.GetFileName(missing)));
                    continue;
                }
                found.Add(new ChainCandidate { ManifestPath = manifestPath, Manifest = manifest, FilePaths = files });
            }
            return found;
        }
    }

    /// <summary>What a restore-chain run planned and did.</summary>
    public class ChainRun
    {
        public bool DryRun { get; set; }
        public ChainPlan Plan { get; set; }
        public ChainResult Result { get; set; }

        /// <summary>Manifests that were skipped while scanning, and why.</summary>
        public List<string> Warnings { get; } = new List<string>();
    }

    public class ChainStepResult
    {
        public string FileName { get; set; }
        public string BackupType { get; set; }

        /// <summary>ok, error, or not run.</summary>
        public string Status { get; set; }

        public string Message { get; set; }
        public long BytesProcessed { get; set; }
    }

    public class ChainResult
    {
        public List<ChainStepResult> Steps { get; } = new List<ChainStepResult>();
        public List<string> Problems { get; } = new List<string>();
        public bool Ok { get { return Problems.Count == 0; } }
        public long BytesProcessed { get; set; }
    }

    public static class RestoreChainRunner
    {
        /// <summary>
        /// The db options for one step: the user's login options plus the target database, the
        /// restore type (log for a log backup), NORECOVERY/RECOVERY/STANDBY by mode, STOPAT on
        /// log steps, and REPLACE, KILL_CONNECTIONS and MOVE on the first step only.
        /// </summary>
        public static Dictionary<string, List<string>> StepOptions(ChainPlan plan, int index, ChainOptions options, Dictionary<string, List<string>> template)
        {
            ChainStep step = plan.Steps[index];
            Dictionary<string, List<string>> o = new Dictionary<string, List<string>>(template.Comparer);
            foreach (KeyValuePair<string, List<string>> pair in template)
            {
                o[pair.Key] = new List<string>(pair.Value);
            }
            o["database"] = new List<string> { options.TargetDatabase };

            bool isLog = step.BackupType == "log";
            if (isLog)
            {
                o["restoretype"] = new List<string> { "log" };
                if (!string.IsNullOrEmpty(options.StopAt))
                {
                    o["STOPAT"] = new List<string> { options.StopAt };
                }
            }

            switch (options.Mode)
            {
                case ChainMode.Standby:
                    o["STANDBY"] = new List<string> { options.StandbyFile };
                    break;
                case ChainMode.NoRecovery:
                    o["NORECOVERY"] = new List<string> { null };
                    break;
                default:
                    o[step.IsLast ? "RECOVERY" : "NORECOVERY"] = new List<string> { null };
                    break;
            }

            if (index == 0)
            {
                if (options.Replace)
                {
                    o["REPLACE"] = new List<string> { null };
                }
                if (options.KillConnections)
                {
                    o["KILL_CONNECTIONS"] = new List<string> { null };
                }
                if (options.Moves != null && options.Moves.Count > 0)
                {
                    o["MOVE"] = new List<string>(options.Moves);
                }
            }
            return o;
        }

        /// <summary>The local storage a backup lives in: every stripe, in order.</summary>
        public static ConfigPair StorageFor(ChainCandidate candidate)
        {
            Dictionary<string, List<string>> parameters = new Dictionary<string, List<string>>(StringComparer.InvariantCultureIgnoreCase);
            parameters["path"] = new List<string>(candidate.FilePaths);
            return new ConfigPair { TransformationType = typeof(LocalStorage), Parameters = parameters };
        }

        /// <summary>
        /// Restores each step in order through the given function. It stops at the first
        /// failure and says which step it was; the database is left restoring, so the chain
        /// can be continued from there once the cause is fixed.
        /// </summary>
        public static ChainResult Run(ChainPlan plan, ChainOptions options, Dictionary<string, List<string>> template,
            Func<ChainStep, ConfigPair, Dictionary<string, List<string>>, BackupStats> restoreStep)
        {
            ChainResult result = new ChainResult();
            bool failed = false;
            for (int i = 0; i < plan.Steps.Count; i++)
            {
                ChainStep step = plan.Steps[i];
                ChainStepResult stepResult = new ChainStepResult { FileName = step.FileName, BackupType = step.BackupType, Status = "not run" };
                result.Steps.Add(stepResult);
                if (failed)
                {
                    continue;
                }
                try
                {
                    BackupStats stats = restoreStep(step, StorageFor(step.Candidate), StepOptions(plan, i, options, template));
                    stepResult.Status = "ok";
                    stepResult.BytesProcessed = stats.BytesProcessed;
                    result.BytesProcessed += stats.BytesProcessed;
                }
                catch (Exception e)
                {
                    failed = true;
                    stepResult.Status = "error";
                    stepResult.Message = ErrorText.Innermost(e);
                    result.Problems.Add(string.Format("{0} ({1}): {2}", step.FileName, step.BackupType, stepResult.Message));
                }
            }
            return result;
        }
    }
}
