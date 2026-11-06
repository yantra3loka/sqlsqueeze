// Backup manifest + verify tests. No SQL Server needed: backups are written
// through the real pipeline (CreatePipeline) into real local files.

using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using SqlSqueeze.Core;
using SqlSqueeze.Plugins;
using SqlSqueeze.Plugins.Storage;
using SqlSqueeze.Plugins.Transform;
using Xunit;

namespace SqlSqueeze.Tests
{
    public class ManifestTests : IDisposable
    {
        private readonly string mDir;

        public ManifestTests()
        {
            mDir = Path.Combine(Path.GetTempPath(), "sqsmanifest-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(mDir);
        }

        public void Dispose()
        {
            Directory.Delete(mDir, true);
        }

        private class NullStreamNotification : IStreamNotification
        {
            public long EstimatedBytes { get; set; }
            public int GetThreadId() { return 0; }
            public TimeSpan UpdateBytesProcessed(long totalBytesProcessedByThread, int threadId) { return TimeSpan.FromHours(1); }
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

        private static byte[] Sample(int size, int seed)
        {
            byte[] data = new byte[size];
            new Random(seed).NextBytes(data);
            Array.Clear(data, 0, data.Length / 2);
            return data;
        }

        private static string Sha(byte[] bytes)
        {
            return Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        }

        private static List<ConfigPair> Gzip()
        {
            return new List<ConfigPair> { new ConfigPair { TransformationType = typeof(GzipTransform), Parameters = Cfg() } };
        }

        private static List<ConfigPair> ZstdAes(string password)
        {
            return new List<ConfigPair>
            {
                new ConfigPair { TransformationType = typeof(ZstdTransform), Parameters = Cfg(("level", "3")) },
                new ConfigPair { TransformationType = typeof(AesTransform), Parameters = Cfg(("password", password), ("iterations", "1000")) },
            };
        }

        /// <summary>
        /// Writes one backup the way BackupPipeSystem does (CreatePipeline with a
        /// recorder), one file per entry in raw, and returns the manifest.
        /// </summary>
        private BackupManifest WriteBackup(LocalStorage storage, string[] paths, byte[][] raw, List<ConfigPair> pipeline)
        {
            var cfg = new Dictionary<string, List<string>>(StringComparer.InvariantCultureIgnoreCase);
            cfg["path"] = new List<string>(paths);

            Stream[] files = storage.GetBackupWriter(cfg);
            ManifestRecorder recorder = new ManifestRecorder();
            Stream[] tops = BackupPipeSystem.CreatePipeline(pipeline, files, true, new NullStreamNotification(), 0, recorder);
            for (int i = 0; i < tops.Length; i++)
            {
                tops[i].Write(raw[i], 0, raw[i].Length);
                tops[i].Dispose();
            }
            // BackupPipeSystem disposes the pipeline and the file streams as
            // separate lists, since zstd/aes do not close the stream below them.
            foreach (Stream file in files)
            {
                file.Dispose();
            }
            List<string> names = new List<string>();
            foreach (string p in paths)
            {
                names.Add(Path.GetFileName(p));
            }
            return recorder.Build("appdb", "full", "local", BackupPipeSystem.DescribePipeline(pipeline), names);
        }

        private static Dictionary<string, List<string>> PathCfg(params string[] paths)
        {
            var cfg = new Dictionary<string, List<string>>(StringComparer.InvariantCultureIgnoreCase);
            cfg["path"] = new List<string>(paths);
            return cfg;
        }

        [Theory]
        [InlineData(typeof(GzipTransform))]
        [InlineData(typeof(Bzip2Transform))]
        [InlineData(typeof(Zip64Transform))]
        [InlineData(typeof(ZstdTransform))]
        public void Compression_RestoreAcceptsAndIgnoresLevel_SoOnePipelineSpecWorksBothWays(Type transform)
        {
            IBackupTransformer t = (IBackupTransformer)Activator.CreateInstance(transform);
            byte[] raw = Sample(50000, 12);
            byte[] stored;
            using (MemoryStream ms = new MemoryStream())
            {
                using (Stream w = t.GetBackupWriter(Cfg(("level", "3")), ms))
                {
                    w.Write(raw, 0, raw.Length);
                }
                stored = ms.ToArray();
            }

            using (Stream r = t.GetRestoreReader(Cfg(("level", "3")), new MemoryStream(stored)))
            using (MemoryStream back = new MemoryStream())
            {
                r.CopyTo(back);
                Assert.Equal(raw, back.ToArray());
            }
        }

        private static ConfigPair Stage(string name)
        {
            switch (name)
            {
                case "gzip": return new ConfigPair { TransformationType = typeof(GzipTransform), Parameters = Cfg() };
                case "bzip2": return new ConfigPair { TransformationType = typeof(Bzip2Transform), Parameters = Cfg() };
                case "zip64": return new ConfigPair { TransformationType = typeof(Zip64Transform), Parameters = Cfg() };
                case "zstd": return new ConfigPair { TransformationType = typeof(ZstdTransform), Parameters = Cfg() };
                case "aes": return new ConfigPair { TransformationType = typeof(AesTransform), Parameters = Cfg(("password", "pw"), ("iterations", "1000")) };
                default: throw new ArgumentException(name);
            }
        }

        public static IEnumerable<object[]> StagePairs()
        {
            string[] names = { "gzip", "bzip2", "zip64", "zstd", "aes" };
            foreach (string first in names)
            {
                foreach (string second in names)
                {
                    if (first != second)
                    {
                        yield return new object[] { first, second };
                    }
                }
            }
        }

        [Theory]
        [MemberData(nameof(StagePairs))]
        public void Pipeline_AnyTwoStages_WriteACompleteBackupThatVerifies(string first, string second)
        {
            // Regression: zstd used to leave the stage below it open, so zstd -> aes
            // never wrote aes's final block and the backup could not be restored.
            List<ConfigPair> pipeline = new List<ConfigPair> { Stage(first), Stage(second) };
            byte[] raw = Sample(120000, 13);
            string path = Path.Combine(mDir, first + "-" + second + ".bak");
            BackupManifest m = WriteBackup(new LocalStorage(), new[] { path }, new[] { raw }, pipeline);

            VerifyResult r = BackupVerifier.Verify(new LocalStorage(), PathCfg(path), pipeline, m);

            Assert.True(r.Ok, string.Join("; ", r.Problems));
            Assert.Equal(raw.Length, r.RawBytes);
        }

        [Fact]
        public void DescribeStage_RedactsSecretsButKeepsEnvVarNames()
        {
            string spec = BackupManifest.DescribeStage("aes", Cfg(("password", "s3cret"), ("iterations", "1000"), ("passwordEnv", "BACKUP_KEY")));

            Assert.Equal("aes(iterations=1000;password=***;passwordEnv=BACKUP_KEY)", spec);
            Assert.DoesNotContain("s3cret", spec);
        }

        [Fact]
        public void DescribeStage_WithoutParametersIsJustTheName()
        {
            Assert.Equal("gzip", BackupManifest.DescribeStage("gzip", Cfg()));
        }

        [Fact]
        public void Manifest_RoundTripsThroughJson()
        {
            BackupManifest m = new BackupManifest
            {
                Database = "appdb",
                BackupType = "diff",
                Storage = "local",
                CreatedUtc = new DateTime(2026, 10, 26, 6, 0, 0, DateTimeKind.Utc),
                Pipeline = new List<string> { "zstd(level=3)", "aes(password=***)" },
                Files = new List<ManifestFile>
                {
                    new ManifestFile { Name = "a.bak.zst", StoredBytes = 10, StoredSha256 = "aa", RawBytes = 20, RawSha256 = "bb" },
                },
            };

            BackupManifest back = BackupManifest.FromJson(m.ToJson());

            Assert.Equal(1, back.FormatVersion);
            Assert.Equal("appdb", back.Database);
            Assert.Equal("diff", back.BackupType);
            Assert.Equal(m.CreatedUtc, back.CreatedUtc);
            Assert.Equal(m.Pipeline, back.Pipeline);
            Assert.Single(back.Files);
            Assert.Equal("bb", back.Files[0].RawSha256);
            Assert.Equal(20, back.Files[0].RawBytes);
        }

        [Fact]
        public void FromJson_RejectsAFormatNewerThanThisBuildUnderstands()
        {
            string json = "{\"formatVersion\":99,\"files\":[]}";

            ArgumentException e = Assert.Throws<ArgumentException>(() => BackupManifest.FromJson(json));

            Assert.Contains("99", e.Message);
        }

        [Fact]
        public void Recorder_RecordsStoredAndRawSizesAndHashes()
        {
            byte[] raw = Sample(400000, 1);
            string path = Path.Combine(mDir, "one.bak.gz");

            BackupManifest m = WriteBackup(new LocalStorage(), new[] { path }, new[] { raw }, Gzip());

            byte[] onDisk = File.ReadAllBytes(path);
            Assert.Single(m.Files);
            Assert.Equal("one.bak.gz", m.Files[0].Name);
            Assert.Equal(raw.Length, m.Files[0].RawBytes);
            Assert.Equal(Sha(raw), m.Files[0].RawSha256);
            Assert.Equal(onDisk.Length, m.Files[0].StoredBytes);
            Assert.Equal(Sha(onDisk), m.Files[0].StoredSha256);
            Assert.Equal(new List<string> { "gzip" }, m.Pipeline);
        }

        [Fact]
        public void LocalStorage_WritesManifestNextToTheFirstFileAndReadsItBack()
        {
            LocalStorage storage = new LocalStorage();
            string path = Path.Combine(mDir, "m.bak.gz");
            BackupManifest m = WriteBackup(storage, new[] { path }, new[] { Sample(1000, 2) }, Gzip());

            storage.WriteManifest(PathCfg(path), m.ToJson());

            Assert.True(File.Exists(path + ".manifest.json"));
            BackupManifest back = BackupManifest.FromJson(new LocalStorage().ReadManifest(PathCfg(path)));
            Assert.Equal("appdb", back.Database);
            Assert.Equal(m.Files[0].StoredSha256, back.Files[0].StoredSha256);
        }

        [Fact]
        public void LocalStorage_RestoreReaderOpensReadOnlyFiles()
        {
            // Backups often belong to another user (the mssql service account) or sit
            // on read-only/immutable storage; reading one must not ask for write access.
            string path = Path.Combine(mDir, "readonly.bak");
            byte[] data = Sample(5000, 14);
            File.WriteAllBytes(path, data);
            File.SetAttributes(path, FileAttributes.ReadOnly);
            try
            {
                Stream[] readers = new LocalStorage().GetRestoreReader(PathCfg(path), out long estimated);
                using (Stream reader = readers[0])
                using (MemoryStream copy = new MemoryStream())
                {
                    reader.CopyTo(copy);
                    Assert.Equal(data, copy.ToArray());
                }
                Assert.Equal(data.Length, estimated);
            }
            finally
            {
                File.SetAttributes(path, FileAttributes.Normal);
            }
        }

        [Fact]
        public void LocalStorage_ReadManifestIsNullWhenThereIsNone()
        {
            Assert.Null(new LocalStorage().ReadManifest(PathCfg(Path.Combine(mDir, "none.bak"))));
        }

        [Fact]
        public void Verify_IntactBackup_IsOkAndMatchesTheManifest()
        {
            byte[] raw = Sample(600000, 3);
            string path = Path.Combine(mDir, "ok.bak");
            List<ConfigPair> pipeline = ZstdAes("pw");
            BackupManifest m = WriteBackup(new LocalStorage(), new[] { path }, new[] { raw }, pipeline);

            VerifyResult r = BackupVerifier.Verify(new LocalStorage(), PathCfg(path), pipeline, m);

            Assert.True(r.Ok, string.Join("; ", r.Problems));
            Assert.True(r.ManifestFound);
            Assert.Equal(raw.Length, r.RawBytes);
            Assert.Empty(r.Problems);
        }

        [Fact]
        public void Verify_StripedBackup_ChecksEveryFile()
        {
            byte[][] raw = { Sample(300000, 4), Sample(250000, 5) };
            string[] paths = { Path.Combine(mDir, "s1.bak"), Path.Combine(mDir, "s2.bak") };
            BackupManifest m = WriteBackup(new LocalStorage(), paths, raw, Gzip());

            VerifyResult ok = BackupVerifier.Verify(new LocalStorage(), PathCfg(paths), Gzip(), m);
            Assert.True(ok.Ok, string.Join("; ", ok.Problems));
            Assert.Equal(raw[0].Length + raw[1].Length, ok.RawBytes);

            byte[] second = File.ReadAllBytes(paths[1]);
            second[second.Length / 2] ^= 0xFF;
            File.WriteAllBytes(paths[1], second);

            VerifyResult bad = BackupVerifier.Verify(new LocalStorage(), PathCfg(paths), Gzip(), m);
            Assert.False(bad.Ok);
            Assert.Contains(bad.Problems, p => p.Contains("s2.bak"));
        }

        [Fact]
        public void Verify_FlippedStoredByte_Fails()
        {
            string path = Path.Combine(mDir, "flip.bak");
            List<ConfigPair> pipeline = Gzip();
            BackupManifest m = WriteBackup(new LocalStorage(), new[] { path }, new[] { Sample(500000, 6) }, pipeline);
            byte[] stored = File.ReadAllBytes(path);
            stored[stored.Length / 2] ^= 0x01;
            File.WriteAllBytes(path, stored);

            VerifyResult r = BackupVerifier.Verify(new LocalStorage(), PathCfg(path), pipeline, m);

            Assert.False(r.Ok);
            Assert.Contains(r.Problems, p => p.Contains("stored sha256"));
        }

        [Fact]
        public void Verify_TruncatedFile_Fails()
        {
            string path = Path.Combine(mDir, "trunc.bak");
            List<ConfigPair> pipeline = ZstdAes("pw");
            BackupManifest m = WriteBackup(new LocalStorage(), new[] { path }, new[] { Sample(500000, 7) }, pipeline);
            byte[] stored = File.ReadAllBytes(path);
            File.WriteAllBytes(path, stored[..(stored.Length - 5000)]);

            VerifyResult r = BackupVerifier.Verify(new LocalStorage(), PathCfg(path), pipeline, m);

            Assert.False(r.Ok);
            Assert.NotEmpty(r.Problems);
        }

        [Fact]
        public void Verify_WrongPassword_Fails()
        {
            byte[] raw = Sample(400000, 8);
            string path = Path.Combine(mDir, "pw.bak");
            BackupManifest m = WriteBackup(new LocalStorage(), new[] { path }, new[] { raw }, ZstdAes("right"));

            VerifyResult r = BackupVerifier.Verify(new LocalStorage(), PathCfg(path), ZstdAes("wrong"), m);

            Assert.False(r.Ok);
            Assert.NotEmpty(r.Problems);
        }

        [Fact]
        public void Verify_AesOnlyWithWrongPassword_IsCaughtByTheRawHash()
        {
            // AES alone cannot tell a wrong password from a right one; the
            // manifest's hash of the decoded stream is what catches it.
            List<ConfigPair> right = new List<ConfigPair> { new ConfigPair { TransformationType = typeof(AesTransform), Parameters = Cfg(("password", "right"), ("iterations", "1000")) } };
            List<ConfigPair> wrong = new List<ConfigPair> { new ConfigPair { TransformationType = typeof(AesTransform), Parameters = Cfg(("password", "wrong"), ("iterations", "1000")) } };
            string path = Path.Combine(mDir, "aesonly.bak");
            BackupManifest m = WriteBackup(new LocalStorage(), new[] { path }, new[] { Sample(100000, 9) }, right);

            VerifyResult r = BackupVerifier.Verify(new LocalStorage(), PathCfg(path), wrong, m);

            Assert.False(r.Ok);
            Assert.Contains(r.Problems, p => p.Contains("raw sha256") || p.Contains("decode"));
        }

        [Fact]
        public void Verify_WithoutManifest_StillChecksThatTheFileDecodes()
        {
            string path = Path.Combine(mDir, "nomanifest.bak");
            List<ConfigPair> pipeline = Gzip();
            WriteBackup(new LocalStorage(), new[] { path }, new[] { Sample(200000, 10) }, pipeline);

            VerifyResult good = BackupVerifier.Verify(new LocalStorage(), PathCfg(path), pipeline, null);
            Assert.True(good.Ok);
            Assert.False(good.ManifestFound);

            byte[] stored = File.ReadAllBytes(path);
            File.WriteAllBytes(path, stored[..(stored.Length / 2)]);
            VerifyResult bad = BackupVerifier.Verify(new LocalStorage(), PathCfg(path), pipeline, null);
            Assert.False(bad.Ok);
        }

        [Fact]
        public void SqlCheck_Passing_KeepsTheResultOkAndSaysSQLServerChecked()
        {
            VerifyResult r = new VerifyResult();

            Assert.False(r.SqlChecked);
            r.RecordSqlCheck(true, "The backup set on file 1 is valid.");

            Assert.True(r.Ok);
            Assert.True(r.SqlChecked);
            Assert.Equal("The backup set on file 1 is valid.", r.SqlMessage);
        }

        [Fact]
        public void SqlCheck_Failing_MakesTheResultFailWithSQLServersMessage()
        {
            VerifyResult r = new VerifyResult();

            r.RecordSqlCheck(false, "Msg 3241: The media family on device is incorrectly formed.");

            Assert.False(r.Ok);
            Assert.True(r.SqlChecked);
            Assert.Contains(r.Problems, p => p.Contains("SQL Server") && p.Contains("Msg 3241"));
        }

        [Fact]
        public void Verify_ManifestListingADifferentNumberOfFiles_Fails()
        {
            string path = Path.Combine(mDir, "count.bak");
            List<ConfigPair> pipeline = Gzip();
            BackupManifest m = WriteBackup(new LocalStorage(), new[] { path }, new[] { Sample(10000, 11) }, pipeline);
            m.Files.Add(new ManifestFile { Name = "ghost.bak" });

            VerifyResult r = BackupVerifier.Verify(new LocalStorage(), PathCfg(path), pipeline, m);

            Assert.False(r.Ok);
            Assert.Contains(r.Problems, p => p.Contains("2 file"));
        }
    }
}
