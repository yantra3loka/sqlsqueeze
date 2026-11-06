// A backup that fails part-way must not leave a truncated object in cloud
// storage that looks like a backup. Writers can be told to abort, and then
// discard what they hold instead of completing the upload on dispose.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using SqlSqueeze.Plugins;
using SqlSqueeze.Plugins.Storage;
using Xunit;

namespace SqlSqueeze.Tests
{
    public class UploadAbortTests
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

        private static readonly string[] S3Cfg = { "endpoint=http://127.0.0.1:9000", "bucket=bkt", "region=us-east-1", "accesskey=AK", "secretkey=SK", "key=db/app.bak", "partsizeMB=5" };
        private static readonly string[] AzureCfg = { "endpoint=http://127.0.0.1:10000/devstoreaccount1", "container=cont", "account=devstoreaccount1",
            "accountkey=Eby8vdM02xNOcqFlqUwJPLlmEtlCDXJ1OUzFT50uSRZ6IFsuFq2UVErCz4I6tq/K1SZFPTOtr/KBHBeksoGMGw==", "blob=db/app.bak", "blocksizeMB=1" };

        private static void WriteMegabytes(Stream stream, int megabytes)
        {
            byte[] chunk = new byte[1024 * 1024];
            new Random(1).NextBytes(chunk);
            for (int i = 0; i < megabytes; i++)
            {
                stream.Write(chunk, 0, chunk.Length);
            }
        }

        // ---- S3 ----

        [Fact]
        public void S3_NormalDispose_CompletesTheUpload()
        {
            FakeS3 fake = new FakeS3();
            Stream writer = new S3Storage(fake).GetBackupWriter(Config(S3Cfg))[0];

            WriteMegabytes(writer, 11);
            writer.Dispose();

            Assert.True(fake.Objects.ContainsKey("/bkt/db/app.bak"));
            Assert.Equal(11 * 1024 * 1024, fake.Objects["/bkt/db/app.bak"].Length);
        }

        [Fact]
        public void S3_Abort_ThenDispose_LeavesNoObject_AndAbortsTheMultipartUpload()
        {
            FakeS3 fake = new FakeS3();
            Stream writer = new S3Storage(fake).GetBackupWriter(Config(S3Cfg))[0];

            WriteMegabytes(writer, 11);          // two 5 MB parts are already uploaded
            ((IAbortableStream)writer).Abort();
            writer.Dispose();

            Assert.False(fake.Objects.ContainsKey("/bkt/db/app.bak"), "a truncated object must not be left behind");
            Assert.Empty(fake.Completed);
            Assert.Equal(new[] { "/bkt/db/app.bak" }, fake.AbortedUploads.ToArray());
        }

        [Fact]
        public void S3_AWriteThatFails_MeansTheUploadIsNeverCompleted()
        {
            FakeS3 fake = new FakeS3 { FailPartNumber = 2 };
            Stream writer = new S3Storage(fake).GetBackupWriter(Config(S3Cfg))[0];

            Assert.ThrowsAny<Exception>(() => WriteMegabytes(writer, 11));
            writer.Dispose();                    // what the pipeline does on its way out

            Assert.False(fake.Objects.ContainsKey("/bkt/db/app.bak"));
            Assert.Empty(fake.Completed);
        }

        [Fact]
        public void S3_Abort_WhenNothingWasUploadedYet_NeedsNoServerCall()
        {
            FakeS3 fake = new FakeS3();
            Stream writer = new S3Storage(fake).GetBackupWriter(Config(S3Cfg))[0];

            WriteMegabytes(writer, 1);           // still buffered, no upload started
            ((IAbortableStream)writer).Abort();
            writer.Dispose();

            Assert.Empty(fake.AbortedUploads);
            Assert.False(fake.Objects.ContainsKey("/bkt/db/app.bak"));
        }

        [Fact]
        public void S3_WritesAfterAnAbort_AreDiscarded_NotUploaded()
        {
            // the pipeline flushes its remaining bytes into the stream while it unwinds
            FakeS3 fake = new FakeS3();
            Stream writer = new S3Storage(fake).GetBackupWriter(Config(S3Cfg))[0];
            WriteMegabytes(writer, 6);           // one part uploaded
            ((IAbortableStream)writer).Abort();
            int uploadsAfterAbort = fake.Completed.Count + fake.AbortedUploads.Count;

            WriteMegabytes(writer, 12);          // would be two more parts if they were kept
            writer.Dispose();

            Assert.Single(fake.AbortedUploads);
            Assert.Empty(fake.Completed);
            Assert.False(fake.Objects.ContainsKey("/bkt/db/app.bak"));
            Assert.Equal(1, uploadsAfterAbort);
        }

        // ---- Azure ----

        [Fact]
        public void Azure_NormalDispose_CommitsTheBlob()
        {
            FakeAzure fake = new FakeAzure();
            Stream writer = new AzureStorage(fake).GetBackupWriter(Config(AzureCfg))[0];

            WriteMegabytes(writer, 3);
            writer.Dispose();

            Assert.True(fake.Objects.ContainsKey("/devstoreaccount1/cont/db/app.bak"));
            Assert.Equal(3 * 1024 * 1024, fake.Objects["/devstoreaccount1/cont/db/app.bak"].Length);
        }

        [Fact]
        public void Azure_Abort_ThenDispose_CommitsNothing()
        {
            FakeAzure fake = new FakeAzure();
            Stream writer = new AzureStorage(fake).GetBackupWriter(Config(AzureCfg))[0];

            WriteMegabytes(writer, 3);           // blocks are staged but not committed
            ((IAbortableStream)writer).Abort();
            writer.Dispose();

            Assert.False(fake.Objects.ContainsKey("/devstoreaccount1/cont/db/app.bak"), "a truncated blob must not be left behind");
        }

        [Fact]
        public void Azure_WritesAfterAnAbort_AreDiscarded()
        {
            FakeAzure fake = new FakeAzure();
            Stream writer = new AzureStorage(fake).GetBackupWriter(Config(AzureCfg))[0];
            WriteMegabytes(writer, 2);
            ((IAbortableStream)writer).Abort();
            int blocksBefore = fake.StagedBlocks;

            WriteMegabytes(writer, 3);
            writer.Dispose();

            Assert.Equal(blocksBefore, fake.StagedBlocks);   // nothing more was staged
            Assert.False(fake.Objects.ContainsKey("/devstoreaccount1/cont/db/app.bak"));
        }

        [Fact]
        public void Azure_AWriteThatFails_MeansTheBlobIsNeverCommitted()
        {
            FakeAzure fake = new FakeAzure { FailBlockNumber = 2 };
            Stream writer = new AzureStorage(fake).GetBackupWriter(Config(AzureCfg))[0];

            Assert.ThrowsAny<Exception>(() => WriteMegabytes(writer, 3));
            writer.Dispose();

            Assert.False(fake.Objects.ContainsKey("/devstoreaccount1/cont/db/app.bak"));
        }

        // ---- the wrappers and the helper ----

        private class Abortable : MemoryStream, IAbortableStream
        {
            public bool Aborted;
            public Exception AbortThrows;
            public void Abort() { Aborted = true; if (AbortThrows != null) { throw AbortThrows; } }
        }

        [Fact]
        public void WriteRetryStream_ForwardsAbortToTheStreamItWraps()
        {
            Abortable inner = new Abortable();
            Stream wrapped = new WriteRetryStream(inner, 3, 1);

            ((IAbortableStream)wrapped).Abort();

            Assert.True(inner.Aborted);
        }

        [Fact]
        public void AbortAll_AbortsTheAbortableOnes_IgnoresTheRest_AndSurvivesAFailingAbort()
        {
            Abortable a = new Abortable { AbortThrows = new IOException("cannot reach the server") };
            Abortable b = new Abortable();
            Stream plain = new MemoryStream();

            StreamAbort.AbortAll(new Stream[] { a, plain, b });

            Assert.True(a.Aborted);
            Assert.True(b.Aborted);       // one failing abort does not stop the next
        }
    }
}
