/*
    This file is part of SqlSqueeze.

    SqlSqueeze is free software: you can redistribute it and/or modify
    it under the terms of the GNU General Public License as published by
    the Free Software Foundation, either version 3 of the License, or
    (at your option) any later version.

    SqlSqueeze is distributed in the hope that it will be useful,
    but WITHOUT ANY WARRANTY; without even the implied warranty of
    MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
    GNU General Public License for more details.

    You should have received a copy of the GNU General Public License
    along with SqlSqueeze.  If not, see <http://www.gnu.org/licenses/>.
*/

using System;
using System.IO;
using System.Threading;

namespace SqlSqueeze.Plugins.Storage
{
    /// <summary>
    /// A write-only decorator that retries transient <see cref="IOException"/>
    /// failures with exponential backoff. Redgate retries through dropped
    /// shares; storage backends wrap their writers in this so a failed
    /// write fails the job only after maxAttempts honest tries.
    /// Reads are passed through without retry.
    /// </summary>
    public class WriteRetryStream : Stream
    {
        private readonly Stream mInner;
        private readonly int mMaxAttempts;
        private readonly int mBaseDelayMs;

        public WriteRetryStream(Stream inner, int maxAttempts, int baseDelayMs)
        {
            if (inner == null)
            {
                throw new ArgumentNullException("inner");
            }
            if (maxAttempts < 1)
            {
                throw new ArgumentOutOfRangeException("maxAttempts", "At least one attempt is required.");
            }
            if (baseDelayMs < 0)
            {
                throw new ArgumentOutOfRangeException("baseDelayMs", "Backoff delay cannot be negative.");
            }
            mInner = inner;
            mMaxAttempts = maxAttempts;
            mBaseDelayMs = baseDelayMs;
        }

        public override bool CanRead { get { return mInner.CanRead; } }
        public override bool CanSeek { get { return mInner.CanSeek; } }
        public override bool CanWrite { get { return mInner.CanWrite; } }
        public override long Length { get { return mInner.Length; } }

        public override long Position
        {
            get { return mInner.Position; }
            set { mInner.Position = value; }
        }

        public override void Flush()
        {
            Retry(delegate { mInner.Flush(); });
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            Retry(delegate { mInner.Write(buffer, offset, count); });
        }

        public override void WriteByte(byte value)
        {
            Retry(delegate { mInner.WriteByte(value); });
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            return mInner.Read(buffer, offset, count);
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            return mInner.Seek(offset, origin);
        }

        public override void SetLength(long value)
        {
            mInner.SetLength(value);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                mInner.Dispose();
            }
            base.Dispose(disposing);
        }

        private delegate void WriteAction();

        private void Retry(WriteAction action)
        {
            int attempt = 0;
            while (true)
            {
                attempt++;
                try
                {
                    action();
                    return;
                }
                catch (IOException)
                {
                    if (attempt >= mMaxAttempts)
                    {
                        throw;
                    }
                    Thread.Sleep(mBaseDelayMs * (1 << (attempt - 1)));
                }
            }
        }
    }
}
