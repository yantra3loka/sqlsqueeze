// unpack: turn a SqlSqueeze backup back into the plain native backup file(s),
// checking it against the manifest on the way. No SQL Server needed.

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
    public class UnpackTests : IDisposable
    {
        private readonly string mDir;

        public UnpackTests()
        {
            mDir = Path.Combine(Path.GetTempPath(), "sqsunpack-" + Guid.NewGuid().ToString("N"));
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

        private static ConfigPair Local(params string[] paths)
        {
            var p = new Dictionary<string, List<string>>(StringComparer.InvariantCultureIgnoreCase);
            p["path"] = new List<string>(paths);
            return new ConfigPair { TransformationType = typeof(LocalStorage), Parameters = p };
        }

        private static List<ConfigPair> ZstdAes()
        {
            return new List<ConfigPair>
            {
                new ConfigPair { TransformationType = typeof(ZstdTransform), Parameters = Cfg() },
                new ConfigPair { TransformationType = typeof(AesTransform), Parameters = Cfg(("password", "pw"), ("iterations", "1000")) },
            };
        }

        private static byte[] Sample(int size, int seed)
        {
            byte[] data = new byte[size];
            new Random(seed).NextBytes(data);
            Array.Clear(data, 0, data.Length / 2);
            return data;
        }

        /// <summary>Writes a backup the way BackupPipeSystem does, plus its manifest next to the first file.</summary>
        private void WriteBackup(string[] paths, byte[][] raw, List<ConfigPair> pipeline, bool withManifest = true)
        {
            LocalStorage storage = new LocalStorage();
            var cfg = Cfg();
            cfg["path"] = new List<string>(paths);
            Stream[] files = storage.GetBackupWriter(cfg);
            ManifestRecorder recorder = new ManifestRecorder();
            Stream[] tops = BackupPipeSystem.CreatePipeline(pipeline, files, true, new NullStreamNotification(), 0, recorder);
            for (int i = 0; i < tops.Length; i++)
            {
                tops[i].Write(raw[i], 0, raw[i].Length);
                tops[i].Dispose();
            }
            foreach (Stream file in files)
            {
                file.Dispose();
            }
            if (withManifest)
            {
                List<string> names = new List<string>();
                foreach (string p in paths)
                {
                    names.Add(Path.GetFileName(p));
                }
                storage.WriteManifest(cfg, recorder.Build("appdb", "full", "local", BackupPipeSystem.DescribePipeline(pipeline), names).ToJson());
            }
        }

        private string P(string name)
        {
            return Path.Combine(mDir, name);
        }

        [Fact]
        public void Unpack_WritesTheNativeFileExactlyAsSqlServerProducedIt()
        {
            byte[] raw = Sample(700000, 1);
            WriteBackup(new[] { P("a.bak.zst.aes") }, new[] { raw }, ZstdAes());

            UnpackResult r = BackupPipeSystem.Unpack(Local(P("a.bak.zst.aes")), ZstdAes(), Local(P("a.bak")));

            Assert.True(r.Ok, string.Join("; ", r.Problems));
            Assert.True(r.ManifestFound);
            Assert.Equal(raw.Length, r.BytesWritten);
            Assert.Equal(raw, File.ReadAllBytes(P("a.bak")));
        }

        [Fact]
        public void Unpack_Striped_WritesEachStripeToItsOwnFile()
        {
            byte[][] raw = { Sample(200000, 2), Sample(150000, 3) };
            WriteBackup(new[] { P("s1.zst"), P("s2.zst") }, raw, new List<ConfigPair> { ZstdAes()[0] });

            UnpackResult r = BackupPipeSystem.Unpack(Local(P("s1.zst"), P("s2.zst")), new List<ConfigPair> { ZstdAes()[0] }, Local(P("s1.bak"), P("s2.bak")));

            Assert.True(r.Ok, string.Join("; ", r.Problems));
            Assert.Equal(raw[0], File.ReadAllBytes(P("s1.bak")));
            Assert.Equal(raw[1], File.ReadAllBytes(P("s2.bak")));
            Assert.Equal(raw[0].Length + raw[1].Length, r.BytesWritten);
        }

        [Fact]
        public void Unpack_ACorruptBackup_FailsAndLeavesNoOutputBehind()
        {
            WriteBackup(new[] { P("c.bak.zst.aes") }, new[] { Sample(400000, 4) }, ZstdAes());
            byte[] stored = File.ReadAllBytes(P("c.bak.zst.aes"));
            stored[stored.Length / 2] ^= 0x01;
            File.WriteAllBytes(P("c.bak.zst.aes"), stored);

            UnpackResult r = BackupPipeSystem.Unpack(Local(P("c.bak.zst.aes")), ZstdAes(), Local(P("c.bak")));

            Assert.False(r.Ok);
            Assert.NotEmpty(r.Problems);
            Assert.False(File.Exists(P("c.bak")), "a half-written native file must not be left looking like a good backup");
        }

        [Fact]
        public void Unpack_AWrongPassword_Fails()
        {
            WriteBackup(new[] { P("w.bak.zst.aes") }, new[] { Sample(100000, 5) }, ZstdAes());
            List<ConfigPair> wrong = ZstdAes();
            wrong[1].Parameters["password"] = new List<string> { "not-it" };

            UnpackResult r = BackupPipeSystem.Unpack(Local(P("w.bak.zst.aes")), wrong, Local(P("w.bak")));

            Assert.False(r.Ok);
            Assert.False(File.Exists(P("w.bak")));
        }

        [Fact]
        public void Unpack_WithoutAManifest_StillConverts_AndSaysItCouldNotCompare()
        {
            byte[] raw = Sample(100000, 6);
            WriteBackup(new[] { P("n.bak.zst") }, new[] { raw }, new List<ConfigPair> { ZstdAes()[0] }, withManifest: false);

            UnpackResult r = BackupPipeSystem.Unpack(Local(P("n.bak.zst")), new List<ConfigPair> { ZstdAes()[0] }, Local(P("n.bak")));

            Assert.True(r.Ok);
            Assert.False(r.ManifestFound);
            Assert.Equal(raw, File.ReadAllBytes(P("n.bak")));
        }

        [Fact]
        public void Unpack_RefusesToOverwriteItsOwnSource()
        {
            WriteBackup(new[] { P("same.bak") }, new[] { Sample(1000, 7) }, new List<ConfigPair>());
            byte[] before = File.ReadAllBytes(P("same.bak"));

            ArgumentException e = Assert.Throws<ArgumentException>(() =>
                BackupPipeSystem.Unpack(Local(P("same.bak")), new List<ConfigPair>(), Local(P("same.bak"))));

            Assert.Contains("same", e.Message);
            Assert.Equal(before, File.ReadAllBytes(P("same.bak")));
        }

        [Fact]
        public void Unpack_NeverOverwritesAnExistingFile()
        {
            WriteBackup(new[] { P("o.bak.zst") }, new[] { Sample(5000, 10) }, new List<ConfigPair> { ZstdAes()[0] });
            File.WriteAllText(P("o.bak"), "precious");

            ArgumentException e = Assert.Throws<ArgumentException>(() =>
                BackupPipeSystem.Unpack(Local(P("o.bak.zst")), new List<ConfigPair> { ZstdAes()[0] }, Local(P("o.bak"))));

            Assert.Contains("already exists", e.Message);
            Assert.Equal("precious", File.ReadAllText(P("o.bak")));
        }

        [Fact]
        public void Unpack_RefusesADifferentNumberOfStripes_BeforeWritingAnything()
        {
            WriteBackup(new[] { P("t1.zst"), P("t2.zst") }, new[] { Sample(1000, 8), Sample(1000, 9) }, new List<ConfigPair> { ZstdAes()[0] });

            ArgumentException e = Assert.Throws<ArgumentException>(() =>
                BackupPipeSystem.Unpack(Local(P("t1.zst"), P("t2.zst")), new List<ConfigPair> { ZstdAes()[0] }, Local(P("only.bak"))));

            Assert.Contains("2", e.Message);
            Assert.False(File.Exists(P("only.bak")));
        }
    }
}
