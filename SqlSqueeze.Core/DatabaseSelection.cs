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
using System.Text.RegularExpressions;

namespace SqlSqueeze.Core
{
    /// <summary>What backup-all needs to know about one database on the server.</summary>
    public class DatabaseInfo
    {
        public string Name { get; set; }

        /// <summary>FULL, BULK_LOGGED or SIMPLE.</summary>
        public string RecoveryModel { get; set; }

        /// <summary>sys.databases.state_desc: ONLINE, RESTORING, OFFLINE, ...</summary>
        public string State { get; set; }

        /// <summary>A log backup is possible: a full backup has anchored the log chain.</summary>
        public bool HasLogBase { get; set; }

        /// <summary>A differential backup is possible: a full backup exists to be the base.</summary>
        public bool HasDiffBase { get; set; }
    }

    public interface IDatabaseCatalog
    {
        List<DatabaseInfo> List();
    }

    /// <summary>
    /// Picks databases from a spec like "USER_DATABASES,-scratch*,Billing":
    /// the keywords USER_DATABASES, SYSTEM_DATABASES and ALL_DATABASES, names
    /// and * / ? wildcards (case-insensitive), and a leading - to exclude.
    /// tempdb is never selected; naming it explicitly is an error.
    /// </summary>
    public static class DatabaseSelector
    {
        private static readonly HashSet<string> SystemNames =
            new HashSet<string>(new[] { "master", "model", "msdb", "tempdb" }, StringComparer.OrdinalIgnoreCase);

        public static List<DatabaseInfo> Select(IList<DatabaseInfo> all, string spec)
        {
            List<string> includes = new List<string>();
            List<string> excludes = new List<string>();
            foreach (string raw in (spec ?? string.Empty).Split(','))
            {
                string item = raw.Trim();
                if (item.Length == 0)
                {
                    continue;
                }
                if (item[0] == '-')
                {
                    excludes.Add(item.Substring(1).Trim());
                }
                else
                {
                    includes.Add(item);
                }
            }
            if (includes.Count == 0)
            {
                throw new ArgumentException("Name the databases to back up, for example USER_DATABASES or app,billing (a - only excludes).");
            }

            foreach (string include in includes)
            {
                if (include.Equals("tempdb", StringComparison.OrdinalIgnoreCase))
                {
                    throw new ArgumentException("tempdb cannot be backed up.");
                }
                if (!IsKeyword(include) && !HasWildcard(include) && !Exists(all, include))
                {
                    throw new ArgumentException(string.Format("database not found: {0}", include));
                }
            }

            List<DatabaseInfo> selected = new List<DatabaseInfo>();
            foreach (DatabaseInfo db in all)
            {
                if (db.Name.Equals("tempdb", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                if (includes.Exists(i => Matches(db, i)) && !excludes.Exists(e => Matches(db, e)))
                {
                    selected.Add(db);
                }
            }
            return selected;
        }

        private static bool Matches(DatabaseInfo db, string item)
        {
            bool isSystem = SystemNames.Contains(db.Name);
            if (item.Equals("USER_DATABASES", StringComparison.OrdinalIgnoreCase))
            {
                return !isSystem;
            }
            if (item.Equals("SYSTEM_DATABASES", StringComparison.OrdinalIgnoreCase))
            {
                return isSystem;
            }
            if (item.Equals("ALL_DATABASES", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
            string regex = "^" + Regex.Escape(item).Replace("\\*", ".*").Replace("\\?", ".") + "$";
            return Regex.IsMatch(db.Name, regex, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        }

        private static bool IsKeyword(string item)
        {
            return item.Equals("USER_DATABASES", StringComparison.OrdinalIgnoreCase)
                || item.Equals("SYSTEM_DATABASES", StringComparison.OrdinalIgnoreCase)
                || item.Equals("ALL_DATABASES", StringComparison.OrdinalIgnoreCase);
        }

        private static bool HasWildcard(string item)
        {
            return item.IndexOf('*') >= 0 || item.IndexOf('?') >= 0;
        }

        private static bool Exists(IList<DatabaseInfo> all, string name)
        {
            foreach (DatabaseInfo db in all)
            {
                if (db.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            return false;
        }
    }

    public class BackupPlan
    {
        public string Database { get; set; }

        /// <summary>The db plugin's backuptype to run: full, differential or log.</summary>
        public string BackupType { get; set; }

        public bool Skip { get; set; }

        /// <summary>Why the database is skipped, or why the type was changed; null when nothing unusual.</summary>
        public string Reason { get; set; }
    }

    /// <summary>
    /// Decides what to do with one database for a requested backup type. A
    /// differential or log backup that cannot be taken yet (no full backup to
    /// build on) becomes a full backup, unless changing the type is switched off.
    /// </summary>
    public static class BackupPlanner
    {
        public static BackupPlan Plan(DatabaseInfo db, string requestedType, bool changeBackupType)
        {
            if (requestedType != "full" && requestedType != "differential" && requestedType != "log")
            {
                throw new ArgumentException(string.Format("Unknown backup type: {0} (use full, differential or log).", requestedType));
            }

            if (!string.Equals(db.State, "ONLINE", StringComparison.OrdinalIgnoreCase))
            {
                return Skip(db, requestedType, string.Format("database is {0}, not ONLINE", db.State));
            }
            if (requestedType == "full")
            {
                return Run(db, "full", null);
            }
            if (db.Name.Equals("master", StringComparison.OrdinalIgnoreCase))
            {
                return Skip(db, requestedType, "master only supports full backups");
            }

            if (requestedType == "log")
            {
                if (string.Equals(db.RecoveryModel, "SIMPLE", StringComparison.OrdinalIgnoreCase))
                {
                    return Skip(db, requestedType, "SIMPLE recovery model has no log backups");
                }
                if (!db.HasLogBase)
                {
                    return changeBackupType
                        ? Run(db, "full", "no log chain yet (it needs a full backup first): took a full backup instead")
                        : Skip(db, requestedType, "no log chain yet (it needs a full backup first)");
                }
                return Run(db, "log", null);
            }

            if (!db.HasDiffBase)
            {
                return changeBackupType
                    ? Run(db, "full", "no differential base yet (it needs a full backup first): took a full backup instead")
                    : Skip(db, requestedType, "no differential base yet (it needs a full backup first)");
            }
            return Run(db, "differential", null);
        }

        private static BackupPlan Run(DatabaseInfo db, string type, string reason)
        {
            return new BackupPlan { Database = db.Name, BackupType = type, Reason = reason };
        }

        private static BackupPlan Skip(DatabaseInfo db, string type, string reason)
        {
            return new BackupPlan { Database = db.Name, BackupType = type, Skip = true, Reason = reason };
        }
    }
}
