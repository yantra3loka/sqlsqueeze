// Prune (retention) tests. No SQL Server needed.

using System;
using System.Collections.Generic;
using System.IO;
using SqlSqueeze.Plugins.Storage;
using Xunit;

namespace SqlSqueeze.Tests
{
    public class PruneTests : IDisposable
    {
        private readonly string _dir;

        public PruneTests()
        {
            _dir = Path.Combine(Path.GetTempPath(), "sqlsqueeze-prune-" + Guid.NewGuid());
            Directory.CreateDirectory(_dir);
        }

        public void Dispose()
        {
            try { Directory.Delete(_dir, true); } catch { }
        }

        private static Dictionary<string, List<string>> Cfg(params (string, string)[] pairs)
        {
            var d = new Dictionary<string, List<string>>(StringComparer.InvariantCultureIgnoreCase);
            foreach ((string k, string v) in pairs)
            {
                d[k] = new List<string> { v };
            }
            return d;
        }

        private string WriteFile(string name, int bytes, DateTime lastWriteUtc)
        {
            string path = Path.Combine(_dir, name);
            File.WriteAllBytes(path, new byte[bytes]);
            File.SetLastWriteTimeUtc(path, lastWriteUtc);
            return path;
        }

        [Fact]
        public void DeletesOnlyAgedMatchingFiles()
        {
            DateTime now = DateTime.UtcNow;
            string oldBak = WriteFile("old.bak.gz", 100, now.AddDays(-40));
            string newBak = WriteFile("new.bak.gz", 200, now.AddHours(-1));
            string oldLog = WriteFile("old.trn", 50, now.AddDays(-40));

            var result = new LocalStorage().PruneOldFiles(
                Cfg(("dir", _dir), ("pattern", "*.bak.gz"), ("olderthandays", "30")));

            Assert.Equal(1, result.DeletedFiles);
            Assert.Equal(100, result.BytesReclaimed);
            Assert.Contains(oldBak, result.DeletedPaths);
            Assert.False(File.Exists(oldBak));
            Assert.True(File.Exists(newBak));
            Assert.True(File.Exists(oldLog)); // pattern did not match
        }

        [Fact]
        public void AgedBackup_TakesItsManifestWithIt()
        {
            DateTime now = DateTime.UtcNow;
            string oldBak = WriteFile("old.bak.zst", 100, now.AddDays(-40));
            string oldManifest = WriteFile("old.bak.zst.manifest.json", 7, now.AddDays(-40));

            var result = new LocalStorage().PruneOldFiles(
                Cfg(("dir", _dir), ("pattern", "*.bak.zst"), ("olderthandays", "30")));

            Assert.Equal(1, result.DeletedFiles);
            Assert.Equal(107, result.BytesReclaimed);
            Assert.False(File.Exists(oldBak));
            Assert.False(File.Exists(oldManifest));
        }

        [Fact]
        public void YoungBackup_KeepsItsManifest()
        {
            DateTime now = DateTime.UtcNow;
            string newBak = WriteFile("new.bak.zst", 100, now.AddHours(-1));
            string newManifest = WriteFile("new.bak.zst.manifest.json", 7, now.AddHours(-1));

            var result = new LocalStorage().PruneOldFiles(
                Cfg(("dir", _dir), ("pattern", "*.bak.zst"), ("olderthandays", "30")));

            Assert.Equal(0, result.DeletedFiles);
            Assert.True(File.Exists(newBak));
            Assert.True(File.Exists(newManifest));
        }

        [Fact]
        public void StarPattern_CountsABackupAndItsManifestOnce()
        {
            DateTime now = DateTime.UtcNow;
            WriteFile("old.bak", 100, now.AddDays(-40));
            WriteFile("old.bak.manifest.json", 7, now.AddDays(-40));

            var result = new LocalStorage().PruneOldFiles(
                Cfg(("dir", _dir), ("olderthandays", "30")));

            Assert.Equal(1, result.DeletedFiles);
            Assert.Equal(107, result.BytesReclaimed);
            Assert.Empty(Directory.GetFiles(_dir));
        }

        [Fact]
        public void DefaultPatternIsStar()
        {
            DateTime now = DateTime.UtcNow;
            WriteFile("a.bak", 10, now.AddDays(-5));
            WriteFile("b.trn", 20, now.AddDays(-5));

            var result = new LocalStorage().PruneOldFiles(
                Cfg(("dir", _dir), ("olderthandays", "1")));

            Assert.Equal(2, result.DeletedFiles);
            Assert.Equal(30, result.BytesReclaimed);
        }

        [Fact]
        public void RejectsMissingDirAndBadRetention()
        {
            var store = new LocalStorage();
            Assert.Throws<DirectoryNotFoundException>(() => store.PruneOldFiles(
                Cfg(("dir", Path.Combine(_dir, "nope")), ("olderthandays", "7"))));
            Assert.Throws<ArgumentException>(() => store.PruneOldFiles(
                Cfg(("dir", _dir), ("olderthandays", "0"))));
            Assert.Throws<ArgumentException>(() => store.PruneOldFiles(
                Cfg(("olderthandays", "7"))));
        }
    }
}
