/*
	Copyright 2026 Krishnan Venkiteswaran

	This file is part of SqlSqueeze (formerly MSSQL Compressed Backup).

    SqlSqueeze is free software: you can redistribute it and/or modify
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
using System.IO;
using ZstdSharp;

namespace SqlSqueeze.Plugins
{
    /// <summary>
    /// Zstandard compression. Faster and usually smaller than gzip;
    /// the recommended default for new backups:
    ///   sqlsqueeze backup [mydb] zstd local(path=c:\b.bak.zst)
    /// </summary>
    public class ZstdTransform : IBackupTransformer
    {
        private static Dictionary<string, ParameterInfo> mBackupParamSchema;
        private static Dictionary<string, ParameterInfo> mRestoreParamSchema;
        static ZstdTransform()
        {
            mBackupParamSchema = new Dictionary<string, ParameterInfo>(StringComparer.InvariantCultureIgnoreCase);
            mBackupParamSchema.Add("level", new ParameterInfo(false, false));

            mRestoreParamSchema = new Dictionary<string, ParameterInfo>(StringComparer.InvariantCultureIgnoreCase);
            // Backup-only: accepted and ignored so one pipeline spec works for backup and restore.
            mRestoreParamSchema.Add("level", new ParameterInfo(false, false));
        }

        public string Name
        {
            get { return "zstd"; }
        }

        public string CommandLineHelp
        {
            get
            {
                return @"zstd Usage:
zstd compresses with Zstandard: faster than gzip at a similar or better
ratio. Level 1 (fastest) to 22 (smallest), default 3:
    zstd(level=6)
Level is ignored on restore.";
            }
        }

        public Stream GetBackupWriter(Dictionary<string, List<string>> config, Stream writeToStream)
        {
            ParameterInfo.ValidateParams(mBackupParamSchema, config);

            int level = 3;
            if (config.TryGetValue("level", out List<string> sLevel))
            {
                if (!int.TryParse(sLevel[0], out level))
                {
                    throw new ArgumentException(string.Format("zstd: Unable to parse the integer: {0}", sLevel[0]));
                }
            }

            if (level < 1 || level > 22)
            {
                throw new ArgumentException(string.Format("zstd: Level must be between 1 and 22: {0}", level));
            }

            Console.WriteLine(string.Format("zstd: level = {0}", level));
            // ZstdSharp leaves the wrapped stream open by default; disposing this stage must
            // also close the next one down (aes needs that to write its final block).
            return new CompressionStream(writeToStream, level, leaveOpen: false);
        }

        public Stream GetRestoreReader(Dictionary<string, List<string>> config, Stream readFromStream)
        {
            ParameterInfo.ValidateParams(mRestoreParamSchema, config);

            Console.WriteLine("zstd");
            return new DecompressionStream(readFromStream, leaveOpen: false);
        }
    }
}
