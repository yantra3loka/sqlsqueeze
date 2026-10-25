// Retrying write stream for storage backends. No SQL Server needed.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using SqlSqueeze.Plugins.Storage;
using Xunit;

namespace SqlSqueeze.Tests
{
    public class WriteRetryTests
    {
        private sealed class FaultStream : MemoryStream
        {
            private int _failuresLeft;
            public int Attempts { get; private set; }

            public FaultStream(int failuresBeforeSuccess)
            {
                _failuresLeft = failuresBeforeSuccess;
            }

            public override void Write(byte[] buffer, int offset, int count)
            {
                Attempts++;
                if (_failuresLeft > 0)
                {
                    _failuresLeft--;
                    throw new IOException("simulated transient write failure");
                }
                base.Write(buffer, offset, count);
            }
        }

        [Fact]
        public void Write_RetriesTransientFailuresThenSucceeds()
        {
            FaultStream inner = new FaultStream(2);
            WriteRetryStream retry = new WriteRetryStream(inner, 5, 1);
            byte[] data = new byte[] { 1, 2, 3, 4 };
            retry.Write(data, 0, data.Length);
            Assert.Equal(3, inner.Attempts);
            Assert.Equal(data, inner.ToArray());
        }

        [Fact]
        public void Write_ThrowsAfterMaxAttempts()
        {
            FaultStream inner = new FaultStream(10);
            WriteRetryStream retry = new WriteRetryStream(inner, 3, 1);
            Assert.Throws<IOException>(() => retry.Write(new byte[] { 9 }, 0, 1));
            Assert.Equal(3, inner.Attempts);
        }

        [Fact]
        public void Write_DoesNotRetryNonIoExceptions()
        {
            FaultStream inner = new FaultStream(0);
            inner.Dispose();
            WriteRetryStream retry = new WriteRetryStream(inner, 3, 1);
            Assert.Throws<ObjectDisposedException>(() => retry.Write(new byte[] { 9 }, 0, 1));
        }

        [Fact]
        public void Backoff_SleepsBetweenAttempts()
        {
            FaultStream inner = new FaultStream(2);
            WriteRetryStream retry = new WriteRetryStream(inner, 3, 50);
            Stopwatch elapsed = Stopwatch.StartNew();
            retry.Write(new byte[] { 7 }, 0, 1);
            elapsed.Stop();
            Assert.True(elapsed.ElapsedMilliseconds >= 100,
                "expected at least 50ms + 100ms backoff, saw " + elapsed.ElapsedMilliseconds + "ms");
        }

        [Fact]
        public void Ctor_RejectsBadArguments()
        {
            Assert.Throws<ArgumentNullException>(() => new WriteRetryStream(null, 3, 10));
            Assert.Throws<ArgumentOutOfRangeException>(() => new WriteRetryStream(new MemoryStream(), 0, 10));
            Assert.Throws<ArgumentOutOfRangeException>(() => new WriteRetryStream(new MemoryStream(), 3, -1));
        }

        [Fact]
        public void Flush_RetriesLikeWrite()
        {
            FlakyFlushStream inner = new FlakyFlushStream();
            WriteRetryStream retry = new WriteRetryStream(inner, 3, 1);
            retry.Flush();
            Assert.Equal(2, inner.FlushAttempts);
        }

        private sealed class FlakyFlushStream : MemoryStream
        {
            public int FlushAttempts { get; private set; }

            public override void Flush()
            {
                FlushAttempts++;
                if (FlushAttempts < 2)
                {
                    throw new IOException("simulated transient flush failure");
                }
                base.Flush();
            }
        }
    }
}
