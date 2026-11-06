// The RESTORE statements the db plugin builds. No SQL Server needed.

using System;
using System.Collections.Generic;
using Microsoft.Data.SqlClient;
using SqlSqueeze.Plugins;
using SqlSqueeze.Plugins.Database;
using Xunit;

namespace SqlSqueeze.Tests
{
    public class RestoreCommandTests
    {
        private static Dictionary<string, List<string>> Cfg(params string[] pairs)
        {
            var d = new Dictionary<string, List<string>>(StringComparer.InvariantCultureIgnoreCase);
            foreach (string pair in pairs)
            {
                int eq = pair.IndexOf('=');
                string key = eq < 0 ? pair : pair.Substring(0, eq);
                string value = eq < 0 ? null : pair.Substring(eq + 1);
                if (!d.ContainsKey(key))
                {
                    d[key] = new List<string>();
                }
                d[key].Add(value);
            }
            return d;
        }

        private static string RestoreSql(params string[] pairs)
        {
            SqlCommand cmd = new SqlCommand();
            new DatabaseSource().ConfigureRestoreCommand(Cfg(pairs), new[] { "dev0" }, cmd);
            return cmd.CommandText;
        }

        [Fact]
        public void Restore_Database_IsUnchanged()
        {
            Assert.Equal("RESTORE DATABASE @databasename FROM VIRTUAL_DEVICE='dev0';", RestoreSql("database=app"));
        }

        [Fact]
        public void VerifyOnly_ReadsTheBackupWithoutNamingADatabase()
        {
            Assert.Equal("RESTORE VERIFYONLY FROM VIRTUAL_DEVICE='dev0';", RestoreSql("database=app", "restoretype=verifyonly"));
        }

        [Fact]
        public void VerifyOnly_KeepsTheChecksumOption_AndEveryStripe()
        {
            SqlCommand cmd = new SqlCommand();
            new DatabaseSource().ConfigureRestoreCommand(Cfg("database=app", "restoretype=verifyonly", "CHECKSUM"), new[] { "dev0", "dev1" }, cmd);

            Assert.Equal("RESTORE VERIFYONLY FROM VIRTUAL_DEVICE='dev0',VIRTUAL_DEVICE='dev1' WITH CHECKSUM;", cmd.CommandText);
        }

        [Fact]
        public void VerifyOnly_DoesNotNeedADatabaseName()
        {
            Assert.Equal("RESTORE VERIFYONLY FROM VIRTUAL_DEVICE='dev0';", RestoreSql("restoretype=verifyonly"));
        }

        [Theory]
        [InlineData("NORECOVERY")]
        [InlineData("REPLACE")]
        [InlineData("RECOVERY")]
        public void VerifyOnly_RejectsOptionsThatOnlyMakeSenseWhenRestoring(string option)
        {
            ArgumentException e = Assert.Throws<ArgumentException>(() => RestoreSql("database=app", "restoretype=verifyonly", option));

            Assert.Contains("verifyonly", e.Message);
            Assert.Contains(option, e.Message);
        }

        [Fact]
        public void AnUnknownRestoreType_StillFails()
        {
            Assert.Throws<ArgumentException>(() => RestoreSql("database=app", "restoretype=nonsense"));
        }

        [Fact]
        public void Partial_AppearsExactlyOnce()
        {
            string sql = RestoreSql("database=app", "PARTIAL");

            Assert.Equal(1, sql.Split(new[] { "PARTIAL" }, StringSplitOptions.None).Length - 1);
        }
    }
}
