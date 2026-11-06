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
using System.Security.Cryptography;

namespace SqlSqueeze.Core
{
    /// <summary>
    /// A pass-through stream that computes the SHA-256 and byte count of
    /// everything read or written through it. The digest stays available
    /// after Dispose, because the manifest is built once the pipeline has
    /// been closed.
    /// </summary>
    public class HashingStream : Stream
    {
        private readonly Stream mInner;
        private readonly bool mLeaveOpen;
        private IncrementalHash mHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        private long mLength;
        private string mFinalDigest;

        public HashingStream(Stream inner, bool leaveOpen)
        {
            mInner = inner;
            mLeaveOpen = leaveOpen;
        }

        public override bool CanRead { get { return mInner.CanRead; } }

        public override bool CanSeek { get { return false; } }

        public override bool CanWrite { get { return mInner.CanWrite; } }

        /// <summary>Bytes that have passed through so far.</summary>
        public override long Length { get { return mLength; } }

        public override long Position
        {
            get { return mLength; }
            set { throw new NotSupportedException(); }
        }

        public string HexDigest()
        {
            if (mFinalDigest != null)
            {
                return mFinalDigest;
            }
            return Convert.ToHexString(mHash.GetCurrentHash()).ToLowerInvariant();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            int read = mInner.Read(buffer, offset, count);
            if (read > 0)
            {
                mHash.AppendData(buffer, offset, read);
                mLength += read;
            }
            return read;
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            mInner.Write(buffer, offset, count);
            mHash.AppendData(buffer, offset, count);
            mLength += count;
        }

        public override void Flush()
        {
            mInner.Flush();
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            throw new NotSupportedException();
        }

        public override void SetLength(long value)
        {
            throw new NotSupportedException();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                if (mFinalDigest == null)
                {
                    mFinalDigest = HexDigest();
                    mHash.Dispose();
                }
                if (!mLeaveOpen)
                {
                    mInner.Dispose();
                }
            }
            base.Dispose(disposing);
        }
    }
}
