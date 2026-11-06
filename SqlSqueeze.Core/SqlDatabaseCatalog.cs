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
using Microsoft.Data.SqlClient;
using SqlSqueeze.Plugins;

namespace SqlSqueeze.Core
{
    /// <summary>
    /// Lists the databases on the server the db plugin options point at, with
    /// the state backup-all needs to plan full, differential and log backups.
    /// Connects like a backup does (same login and instance options).
    /// </summary>
    public class ServerPaths
    {
        public string Data { get; set; }
        public string Log { get; set; }
    }

    public class SqlDatabaseCatalog : IDatabaseCatalog
    {
        private const string Query = @"
SELECT d.name,
       d.recovery_model_desc,
       d.state_desc,
       CASE WHEN r.last_log_backup_lsn IS NULL THEN 0 ELSE 1 END,
       CASE WHEN EXISTS (SELECT 1 FROM sys.master_files f
                         WHERE f.database_id = d.database_id AND f.file_id = 1
                           AND f.differential_base_lsn IS NOT NULL) THEN 1 ELSE 0 END
FROM sys.databases d
LEFT JOIN sys.database_recovery_status r ON r.database_id = d.database_id
ORDER BY d.name;";

        private readonly ConfigPair mDatabaseConfig;

        public SqlDatabaseCatalog(ConfigPair databaseConfig)
        {
            mDatabaseConfig = databaseConfig;
        }

        /// <summary>A connection to the server the db options point at, with the same login and instance rules a backup uses.</summary>
        internal static SqlConnection OpenConnection(ConfigPair databaseConfig)
        {
            IBackupDatabase plugin = databaseConfig.TransformationType.GetConstructor(new System.Type[0]).Invoke(new object[0]) as IBackupDatabase;
            string dataSource = SqlThread.ResolveDataSource(
                plugin.GetClusterNetworkName(databaseConfig.Parameters),
                plugin.GetInstanceName(databaseConfig.Parameters));
            SqlConnection cnn = new SqlConnection(SqlThread.BuildConnectionString(dataSource, databaseConfig.Parameters));
            cnn.Open();
            return cnn;
        }

        /// <summary>The server's default data and log folders, where a restore under a new name puts its files.</summary>
        public ServerPaths GetDefaultPaths()
        {
            using (SqlConnection cnn = OpenConnection(mDatabaseConfig))
            using (SqlCommand cmd = new SqlCommand("SELECT CONVERT(nvarchar(4000), SERVERPROPERTY('InstanceDefaultDataPath')), CONVERT(nvarchar(4000), SERVERPROPERTY('InstanceDefaultLogPath'));", cnn))
            using (SqlDataReader reader = cmd.ExecuteReader())
            {
                reader.Read();
                if (reader.IsDBNull(0) || reader.IsDBNull(1))
                {
                    throw new InvalidOperationException("This SQL Server does not report its default data and log folders (it needs SQL Server 2012 or later).");
                }
                return new ServerPaths { Data = reader.GetString(0), Log = reader.GetString(1) };
            }
        }

        /// <summary>Users of a (just restored) database whose login does not exist on this server.</summary>
        public List<string> ListOrphanedUsers(string database)
        {
            List<string> users = new List<string>();
            using (SqlConnection cnn = OpenConnection(mDatabaseConfig))
            using (SqlCommand cmd = new SqlCommand(RestorePreparation.OrphanedUsersSql(database), cnn))
            using (SqlDataReader reader = cmd.ExecuteReader())
            {
                while (reader.Read())
                {
                    users.Add(reader.GetString(0));
                }
            }
            return users;
        }

        /// <summary>The files of one database (logical name and type), for the manifest.</summary>
        public List<DatabaseFileInfo> ListFiles(string database)
        {
            List<DatabaseFileInfo> files = new List<DatabaseFileInfo>();
            using (SqlConnection cnn = OpenConnection(mDatabaseConfig))
            using (SqlCommand cmd = new SqlCommand("SELECT name, type_desc FROM sys.master_files WHERE database_id = DB_ID(@database) ORDER BY file_id;", cnn))
            {
                cmd.Parameters.AddWithValue("@database", database);
                using (SqlDataReader reader = cmd.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        files.Add(new DatabaseFileInfo { LogicalName = reader.GetString(0), Type = reader.GetString(1) });
                    }
                }
            }
            return files;
        }

        public List<DatabaseInfo> List()
        {
            List<DatabaseInfo> databases = new List<DatabaseInfo>();
            using (SqlConnection cnn = OpenConnection(mDatabaseConfig))
            {
                using (SqlCommand cmd = new SqlCommand(Query, cnn))
                using (SqlDataReader reader = cmd.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        databases.Add(new DatabaseInfo
                        {
                            Name = reader.GetString(0),
                            RecoveryModel = reader.GetString(1),
                            State = reader.GetString(2),
                            HasLogBase = reader.GetInt32(3) == 1,
                            HasDiffBase = reader.GetInt32(4) == 1,
                        });
                    }
                }
            }
            return databases;
        }
    }
}
