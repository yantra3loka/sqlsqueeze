// copy: take a finished backup (and its manifest) from one storage to another,
// retrying through outages until a deadline, and checking what was copied
// against the manifest. Storages are in-memory fakes; time is a fake clock.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using SqlSqueeze.Core;
using SqlSqueeze.Plugins;
using Xunit;

namespace SqlSqueeze.Tests
{
    public class BackupCopierTests
    {
        // ---- fakes ----

        private class MemoryStorage : IBackupStorage, IManifestStorage, IResumableStorage
        {
            public int ResumableWriterCalls;
            public int Suspends;
            public int Aborts;
            public long SkippedPerWriter;       // what each resumable writer reports it did not need to send
            public List<string> Names = new List<string>();
            public List<byte[]> Contents = new List<byte[]>();
            public string Manifest;
            public int WriterCalls;
            public int CleanupCalls;
            public int FailWrites;                    // fail this many commits with a transient error
            public Exception FailWith = new IOException("connection reset");
            public Exception FailManifestWith;

            public string Name { get { return "memory"; } }
            public string CommandLineHelp { get { return ""; } }
            public int GetNumberOfDevices(Dictionary<string, List<string>> config) { return Names.Count > 0 ? Names.Count : config["path"].Count; }

            public Stream[] GetResumableBackupWriter(Dictionary<string, List<string>> config)
            {
                ResumableWriterCalls++;
                return GetBackupWriter(config);
            }

            public Stream[] GetBackupWriter(Dictionary<string, List<string>> config)
            {
                WriterCalls++;
                List<Stream> writers = new List<Stream>();
                for (int i = 0; i < config["path"].Count; i++)
                {
                    int index = i;
                    writers.Add(new CommitStream(() => Suspends++, () => Aborts++, SkippedPerWriter, bytes =>
                    {
                        if (FailWrites > 0)
                        {
                            FailWrites--;
                            throw FailWith;
                        }
                        while (Contents.Count <= index) { Contents.Add(null); Names.Add(null); }
                        Contents[index] = bytes;
                        Names[index] = config["path"][index];
                    }));
                }
                return writers.ToArray();
            }

            public Stream[] GetRestoreReader(Dictionary<string, List<string>> config, out long estimatedTotalBytes)
            {
                estimatedTotalBytes = Contents.Sum(c => (long)c.Length);
                return Contents.Select(c => (Stream)new MemoryStream(c, false)).ToArray();
            }

            public void CleanupOnAbort() { CleanupCalls++; }

            public IList<string> GetFileNames(Dictionary<string, List<string>> config)
            {
                return config["path"].Select(p => Path.GetFileName(p)).ToList();
            }

            public Dictionary<string, List<string>> ResolveConfig(Dictionary<string, List<string>> config) { return config; }

            public void WriteManifest(Dictionary<string, List<string>> config, string json)
            {
                if (FailManifestWith != null) { throw FailManifestWith; }
                Manifest = json;
            }

            public string ReadManifest(Dictionary<string, List<string>> config) { return Manifest; }
        }

        private class CommitStream : MemoryStream, IResumableStream
        {
            private readonly Action mOnSuspend;
            private readonly Action mOnAbort;
            private readonly Action<byte[]> mCommit;
            private bool mCommitted;
            private bool mDiscard;
            public CommitStream(Action onSuspend, Action onAbort, long skipped, Action<byte[]> commit) { mOnSuspend = onSuspend; mOnAbort = onAbort; BytesSkipped = skipped; mCommit = commit; }
            public long BytesSkipped { get; private set; }
            public void Suspend() { mDiscard = true; mOnSuspend(); }
            public void Abort() { mDiscard = true; mOnAbort(); }
            protected override void Dispose(bool disposing)
            {
                if (disposing && !mCommitted)
                {
                    mCommitted = true;
                    if (!mDiscard)
                    {
                        mCommit(ToArray());
                    }
                }
                base.Dispose(disposing);
            }
        }

        private class Clock
        {
            public DateTime Now = new DateTime(2026, 10, 26, 0, 0, 0, DateTimeKind.Utc);
            public List<TimeSpan> Sleeps = new List<TimeSpan>();
            public void Sleep(TimeSpan span) { Sleeps.Add(span); Now = Now.Add(span); }
        }

        private static CopyOptions Options(Clock clock, double maxHours = 24, int baseSeconds = 5, int capSeconds = 300)
        {
            return new CopyOptions
            {
                MaxDuration = TimeSpan.FromHours(maxHours),
                BackoffBase = TimeSpan.FromSeconds(baseSeconds),
                BackoffMax = TimeSpan.FromSeconds(capSeconds),
                Sleep = clock.Sleep,
                UtcNow = () => clock.Now,
            };
        }

        private static string Sha(byte[] b) { return Convert.ToHexString(SHA256.HashData(b)).ToLowerInvariant(); }

        private static byte[] Data(int size, int seed)
        {
            byte[] d = new byte[size];
            new Random(seed).NextBytes(d);
            return d;
        }

        private static Dictionary<string, List<string>> Paths(params string[] paths)
        {
            return new Dictionary<string, List<string>>(StringComparer.InvariantCultureIgnoreCase) { { "path", paths.ToList() } };
        }

        private static MemoryStorage Source(params byte[][] files)
        {
            MemoryStorage s = new MemoryStorage();
            for (int i = 0; i < files.Length; i++)
            {
                s.Names.Add("/src/f" + i + ".bak");
                s.Contents.Add(files[i]);
            }
            s.Manifest = new BackupManifest
            {
                Database = "app", BackupType = "full",
                Files = files.Select((f, i) => new ManifestFile { Name = "f" + i + ".bak", StoredBytes = f.Length, StoredSha256 = Sha(f) }).ToList(),
            }.ToJson();
            return s;
        }

        // ---- copying ----

        [Fact]
        public void Copy_CopiesEveryStripeAndTheManifest()
        {
            byte[][] files = { Data(5000, 1), Data(3000, 2) };
            MemoryStorage source = Source(files);
            MemoryStorage dest = new MemoryStorage();

            CopyResult r = BackupCopier.Copy(source, Paths("a", "b"), dest, Paths("/dst/x.bak", "/dst/y.bak"), Options(new Clock()));

            Assert.True(r.Ok, string.Join("; ", r.Problems));
            Assert.Equal(1, r.Attempts);
            Assert.Equal(8000, r.BytesCopied);
            Assert.Equal(files[0], dest.Contents[0]);
            Assert.Equal(files[1], dest.Contents[1]);
            Assert.NotNull(dest.Manifest);
        }

        [Fact]
        public void Copy_TheCopiedManifestNamesTheDestinationFiles()
        {
            MemoryStorage source = Source(Data(100, 3));
            MemoryStorage dest = new MemoryStorage();

            BackupCopier.Copy(source, Paths("a"), dest, Paths("/dst/renamed.bak"), Options(new Clock()));

            BackupManifest copied = BackupManifest.FromJson(dest.Manifest);
            Assert.Equal("renamed.bak", copied.Files[0].Name);
            Assert.Equal(Sha(Data(100, 3)), copied.Files[0].StoredSha256);
            Assert.Equal("app", copied.Database);
        }

        [Fact]
        public void Copy_WithoutAManifest_StillCopies_AndWarns()
        {
            MemoryStorage source = Source(Data(100, 4));
            source.Manifest = null;
            MemoryStorage dest = new MemoryStorage();

            CopyResult r = BackupCopier.Copy(source, Paths("a"), dest, Paths("/dst/x.bak"), Options(new Clock()));

            Assert.True(r.Ok);
            Assert.Null(dest.Manifest);
            Assert.Contains(r.Warnings, w => w.Contains("manifest"));
        }

        // ---- retrying ----

        [Fact]
        public void Copy_RetriesThroughATransientOutage_WithGrowingBackoff()
        {
            MemoryStorage source = Source(Data(2000, 5));
            MemoryStorage dest = new MemoryStorage { FailWrites = 3 };
            Clock clock = new Clock();

            CopyResult r = BackupCopier.Copy(source, Paths("a"), dest, Paths("/dst/x.bak"), Options(clock, baseSeconds: 5, capSeconds: 300));

            Assert.True(r.Ok, string.Join("; ", r.Problems));
            Assert.Equal(4, r.Attempts);
            Assert.Equal(new[] { 5.0, 10.0, 20.0 }, clock.Sleeps.Select(s => s.TotalSeconds).ToArray());
            Assert.Equal(3, dest.Suspends);       // each failed attempt keeps its partial upload for the next
            Assert.Equal(0, dest.Aborts);
            Assert.Equal(0, dest.CleanupCalls);   // and nothing is cleaned up until the copy finally fails
            Assert.Equal(Data(2000, 5), dest.Contents[0]);
        }

        [Fact]
        public void Copy_BackoffIsCapped()
        {
            MemoryStorage dest = new MemoryStorage { FailWrites = 6 };
            Clock clock = new Clock();

            BackupCopier.Copy(Source(Data(10, 6)), Paths("a"), dest, Paths("/dst/x.bak"), Options(clock, baseSeconds: 5, capSeconds: 30));

            Assert.Equal(new[] { 5.0, 10.0, 20.0, 30.0, 30.0, 30.0 }, clock.Sleeps.Select(s => s.TotalSeconds).ToArray());
        }

        [Fact]
        public void Copy_GivesUpAtTheDeadline_AndSaysWhy()
        {
            MemoryStorage dest = new MemoryStorage { FailWrites = int.MaxValue, FailWith = new IOException("network unreachable") };
            Clock clock = new Clock();

            CopyResult r = BackupCopier.Copy(Source(Data(10, 7)), Paths("a"), dest, Paths("/dst/x.bak"), Options(clock, maxHours: 1, baseSeconds: 60, capSeconds: 600));

            Assert.False(r.Ok);
            Assert.True(r.Attempts > 3);
            Assert.True(clock.Now - new DateTime(2026, 10, 26, 0, 0, 0, DateTimeKind.Utc) <= TimeSpan.FromHours(1) + TimeSpan.FromSeconds(600));
            Assert.Contains(r.Problems, p => p.Contains("network unreachable") && p.Contains("gave up"));
            Assert.Equal(1, dest.CleanupCalls);   // giving up is when the leftover partial upload is removed
        }

        [Fact]
        public void Copy_UsesTheResumableWriter_WhenTheDestinationOffersOne_OnEveryAttempt()
        {
            MemoryStorage dest = new MemoryStorage { FailWrites = 2 };

            CopyResult r = BackupCopier.Copy(Source(Data(100, 20)), Paths("a"), dest, Paths("/dst/x.bak"), Options(new Clock()));

            Assert.True(r.Ok);
            Assert.Equal(3, dest.ResumableWriterCalls);
            Assert.Equal(0, dest.WriterCalls - dest.ResumableWriterCalls);   // the plain writer was never the one asked for
        }

        [Fact]
        public void Copy_ReportsHowMuchOfTheUploadWasAlreadyAtTheDestination()
        {
            MemoryStorage dest = new MemoryStorage { SkippedPerWriter = 4000 };

            CopyResult r = BackupCopier.Copy(Source(Data(5000, 22), Data(3000, 23)), Paths("a", "b"), dest, Paths("/dst/x.bak", "/dst/y.bak"), Options(new Clock()));

            Assert.True(r.Ok);
            Assert.Equal(8000, r.BytesCopied);
            Assert.Equal(8000, r.BytesSkipped);    // 4000 for each of the two files
        }

        [Fact]
        public void Copy_ANonTransientFailure_AbortsTheWriters_DoesNotKeepThem()
        {
            MemoryStorage dest = new MemoryStorage { FailWrites = 1, FailWith = new InvalidOperationException("403 forbidden") };

            CopyResult r = BackupCopier.Copy(Source(Data(100, 21)), Paths("a"), dest, Paths("/dst/x.bak"), Options(new Clock()));

            Assert.False(r.Ok);
            Assert.Equal(0, dest.Suspends);
            Assert.True(dest.Aborts >= 1);
            Assert.Equal(1, dest.CleanupCalls);
        }

        [Theory]
        [InlineData(typeof(IOException))]
        [InlineData(typeof(HttpRequestException))]
        [InlineData(typeof(TimeoutException))]
        public void Copy_TransientErrorsAreRetried(Type error)
        {
            MemoryStorage dest = new MemoryStorage { FailWrites = 1, FailWith = (Exception)Activator.CreateInstance(error, "boom") };

            CopyResult r = BackupCopier.Copy(Source(Data(10, 8)), Paths("a"), dest, Paths("/dst/x.bak"), Options(new Clock()));

            Assert.True(r.Ok);
            Assert.Equal(2, r.Attempts);
        }

        [Theory]
        [InlineData(typeof(InvalidOperationException))]   // a 4xx from S3/Azure: bad credentials, no such bucket
        [InlineData(typeof(ArgumentException))]
        [InlineData(typeof(UnauthorizedAccessException))]
        [InlineData(typeof(FileNotFoundException))]
        [InlineData(typeof(DirectoryNotFoundException))]
        public void Copy_ErrorsThatRetryingCannotFix_FailAtOnce(Type error)
        {
            MemoryStorage dest = new MemoryStorage { FailWrites = 1, FailWith = (Exception)Activator.CreateInstance(error, "no") };
            Clock clock = new Clock();

            CopyResult r = BackupCopier.Copy(Source(Data(10, 9)), Paths("a"), dest, Paths("/dst/x.bak"), Options(clock));

            Assert.False(r.Ok);
            Assert.Equal(1, r.Attempts);
            Assert.Empty(clock.Sleeps);
            Assert.Equal(1, dest.CleanupCalls);
        }

        [Fact]
        public void Copy_ASourceThatDoesNotMatchItsManifest_IsNotCopiedAsIfItWereGood()
        {
            byte[] real = Data(4000, 10);
            MemoryStorage source = Source(real);
            source.Contents[0] = Data(4000, 11);   // damaged since the manifest was written
            MemoryStorage dest = new MemoryStorage();
            Clock clock = new Clock();

            CopyResult r = BackupCopier.Copy(source, Paths("a"), dest, Paths("/dst/x.bak"), Options(clock));

            Assert.False(r.Ok);
            Assert.Equal(1, r.Attempts);          // damage is not a transient fault
            Assert.Empty(clock.Sleeps);
            Assert.Contains(r.Problems, p => p.Contains("sha256"));
            Assert.Null(dest.Manifest);
            Assert.Equal(1, dest.CleanupCalls);
        }

        [Fact]
        public void Copy_AManifestThatFailsToWrite_IsRetriedLikeAnythingElse()
        {
            MemoryStorage dest = new MemoryStorage { FailManifestWith = new IOException("timeout writing manifest") };
            Clock clock = new Clock();
            int tries = 0;
            CopyOptions options = Options(clock);
            options.OnAttemptFailed = (attempt, e) => { tries++; if (tries == 2) { dest.FailManifestWith = null; } };

            CopyResult r = BackupCopier.Copy(Source(Data(100, 12)), Paths("a"), dest, Paths("/dst/x.bak"), options);

            Assert.True(r.Ok, string.Join("; ", r.Problems));
            Assert.Equal(3, r.Attempts);
            Assert.NotNull(dest.Manifest);
        }

        [Fact]
        public void Copy_ADifferentNumberOfFiles_IsRefusedBeforeCopyingAnything()
        {
            MemoryStorage source = Source(Data(10, 13), Data(10, 14));
            MemoryStorage dest = new MemoryStorage();

            ArgumentException e = Assert.Throws<ArgumentException>(() => BackupCopier.Copy(source, Paths("a", "b"), dest, Paths("/dst/only.bak"), Options(new Clock())));

            Assert.Contains("2", e.Message);
            Assert.Contains("1", e.Message);
            Assert.Equal(0, dest.WriterCalls);
        }
    }
}
