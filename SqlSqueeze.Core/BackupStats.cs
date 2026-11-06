/*
	Copyright 2009 Clay Lenhart <clay@lenharts.net>

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

namespace SqlSqueeze.Core
{
    /// <summary>
    /// Totals reported by BackupPipeSystem.Backup/Restore. BytesProcessed
    /// counts bytes moved through the top of the pipeline (uncompressed
    /// side); EstimatedBytes is the pre-run size estimate and may be 0
    /// when it cannot be estimated (e.g. restores).
    /// </summary>
    public class CopyOutcome
    {
        public string Destination { get; set; }
        public bool Ok { get; set; }
        public int Attempts { get; set; }
        public long BytesCopied { get; set; }
        public long BytesSkipped { get; set; }
        public System.Collections.Generic.List<string> Problems { get; } = new System.Collections.Generic.List<string>();
    }

    public class BackupStats
    {
        public long BytesProcessed;
        public long EstimatedBytes;
        public TimeSpan Elapsed;

        /// <summary>Where this backup was copied after it was taken (--copy-to), and how each copy went.</summary>
        public System.Collections.Generic.List<CopyOutcome> Copies = new System.Collections.Generic.List<CopyOutcome>();

        /// <summary>
        /// What went wrong copying a backup that itself succeeded. Not a backup failure, but not
        /// something to hide either: the caller reports it and exits non-zero.
        /// </summary>
        public System.Collections.Generic.List<string> CopyProblems = new System.Collections.Generic.List<string>();

        /// <summary>Things worth telling the user that did not fail the operation (for example orphaned users after a restore).</summary>
        public System.Collections.Generic.List<string> Warnings = new System.Collections.Generic.List<string>();

        public double MegaBytesPerSecond
        {
            get
            {
                if (Elapsed.TotalSeconds <= 0)
                {
                    return 0;
                }
                return (BytesProcessed / (1024.0 * 1024.0)) / Elapsed.TotalSeconds;
            }
        }
    }
}
