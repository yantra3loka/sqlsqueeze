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

namespace SqlSqueeze.Core
{
    /// <summary>
    /// What a restore does around the RESTORE statement itself: end other
    /// connections first when asked (a restore over an existing database needs
    /// exclusive access), and report orphaned users afterwards.
    /// </summary>
    internal static class RestorePreparation
    {
        /// <summary>KILL_CONNECTIONS was given: roll back other sessions so the restore can get exclusive access.</summary>
        public static bool WantsKillConnections(Dictionary<string, List<string>> config)
        {
            return config.ContainsKey("KILL_CONNECTIONS");
        }

        /// <summary>
        /// Orphans can only be judged once the database is usable again: not after a
        /// verify-only, and not while it is restored in steps (NORECOVERY/STANDBY). The
        /// last log of a chain, restored with RECOVERY, does bring it online.
        /// </summary>
        public static bool ShouldReportOrphans(Dictionary<string, List<string>> config)
        {
            if (config.ContainsKey("NORECOVERY") || config.ContainsKey("STANDBY"))
            {
                return false;
            }
            List<string> type;
            if (config.TryGetValue("restoretype", out type) && type.Count > 0
                && string.Equals(type[0], "verifyonly", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
            return true;
        }

        public static string KillConnectionsSql(string database)
        {
            return string.Format("IF DB_ID({0}) IS NOT NULL ALTER DATABASE {1} SET SINGLE_USER WITH ROLLBACK IMMEDIATE;", Literal(database), Bracket(database));
        }

        /// <summary>
        /// Puts the database back to multi-user after a failed restore. Only an ONLINE
        /// database can be altered; one left RESTORING is not touched.
        /// </summary>
        public static string MultiUserSql(string database)
        {
            return string.Format("IF DB_ID({0}) IS NOT NULL AND (SELECT state FROM sys.databases WHERE name = {0}) = 0 ALTER DATABASE {1} SET MULTI_USER;", Literal(database), Bracket(database));
        }

        /// <summary>
        /// Users in the restored database that map to a login which does not exist on
        /// this server (common after restoring onto another server). Contained-database
        /// users have no login and are not orphans.
        /// </summary>
        public static string OrphanedUsersSql(string database)
        {
            return string.Format(
                "SELECT dp.name FROM {0}.sys.database_principals dp " +
                "LEFT JOIN sys.server_principals sp ON dp.sid = sp.sid " +
                "WHERE dp.type IN ('S', 'U', 'G') AND dp.authentication_type = 1 AND dp.principal_id > 4 " +
                "AND dp.name NOT IN ('guest', 'INFORMATION_SCHEMA', 'sys') AND sp.sid IS NULL ORDER BY dp.name;",
                Bracket(database));
        }

        private static string Bracket(string name)
        {
            return "[" + name.Replace("]", "]]") + "]";
        }

        private static string Literal(string name)
        {
            return "N'" + name.Replace("'", "''") + "'";
        }
    }
}
