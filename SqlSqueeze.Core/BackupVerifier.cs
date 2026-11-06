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
using System.Collections.Generic;
using System.IO;
using SqlSqueeze.Plugins;

namespace SqlSqueeze.Core
{
    public class VerifyResult
    {
        /// <summary>True when nothing went wrong. Without a manifest this only means the file decoded.</summary>
        public bool Ok { get { return Problems.Count == 0; } }
        public bool ManifestFound { get; set; }
        public long StoredBytes { get; set; }
        public long RawBytes { get; set; }
        public List<string> Problems { get; } = new List<string>();

        /// <summary>SQL Server was also asked to verify the backup (RESTORE VERIFYONLY through VDI).</summary>
        public bool SqlChecked { get; private set; }

        /// <summary>What SQL Server said, when it was asked.</summary>
        public string SqlMessage { get; private set; }

        public void RecordSqlCheck(bool ok, string message)
        {
            SqlChecked = true;
            SqlMessage = message;
            if (!ok)
            {
                Problems.Add("SQL Server RESTORE VERIFYONLY failed: " + message);
            }
        }
    }

    /// <summary>The outcome of unpack: the native file(s) were written and match the manifest, or they were removed.</summary>
    public class UnpackResult
    {
        private readonly VerifyResult mVerified;

        internal UnpackResult(VerifyResult verified)
        {
            mVerified = verified;
        }

        public bool Ok { get { return mVerified.Ok; } }
        public bool ManifestFound { get { return mVerified.ManifestFound; } }

        /// <summary>Bytes of native backup written (the decoded size).</summary>
        public long BytesWritten { get { return mVerified.RawBytes; } }

        public List<string> Problems { get { return mVerified.Problems; } }
    }

    /// <summary>
    /// Re-reads a backup through its pipeline (decrypt, decompress) without
    /// SQL Server, and compares sizes and SHA-256 hashes of the stored file
    /// and of the decoded stream with the manifest written at backup time.
    /// This proves the stored bytes are the bytes that were written and that
    /// the pipeline hands SQL Server back the same stream. It does not ask SQL
    /// Server whether that stream is a valid backup.
    /// </summary>
    public static class BackupVerifier
    {
        private class NullStreamNotification : IStreamNotification
        {
            public long EstimatedBytes { get; set; }
            public int GetThreadId() { return 0; }
            public TimeSpan UpdateBytesProcessed(long totalBytesProcessedByThread, int threadId) { return TimeSpan.FromHours(1); }
        }

        public static VerifyResult Verify(IBackupStorage storage, Dictionary<string, List<string>> storageParams, List<ConfigPair> pipeline, BackupManifest manifest)
        {
            return Verify(storage, storageParams, pipeline, manifest, null);
        }

        /// <summary>
        /// As above, and writes the decoded stream of file i to sinks[i] on the way
        /// (that is what unpack does). The caller owns and disposes the sinks.
        /// </summary>
        internal static VerifyResult Verify(IBackupStorage storage, Dictionary<string, List<string>> storageParams, List<ConfigPair> pipeline, BackupManifest manifest, IList<Stream> sinks)
        {
            VerifyResult result = new VerifyResult { ManifestFound = manifest != null };
            ManifestRecorder recorder = new ManifestRecorder();
            Stream[] files = null;
            Stream[] tops = null;
            try
            {
                files = storage.GetRestoreReader(storageParams, out long estimatedBytes);
                tops = BackupPipeSystem.CreatePipeline(pipeline, files, false, new NullStreamNotification(), estimatedBytes, recorder);

                for (int i = 0; i < tops.Length; i++)
                {
                    try
                    {
                        Stream sink = sinks == null ? Stream.Null : sinks[i];
                        tops[i].CopyTo(sink);
                        sink.Flush();
                    }
                    catch (Exception e)
                    {
                        result.Problems.Add(string.Format("{0}: decode failed: {1}", NameOf(manifest, i), Innermost(e)));
                    }
                }
                recorder.DrainStored();
            }
            catch (Exception e)
            {
                result.Problems.Add(string.Format("could not read the backup: {0}", Innermost(e)));
            }
            finally
            {
                // Like a backup, the pipeline and the files are separate lists: some
                // stages (zstd, aes) do not close the stream below them.
                CloseAll(tops);
                CloseAll(files);
            }

            List<ManifestFile> observed = recorder.Observed(null);
            foreach (ManifestFile file in observed)
            {
                result.StoredBytes += file.StoredBytes;
                result.RawBytes += file.RawBytes;
            }
            if (manifest != null)
            {
                Compare(manifest, observed, result);
            }
            return result;
        }

        private static void Compare(BackupManifest manifest, List<ManifestFile> observed, VerifyResult result)
        {
            if (manifest.Files.Count != observed.Count)
            {
                result.Problems.Add(string.Format("the manifest lists {0} file(s) but {1} were read", manifest.Files.Count, observed.Count));
            }
            int shared = Math.Min(manifest.Files.Count, observed.Count);
            for (int i = 0; i < shared; i++)
            {
                ManifestFile expected = manifest.Files[i];
                ManifestFile actual = observed[i];
                string name = NameOf(manifest, i);
                if (expected.StoredBytes != actual.StoredBytes)
                {
                    result.Problems.Add(string.Format("{0}: stored size is {1} bytes, the manifest says {2}", name, actual.StoredBytes, expected.StoredBytes));
                }
                if (!SameHash(expected.StoredSha256, actual.StoredSha256))
                {
                    result.Problems.Add(string.Format("{0}: stored sha256 mismatch (manifest {1}, read {2})", name, Short(expected.StoredSha256), Short(actual.StoredSha256)));
                }
                if (expected.RawBytes != actual.RawBytes)
                {
                    result.Problems.Add(string.Format("{0}: decoded size is {1} bytes, the manifest says {2}", name, actual.RawBytes, expected.RawBytes));
                }
                if (!SameHash(expected.RawSha256, actual.RawSha256))
                {
                    result.Problems.Add(string.Format("{0}: raw sha256 mismatch (manifest {1}, decoded {2})", name, Short(expected.RawSha256), Short(actual.RawSha256)));
                }
            }
        }

        private static bool SameHash(string expected, string actual)
        {
            return string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase);
        }

        private static string Short(string hash)
        {
            return string.IsNullOrEmpty(hash) ? "(none)" : hash.Substring(0, Math.Min(12, hash.Length));
        }

        private static string NameOf(BackupManifest manifest, int index)
        {
            if (manifest != null && index < manifest.Files.Count && !string.IsNullOrEmpty(manifest.Files[index].Name))
            {
                return manifest.Files[index].Name;
            }
            return "file " + (index + 1);
        }

        private static string Innermost(Exception e)
        {
            while (e.InnerException != null)
            {
                e = e.InnerException;
            }
            return e.Message;
        }

        private static void CloseAll(Stream[] streams)
        {
            if (streams == null)
            {
                return;
            }
            foreach (Stream stream in streams)
            {
                try
                {
                    stream.Dispose();
                }
                catch (Exception)
                {
                    // decoders can throw again on dispose after a bad stream; the problem is already recorded
                }
            }
        }
    }
}
