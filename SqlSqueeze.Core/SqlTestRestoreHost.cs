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
using Microsoft.Data.SqlClient;

namespace SqlSqueeze.Core
{
    /// <summary>
    /// The real SQL Server behind a test restore: restores through the normal VDI
    /// restore path under the scratch name, runs DBCC CHECKDB, drops the database.
    /// </summary>
    public class SqlTestRestoreHost : ITestRestoreHost
    {
        private readonly ConfigPair mStorage;
        private readonly List<ConfigPair> mPipeline;
        private readonly ConfigPair mDatabase;
        private readonly IUpdateNotification mNotifier;
        private string mDataPath;
        private string mLogPath;

        public SqlTestRestoreHost(ConfigPair storageConfig, List<ConfigPair> pipelineConfig, ConfigPair databaseConfig, IUpdateNotification notifier)
        {
            mStorage = storageConfig;
            mPipeline = pipelineConfig;
            mDatabase = databaseConfig;
            mNotifier = notifier;
        }

        public string DataPath { get { LoadPaths(); return mDataPath; } }

        public string LogPath { get { LoadPaths(); return mLogPath; } }

        private void LoadPaths()
        {
            if (mDataPath != null)
            {
                return;
            }
            ServerPaths paths = new SqlDatabaseCatalog(mDatabase).GetDefaultPaths();
            mDataPath = paths.Data;
            mLogPath = paths.Log;
        }

        public bool DatabaseExists(string name)
        {
            using (SqlConnection cnn = SqlDatabaseCatalog.OpenConnection(mDatabase))
            using (SqlCommand cmd = new SqlCommand("SELECT COUNT(*) FROM sys.databases WHERE name = @name;", cnn))
            {
                cmd.Parameters.AddWithValue("@name", name);
                return (int)cmd.ExecuteScalar() > 0;
            }
        }

        public BackupStats Restore(string scratchName, List<string> moves)
        {
            Dictionary<string, List<string>> parameters = new Dictionary<string, List<string>>(mDatabase.Parameters.Comparer);
            foreach (KeyValuePair<string, List<string>> pair in mDatabase.Parameters)
            {
                parameters[pair.Key] = new List<string>(pair.Value);
            }
            parameters["database"] = new List<string> { scratchName };
            parameters["MOVE"] = new List<string>(moves);
            ConfigPair restoreConfig = new ConfigPair { TransformationType = mDatabase.TransformationType, Parameters = parameters };
            return BackupPipeSystem.Restore(mStorage, mPipeline, restoreConfig, mNotifier);
        }

        public string CheckDb(string scratchName, bool physicalOnly)
        {
            List<string> messages = new List<string>();
            using (SqlConnection cnn = SqlDatabaseCatalog.OpenConnection(mDatabase))
            {
                // Deliver DBCC's error-severity messages here, not as one exception that hides the rest.
                cnn.FireInfoMessageEventOnUserErrors = true;
                cnn.InfoMessage += (sender, e) => messages.Add(e.Message);
                using (SqlCommand cmd = new SqlCommand(string.Format("DBCC CHECKDB ({0}) WITH NO_INFOMSGS, ALL_ERRORMSGS{1};", Quote(scratchName), physicalOnly ? ", PHYSICAL_ONLY" : string.Empty), cnn))
                {
                    cmd.CommandTimeout = 0;
                    try
                    {
                        cmd.ExecuteNonQuery();
                    }
                    catch (SqlException e)
                    {
                        messages.Add(e.Message);
                    }
                }
            }
            return messages.Count == 0 ? null : string.Join(" | ", messages);
        }

        public void Drop(string scratchName)
        {
            using (SqlConnection cnn = SqlDatabaseCatalog.OpenConnection(mDatabase))
            using (SqlCommand cmd = new SqlCommand(string.Format("DROP DATABASE {0};", Quote(scratchName)), cnn))
            {
                cmd.ExecuteNonQuery();
            }
        }

        // The scratch name is generated here from letters, digits and underscores, but quote it properly anyway.
        private static string Quote(string name)
        {
            return "[" + name.Replace("]", "]]") + "]";
        }
    }
}
