/*
	Copyright 2009 Clay Lenhart <clay@lenharts.net>


	This file is part of MSSQL Compressed Backup.

    MSSQL Compressed Backup is free software: you can redistribute it and/or modify
    it under the terms of the GNU General Public License as published by
    the Free Software Foundation, either version 3 of the License, or
    (at your option) any later version.

    Foobar is distributed in the hope that it will be useful,
    but WITHOUT ANY WARRANTY; without even the implied warranty of
    MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
    GNU General Public License for more details.

    You should have received a copy of the GNU General Public License
    along with Foobar.  If not, see <http://www.gnu.org/licenses/>.
*/

using System;
using System.Collections.Generic;
using System.Text;
using System.IO;

namespace SqlSqueeze.Plugins.Storage
{
    public class LocalStorage : IBackupStorage, IPrunableStorage
    {
        private List<bool> mDeleteOnAbort = new List<bool>();
        private List<FileInfo> mFileInfosToDeleteOnAbort = new List<FileInfo>();

        private static Dictionary<string, ParameterInfo> mBackupParamSchema;
        static LocalStorage()
        {
            mBackupParamSchema = new Dictionary<string, ParameterInfo>(StringComparer.InvariantCultureIgnoreCase);
            mBackupParamSchema.Add("path", new ParameterInfo(true, true));
            mBackupParamSchema.Add("writeretries", new ParameterInfo(false, false));
            mBackupParamSchema.Add("writeretrybackoffms", new ParameterInfo(false, false));
        }

        #region IBackupStorage Members

        public string Name
        {
            get { return "local"; }
        }

        public int GetNumberOfDevices(Dictionary<string, List<string>> config)
        {
            ParameterInfo.ValidateParams(mBackupParamSchema, config);



            return config["path"].Count;
        }


        public Stream[] GetBackupWriter(Dictionary<string, List<string>> config)
        {

            mDeleteOnAbort.Clear();
            mFileInfosToDeleteOnAbort.Clear();

            ParameterInfo.ValidateParams(mBackupParamSchema, config);


            List<string> paths = config["path"];
            List<FileInfo> fileInfos = paths.ConvertAll<FileInfo>(delegate(string path)
            {
                return new FileInfo(ExpandDateTokens(path));
            });


            // initialize to false:
            mDeleteOnAbort = new List<bool>(new bool[fileInfos.Count]);
            mFileInfosToDeleteOnAbort = fileInfos;

            for (int i = 0; i < mDeleteOnAbort.Count; i++)
            {
                mDeleteOnAbort[i] = !fileInfos[i].Exists;
            }



            Console.WriteLine(string.Format("local:"));
            foreach (FileInfo fi in fileInfos)
            {
                Console.WriteLine(string.Format("\tpath={0}", fi.FullName));
            }

            int writeRetries = ResolveWriterRetries(config);
            int writeRetryBackoffMs = ResolveWriterRetryBackoffMs(config);
            if (writeRetries > 1)
            {
                Console.WriteLine(string.Format("\twriteretries={0} writeretrybackoffms={1}", writeRetries, writeRetryBackoffMs));
            }

            List<Stream> results = new List<Stream>(fileInfos.Count);
            foreach (FileInfo fi in fileInfos)
            {
                Stream writer = fi.Open(FileMode.Create);
                if (writeRetries > 1)
                {
                    writer = new WriteRetryStream(writer, writeRetries, writeRetryBackoffMs);
                }
                results.Add(writer);
            }

            return results.ToArray();
        }

        public Stream[] GetRestoreReader(Dictionary<string, List<string>> config, out long estimatedTotalBytes)
        {


            mDeleteOnAbort.Clear();
            mFileInfosToDeleteOnAbort.Clear();

            ParameterInfo.ValidateParams(mBackupParamSchema, config);



            List<string> paths = config["path"];
            List<FileInfo> fileInfos = paths.ConvertAll<FileInfo>(delegate(string path)
            {
                return new FileInfo(path);
            });


            long combinedSize = 0;

            Console.WriteLine(string.Format("local:"));
            foreach (FileInfo fi in fileInfos)
            {
                Console.WriteLine(string.Format("\tpath={0}", fi.FullName));
                combinedSize += fi.Length;
            }

            estimatedTotalBytes = combinedSize;

            List<Stream> results = new List<Stream>(fileInfos.Count);
            foreach (FileInfo fi in fileInfos)
            {
                results.Add(fi.Open(FileMode.Open));
            }

            return results.ToArray();
        }

        public string CommandLineHelp
        {
            get
            {
                return @"local Usage:
This is a plugin to store or read a backup file.
To reference a file, enter:
local(path=<file>)

sqlsqueeze has an alias for the local plugin.  If it begins with file://, it is
converted to the 'local' plugin equivalent.  file:///c:\model.bak is converted
to local(path=c:\model.bak).

For flaky shares add writeretries=<total attempts> and
writeretrybackoffms=<base backoff in ms, doubling>; only transient
write/flush errors are retried.
";
            }
        }

        private static int ResolveWriterRetries(Dictionary<string, List<string>> config)
        {
            List<string> values;
            if (!config.TryGetValue("writeretries", out values) || values.Count == 0)
            {
                return 1;
            }
            int retries;
            if (!int.TryParse(values[0], out retries) || retries < 1)
            {
                throw new ArgumentException("writeretries must be an integer >= 1 (total attempts).");
            }
            return retries;
        }

        private static int ResolveWriterRetryBackoffMs(Dictionary<string, List<string>> config)
        {
            List<string> values;
            if (!config.TryGetValue("writeretrybackoffms", out values) || values.Count == 0)
            {
                return 1000;
            }
            int backoffMs;
            if (!int.TryParse(values[0], out backoffMs) || backoffMs < 0)
            {
                throw new ArgumentException("writeretrybackoffms must be an integer >= 0.");
            }
            return backoffMs;
        }

        public void CleanupOnAbort()
        {

            for (int i = 0; i < mFileInfosToDeleteOnAbort.Count; i++)
            {
                FileInfo fi = mFileInfosToDeleteOnAbort[i];
                bool deleteOnAbort = mDeleteOnAbort[i];
                if (deleteOnAbort && fi != null)
                {
                    fi.Delete();
                }
            }
            mFileInfosToDeleteOnAbort = null;

        }

        #endregion

        /// <summary>
        /// Backup paths only: {…} is a .NET date/time format evaluated once
        /// in local time, so Agent jobs can write
        /// local(path=D:\B\DB_{yyyyMMdd_HHmmss}.bak.zst). Restore takes
        /// literal names and never expands.
        /// </summary>
        public static string ExpandDateTokens(string path)
        {
            int open = path.IndexOf('{');
            while (open >= 0)
            {
                int close = path.IndexOf('}', open + 1);
                if (close < 0)
                {
                    break;
                }
                string format = path.Substring(open + 1, close - open - 1);
                string stamp;
                try
                {
                    stamp = DateTime.Now.ToString(format);
                }
                catch (FormatException)
                {
                    throw new ArgumentException(string.Format("local: bad date token '{{{0}}}' in path: {1}", format, path));
                }
                path = path.Substring(0, open) + stamp + path.Substring(close + 1);
                open = path.IndexOf('{', open + stamp.Length);
            }
            return path;
        }

        #region IPrunableStorage Members

        public PruneResult PruneOldFiles(Dictionary<string, List<string>> config)
        {
            if (config.Comparer != StringComparer.InvariantCultureIgnoreCase)
            {
                throw new ArgumentException("Programming error: prune config must use InvariantCultureIgnoreCase.");
            }
            if (!config.TryGetValue("dir", out List<string> dirs) || dirs.Count != 1 || string.IsNullOrWhiteSpace(dirs[0]))
            {
                throw new ArgumentException("local: prune needs exactly one dir=<backup folder>.");
            }
            if (!config.TryGetValue("olderthandays", out List<string> ages)
                || !int.TryParse(ages[0], out int olderThanDays) || olderThanDays < 1)
            {
                throw new ArgumentException("local: prune needs olderThanDays=<integer >= 1>.");
            }
            string pattern = config.TryGetValue("pattern", out List<string> patterns) ? patterns[0] : "*";

            DirectoryInfo dir = new DirectoryInfo(dirs[0]);
            if (!dir.Exists)
            {
                throw new DirectoryNotFoundException(string.Format("local: prune directory does not exist: {0}", dir.FullName));
            }

            DateTime cutoffUtc = DateTime.UtcNow.Subtract(TimeSpan.FromDays(olderThanDays));
            PruneResult result = new PruneResult();
            foreach (FileInfo file in dir.GetFiles(pattern, SearchOption.TopDirectoryOnly))
            {
                if (file.LastWriteTimeUtc < cutoffUtc)
                {
                    long length = file.Length;
                    file.Delete();
                    result.DeletedFiles++;
                    result.BytesReclaimed += length;
                    result.DeletedPaths.Add(file.FullName);
                }
            }
            return result;
        }

        #endregion


    }
}
