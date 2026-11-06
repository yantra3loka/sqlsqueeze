// --copy-to: after a backup, copy it (and its manifest) to other places. The copy
// needs the names the backup really used, so storages report their resolved
// parameters, and a failed copy never turns a good backup into a failed one.

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
    public class CopyToTests : IDisposable
    {
        private readonly string mDir;

        public CopyToTests()
        {
            mDir = Path.Combine(Path.GetTempPath(), "sqscopyto-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(mDir);
        }

        public void Dispose()
        {
            Directory.Delete(mDir, true);
        }

        private string P(params string[] parts)
        {
            return Path.Combine(new[] { mDir }.Concat(parts).ToArray());
        }

        private static Dictionary<string, List<string>> Config(params string[] pairs)
        {
            var d = new Dictionary<string, List<string>>(StringComparer.InvariantCultureIgnoreCase);
            foreach (string pair in pairs)
            {
                int eq = pair.IndexOf('=');
                string key = pair.Substring(0, eq);
                if (!d.ContainsKey(key)) { d[key] = new List<string>(); }
                d[key].Add(pair.Substring(eq + 1));
            }
            return d;
        }

        private static ConfigPair Local(string path)
        {
            return new ConfigPair { TransformationType = typeof(LocalStorage), Parameters = Config("path=" + path) };
        }

        // ---- the names a storage really used ----

        [Fact]
        public void LocalStorage_ResolveConfig_GivesTheExpandedFullPathsTheWriterUsed()
        {
            LocalStorage storage = new LocalStorage();
            var cfg = Config("path=" + P("app_{yyyy}.bak"), "path=" + P("app2_{yyyy}.bak"));
            storage.GetBackupWriter(cfg).ToList().ForEach(s => s.Dispose());

            var resolved = storage.ResolveConfig(cfg);

            Assert.Equal(new[] { P("app_" + DateTime.Now.Year + ".bak"), P("app2_" + DateTime.Now.Year + ".bak") }, resolved["path"].ToArray());
        }

        [Fact]
        public void LocalStorage_ResolveConfig_DoesNotChangeTheCallersConfig_AndKeepsOtherOptions()
        {
            LocalStorage storage = new LocalStorage();
            var cfg = Config("path=" + P("a_{yyyy}.bak"), "writeretries=3");
            storage.GetBackupWriter(cfg).ToList().ForEach(s => s.Dispose());

            var resolved = storage.ResolveConfig(cfg);

            Assert.Equal(P("a_{yyyy}.bak"), cfg["path"][0]);
            Assert.Equal("3", resolved["writeretries"][0]);
        }

        [Fact]
        public void S3_ResolveConfig_GivesTheKeysTheWriterUsed()
        {
            FakeS3 fake = new FakeS3();
            S3Storage storage = new S3Storage(fake);
            var cfg = Config("endpoint=http://127.0.0.1:9000", "bucket=bkt", "region=us-east-1", "accesskey=AK", "secretkey=SK", "key=db/app_{yyyy}.bak");
            storage.GetBackupWriter(cfg);

            var resolved = storage.ResolveConfig(cfg);

            Assert.Equal(new[] { "db/app_" + DateTime.Now.Year + ".bak" }, resolved["key"].ToArray());
            Assert.Equal("bkt", resolved["bucket"][0]);
        }

        [Fact]
        public void Azure_ResolveConfig_GivesTheBlobsTheWriterUsed()
        {
            FakeAzure fake = new FakeAzure();
            AzureStorage storage = new AzureStorage(fake);
            var cfg = Config("endpoint=http://127.0.0.1:10000/devstoreaccount1", "container=cont", "account=devstoreaccount1",
                "accountkey=Eby8vdM02xNOcqFlqUwJPLlmEtlCDXJ1OUzFT50uSRZ6IFsuFq2UVErCz4I6tq/K1SZFPTOtr/KBHBeksoGMGw==", "blob=db/app_{yyyy}.bak");
            storage.GetBackupWriter(cfg);

            var resolved = storage.ResolveConfig(cfg);

            Assert.Equal(new[] { "db/app_" + DateTime.Now.Year + ".bak" }, resolved["blob"].ToArray());
            Assert.Equal("cont", resolved["container"][0]);
        }

        [Fact]
        public void ResolveConfig_BeforeAnythingWasWritten_IsJustTheConfig()
        {
            var cfg = Config("path=" + P("x.bak"));

            Assert.Equal(P("x.bak"), new LocalStorage().ResolveConfig(cfg)["path"][0]);
        }

        // ---- copying after a backup ----

        private static CopyOptions Instant()
        {
            return new CopyOptions { MaxDuration = TimeSpan.FromSeconds(1), BackoffBase = TimeSpan.FromMilliseconds(1), BackoffMax = TimeSpan.FromMilliseconds(1), Sleep = s => { } };
        }

        /// <summary>A finished local backup named with a date token, with its manifest, the way backup leaves it.</summary>
        private LocalStorage FinishedBackup(string templatePath, byte[] data, out ConfigPair storageConfig)
        {
            LocalStorage storage = new LocalStorage();
            storageConfig = Local(templatePath);
            Stream[] writers = storage.GetBackupWriter(storageConfig.Parameters);
            writers[0].Write(data, 0, data.Length);
            writers[0].Dispose();
            var recorder = new BackupManifest
            {
                Database = "app", BackupType = "full", Storage = "local",
                Files = new List<ManifestFile>
                {
                    new ManifestFile
                    {
                        Name = storage.GetFileNames(storageConfig.Parameters)[0], StoredBytes = data.Length,
                        StoredSha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(data)).ToLowerInvariant(),
                    },
                },
            };
            storage.WriteManifest(storageConfig.Parameters, recorder.ToJson());
            return storage;
        }

        [Fact]
        public void CopyBackupTo_CopiesTheFileThatWasReallyWritten_AndItsManifest_ToEveryDestination()
        {
            byte[] data = new byte[5000];
            new Random(1).NextBytes(data);
            ConfigPair config;
            LocalStorage used = FinishedBackup(P("src", "app_{yyyy}.bak"), data, out config);
            BackupStats stats = new BackupStats();

            BackupPipeSystem.CopyBackupTo(used, config, new List<ConfigPair> { Local(P("one", "app.bak")), Local(P("two", "app.bak")) }, Instant(), stats);

            Assert.Empty(stats.CopyProblems);
            Assert.Equal(2, stats.Copies.Count);
            Assert.All(stats.Copies, c => Assert.True(c.Ok));
            Assert.Equal(data, File.ReadAllBytes(P("one", "app.bak")));
            Assert.Equal(data, File.ReadAllBytes(P("two", "app.bak")));
            Assert.True(File.Exists(P("one", "app.bak.manifest.json")));
            Assert.Equal("app.bak", BackupManifest.FromJson(File.ReadAllText(P("two", "app.bak.manifest.json"))).Files[0].Name);
        }

        [Fact]
        public void CopyBackupTo_AFailedCopy_IsReported_ButDoesNotStopTheOthers()
        {
            byte[] data = new byte[2000];
            new Random(2).NextBytes(data);
            ConfigPair config;
            LocalStorage used = FinishedBackup(P("src", "b.bak"), data, out config);
            File.WriteAllText(P("blocked"), "a file where a folder is needed");
            BackupStats stats = new BackupStats();

            BackupPipeSystem.CopyBackupTo(used, config,
                new List<ConfigPair> { Local(P("blocked", "b.bak")), Local(P("good", "b.bak")) }, Instant(), stats);

            Assert.Equal(2, stats.Copies.Count);
            Assert.False(stats.Copies[0].Ok);
            Assert.True(stats.Copies[1].Ok);
            Assert.Equal(data, File.ReadAllBytes(P("good", "b.bak")));
            Assert.Contains(stats.CopyProblems, p => p.Contains("blocked"));
        }

        [Fact]
        public void CopyBackupTo_ADestinationThatIsTheBackupItself_IsReported_NotCopiedOverItself()
        {
            byte[] data = new byte[1000];
            ConfigPair config;
            LocalStorage used = FinishedBackup(P("src", "c.bak"), data, out config);
            BackupStats stats = new BackupStats();

            BackupPipeSystem.CopyBackupTo(used, config, new List<ConfigPair> { Local(P("src", "c.bak")) }, Instant(), stats);

            Assert.Single(stats.CopyProblems);
            Assert.Equal(data, File.ReadAllBytes(P("src", "c.bak")));
        }

        [Fact]
        public void CopyBackupTo_WithNoDestinations_DoesNothing()
        {
            ConfigPair config;
            LocalStorage used = FinishedBackup(P("src", "d.bak"), new byte[10], out config);
            BackupStats stats = new BackupStats();

            BackupPipeSystem.CopyBackupTo(used, config, new List<ConfigPair>(), Instant(), stats);

            Assert.Empty(stats.Copies);
            Assert.Empty(stats.CopyProblems);
        }

        [Fact]
        public void CopyBackupTo_NamesWhereEachCopyWent()
        {
            ConfigPair config;
            LocalStorage used = FinishedBackup(P("src", "e.bak"), new byte[10], out config);
            BackupStats stats = new BackupStats();

            BackupPipeSystem.CopyBackupTo(used, config, new List<ConfigPair> { Local(P("elsewhere", "e.bak")) }, Instant(), stats);

            Assert.Contains("local", stats.Copies[0].Destination);
            Assert.Contains(P("elsewhere", "e.bak"), stats.Copies[0].Destination);
        }

        [Fact]
        public void Describe_NeverShowsSecrets()
        {
            ConfigPair s3 = new ConfigPair
            {
                TransformationType = typeof(S3Storage),
                Parameters = Config("endpoint=http://h:9000", "bucket=bkt", "key=k.bak", "accesskey=AKIDSECRET", "secretkey=TOPSECRET", "secretkeyenv=SK"),
            };

            string text = BackupPipeSystem.DescribeDestination(s3);

            Assert.Contains("bkt", text);
            Assert.Contains("k.bak", text);
            Assert.DoesNotContain("AKIDSECRET", text);
            Assert.DoesNotContain("TOPSECRET", text);
        }

        // ---- backup-all ----

        private class FakeCatalog : IDatabaseCatalog
        {
            public List<DatabaseInfo> List()
            {
                return new List<DatabaseInfo>
                {
                    new DatabaseInfo { Name = "app", RecoveryModel = "FULL", State = "ONLINE", HasLogBase = true, HasDiffBase = true },
                    new DatabaseInfo { Name = "billing", RecoveryModel = "FULL", State = "ONLINE", HasLogBase = true, HasDiffBase = true },
                };
            }
        }

        private static ConfigPair DbTemplate()
        {
            return new ConfigPair { TransformationType = typeof(object), Parameters = Config("user=sa") };
        }

        [Fact]
        public void ExpandTokens_ReplacesDatabaseAndBackupTypeInEveryParameter_AndKeepsTheOriginal()
        {
            ConfigPair template = Local("/b/{database}/{database}_{backuptype}.bak");

            ConfigPair expanded = BackupAllRunner.ExpandTokens(template, "odd:name", "log");

            Assert.Equal("/b/odd_name/odd_name_log.bak", expanded.Parameters["path"][0]);
            Assert.Equal("/b/{database}/{database}_{backuptype}.bak", template.Parameters["path"][0]);
        }

        [Fact]
        public void BackupAll_ACopyThatFailed_MakesTheRunNotOk_ButTheBackupItselfStaysOk()
        {
            BackupAllResult result = BackupAllRunner.Run(new FakeCatalog(), "app,billing", "full", true, DbTemplate(), new List<ConfigPair>(), Local("/b/{database}.bak"),
                (db, pipe, storage) =>
                {
                    BackupStats stats = new BackupStats { BytesProcessed = 10 };
                    if (db.Parameters["database"][0] == "billing")
                    {
                        stats.CopyProblems.Add("copy to s3(bkt/billing.bak): connection refused");
                    }
                    return stats;
                });

            Assert.False(result.AllOk);
            Assert.Equal(new[] { "ok", "ok" }, result.Items.Select(i => i.Status).ToArray());
            Assert.Empty(result.Items[0].CopyProblems);
            Assert.Single(result.Items[1].CopyProblems);
        }

        [Fact]
        public void BackupAll_SeveralDatabasesToOneCopyToPath_IsRefusedBeforeBackingUpAnything()
        {
            int backups = 0;

            ArgumentException e = Assert.Throws<ArgumentException>(() =>
                BackupAllRunner.Run(new FakeCatalog(), "app,billing", "full", true, DbTemplate(), new List<ConfigPair>(), Local("/b/{database}.bak"),
                    (db, pipe, storage) => { backups++; return new BackupStats(); },
                    new List<ConfigPair> { Local("/mirror/all.bak") }));

            Assert.Contains("copy-to", e.Message);
            Assert.Contains("{database}", e.Message);
            Assert.Equal(0, backups);
        }
    }
}
