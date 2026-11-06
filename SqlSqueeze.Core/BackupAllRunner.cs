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
    public class BackupAllItem
    {
        public string Database { get; set; }

        /// <summary>The type actually run (may differ from the one requested).</summary>
        public string BackupType { get; set; }

        /// <summary>ok, skipped or error.</summary>
        public string Status { get; set; }

        public string Message { get; set; }
        public BackupStats Stats { get; set; }

        /// <summary>What went wrong copying this backup elsewhere (--copy-to). The backup itself is fine.</summary>
        public List<string> CopyProblems { get; } = new List<string>();
    }

    public class BackupAllResult
    {
        public List<BackupAllItem> Items { get; } = new List<BackupAllItem>();

        /// <summary>True when no database failed and no copy failed. Skipped databases are not failures.</summary>
        public bool AllOk
        {
            get { return !Items.Exists(i => i.Status == "error" || i.CopyProblems.Count > 0); }
        }
    }

    /// <summary>
    /// Backs up every database a spec selects, one VDI backup each. {database}
    /// and {backuptype} in any storage parameter are replaced per database. One
    /// failing database is reported and does not stop the others.
    /// </summary>
    public static class BackupAllRunner
    {
        public static BackupAllResult Run(IDatabaseCatalog catalog, string databasesSpec, string requestedType, bool changeBackupType,
            ConfigPair databaseTemplate, List<ConfigPair> pipeline, ConfigPair storageTemplate,
            Func<ConfigPair, List<ConfigPair>, ConfigPair, BackupStats> backup, IList<ConfigPair> copyTo = null)
        {
            List<DatabaseInfo> selected = DatabaseSelector.Select(catalog.List(), databasesSpec);
            if (selected.Count == 0)
            {
                throw new ArgumentException(string.Format("no databases matched: {0}", databasesSpec));
            }

            List<BackupPlan> plans = new List<BackupPlan>();
            foreach (DatabaseInfo db in selected)
            {
                plans.Add(BackupPlanner.Plan(db, requestedType, changeBackupType));
            }

            if (selected.Count > 1 && !HasToken(storageTemplate, "{database}"))
            {
                throw new ArgumentException("Several databases would be written to the same place. Put {database} in the storage path, for example local(path=/backups/{database}/{database}_{backuptype}_{yyyyMMdd_HHmmss}.bak).");
            }
            if (copyTo != null && selected.Count > 1)
            {
                foreach (ConfigPair destination in copyTo)
                {
                    if (!HasToken(destination, "{database}"))
                    {
                        throw new ArgumentException("Several databases would be copied to the same place. Put {database} in each --copy-to storage path too, for example local(path=/mirror/{database}/{database}_{backuptype}.bak).");
                    }
                }
            }
            RejectDuplicatePaths(plans, storageTemplate);

            BackupAllResult result = new BackupAllResult();
            foreach (BackupPlan plan in plans)
            {
                BackupAllItem item = new BackupAllItem { Database = plan.Database, BackupType = plan.BackupType };
                result.Items.Add(item);
                if (plan.Skip)
                {
                    item.Status = "skipped";
                    item.Message = plan.Reason;
                    continue;
                }

                try
                {
                    item.Stats = backup(WithDatabase(databaseTemplate, plan), pipeline, WithTokens(storageTemplate, plan));
                    item.Status = "ok";
                    item.Message = plan.Reason;
                    if (item.Stats != null)
                    {
                        item.CopyProblems.AddRange(item.Stats.CopyProblems);
                    }
                }
                catch (Exception e)
                {
                    item.Status = "error";
                    item.Message = ErrorText.Innermost(e);
                }
            }
            return result;
        }

        private static ConfigPair WithDatabase(ConfigPair template, BackupPlan plan)
        {
            ConfigPair copy = Copy(template);
            copy.Parameters["database"] = new List<string> { plan.Database };
            copy.Parameters["backuptype"] = new List<string> { plan.BackupType };
            return copy;
        }

        private static ConfigPair WithTokens(ConfigPair template, BackupPlan plan)
        {
            return ExpandTokens(template, plan.Database, plan.BackupType);
        }

        /// <summary>A copy of the config with {database} and {backuptype} replaced in every parameter (the database name made safe for a file name).</summary>
        public static ConfigPair ExpandTokens(ConfigPair template, string database, string backupType)
        {
            ConfigPair copy = Copy(template);
            foreach (string key in new List<string>(copy.Parameters.Keys))
            {
                List<string> values = copy.Parameters[key];
                for (int i = 0; i < values.Count; i++)
                {
                    values[i] = values[i]
                        .Replace("{database}", SafeFileName(database))
                        .Replace("{backuptype}", backupType);
                }
            }
            return copy;
        }

        private static ConfigPair Copy(ConfigPair template)
        {
            Dictionary<string, List<string>> parameters = new Dictionary<string, List<string>>(template.Parameters.Comparer);
            foreach (KeyValuePair<string, List<string>> pair in template.Parameters)
            {
                parameters[pair.Key] = new List<string>(pair.Value);
            }
            return new ConfigPair { TransformationType = template.TransformationType, Parameters = parameters };
        }

        private static bool HasToken(ConfigPair storage, string token)
        {
            foreach (List<string> values in storage.Parameters.Values)
            {
                if (values.Exists(v => v.Contains(token)))
                {
                    return true;
                }
            }
            return false;
        }

        // Two names can sanitize to the same file name (a:b and a/b).
        private static void RejectDuplicatePaths(List<BackupPlan> plans, ConfigPair storage)
        {
            HashSet<string> seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (BackupPlan plan in plans)
            {
                if (plan.Skip)
                {
                    continue;
                }
                StringBuilder key = new StringBuilder();
                ConfigPair resolved = WithTokens(storage, plan);
                List<string> keys = new List<string>(resolved.Parameters.Keys);
                keys.Sort(StringComparer.Ordinal);
                foreach (string name in keys)
                {
                    key.Append(name).Append('=').Append(string.Join("|", resolved.Parameters[name])).Append(';');
                }
                if (!seen.Add(key.ToString()))
                {
                    throw new ArgumentException(string.Format("Two databases would write the same file (check {0}); make the storage path more specific.", plan.Database));
                }
            }
        }

        private static string SafeFileName(string name)
        {
            StringBuilder safe = new StringBuilder(name.Length);
            foreach (char c in name)
            {
                safe.Append(Array.IndexOf(UnsafeChars, c) >= 0 || char.IsControl(c) ? '_' : c);
            }
            return safe.ToString();
        }

        private static readonly char[] UnsafeChars = { '\\', '/', ':', '*', '?', '"', '<', '>', '|' };

    }
}
