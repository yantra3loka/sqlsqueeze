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
using System.Text;

namespace SqlSqueeze.Core
{
    /// <summary>One file of a database as it was when it was backed up (kept in the manifest).</summary>
    public class DatabaseFileInfo
    {
        public string LogicalName { get; set; }

        /// <summary>sys.master_files.type_desc: ROWS, LOG, FILESTREAM, ...</summary>
        public string Type { get; set; }
    }

    /// <summary>
    /// Names the scratch database and places its files, so a backup can be restored
    /// next to the original without touching it.
    /// </summary>
    public static class TestRestorePlanner
    {
        private const string Prefix = "sqs_testrestore_";
        private const int MaxNameLength = 128;

        public static string ScratchName(string database, DateTime nowUtc)
        {
            string stamp = nowUtc.ToString("yyyyMMddHHmmss");
            StringBuilder safe = new StringBuilder();
            foreach (char c in database)
            {
                safe.Append(char.IsLetterOrDigit(c) && c < 128 || c == '_' ? c : '_');
            }
            int room = MaxNameLength - Prefix.Length - 1 - stamp.Length;
            string middle = safe.Length > room ? safe.ToString(0, room) : safe.ToString();
            return Prefix + middle + "_" + stamp;
        }

        /// <summary>
        /// One MOVE='logical'TO'path' per file, with paths built from the scratch
        /// name (never from the original file names) in the server's data and log folders.
        /// </summary>
        public static List<string> MoveClauses(IList<DatabaseFileInfo> files, string scratchName, string dataDir, string logDir)
        {
            List<string> moves = new List<string>();
            int dataIndex = 0;
            int logIndex = 0;
            int filestreamIndex = 0;
            foreach (DatabaseFileInfo file in files)
            {
                if (file.LogicalName.Contains("'"))
                {
                    throw new ArgumentException(string.Format("A logical file name with a quote cannot be moved: {0}", file.LogicalName));
                }

                string path;
                if (string.Equals(file.Type, "ROWS", StringComparison.OrdinalIgnoreCase))
                {
                    string name = dataIndex == 0 ? scratchName + ".mdf" : string.Format("{0}_{1}.ndf", scratchName, dataIndex);
                    path = Join(dataDir, name);
                    dataIndex++;
                }
                else if (string.Equals(file.Type, "LOG", StringComparison.OrdinalIgnoreCase))
                {
                    string name = logIndex == 0 ? scratchName + "_log.ldf" : string.Format("{0}_log_{1}.ldf", scratchName, logIndex);
                    path = Join(logDir, name);
                    logIndex++;
                }
                else if (string.Equals(file.Type, "FILESTREAM", StringComparison.OrdinalIgnoreCase))
                {
                    // FILESTREAM targets a directory, not a file.
                    string name = filestreamIndex == 0 ? scratchName + "_fs" : string.Format("{0}_fs_{1}", scratchName, filestreamIndex);
                    path = Join(dataDir, name);
                    filestreamIndex++;
                }
                else
                {
                    throw new ArgumentException(string.Format("Test restore cannot place a {0} file ({1}); restore this database by hand.", file.Type, file.LogicalName));
                }
                moves.Add(string.Format("'{0}'TO'{1}'", file.LogicalName, path));
            }
            return moves;
        }

        private static string Join(string dir, string file)
        {
            if (dir.EndsWith("/") || dir.EndsWith("\\"))
            {
                return dir + file;
            }
            return dir + (dir.Contains("\\") ? "\\" : "/") + file;
        }
    }

    /// <summary>What a test restore needs from SQL Server.</summary>
    public interface ITestRestoreHost
    {
        /// <summary>The server's default data folder, where scratch data files go.</summary>
        string DataPath { get; }

        /// <summary>The server's default log folder.</summary>
        string LogPath { get; }

        bool DatabaseExists(string name);

        /// <summary>Restores the backup under the scratch name, with the given MOVE clauses.</summary>
        BackupStats Restore(string scratchName, List<string> moves);

        /// <summary>Runs DBCC CHECKDB; returns null when clean, otherwise what it reported.</summary>
        string CheckDb(string scratchName, bool physicalOnly);

        void Drop(string scratchName);
    }

    public class TestRestoreResult
    {
        public bool Ok { get { return Problems.Count == 0; } }
        public string ScratchDatabase { get; set; }
        public long RestoredBytes { get; set; }

        /// <summary>The scratch database was dropped.</summary>
        public bool Dropped { get; set; }

        public List<string> Problems { get; } = new List<string>();
    }

    /// <summary>
    /// Restores a full backup into a scratch database, runs DBCC CHECKDB on it, and
    /// drops it again, so a backup is known to restore to a healthy database. It
    /// only ever drops the scratch database it created, and it refuses to start if
    /// one with that name already exists.
    /// </summary>
    public static class TestRestoreRunner
    {
        public static TestRestoreResult Run(BackupManifest manifest, ITestRestoreHost host, bool keep, bool physicalOnly, DateTime nowUtc)
        {
            if (manifest == null)
            {
                throw new ArgumentException("There is no manifest next to this backup, and a test restore needs it for the database name and file list. Backups taken by this version write one for local storage.");
            }
            if (!string.Equals(manifest.BackupType, "full", StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException(string.Format("A test restore needs a full backup; this one is {0}.", manifest.BackupType));
            }
            if (manifest.DatabaseFiles == null || manifest.DatabaseFiles.Count == 0)
            {
                throw new ArgumentException("The manifest has no file list (the backup was taken before manifests recorded one), so the scratch restore cannot place the files. Take a new backup, or restore it by hand with MOVE.");
            }

            string scratch = TestRestorePlanner.ScratchName(manifest.Database, nowUtc);
            if (host.DatabaseExists(scratch))
            {
                throw new ArgumentException(string.Format("A database named {0} already exists; not touching it.", scratch));
            }
            List<string> moves = TestRestorePlanner.MoveClauses(manifest.DatabaseFiles, scratch, host.DataPath, host.LogPath);

            TestRestoreResult result = new TestRestoreResult { ScratchDatabase = scratch };
            bool restored = false;
            try
            {
                result.RestoredBytes = host.Restore(scratch, moves).BytesProcessed;
                restored = true;
            }
            catch (Exception e)
            {
                result.Problems.Add("restore failed: " + ErrorText.Innermost(e));
            }

            if (restored)
            {
                try
                {
                    string errors = host.CheckDb(scratch, physicalOnly);
                    if (!string.IsNullOrEmpty(errors))
                    {
                        result.Problems.Add("DBCC CHECKDB reported errors: " + errors);
                    }
                }
                catch (Exception e)
                {
                    result.Problems.Add("DBCC CHECKDB could not run: " + ErrorText.Innermost(e));
                }
            }

            if (!keep)
            {
                // A failed restore can still leave a database in the RESTORING state.
                try
                {
                    if (host.DatabaseExists(scratch))
                    {
                        host.Drop(scratch);
                        result.Dropped = true;
                    }
                }
                catch (Exception e)
                {
                    result.Problems.Add(string.Format("could not drop scratch database {0}: {1}", scratch, ErrorText.Innermost(e)));
                }
            }
            return result;
        }
    }
}
