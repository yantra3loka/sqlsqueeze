// restore-chain: pick the latest full, the latest differential after it and the
// logs after that, from the manifests in a folder, and restore them in order.
// No SQL Server needed: selection, options and sequencing are tested here.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using SqlSqueeze.Core;
using SqlSqueeze.Plugins.Storage;
using Xunit;

namespace SqlSqueeze.Tests
{
    public class RestoreChainTests : IDisposable
    {
        private readonly string mDir;

        public RestoreChainTests()
        {
            mDir = Path.Combine(Path.GetTempPath(), "sqschain-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(mDir);
        }

        public void Dispose()
        {
            Directory.Delete(mDir, true);
        }

        private static DateTime T(string hhmm)
        {
            return DateTime.Parse("2026-10-26T" + hhmm + ":00Z", null, System.Globalization.DateTimeStyles.AdjustToUniversal);
        }

        private static ChainCandidate Cand(string type, string hhmm, string database = "app", bool copyOnly = false, string name = null)
        {
            name = name ?? (database + "_" + type + "_" + hhmm.Replace(":", "") + ".bak");
            return new ChainCandidate
            {
                ManifestPath = "/b/" + name + ".manifest.json",
                FilePaths = new List<string> { "/b/" + name },
                Manifest = new BackupManifest
                {
                    Database = database,
                    BackupType = type,
                    CopyOnly = copyOnly,
                    CreatedUtc = T(hhmm),
                    Files = new List<ManifestFile> { new ManifestFile { Name = name } },
                    Pipeline = new List<string> { "zstd" },
                },
            };
        }

        private static string[] Names(ChainPlan plan)
        {
            return plan.Steps.Select(s => Path.GetFileName(s.Candidate.FilePaths[0])).ToArray();
        }

        // ---- planning ----

        [Fact]
        public void Plan_IsTheLatestFull_TheLatestDiffAfterIt_AndEveryLogAfterThat_InOrder()
        {
            var all = new List<ChainCandidate>
            {
                Cand("full", "01:00"), Cand("full", "09:00"),
                Cand("differential", "06:00"),                                   // before the chosen full: stale
                Cand("differential", "10:00"), Cand("differential", "12:00"),
                Cand("log", "09:30"),                                            // before the chosen diff
                Cand("log", "13:00"), Cand("log", "12:30"), Cand("log", "13:30"),
            };

            ChainPlan plan = RestoreChainPlanner.Plan(all, "app", null);

            Assert.Equal(new[] { "app_full_0900.bak", "app_differential_1200.bak", "app_log_1230.bak", "app_log_1300.bak", "app_log_1330.bak" }, Names(plan));
        }

        [Fact]
        public void Plan_WithoutADiff_TakesTheLogsAfterTheFull()
        {
            var all = new List<ChainCandidate> { Cand("full", "09:00"), Cand("log", "08:00"), Cand("log", "10:00"), Cand("log", "11:00") };

            Assert.Equal(new[] { "app_full_0900.bak", "app_log_1000.bak", "app_log_1100.bak" }, Names(RestoreChainPlanner.Plan(all, "app", null)));
        }

        [Fact]
        public void Plan_OnlyAFull_IsJustTheFull()
        {
            Assert.Equal(new[] { "app_full_0900.bak" }, Names(RestoreChainPlanner.Plan(new List<ChainCandidate> { Cand("full", "09:00") }, "app", null)));
        }

        [Fact]
        public void Plan_StopAt_ChoosesWhatWasCurrentAtThatTime_AndEndsWithTheLogThatCoversIt()
        {
            var all = new List<ChainCandidate>
            {
                Cand("full", "01:00"), Cand("full", "09:00"),
                Cand("differential", "10:00"), Cand("differential", "12:00"),
                Cand("log", "10:30"), Cand("log", "11:00"), Cand("log", "11:30"), Cand("log", "12:30"), Cand("log", "13:00"),
            };

            // 11:10: the latest full before it (09:00), the diff before it (10:00), logs up to and including the first one at or after 11:10 (11:30)
            ChainPlan plan = RestoreChainPlanner.Plan(all, "app", T("11:10"));

            Assert.Equal(new[] { "app_full_0900.bak", "app_differential_1000.bak", "app_log_1030.bak", "app_log_1100.bak", "app_log_1130.bak" }, Names(plan));
        }

        [Fact]
        public void Plan_StopAtBeforeAnyFull_IsAnError()
        {
            ArgumentException e = Assert.Throws<ArgumentException>(() =>
                RestoreChainPlanner.Plan(new List<ChainCandidate> { Cand("full", "09:00") }, "app", T("08:00")));

            Assert.Contains("full", e.Message);
        }

        [Fact]
        public void Plan_CopyOnlyBackupsAreNotPartOfAChain()
        {
            var all = new List<ChainCandidate> { Cand("full", "09:00"), Cand("full", "11:00", copyOnly: true), Cand("log", "12:00") };

            Assert.Equal(new[] { "app_full_0900.bak", "app_log_1200.bak" }, Names(RestoreChainPlanner.Plan(all, "app", null)));
        }

        [Fact]
        public void Plan_SkipsCopyOnlyDiffsAndLogs()
        {
            var all = new List<ChainCandidate>
            {
                Cand("full", "09:00"),
                Cand("differential", "09:30"), Cand("differential", "10:00", copyOnly: true),
                Cand("log", "10:30"), Cand("log", "11:00", copyOnly: true),
            };

            Assert.Equal(new[] { "app_full_0900.bak", "app_differential_0930.bak", "app_log_1030.bak" }, Names(RestoreChainPlanner.Plan(all, "app", null)));
        }

        [Fact]
        public void Plan_IgnoresOtherDatabases_AndMatchesTheNameWithoutCase()
        {
            var all = new List<ChainCandidate> { Cand("full", "09:00", "other"), Cand("full", "08:00", "App"), Cand("log", "10:00", "other") };

            Assert.Equal(new[] { "App_full_0800.bak" }, Names(RestoreChainPlanner.Plan(all, "APP", null)));
        }

        [Fact]
        public void Plan_NoFullBackup_IsAClearError()
        {
            ArgumentException e = Assert.Throws<ArgumentException>(() =>
                RestoreChainPlanner.Plan(new List<ChainCandidate> { Cand("log", "10:00"), Cand("full", "09:00", "other") }, "app", null));

            Assert.Contains("no full backup", e.Message);
            Assert.Contains("app", e.Message);
        }

        [Fact]
        public void Plan_MarksOnlyTheLastStepAsLast()
        {
            ChainPlan plan = RestoreChainPlanner.Plan(new List<ChainCandidate> { Cand("full", "09:00"), Cand("log", "10:00"), Cand("log", "11:00") }, "app", null);

            Assert.Equal(new[] { false, false, true }, plan.Steps.Select(s => s.IsLast).ToArray());
        }

        // ---- the options of each step ----

        private static ChainOptions Options(ChainMode mode = ChainMode.Recovery, string standby = null, string target = "app",
            bool replace = false, bool kill = false, string stopAt = null, params string[] moves)
        {
            return new ChainOptions { TargetDatabase = target, Mode = mode, StandbyFile = standby, Replace = replace, KillConnections = kill, StopAt = stopAt, Moves = moves.ToList() };
        }

        private static ChainPlan ThreeSteps()
        {
            return RestoreChainPlanner.Plan(new List<ChainCandidate> { Cand("full", "09:00"), Cand("differential", "10:00"), Cand("log", "11:00") }, "app", null);
        }

        private static Dictionary<string, List<string>> Template()
        {
            var d = new Dictionary<string, List<string>>(StringComparer.InvariantCultureIgnoreCase);
            d["user"] = new List<string> { "sa" };
            d["passwordenv"] = new List<string> { "PW" };
            return d;
        }

        [Fact]
        public void StepOptions_Recovery_LeavesEveryStepRestoringExceptTheLast()
        {
            ChainPlan plan = ThreeSteps();

            var first = RestoreChainRunner.StepOptions(plan, 0, Options(), Template());
            var last = RestoreChainRunner.StepOptions(plan, 2, Options(), Template());

            Assert.True(first.ContainsKey("NORECOVERY"));
            Assert.False(first.ContainsKey("RECOVERY"));
            Assert.True(last.ContainsKey("RECOVERY"));
            Assert.False(last.ContainsKey("NORECOVERY"));
        }

        [Fact]
        public void StepOptions_NoRecovery_LeavesEverythingRestoring()
        {
            ChainPlan plan = ThreeSteps();

            for (int i = 0; i < 3; i++)
            {
                var o = RestoreChainRunner.StepOptions(plan, i, Options(ChainMode.NoRecovery), Template());
                Assert.True(o.ContainsKey("NORECOVERY"), "step " + i);
                Assert.False(o.ContainsKey("RECOVERY"), "step " + i);
            }
        }

        [Fact]
        public void StepOptions_Standby_UsesTheStandbyFileOnEveryStep()
        {
            ChainPlan plan = ThreeSteps();

            for (int i = 0; i < 3; i++)
            {
                var o = RestoreChainRunner.StepOptions(plan, i, Options(ChainMode.Standby, "/var/opt/mssql/undo.sby"), Template());
                Assert.Equal("/var/opt/mssql/undo.sby", o["STANDBY"][0]);
                Assert.False(o.ContainsKey("NORECOVERY"));
                Assert.False(o.ContainsKey("RECOVERY"));
            }
        }

        [Fact]
        public void StepOptions_LogStepsAreLogRestores_AndOnlyTheyGetStopAt()
        {
            ChainPlan plan = ThreeSteps();
            ChainOptions options = Options(stopAt: "2026-10-26 11:10:00");

            var full = RestoreChainRunner.StepOptions(plan, 0, options, Template());
            var diff = RestoreChainRunner.StepOptions(plan, 1, options, Template());
            var log = RestoreChainRunner.StepOptions(plan, 2, options, Template());

            Assert.False(full.ContainsKey("restoretype"));
            Assert.False(diff.ContainsKey("restoretype"));
            Assert.Equal("log", log["restoretype"][0]);
            Assert.False(full.ContainsKey("STOPAT"));
            Assert.False(diff.ContainsKey("STOPAT"));
            Assert.Equal("2026-10-26 11:10:00", log["STOPAT"][0]);
        }

        [Fact]
        public void StepOptions_ReplaceKillAndMove_AreForTheFirstStepOnly()
        {
            ChainPlan plan = ThreeSteps();
            ChainOptions options = Options(replace: true, kill: true, moves: "'a'TO'/d/a.mdf'");

            var first = RestoreChainRunner.StepOptions(plan, 0, options, Template());
            var second = RestoreChainRunner.StepOptions(plan, 1, options, Template());

            Assert.True(first.ContainsKey("REPLACE"));
            Assert.True(first.ContainsKey("KILL_CONNECTIONS"));
            Assert.Equal("'a'TO'/d/a.mdf'", first["MOVE"][0]);
            Assert.False(second.ContainsKey("REPLACE"));
            Assert.False(second.ContainsKey("KILL_CONNECTIONS"));
            Assert.False(second.ContainsKey("MOVE"));
        }

        [Fact]
        public void StepOptions_NameTheTargetDatabase_KeepTheLoginOptions_AndDoNotChangeTheTemplate()
        {
            ChainPlan plan = ThreeSteps();
            var template = Template();

            var o = RestoreChainRunner.StepOptions(plan, 0, Options(target: "app_copy"), template);

            Assert.Equal("app_copy", o["database"][0]);
            Assert.Equal("sa", o["user"][0]);
            Assert.Equal("PW", o["passwordenv"][0]);
            Assert.False(template.ContainsKey("database"));
            Assert.False(template.ContainsKey("NORECOVERY"));
        }

        // ---- finding the backups ----

        [Fact]
        public void StorageFor_ListsEveryStripeNextToTheManifest()
        {
            ChainCandidate c = new ChainCandidate
            {
                ManifestPath = Path.Combine(mDir, "x", "a.bak.manifest.json"),
                FilePaths = new List<string> { Path.Combine(mDir, "x", "a.bak"), Path.Combine(mDir, "x", "a_2.bak") },
                Manifest = new BackupManifest(),
            };

            ConfigPair storage = RestoreChainRunner.StorageFor(c);

            Assert.Equal(typeof(LocalStorage), storage.TransformationType);
            Assert.Equal(c.FilePaths, storage.Parameters["path"]);
        }

        private void WriteBackupFiles(string subdir, string database, string type, string hhmm, bool withFile = true, bool copyOnly = false)
        {
            string dir = Path.Combine(mDir, subdir);
            Directory.CreateDirectory(dir);
            string name = database + "_" + type + "_" + hhmm + ".bak";
            if (withFile)
            {
                File.WriteAllText(Path.Combine(dir, name), "x");
            }
            BackupManifest m = new BackupManifest
            {
                Database = database, BackupType = type, CopyOnly = copyOnly, CreatedUtc = T(hhmm.Insert(2, ":")),
                Files = new List<ManifestFile> { new ManifestFile { Name = name } },
            };
            File.WriteAllText(Path.Combine(dir, name + ".manifest.json"), m.ToJson());
        }

        [Fact]
        public void Scan_FindsManifestsInSubfolders_WithTheirBackupFilesResolved()
        {
            WriteBackupFiles("app", "app", "full", "0900");
            WriteBackupFiles(Path.Combine("deep", "er"), "app", "log", "1000");
            List<string> warnings = new List<string>();

            List<ChainCandidate> found = RestoreChainScanner.Scan(mDir, warnings);

            Assert.Equal(2, found.Count);
            Assert.Empty(warnings);
            Assert.All(found, c => Assert.True(File.Exists(c.FilePaths[0])));
        }

        [Fact]
        public void Scan_SkipsAManifestWhoseBackupFileIsMissing_WithAWarning()
        {
            WriteBackupFiles("app", "app", "full", "0900");
            WriteBackupFiles("app", "app", "log", "1000", withFile: false);
            List<string> warnings = new List<string>();

            List<ChainCandidate> found = RestoreChainScanner.Scan(mDir, warnings);

            Assert.Single(found);
            Assert.Contains(warnings, w => w.Contains("app_log_1000.bak") && w.Contains("missing"));
        }

        [Fact]
        public void Scan_SkipsAManifestItCannotRead_WithAWarning()
        {
            WriteBackupFiles("app", "app", "full", "0900");
            File.WriteAllText(Path.Combine(mDir, "app", "broken.bak.manifest.json"), "{ not json");
            List<string> warnings = new List<string>();

            List<ChainCandidate> found = RestoreChainScanner.Scan(mDir, warnings);

            Assert.Single(found);
            Assert.Contains(warnings, w => w.Contains("broken.bak.manifest.json"));
        }

        [Fact]
        public void Scan_AFolderThatDoesNotExist_IsAnError()
        {
            Assert.Throws<DirectoryNotFoundException>(() => RestoreChainScanner.Scan(Path.Combine(mDir, "nope"), new List<string>()));
        }

        // ---- running the plan ----

        [Fact]
        public void Run_RestoresEveryStepInOrder_WithTheStepsOwnStorageAndOptions()
        {
            ChainPlan plan = ThreeSteps();
            List<string> seen = new List<string>();

            ChainResult result = RestoreChainRunner.Run(plan, Options(), Template(), (step, storage, options) =>
            {
                seen.Add(Path.GetFileName(storage.Parameters["path"][0]) + ":" + (options.ContainsKey("RECOVERY") ? "RECOVERY" : "NORECOVERY"));
                return new BackupStats { BytesProcessed = 100 };
            });

            Assert.True(result.Ok);
            Assert.Equal(new[] { "app_full_0900.bak:NORECOVERY", "app_differential_1000.bak:NORECOVERY", "app_log_1100.bak:RECOVERY" }, seen.ToArray());
            Assert.Equal(300, result.BytesProcessed);
            Assert.Equal(new[] { "ok", "ok", "ok" }, result.Steps.Select(s => s.Status).ToArray());
        }

        [Fact]
        public void Run_StopsAtTheFirstFailure_SaysWhichStep_AndDoesNotRunTheRest()
        {
            ChainPlan plan = ThreeSteps();
            int calls = 0;

            ChainResult result = RestoreChainRunner.Run(plan, Options(), Template(), (step, storage, options) =>
            {
                calls++;
                if (calls == 2)
                {
                    throw new InvalidOperationException("The log in this backup set begins at LSN 1, which is too recent");
                }
                return new BackupStats();
            });

            Assert.False(result.Ok);
            Assert.Equal(2, calls);
            Assert.Equal(new[] { "ok", "error", "not run" }, result.Steps.Select(s => s.Status).ToArray());
            Assert.Contains("LSN", result.Steps[1].Message);
            Assert.Contains("app_differential_1000.bak", result.Problems.Single());
        }
    }
}
