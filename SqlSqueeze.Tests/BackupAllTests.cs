// Multi-database backup: selection, full/diff/log planning, and the runner.
// No SQL Server needed: the catalog and the backup call are faked.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using SqlSqueeze.Core;
using SqlSqueeze.Plugins;
using SqlSqueeze.Plugins.Storage;
using Xunit;

namespace SqlSqueeze.Tests
{
    public class BackupAllTests
    {
        private static DatabaseInfo Db(string name, string recovery = "FULL", string state = "ONLINE", bool logBase = true, bool diffBase = true)
        {
            return new DatabaseInfo { Name = name, RecoveryModel = recovery, State = state, HasLogBase = logBase, HasDiffBase = diffBase };
        }

        private static List<DatabaseInfo> Server()
        {
            return new List<DatabaseInfo>
            {
                Db("master", "SIMPLE"), Db("tempdb", "SIMPLE"), Db("model"), Db("msdb", "SIMPLE"),
                Db("app"), Db("app_archive"), Db("Billing"), Db("scratch1"), Db("scratch2"),
            };
        }

        private static string[] Names(IEnumerable<DatabaseInfo> dbs)
        {
            return dbs.Select(d => d.Name).ToArray();
        }

        // ---- selection ----

        [Fact]
        public void UserDatabases_ExcludesTheSystemDatabases()
        {
            Assert.Equal(new[] { "app", "app_archive", "Billing", "scratch1", "scratch2" },
                Names(DatabaseSelector.Select(Server(), "USER_DATABASES")));
        }

        [Fact]
        public void SystemDatabases_AreMasterModelMsdbButNeverTempdb()
        {
            Assert.Equal(new[] { "master", "model", "msdb" },
                Names(DatabaseSelector.Select(Server(), "SYSTEM_DATABASES")));
        }

        [Fact]
        public void AllDatabases_IsUserPlusSystemWithoutTempdb()
        {
            string[] all = Names(DatabaseSelector.Select(Server(), "ALL_DATABASES"));

            Assert.Equal(8, all.Length);
            Assert.DoesNotContain("tempdb", all);
        }

        [Fact]
        public void NamesAndWildcards_AreCaseInsensitive()
        {
            Assert.Equal(new[] { "app", "app_archive", "Billing" },
                Names(DatabaseSelector.Select(Server(), "APP*, billing")));
        }

        [Fact]
        public void ADashRemovesWhatWasIncluded()
        {
            Assert.Equal(new[] { "app", "app_archive", "Billing" },
                Names(DatabaseSelector.Select(Server(), "USER_DATABASES,-scratch*")));
        }

        [Fact]
        public void Selection_RequiresAtLeastOneInclude()
        {
            Assert.Throws<ArgumentException>(() => DatabaseSelector.Select(Server(), "-scratch1"));
            Assert.Throws<ArgumentException>(() => DatabaseSelector.Select(Server(), "  "));
        }

        [Fact]
        public void Tempdb_CannotBeBackedUp()
        {
            ArgumentException e = Assert.Throws<ArgumentException>(() => DatabaseSelector.Select(Server(), "tempdb"));
            Assert.Contains("tempdb", e.Message);
        }

        [Fact]
        public void AnExplicitNameThatDoesNotExist_IsAnError_ButAWildcardMatchingNothingIsNot()
        {
            ArgumentException e = Assert.Throws<ArgumentException>(() => DatabaseSelector.Select(Server(), "app,nosuchdb"));
            Assert.Contains("nosuchdb", e.Message);

            Assert.Empty(DatabaseSelector.Select(Server(), "zzz*"));
        }

        [Fact]
        public void ADatabaseNamedTwice_IsSelectedOnce()
        {
            Assert.Equal(new[] { "app" }, Names(DatabaseSelector.Select(Server(), "app,APP,a*p")));
        }

        // ---- planning ----

        [Fact]
        public void Full_BacksUpAnOnlineDatabase()
        {
            BackupPlan plan = BackupPlanner.Plan(Db("app", "SIMPLE"), "full", true);

            Assert.False(plan.Skip);
            Assert.Equal("full", plan.BackupType);
        }

        [Fact]
        public void ADatabaseThatIsNotOnline_IsSkippedWithTheReason()
        {
            BackupPlan plan = BackupPlanner.Plan(Db("app", state: "RESTORING"), "full", true);

            Assert.True(plan.Skip);
            Assert.Contains("RESTORING", plan.Reason);
        }

        [Fact]
        public void Differential_WithABase_IsDifferential()
        {
            BackupPlan plan = BackupPlanner.Plan(Db("app", diffBase: true), "differential", true);

            Assert.False(plan.Skip);
            Assert.Equal("differential", plan.BackupType);
        }

        [Fact]
        public void Differential_WithoutABase_FallsBackToFull_OrSkipsWhenFallbackIsOff()
        {
            BackupPlan changed = BackupPlanner.Plan(Db("app", diffBase: false), "differential", true);
            Assert.False(changed.Skip);
            Assert.Equal("full", changed.BackupType);
            Assert.Contains("no differential base", changed.Reason);

            BackupPlan skipped = BackupPlanner.Plan(Db("app", diffBase: false), "differential", false);
            Assert.True(skipped.Skip);
        }

        [Fact]
        public void Master_OnlySupportsFull()
        {
            Assert.True(BackupPlanner.Plan(Db("master", "SIMPLE"), "differential", true).Skip);
            Assert.True(BackupPlanner.Plan(Db("master", "SIMPLE"), "log", true).Skip);
            Assert.False(BackupPlanner.Plan(Db("master", "SIMPLE"), "full", true).Skip);
        }

        [Theory]
        [InlineData("FULL")]
        [InlineData("BULK_LOGGED")]
        public void Log_InFullOrBulkLoggedRecoveryWithALogBase_IsLog(string recovery)
        {
            BackupPlan plan = BackupPlanner.Plan(Db("app", recovery, logBase: true), "log", true);

            Assert.False(plan.Skip);
            Assert.Equal("log", plan.BackupType);
        }

        [Fact]
        public void Log_InSimpleRecovery_IsSkipped()
        {
            BackupPlan plan = BackupPlanner.Plan(Db("app", "SIMPLE"), "log", true);

            Assert.True(plan.Skip);
            Assert.Contains("SIMPLE", plan.Reason);
        }

        [Fact]
        public void Log_WithoutAFullBackupToAnchorTheChain_FallsBackToFull()
        {
            BackupPlan plan = BackupPlanner.Plan(Db("app", "FULL", logBase: false), "log", true);

            Assert.False(plan.Skip);
            Assert.Equal("full", plan.BackupType);
            Assert.Contains("log chain", plan.Reason);

            Assert.True(BackupPlanner.Plan(Db("app", "FULL", logBase: false), "log", false).Skip);
        }

        [Fact]
        public void AnUnknownBackupType_IsRejected()
        {
            Assert.Throws<ArgumentException>(() => BackupPlanner.Plan(Db("app"), "incremental", true));
        }

        // ---- runner ----

        private class FakeCatalog : IDatabaseCatalog
        {
            private readonly List<DatabaseInfo> mDatabases;
            public FakeCatalog(List<DatabaseInfo> databases) { mDatabases = databases; }
            public List<DatabaseInfo> List() { return mDatabases; }
        }

        private class Call
        {
            public string Database;
            public string BackupType;
            public string Path;
            public bool PipelineWasShared;
        }

        private static ConfigPair DbTemplate()
        {
            Dictionary<string, List<string>> p = new Dictionary<string, List<string>>(StringComparer.InvariantCultureIgnoreCase);
            p["user"] = new List<string> { "sa" };
            p["passwordenv"] = new List<string> { "PW" };
            return new ConfigPair { TransformationType = typeof(object), Parameters = p };
        }

        private static ConfigPair Storage(string path)
        {
            Dictionary<string, List<string>> p = new Dictionary<string, List<string>>(StringComparer.InvariantCultureIgnoreCase);
            p["path"] = new List<string> { path };
            return new ConfigPair { TransformationType = typeof(LocalStorage), Parameters = p };
        }

        private static BackupAllResult Run(List<DatabaseInfo> server, string spec, string type, string path, List<Call> calls, Func<string, bool> fails = null, bool changeBackupType = true)
        {
            List<ConfigPair> pipeline = new List<ConfigPair>();
            return BackupAllRunner.Run(new FakeCatalog(server), spec, type, changeBackupType, DbTemplate(), pipeline, Storage(path),
                (db, pipe, storage) =>
                {
                    string name = db.Parameters["database"][0];
                    calls.Add(new Call
                    {
                        Database = name,
                        BackupType = db.Parameters["backuptype"][0],
                        Path = storage.Parameters["path"][0],
                        PipelineWasShared = ReferenceEquals(pipe, pipeline),
                    });
                    if (fails != null && fails(name))
                    {
                        throw new InvalidOperationException("disk full");
                    }
                    return new BackupStats { BytesProcessed = 1000 };
                });
        }

        [Fact]
        public void Runner_BacksUpEachSelectedDatabaseWithItsOwnPathAndTheLoginOptionsKept()
        {
            List<Call> calls = new List<Call>();

            BackupAllResult result = Run(Server(), "app,Billing", "full", "/b/{database}/{database}_{backuptype}.bak", calls);

            Assert.Equal(new[] { "app", "Billing" }, calls.Select(c => c.Database).ToArray());
            Assert.Equal("/b/app/app_full.bak", calls[0].Path);
            Assert.Equal("/b/Billing/Billing_full.bak", calls[1].Path);
            Assert.All(calls, c => Assert.True(c.PipelineWasShared));
            Assert.True(result.AllOk);
            Assert.Equal(new[] { "ok", "ok" }, result.Items.Select(i => i.Status).ToArray());
        }

        [Fact]
        public void Runner_DoesNotChangeTheCallersConfig()
        {
            ConfigPair db = DbTemplate();
            ConfigPair storage = Storage("/b/{database}.bak");

            BackupAllRunner.Run(new FakeCatalog(Server()), "app,Billing", "full", true, db, new List<ConfigPair>(), storage,
                (d, p, s) => new BackupStats());

            Assert.False(db.Parameters.ContainsKey("database"));
            Assert.Equal("/b/{database}.bak", storage.Parameters["path"][0]);
        }

        [Fact]
        public void Runner_RefusesSeveralDatabasesIntoOnePath_BeforeBackingUpAnything()
        {
            List<Call> calls = new List<Call>();

            ArgumentException e = Assert.Throws<ArgumentException>(() => Run(Server(), "app,Billing", "full", "/b/all.bak", calls));

            Assert.Contains("{database}", e.Message);
            Assert.Empty(calls);
        }

        [Fact]
        public void Runner_AllowsAFixedPathWhenOnlyOneDatabaseIsSelected()
        {
            List<Call> calls = new List<Call>();

            BackupAllResult result = Run(Server(), "app", "full", "/b/app.bak", calls);

            Assert.True(result.AllOk);
            Assert.Single(calls);
        }

        [Fact]
        public void Runner_OneFailureDoesNotStopTheRest_ButIsReported()
        {
            List<Call> calls = new List<Call>();

            BackupAllResult result = Run(Server(), "app,Billing,scratch1", "full", "/b/{database}.bak", calls, name => name == "Billing");

            Assert.Equal(3, calls.Count);
            Assert.False(result.AllOk);
            BackupAllItem failed = result.Items.Single(i => i.Status == "error");
            Assert.Equal("Billing", failed.Database);
            Assert.Contains("disk full", failed.Message);
            Assert.Equal(2, result.Items.Count(i => i.Status == "ok"));
        }

        [Fact]
        public void Runner_ReportsSkipsWithTheirReason_AndDoesNotBackThemUp()
        {
            List<Call> calls = new List<Call>();
            List<DatabaseInfo> server = new List<DatabaseInfo> { Db("simple1", "SIMPLE"), Db("full1", "FULL") };

            BackupAllResult result = Run(server, "USER_DATABASES", "log", "/b/{database}_{backuptype}.trn", calls);

            Assert.Equal(new[] { "full1" }, calls.Select(c => c.Database).ToArray());
            Assert.Equal("log", calls[0].BackupType);
            BackupAllItem skipped = result.Items.Single(i => i.Status == "skipped");
            Assert.Equal("simple1", skipped.Database);
            Assert.Contains("SIMPLE", skipped.Message);
            Assert.True(result.AllOk);
        }

        [Fact]
        public void Runner_AFallbackToFull_UsesTheActualTypeInThePathAndSaysSo()
        {
            List<Call> calls = new List<Call>();
            List<DatabaseInfo> server = new List<DatabaseInfo> { Db("fresh", "FULL", logBase: false), Db("steady", "FULL") };

            BackupAllResult result = Run(server, "USER_DATABASES", "log", "/b/{database}_{backuptype}.bak", calls);

            Assert.Equal("/b/fresh_full.bak", calls.Single(c => c.Database == "fresh").Path);
            Assert.Equal("full", calls.Single(c => c.Database == "fresh").BackupType);
            Assert.Equal("/b/steady_log.bak", calls.Single(c => c.Database == "steady").Path);
            BackupAllItem fresh = result.Items.Single(i => i.Database == "fresh");
            Assert.Equal("full", fresh.BackupType);
            Assert.Contains("log chain", fresh.Message);
        }

        [Fact]
        public void Runner_MakesDatabaseNamesSafeForFileNames()
        {
            List<Call> calls = new List<Call>();
            List<DatabaseInfo> server = new List<DatabaseInfo> { Db("odd:name/with*chars") };

            Run(server, "USER_DATABASES", "full", "/b/{database}.bak", calls);

            Assert.Equal("/b/odd_name_with_chars.bak", calls[0].Path);
            Assert.Equal("odd:name/with*chars", calls[0].Database);
        }

        [Fact]
        public void Runner_TwoNamesThatSanitizeToTheSameFile_AreRefusedBeforeBackingUpAnything()
        {
            List<Call> calls = new List<Call>();
            List<DatabaseInfo> server = new List<DatabaseInfo> { Db("a:b"), Db("a/b") };

            ArgumentException e = Assert.Throws<ArgumentException>(() => Run(server, "USER_DATABASES", "full", "/b/{database}.bak", calls));

            Assert.Contains("same file", e.Message);
            Assert.Empty(calls);
        }

        [Fact]
        public void Runner_SelectingNothing_IsAnError()
        {
            List<Call> calls = new List<Call>();

            ArgumentException e = Assert.Throws<ArgumentException>(() => Run(Server(), "zzz*", "full", "/b/{database}.bak", calls));

            Assert.Contains("no databases", e.Message);
        }

        // ---- storage ----

        [Fact]
        public void LocalStorage_CreatesMissingFolders_SoPerDatabaseFoldersWork()
        {
            string root = Path.Combine(Path.GetTempPath(), "sqsall-" + Guid.NewGuid().ToString("N"));
            try
            {
                string path = Path.Combine(root, "app", "deep", "app.bak");
                Dictionary<string, List<string>> cfg = new Dictionary<string, List<string>>(StringComparer.InvariantCultureIgnoreCase);
                cfg["path"] = new List<string> { path };

                Stream[] writers = new LocalStorage().GetBackupWriter(cfg);
                writers[0].Write(new byte[] { 1, 2, 3 }, 0, 3);
                writers[0].Dispose();

                Assert.Equal(3, new FileInfo(path).Length);
            }
            finally
            {
                if (Directory.Exists(root))
                {
                    Directory.Delete(root, true);
                }
            }
        }
    }
}
