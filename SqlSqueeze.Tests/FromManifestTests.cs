// --from-manifest: a backup's manifest says which pipeline made it, and that
// pipeline can be parsed back and used to read the backup. No SQL Server needed.

using System;
using System.Collections.Generic;
using System.IO;
using SqlSqueeze.Core;
using SqlSqueeze.Plugins;
using SqlSqueeze.Plugins.Storage;
using SqlSqueeze.Plugins.Transform;
using Xunit;

namespace SqlSqueeze.Tests
{
    public class FromManifestTests : IDisposable
    {
        private readonly string mDir;

        public FromManifestTests()
        {
            mDir = Path.Combine(Path.GetTempPath(), "sqsfrom-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(mDir);
        }

        public void Dispose()
        {
            Directory.Delete(mDir, true);
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

        private static Dictionary<string, Type> Components()
        {
            return new Dictionary<string, Type>(StringComparer.InvariantCultureIgnoreCase)
            {
                { "gzip", typeof(GzipTransform) }, { "bzip2", typeof(Bzip2Transform) }, { "zip64", typeof(Zip64Transform) },
                { "zstd", typeof(ZstdTransform) }, { "aes", typeof(AesTransform) }, { "rate", typeof(RateTransform) },
                { "local", typeof(LocalStorage) },
            };
        }

        // ---- the spec text in the manifest ----

        [Fact]
        public void DescribeStage_EscapesSemicolonsSoTheSpecParsesBack()
        {
            string spec = BackupManifest.DescribeStage("zip64", Cfg(("filename", "a;b")));

            Assert.Equal("zip64(filename=a;;b)", spec);
        }

        [Fact]
        public void ADescribedStage_ParsesBackToTheSameParameters()
        {
            string spec = BackupManifest.DescribeStage("zip64", Cfg(("filename", "a;b"), ("level", "5")));

            ConfigPair parsed = ConfigUtil.ParseComponentConfig(Components(), spec);

            Assert.Equal(typeof(Zip64Transform), parsed.TransformationType);
            Assert.Equal("a;b", parsed.Parameters["filename"][0]);
            Assert.Equal("5", parsed.Parameters["level"][0]);
        }

        // ---- using the manifest's pipeline ----

        [Fact]
        public void PipelineSpecsForRestore_ReturnsTheStagesInOrder()
        {
            BackupManifest m = new BackupManifest { Pipeline = new List<string> { "zstd(level=3)", "aes(iterations=1000;passwordEnv=KEY)" } };

            Assert.Equal(new[] { "zstd(level=3)", "aes(iterations=1000;passwordEnv=KEY)" }, m.PipelineSpecsForRestore().ToArray());
        }

        [Fact]
        public void PipelineSpecsForRestore_RefusesAStageWhoseSecretWasNotKept()
        {
            BackupManifest m = new BackupManifest { Pipeline = new List<string> { "zstd", "aes(iterations=1000;password=***)" } };

            ArgumentException e = Assert.Throws<ArgumentException>(() => m.PipelineSpecsForRestore());

            Assert.Contains("aes", e.Message);
            Assert.Contains("password", e.Message);
        }

        [Fact]
        public void PipelineSpecsForRestore_AnEmptyPipelineIsAPlainBackup()
        {
            Assert.Empty(new BackupManifest { Pipeline = new List<string>() }.PipelineSpecsForRestore());
        }

        // ---- every stage's backup-time options are accepted when reading ----

        [Theory]
        [InlineData("gzip", "gzip(level=5)")]
        [InlineData("bzip2", "bzip2(level=5)")]
        [InlineData("zip64", "zip64(level=5;filename=backup)")]
        [InlineData("zstd", "zstd(level=5)")]
        [InlineData("rate", "rate(ratemb=500)")]
        [InlineData("aes", "aes(iterations=1000;passwordEnv=SQS_FROM_MANIFEST_KEY)")]
        public void AStageDescribedAtBackupTime_CanReadTheBackupItMade(string name, string spec)
        {
            Environment.SetEnvironmentVariable("SQS_FROM_MANIFEST_KEY", "k3y");
            try
            {
                ConfigPair stage = ConfigUtil.ParseComponentConfig(Components(), spec);
                IBackupTransformer t = (IBackupTransformer)Activator.CreateInstance(stage.TransformationType);
                byte[] raw = new byte[40000];
                new Random(1).NextBytes(raw);
                byte[] stored;
                using (MemoryStream ms = new MemoryStream())
                {
                    using (Stream w = t.GetBackupWriter(stage.Parameters, ms))
                    {
                        w.Write(raw, 0, raw.Length);
                    }
                    stored = ms.ToArray();
                }

                // the manifest records the spec; restoring uses it unchanged
                ConfigPair again = ConfigUtil.ParseComponentConfig(Components(), BackupManifest.DescribeStage(name, stage.Parameters));
                using (Stream r = t.GetRestoreReader(again.Parameters, new MemoryStream(stored)))
                using (MemoryStream back = new MemoryStream())
                {
                    r.CopyTo(back);
                    Assert.Equal(raw, back.ToArray());
                }
            }
            finally
            {
                Environment.SetEnvironmentVariable("SQS_FROM_MANIFEST_KEY", null);
            }
        }

        // ---- finding the manifest ----

        [Fact]
        public void ReadManifest_ReturnsNullWhenThereIsNone_AndTheManifestWhenThereIs()
        {
            string path = Path.Combine(mDir, "x.bak");
            var cfg = new ConfigPair { TransformationType = typeof(LocalStorage), Parameters = new Dictionary<string, List<string>>(StringComparer.InvariantCultureIgnoreCase) };
            cfg.Parameters["path"] = new List<string> { path };
            Assert.Null(BackupPipeSystem.ReadManifest(cfg));

            File.WriteAllText(path + ".manifest.json", new BackupManifest { Database = "appdb", BackupType = "full" }.ToJson());

            Assert.Equal("appdb", BackupPipeSystem.ReadManifest(cfg).Database);
        }
    }
}
