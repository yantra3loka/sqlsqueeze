// HashingStream passes bytes through unchanged while computing SHA-256 + length.

using System;
using System.IO;
using System.Security.Cryptography;
using SqlSqueeze.Core;
using Xunit;

namespace SqlSqueeze.Tests
{
    public class HashingStreamTests
    {
        private static byte[] Sample()
        {
            byte[] data = new byte[300000];
            new Random(7).NextBytes(data);
            return data;
        }

        private static string Hex(byte[] bytes)
        {
            return Convert.ToHexString(bytes).ToLowerInvariant();
        }

        [Fact]
        public void Write_PassesBytesThroughAndHashesThem()
        {
            byte[] data = Sample();
            MemoryStream sink = new MemoryStream();
            using (HashingStream hashing = new HashingStream(sink, leaveOpen: true))
            {
                // odd chunk sizes: the hash must not depend on how writes are split
                hashing.Write(data, 0, 1);
                hashing.Write(data, 1, 99999);
                hashing.Write(data, 100000, data.Length - 100000);
                hashing.Flush();

                Assert.Equal(data.Length, hashing.Length);
                Assert.Equal(Hex(SHA256.HashData(data)), hashing.HexDigest());
            }
            Assert.Equal(data, sink.ToArray());
        }

        [Fact]
        public void Read_PassesBytesThroughAndHashesThem()
        {
            byte[] data = Sample();
            using (HashingStream hashing = new HashingStream(new MemoryStream(data), leaveOpen: false))
            using (MemoryStream copy = new MemoryStream())
            {
                hashing.CopyTo(copy);

                Assert.Equal(data, copy.ToArray());
                Assert.Equal(data.Length, hashing.Length);
                Assert.Equal(Hex(SHA256.HashData(data)), hashing.HexDigest());
            }
        }

        [Fact]
        public void HexDigest_OfNothingIsTheEmptyInputHash()
        {
            using (HashingStream hashing = new HashingStream(new MemoryStream(), leaveOpen: false))
            {
                Assert.Equal(0, hashing.Length);
                Assert.Equal("e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855", hashing.HexDigest());
            }
        }

        [Fact]
        public void HexDigest_IsStillAvailableAfterDispose()
        {
            byte[] data = Sample();
            HashingStream hashing = new HashingStream(new MemoryStream(), leaveOpen: false);
            hashing.Write(data, 0, data.Length);
            hashing.Dispose();

            Assert.Equal(data.Length, hashing.Length);
            Assert.Equal(Hex(SHA256.HashData(data)), hashing.HexDigest());
        }

        [Fact]
        public void Dispose_ClosesInnerStreamUnlessLeftOpen()
        {
            MemoryStream closed = new MemoryStream();
            new HashingStream(closed, leaveOpen: false).Dispose();
            Assert.False(closed.CanWrite);

            MemoryStream open = new MemoryStream();
            new HashingStream(open, leaveOpen: true).Dispose();
            Assert.True(open.CanWrite);
        }
    }
}
