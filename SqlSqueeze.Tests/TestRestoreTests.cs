// Test restore: restore a backup into a scratch database, DBCC CHECKDB it,
// drop it. The SQL Server side is faked here.

using System;
using System.Collections.Generic;
using System.Linq;
using SqlSqueeze.Core;
using Xunit;

namespace SqlSqueeze.Tests
{
    public class TestRestoreTests
    {
        private static DatabaseFileInfo File(string logical, string type)
        {
            return new DatabaseFileInfo { LogicalName = logical, Type = type };
        }

        // ---- naming and file placement ----

        [Fact]
        public void ScratchName_IsPrefixedStampedAndSafe()
        {
            string name = TestRestorePlanner.ScratchName("My App-DB", new DateTime(2026, 10, 26, 7, 5, 9, DateTimeKind.Utc));

            Assert.Equal("sqs_testrestore_My_App_DB_20261026070509", name);
        }

        [Fact]
        public void ScratchName_IsShortEnoughForSqlServer()
        {
            string name = TestRestorePlanner.ScratchName(new string('x', 300), DateTime.UtcNow);

            Assert.True(name.Length <= 128, name.Length.ToString());
            Assert.StartsWith("sqs_testrestore_", name);
            Assert.Matches(@"\d{14}$", name);   // the timestamp is kept even when the name is cut
        }

        [Fact]
        public void MoveClauses_PutDataAndLogFilesInTheirOwnFolders_NamedAfterTheScratchDatabase()
        {
            List<string> moves = TestRestorePlanner.MoveClauses(
                new[] { File("app", "ROWS"), File("app_log", "LOG") },
                "sqs_testrestore_app_1", "/var/opt/mssql/data/", "/var/opt/mssql/log/");

            Assert.Equal(new[]
            {
                "'app'TO'/var/opt/mssql/data/sqs_testrestore_app_1.mdf'",
                "'app_log'TO'/var/opt/mssql/log/sqs_testrestore_app_1_log.ldf'",
            }, moves.ToArray());
        }

        [Fact]
        public void MoveClauses_NumberTheExtraFiles()
        {
            List<string> moves = TestRestorePlanner.MoveClauses(
                new[] { File("a", "ROWS"), File("b", "ROWS"), File("c", "ROWS"), File("l1", "LOG"), File("l2", "LOG") },
                "s", @"D:\data", @"E:\log");

            Assert.Equal(new[]
            {
                @"'a'TO'D:\data\s.mdf'",
                @"'b'TO'D:\data\s_1.ndf'",
                @"'c'TO'D:\data\s_2.ndf'",
                @"'l1'TO'E:\log\s_log.ldf'",
                @"'l2'TO'E:\log\s_log_1.ldf'",
            }, moves.ToArray());
        }

        [Fact]
        public void MoveClauses_PlacesFilestreamFilesInTheirOwnFolders()
        {
            List<string> moves = TestRestorePlanner.MoveClauses(
                new[] { File("a", "ROWS"), File("fs1", "FILESTREAM"), File("fs2", "filestream"), File("l", "LOG") },
                "s", "/d/", "/l/");

            Assert.Equal(new[]
            {
                "'a'TO'/d/s.mdf'",
                "'fs1'TO'/d/s_fs'",
                "'fs2'TO'/d/s_fs_1'",
                "'l'TO'/l/s_log.ldf'",
            }, moves.ToArray());
        }

        [Fact]
        public void MoveClauses_RefuseAnythingElseItCannotPlace()
        {
            ArgumentException e = Assert.Throws<ArgumentException>(() =>
                TestRestorePlanner.MoveClauses(new[] { File("a", "ROWS"), File("ft", "FULLTEXT") }, "s", "/d/", "/l/"));

            Assert.Contains("FULLTEXT", e.Message);
        }

        [Fact]
        public void MoveClauses_RefuseALogicalNameWithAQuote()
        {
            Assert.Throws<ArgumentException>(() =>
                TestRestorePlanner.MoveClauses(new[] { File("o'brien", "ROWS") }, "s", "/d/", "/l/"));
        }

        // ---- the runner ----

        private class FakeHost : ITestRestoreHost
        {
            public string DataPath { get { return "/data/"; } }
            public string LogPath { get { return "/log/"; } }
            public List<string> Calls = new List<string>();
            public HashSet<string> Existing = new HashSet<string>();
            public string CheckDbErrors;          // null = clean
            public Exception RestoreThrows;
            public Exception DropThrows;
            public Exception CheckDbThrows;
            public bool LastPhysicalOnly;
            public List<string> LastMoves;

            public bool DatabaseExists(string name) { Calls.Add("exists:" + name); return Existing.Contains(name); }

            public BackupStats Restore(string scratch, List<string> moves)
            {
                Calls.Add("restore:" + scratch);
                LastMoves = moves;
                Existing.Add(scratch);   // even a failed restore can leave a RESTORING database behind
                if (RestoreThrows != null) { throw RestoreThrows; }
                return new BackupStats { BytesProcessed = 5000 };
            }

            public string CheckDb(string scratch, bool physicalOnly)
            {
                Calls.Add("checkdb:" + scratch);
                LastPhysicalOnly = physicalOnly;
                if (CheckDbThrows != null) { throw CheckDbThrows; }
                return CheckDbErrors;
            }

            public void Drop(string scratch)
            {
                Calls.Add("drop:" + scratch);
                if (DropThrows != null) { throw DropThrows; }
                Existing.Remove(scratch);
            }
        }

        private static BackupManifest Manifest(string type = "full", bool withFiles = true)
        {
            BackupManifest m = new BackupManifest { Database = "app", BackupType = type };
            if (withFiles)
            {
                m.DatabaseFiles = new List<DatabaseFileInfo> { File("app", "ROWS"), File("app_log", "LOG") };
            }
            return m;
        }

        private static readonly DateTime Now = new DateTime(2026, 10, 26, 7, 0, 0, DateTimeKind.Utc);
        private const string Scratch = "sqs_testrestore_app_20261026070000";

        [Fact]
        public void Runner_RestoresChecksAndDrops_WhenAllIsWell()
        {
            FakeHost host = new FakeHost();

            TestRestoreResult r = TestRestoreRunner.Run(Manifest(), host, keep: false, physicalOnly: false, nowUtc: Now);

            Assert.True(r.Ok, string.Join("; ", r.Problems));
            Assert.Equal(Scratch, r.ScratchDatabase);
            Assert.Equal(5000, r.RestoredBytes);
            Assert.True(r.Dropped);
            Assert.Equal(new[] { "exists:" + Scratch, "restore:" + Scratch, "checkdb:" + Scratch, "exists:" + Scratch, "drop:" + Scratch }, host.Calls.ToArray());
            Assert.Equal("'app'TO'/data/" + Scratch + ".mdf'", host.LastMoves[0]);
        }

        [Fact]
        public void Runner_ReportsCheckDbErrors_AndStillDrops()
        {
            FakeHost host = new FakeHost { CheckDbErrors = "Msg 8939: Table error: Object ID 1" };

            TestRestoreResult r = TestRestoreRunner.Run(Manifest(), host, false, false, Now);

            Assert.False(r.Ok);
            Assert.Contains(r.Problems, p => p.Contains("DBCC CHECKDB") && p.Contains("Msg 8939"));
            Assert.True(r.Dropped);
        }

        [Fact]
        public void Runner_ACheckDbThatThrows_IsAProblem_AndStillDrops()
        {
            FakeHost host = new FakeHost { CheckDbThrows = new InvalidOperationException("connection lost") };

            TestRestoreResult r = TestRestoreRunner.Run(Manifest(), host, false, false, Now);

            Assert.False(r.Ok);
            Assert.Contains(r.Problems, p => p.Contains("connection lost"));
            Assert.True(r.Dropped);
        }

        [Fact]
        public void Runner_AFailedRestore_IsReported_NoCheckDb_AndTheLeftoverIsDropped()
        {
            FakeHost host = new FakeHost { RestoreThrows = new InvalidOperationException("Msg 3241: media family incorrectly formed") };

            TestRestoreResult r = TestRestoreRunner.Run(Manifest(), host, false, false, Now);

            Assert.False(r.Ok);
            Assert.Contains(r.Problems, p => p.Contains("restore failed") && p.Contains("Msg 3241"));
            Assert.DoesNotContain("checkdb:" + Scratch, host.Calls);
            Assert.Contains("drop:" + Scratch, host.Calls);
            Assert.True(r.Dropped);
        }

        [Fact]
        public void Runner_Keep_LeavesTheScratchDatabaseAndSaysSo()
        {
            FakeHost host = new FakeHost();

            TestRestoreResult r = TestRestoreRunner.Run(Manifest(), host, keep: true, physicalOnly: false, nowUtc: Now);

            Assert.True(r.Ok);
            Assert.False(r.Dropped);
            Assert.DoesNotContain("drop:" + Scratch, host.Calls);
        }

        [Fact]
        public void Runner_AFailedDrop_IsAProblem_SoSomeoneCleansUp()
        {
            FakeHost host = new FakeHost { DropThrows = new InvalidOperationException("database is in use") };

            TestRestoreResult r = TestRestoreRunner.Run(Manifest(), host, false, false, Now);

            Assert.False(r.Ok);
            Assert.False(r.Dropped);
            Assert.Contains(r.Problems, p => p.Contains("could not drop") && p.Contains(Scratch) && p.Contains("database is in use"));
        }

        [Fact]
        public void Runner_NeverTouchesADatabaseThatAlreadyHasTheScratchName()
        {
            FakeHost host = new FakeHost();
            host.Existing.Add(Scratch);

            ArgumentException e = Assert.Throws<ArgumentException>(() => TestRestoreRunner.Run(Manifest(), host, false, false, Now));

            Assert.Contains(Scratch, e.Message);
            Assert.DoesNotContain("restore:" + Scratch, host.Calls);
            Assert.DoesNotContain("drop:" + Scratch, host.Calls);
        }

        [Fact]
        public void Runner_PassesPhysicalOnlyToCheckDb()
        {
            FakeHost host = new FakeHost();

            TestRestoreRunner.Run(Manifest(), host, false, physicalOnly: true, nowUtc: Now);

            Assert.True(host.LastPhysicalOnly);
        }

        [Fact]
        public void Runner_NeedsAFullBackup()
        {
            ArgumentException e = Assert.Throws<ArgumentException>(() => TestRestoreRunner.Run(Manifest("log"), new FakeHost(), false, false, Now));

            Assert.Contains("full", e.Message);
        }

        [Fact]
        public void Runner_NeedsTheFileListInTheManifest()
        {
            ArgumentException e = Assert.Throws<ArgumentException>(() => TestRestoreRunner.Run(Manifest(withFiles: false), new FakeHost(), false, false, Now));

            Assert.Contains("file list", e.Message);
        }

        [Fact]
        public void Runner_NeedsTheManifestAtAll()
        {
            ArgumentException e = Assert.Throws<ArgumentException>(() => TestRestoreRunner.Run(null, new FakeHost(), false, false, Now));

            Assert.Contains("manifest", e.Message);
        }

        // ---- the manifest carries the file list ----

        [Fact]
        public void Manifest_RoundTripsTheDatabaseFileList()
        {
            BackupManifest back = BackupManifest.FromJson(Manifest().ToJson());

            Assert.Equal(2, back.DatabaseFiles.Count);
            Assert.Equal("app_log", back.DatabaseFiles[1].LogicalName);
            Assert.Equal("LOG", back.DatabaseFiles[1].Type);
        }

        [Fact]
        public void Manifest_WrittenBeforeTheFileListExisted_StillLoads()
        {
            BackupManifest old = BackupManifest.FromJson("{\"formatVersion\":1,\"database\":\"app\",\"backupType\":\"full\",\"files\":[]}");

            Assert.Equal("app", old.Database);
            Assert.True(old.DatabaseFiles == null || old.DatabaseFiles.Count == 0);
        }
    }
}
