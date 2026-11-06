// Resuming a copy to S3 or Azure: a transient failure suspends the upload instead of
// discarding it, and the next attempt skips the parts the server already has,
// but only when their content is identical.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using SqlSqueeze.Plugins;
using SqlSqueeze.Plugins.Storage;
using Xunit;

namespace SqlSqueeze.Tests
{
    public class ResumableUploadTests
    {
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

        private static readonly string[] S3Base = { "endpoint=http://127.0.0.1:9000", "bucket=bkt", "region=us-east-1", "accesskey=AK", "secretkey=SK", "partsizeMB=5" };
        private static readonly string[] AzureBase = { "endpoint=http://127.0.0.1:10000/devstoreaccount1", "container=cont", "account=devstoreaccount1",
            "accountkey=Eby8vdM02xNOcqFlqUwJPLlmEtlCDXJ1OUzFT50uSRZ6IFsuFq2UVErCz4I6tq/K1SZFPTOtr/KBHBeksoGMGw==", "blocksizeMB=1" };

        private static Dictionary<string, List<string>> S3(string key) { return Config(S3Base.Concat(new[] { "key=" + key }).ToArray()); }
        private static Dictionary<string, List<string>> Azure(string blob) { return Config(AzureBase.Concat(new[] { "blob=" + blob }).ToArray()); }

        private static byte[] Data(int bytes, int seed)
        {
            byte[] d = new byte[bytes];
            new Random(seed).NextBytes(d);
            return d;
        }

        private const int MB = 1024 * 1024;

        private static void Send(Stream stream, byte[] data)
        {
            // in odd-sized chunks, the way a real source delivers it
            for (int offset = 0; offset < data.Length; offset += 777777)
            {
                stream.Write(data, offset, Math.Min(777777, data.Length - offset));
            }
        }

        private static void SuspendAndDispose(Stream writer)
        {
            ((IResumableStream)writer).Suspend();
            writer.Dispose();
        }

        [Fact]
        public void WriteRetryStream_ForwardsSuspendToTheStreamItWraps_SoRetriesDoNotDestroyAResumableUpload()
        {
            Resumable inner = new Resumable { Skipped = 123 };
            Stream wrapped = new WriteRetryStream(inner, 3, 1);

            ((IResumableStream)wrapped).Suspend();

            Assert.True(inner.Suspended);
            Assert.False(inner.Aborted);
            Assert.Equal(123, ((IResumableStream)wrapped).BytesSkipped);
        }

        [Fact]
        public void WriteRetryStream_AroundAStreamThatCannotResume_SuspendFallsBackToAbort()
        {
            Abortable inner = new Abortable();
            Stream wrapped = new WriteRetryStream(inner, 3, 1);

            ((IResumableStream)wrapped).Suspend();

            Assert.True(inner.Aborted);
            Assert.Equal(0, ((IResumableStream)wrapped).BytesSkipped);
        }

        private class Resumable : MemoryStream, IResumableStream
        {
            public bool Suspended, Aborted;
            public long Skipped;
            public long BytesSkipped { get { return Skipped; } }
            public void Suspend() { Suspended = true; }
            public void Abort() { Aborted = true; }
        }

        private class Abortable : MemoryStream, IAbortableStream
        {
            public bool Aborted;
            public void Abort() { Aborted = true; }
        }

        // ---- S3 ----

        [Fact]
        public void S3_ASuspendedUpload_IsResumedByTheNextAttempt_SkippingThePartsTheServerAlreadyHas()
        {
            FakeS3 fake = new FakeS3();
            byte[] data = Data(11 * MB, 1);
            var cfg = S3("db/app.bak");

            Stream first = ((IResumableStorage)new S3Storage(fake)).GetResumableBackupWriter(cfg)[0];
            Send(first, data);
            SuspendAndDispose(first);

            Assert.False(fake.Objects.ContainsKey("/bkt/db/app.bak"), "a suspended upload must not appear as an object");
            Assert.Equal(1, fake.OpenUploads);
            Assert.Equal(new[] { 1, 2 }, fake.PartPuts.ToArray());

            Stream second = ((IResumableStorage)new S3Storage(fake)).GetResumableBackupWriter(cfg)[0];
            Send(second, data);
            Assert.Equal(10L * MB, ((IResumableStream)second).BytesSkipped);   // two 5 MB parts were already there
            second.Dispose();

            Assert.Equal(data, fake.Objects["/bkt/db/app.bak"]);
            Assert.Equal(1, fake.UploadsStarted);                       // the same upload was continued
            Assert.Equal(new[] { 1, 2, 3 }, fake.PartPuts.ToArray());   // parts 1 and 2 were not sent again
            Assert.Equal(0, fake.OpenUploads);
        }

        [Fact]
        public void S3_AResumedUpload_SendsAgainAPartWhoseContentChanged()
        {
            FakeS3 fake = new FakeS3();
            byte[] before = Data(11 * MB, 2);
            byte[] after = Data(11 * MB, 3);
            var cfg = S3("db/app.bak");
            Stream first = ((IResumableStorage)new S3Storage(fake)).GetResumableBackupWriter(cfg)[0];
            Send(first, before);
            SuspendAndDispose(first);

            Stream second = ((IResumableStorage)new S3Storage(fake)).GetResumableBackupWriter(cfg)[0];
            Send(second, after);
            second.Dispose();

            Assert.Equal(after, fake.Objects["/bkt/db/app.bak"]);       // never a mix of the two
            Assert.Equal(new[] { 1, 2, 1, 2, 3 }, fake.PartPuts.ToArray());
        }

        [Fact]
        public void S3_ResumingPicksTheUploadForItsOwnKey_NotAnotherOne()
        {
            FakeS3 fake = new FakeS3();
            Stream other = ((IResumableStorage)new S3Storage(fake)).GetResumableBackupWriter(S3("db/other.bak"))[0];
            Send(other, Data(6 * MB, 4));
            SuspendAndDispose(other);

            Stream mine = ((IResumableStorage)new S3Storage(fake)).GetResumableBackupWriter(S3("db/app.bak"))[0];
            Send(mine, Data(6 * MB, 4));    // even identical bytes: it is a different key
            mine.Dispose();

            Assert.Equal(2, fake.UploadsStarted);
            Assert.True(fake.Objects.ContainsKey("/bkt/db/app.bak"));
            Assert.False(fake.Objects.ContainsKey("/bkt/db/other.bak"));
            Assert.Equal(1, fake.OpenUploads);                          // the other key's upload is untouched
        }

        [Fact]
        public void S3_ANormalWriter_NeverContinuesAnUnfinishedUpload()
        {
            FakeS3 fake = new FakeS3();
            Stream left = ((IResumableStorage)new S3Storage(fake)).GetResumableBackupWriter(S3("db/app.bak"))[0];
            Send(left, Data(6 * MB, 5));
            SuspendAndDispose(left);

            Stream normal = new S3Storage(fake).GetBackupWriter(S3("db/app.bak"))[0];
            Send(normal, Data(6 * MB, 5));
            normal.Dispose();

            Assert.Equal(2, fake.UploadsStarted);
        }

        [Fact]
        public void S3_SuspendThenDispose_NeitherCompletesNorAborts()
        {
            FakeS3 fake = new FakeS3();
            Stream writer = ((IResumableStorage)new S3Storage(fake)).GetResumableBackupWriter(S3("db/app.bak"))[0];
            Send(writer, Data(7 * MB, 6));

            SuspendAndDispose(writer);

            Assert.Empty(fake.Completed);
            Assert.Empty(fake.AbortedUploads);
            Assert.Equal(1, fake.OpenUploads);
        }

        [Fact]
        public void S3_CleanupOnAbort_RemovesTheUploadAResumableWriterLeftBehind()
        {
            FakeS3 fake = new FakeS3();
            S3Storage storage = new S3Storage(fake);
            var cfg = S3("db/app.bak");
            Stream writer = ((IResumableStorage)storage).GetResumableBackupWriter(cfg)[0];
            Send(writer, Data(7 * MB, 7));
            SuspendAndDispose(writer);

            storage.CleanupOnAbort();

            Assert.Equal(0, fake.OpenUploads);
            Assert.Equal(new[] { "/bkt/db/app.bak" }, fake.AbortedUploads.ToArray());
        }

        [Fact]
        public void S3_CleanupOnAbort_DoesNothingWhenNothingWasLeft()
        {
            FakeS3 fake = new FakeS3();
            S3Storage storage = new S3Storage(fake);

            storage.CleanupOnAbort();

            Assert.Empty(fake.AbortedUploads);
        }

        // ---- Azure ----

        private static string[] BlockIds(FakeAzure fake) { return fake.BlockPuts.ToArray(); }

        [Fact]
        public void Azure_ASuspendedUpload_IsResumedByTheNextAttempt_SkippingBlocksAlreadyStaged()
        {
            FakeAzure fake = new FakeAzure();
            byte[] data = Data(3 * MB + MB / 2, 8);
            var cfg = Azure("db/app.bak");

            Stream first = ((IResumableStorage)new AzureStorage(fake)).GetResumableBackupWriter(cfg)[0];
            Send(first, data);
            SuspendAndDispose(first);

            Assert.False(fake.Objects.ContainsKey("/devstoreaccount1/cont/db/app.bak"));
            Assert.Equal(3, fake.BlockPuts.Count);                       // three full blocks went up; the partial last one did not

            Stream second = ((IResumableStorage)new AzureStorage(fake)).GetResumableBackupWriter(cfg)[0];
            Send(second, data);
            Assert.Equal(3L * MB, ((IResumableStream)second).BytesSkipped);   // three 1 MB blocks were already there
            second.Dispose();

            Assert.Equal(data, fake.Objects["/devstoreaccount1/cont/db/app.bak"]);
            Assert.Equal(4, fake.BlockPuts.Count);                       // only the last block was new
            Assert.Equal(4, BlockIds(fake).Distinct().Count());
        }

        [Fact]
        public void Azure_ABlockWhoseContentChanged_IsStagedAgain_NeverMixedIn()
        {
            FakeAzure fake = new FakeAzure();
            byte[] before = Data(3 * MB, 9);
            byte[] after = Data(3 * MB, 10);                             // same length: only the content tells them apart
            var cfg = Azure("db/app.bak");
            Stream first = ((IResumableStorage)new AzureStorage(fake)).GetResumableBackupWriter(cfg)[0];
            Send(first, before);
            SuspendAndDispose(first);

            Stream second = ((IResumableStorage)new AzureStorage(fake)).GetResumableBackupWriter(cfg)[0];
            Send(second, after);
            second.Dispose();

            Assert.Equal(after, fake.Objects["/devstoreaccount1/cont/db/app.bak"]);
            Assert.Equal(6, fake.BlockPuts.Count);                       // all three were sent again
        }

        [Fact]
        public void Azure_SuspendThenDispose_CommitsNothing()
        {
            FakeAzure fake = new FakeAzure();
            Stream writer = ((IResumableStorage)new AzureStorage(fake)).GetResumableBackupWriter(Azure("db/app.bak"))[0];
            Send(writer, Data(2 * MB, 11));

            SuspendAndDispose(writer);

            Assert.False(fake.Objects.ContainsKey("/devstoreaccount1/cont/db/app.bak"));
        }

        [Fact]
        public void Azure_IdenticalBlocksAtDifferentPositions_AreNotConfusedForEachOther()
        {
            // two blocks with the same content get different ids, because the id carries the position too
            FakeAzure fake = new FakeAzure();
            byte[] block = Data(MB, 12);
            byte[] data = block.Concat(block).ToArray();
            Stream writer = ((IResumableStorage)new AzureStorage(fake)).GetResumableBackupWriter(Azure("db/app.bak"))[0];

            Send(writer, data);
            writer.Dispose();

            Assert.Equal(data, fake.Objects["/devstoreaccount1/cont/db/app.bak"]);
            Assert.Equal(2, fake.BlockPuts.Distinct().Count());
        }
    }
}
