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
using System.Net.Http;
using System.Threading;
using SqlSqueeze.Plugins;

namespace SqlSqueeze.Core
{
    public class CopyOptions
    {
        /// <summary>How long to keep retrying through outages (Redgate retries for up to 24 hours).</summary>
        public TimeSpan MaxDuration { get; set; } = TimeSpan.FromHours(24);

        /// <summary>The first wait between attempts; it doubles each time up to BackoffMax.</summary>
        public TimeSpan BackoffBase { get; set; } = TimeSpan.FromSeconds(5);

        public TimeSpan BackoffMax { get; set; } = TimeSpan.FromMinutes(5);

        public Action<TimeSpan> Sleep { get; set; } = Thread.Sleep;
        public Func<DateTime> UtcNow { get; set; } = () => DateTime.UtcNow;

        /// <summary>Called after each failed attempt (attempt number, the error), before any wait.</summary>
        public Action<int, Exception> OnAttemptFailed { get; set; }
    }

    public class CopyResult
    {
        public bool Ok { get { return Problems.Count == 0; } }
        public int Attempts { get; set; }
        public long BytesCopied { get; set; }

        /// <summary>Bytes the destination already held from an earlier attempt, so they were read but not sent again.</summary>
        public long BytesSkipped { get; set; }

        public List<string> Problems { get; } = new List<string>();
        public List<string> Warnings { get; } = new List<string>();
    }

    /// <summary>
    /// Copies a finished backup, and its manifest, from one storage to another, for a second
    /// copy off the SQL Server machine or a move to the cloud. It retries through outages with
    /// a growing wait until a deadline, checks what it read against the manifest so a damaged
    /// source is not copied as if it were good, and writes the manifest last so that a copy
    /// without a manifest is known to be unfinished. Running it again is always safe.
    /// </summary>
    public static class BackupCopier
    {
        public static CopyResult Copy(IBackupStorage source, Dictionary<string, List<string>> sourceParams,
            IBackupStorage destination, Dictionary<string, List<string>> destinationParams, CopyOptions options)
        {
            int sourceFiles = source.GetNumberOfDevices(sourceParams);
            int destinationFiles = destination.GetNumberOfDevices(destinationParams);
            if (sourceFiles != destinationFiles)
            {
                throw new ArgumentException(string.Format("The source has {0} file(s) but the destination has {1}; give one destination file for each source file.", sourceFiles, destinationFiles));
            }

            CopyResult result = new CopyResult();
            DateTime started = options.UtcNow();
            DateTime deadline = started + options.MaxDuration;
            TimeSpan wait = options.BackoffBase;

            while (true)
            {
                result.Attempts++;
                try
                {
                    result.BytesCopied = CopyOnce(source, sourceParams, destination, destinationParams, result);
                    return result;
                }
                catch (Exception e)
                {
                    if (options.OnAttemptFailed != null)
                    {
                        options.OnAttemptFailed(result.Attempts, e);
                    }
                    string message = ErrorText.Innermost(e);
                    if (!IsTransient(e))
                    {
                        destination.CleanupOnAbort();
                        result.Problems.Add(message);
                        return result;
                    }
                    if (options.UtcNow() + wait > deadline)
                    {
                        // Between attempts the partial upload is kept so the next one can continue it;
                        // only now, when the copy is given up, is the leftover removed.
                        destination.CleanupOnAbort();
                        result.Problems.Add(string.Format("gave up after {0} attempt(s) over {1:0} minute(s): {2}",
                            result.Attempts, (options.UtcNow() - started).TotalMinutes, message));
                        return result;
                    }
                    options.Sleep(wait);
                    wait = TimeSpan.FromTicks(Math.Min(wait.Ticks * 2, options.BackoffMax.Ticks));
                }
            }
        }

        private static long CopyOnce(IBackupStorage source, Dictionary<string, List<string>> sourceParams,
            IBackupStorage destination, Dictionary<string, List<string>> destinationParams, CopyResult result)
        {
            BackupManifest manifest = ReadManifest(source, sourceParams);
            Stream[] inputs = null;
            Stream[] outputs = null;
            HashingStream[] hashed = null;
            bool finished = false;
            try
            {
                inputs = source.GetRestoreReader(sourceParams, out long estimated);
                // The source can be read again from the start, so a destination that can resume an
                // unfinished upload is asked to: a retry then skips what the server already has.
                IResumableStorage resumable = destination as IResumableStorage;
                outputs = resumable != null ? resumable.GetResumableBackupWriter(destinationParams) : destination.GetBackupWriter(destinationParams);
                hashed = new HashingStream[inputs.Length];
                for (int i = 0; i < inputs.Length; i++)
                {
                    hashed[i] = new HashingStream(inputs[i], leaveOpen: false);
                    hashed[i].CopyTo(outputs[i]);
                }

                // Before anything is committed to the destination: is what we read what was backed up?
                if (manifest != null)
                {
                    CheckAgainstManifest(manifest, hashed);
                }

                // Committing a writer is what makes the object appear, and can itself fail on the network.
                for (int i = 0; i < outputs.Length; i++)
                {
                    IResumableStream resumableWriter = outputs[i] as IResumableStream;
                    if (resumableWriter != null)
                    {
                        result.BytesSkipped += resumableWriter.BytesSkipped;
                    }
                    outputs[i].Dispose();
                }
                finished = true;
            }
            catch (Exception e)
            {
                if (outputs != null)
                {
                    // A fault that waiting can fix keeps the partial upload for the next attempt (suspend);
                    // anything else discards it (abort), so nothing half-written is ever committed.
                    if (IsTransient(e))
                    {
                        StreamAbort.SuspendAll(outputs);
                    }
                    else
                    {
                        StreamAbort.AbortAll(outputs);
                    }
                }
                throw;
            }
            finally
            {
                Close(hashed);
                Close(inputs);
                Close(outputs);
            }

            if (manifest != null)
            {
                IManifestStorage manifests = destination as IManifestStorage;
                if (manifests == null)
                {
                    result.Warnings.Add("the destination storage does not keep manifests, so the manifest was not copied");
                }
                else
                {
                    IList<string> names = manifests.GetFileNames(destinationParams);
                    for (int i = 0; i < manifest.Files.Count && i < names.Count; i++)
                    {
                        manifest.Files[i].Name = names[i];
                    }
                    manifests.WriteManifest(destinationParams, manifest.ToJson());
                }
            }
            else
            {
                result.Warnings.Add("there is no manifest next to the source, so the copy could not be checked against one and none was written");
            }

            long total = 0;
            foreach (HashingStream stream in hashed)
            {
                total += stream.Length;
            }
            return total;
        }

        private static BackupManifest ReadManifest(IBackupStorage source, Dictionary<string, List<string>> sourceParams)
        {
            IManifestStorage manifests = source as IManifestStorage;
            if (manifests == null)
            {
                return null;
            }
            string json = manifests.ReadManifest(sourceParams);
            return json == null ? null : BackupManifest.FromJson(json);
        }

        private static void CheckAgainstManifest(BackupManifest manifest, HashingStream[] read)
        {
            if (manifest.Files.Count != read.Length)
            {
                throw new InvalidDataException(string.Format("the manifest lists {0} file(s) but {1} were read", manifest.Files.Count, read.Length));
            }
            for (int i = 0; i < read.Length; i++)
            {
                ManifestFile expected = manifest.Files[i];
                if (expected.StoredBytes != read[i].Length
                    || !string.Equals(expected.StoredSha256, read[i].HexDigest(), StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException(string.Format(
                        "{0} does not match its manifest (stored sha256 {1}, read {2}); it has changed or been damaged since it was backed up, so it was not copied",
                        expected.Name, Short(expected.StoredSha256), Short(read[i].HexDigest())));
                }
            }
        }

        private static string Short(string hash)
        {
            return string.IsNullOrEmpty(hash) ? "(none)" : hash.Substring(0, Math.Min(12, hash.Length));
        }

        /// <summary>
        /// A fault that waiting and trying again can fix: the network, a timeout, a server that is
        /// busy or down (5xx). Not a missing file, bad credentials (4xx), bad arguments or damage.
        /// </summary>
        internal static bool IsTransient(Exception e)
        {
            if (e is AggregateException aggregate && aggregate.InnerExceptions.Count > 0)
            {
                return IsTransient(aggregate.InnerExceptions[0]);
            }
            if (e is FileNotFoundException || e is DirectoryNotFoundException || e is PathTooLongException || e is InvalidDataException)
            {
                return false;
            }
            return e is IOException || e is HttpRequestException || e is TimeoutException || e is OperationCanceledException;
        }

        private static void Close(IEnumerable<Stream> streams)
        {
            if (streams == null)
            {
                return;
            }
            foreach (Stream stream in streams)
            {
                try
                {
                    if (stream != null)
                    {
                        stream.Dispose();
                    }
                }
                catch (Exception)
                {
                    // a failure already being handled, or an already-committed writer
                }
            }
        }
    }
}
