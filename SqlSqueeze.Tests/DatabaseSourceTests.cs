using System;
using System.Collections.Generic;
using Microsoft.Data.SqlClient;
using SqlSqueeze.Plugins.Database;
using Xunit;

namespace SqlSqueeze.Tests
{
    public class DatabaseSourceTests
    {
        private static Dictionary<string, List<string>> Cfg(params (string, string)[] pairs)
        {
            var d = new Dictionary<string, List<string>>(StringComparer.InvariantCultureIgnoreCase);
            foreach ((string k, string v) in pairs)
            {
                if (d.ContainsKey(k))
                {
                    d[k].Add(v);
                }
                else
                {
                    d[k] = new List<string> { v };
                }
            }
            return d;
        }

        private static SqlCommand Backup(params (string, string)[] pairs)
        {
            var cmd = new SqlCommand();
            new DatabaseSource().ConfigureBackupCommand(Cfg(pairs), new[] { "vd0" }, cmd);
            return cmd;
        }

        private static SqlCommand Restore(params (string, string)[] pairs)
        {
            var cmd = new SqlCommand();
            new DatabaseSource().ConfigureRestoreCommand(Cfg(pairs), new[] { "vd0" }, cmd);
            return cmd;
        }

        private static int CountOccurrences(string haystack, string needle)
        {
            return (haystack.Length - haystack.Replace(needle, "").Length) / needle.Length;
        }

        [Theory]
        [InlineData("full")]
        [InlineData("Full")]
        [InlineData("FULL")]
        [InlineData("fUlL")]
        public void BackupType_CaseInsensitive_Full(string backupType)
        {
            SqlCommand cmd = Backup(("database", "mydb"), ("backuptype", backupType));
            Assert.StartsWith("BACKUP DATABASE", cmd.CommandText);
            Assert.DoesNotContain("DIFFERENTIAL", cmd.CommandText);
        }

        [Theory]
        [InlineData("differential")]
        [InlineData("Differential")]
        [InlineData("DIFFERENTIAL")]
        public void BackupType_CaseInsensitive_Differential(string backupType)
        {
            SqlCommand cmd = Backup(("database", "mydb"), ("backuptype", backupType));
            Assert.Contains("DIFFERENTIAL", cmd.CommandText);
        }

        [Theory]
        [InlineData("log")]
        [InlineData("Log")]
        [InlineData("LOG")]
        public void BackupType_CaseInsensitive_Log(string backupType)
        {
            SqlCommand cmd = Backup(("database", "mydb"), ("backuptype", backupType));
            Assert.StartsWith("BACKUP LOG", cmd.CommandText);
        }

        [Fact]
        public void BackupType_Unknown_Throws()
        {
            Assert.Throws<ArgumentException>(() => Backup(("database", "mydb"), ("backuptype", "banana")));
        }

        [Theory]
        [InlineData("database")]
        [InlineData("Database")]
        [InlineData("DATABASE")]
        public void RestoreType_CaseInsensitive_Database(string restoreType)
        {
            SqlCommand cmd = Restore(("database", "mydb"), ("restoretype", restoreType));
            Assert.StartsWith("RESTORE DATABASE", cmd.CommandText);
        }

        [Theory]
        [InlineData("log")]
        [InlineData("Log")]
        [InlineData("LOG")]
        public void RestoreType_CaseInsensitive_Log(string restoreType)
        {
            SqlCommand cmd = Restore(("database", "mydb"), ("restoretype", restoreType));
            Assert.StartsWith("RESTORE LOG", cmd.CommandText);
        }

        [Fact]
        public void RestoreType_Unknown_Throws()
        {
            Assert.Throws<ArgumentException>(() => Restore(("database", "mydb"), ("restoretype", "banana")));
        }

        [Fact]
        public void Partial_EmittedOnce()
        {
            SqlCommand cmd = Restore(("database", "mydb"), ("partial", "x"));
            Assert.Contains("PARTIAL", cmd.CommandText);
            Assert.Equal(1, CountOccurrences(cmd.CommandText, "PARTIAL"));
        }

        [Fact]
        public void Backup_ChecksumAndNoChecksum_Throws()
        {
            Assert.Throws<ArgumentException>(() => Backup(("database", "mydb"), ("checksum", "x"), ("no_checksum", "x")));
        }

        [Fact]
        public void Backup_StopOnErrorAndContinueAfterError_Throws()
        {
            Assert.Throws<ArgumentException>(() => Backup(("database", "mydb"), ("stop_on_error", "x"), ("continue_after_error", "x")));
        }

        [Fact]
        public void Backup_ChecksumAlone_Works()
        {
            SqlCommand cmd = Backup(("database", "mydb"), ("checksum", "x"));
            Assert.Contains("CHECKSUM", cmd.CommandText);
        }

        [Fact]
        public void Restore_ChecksumAndNoChecksum_Throws()
        {
            Assert.Throws<ArgumentException>(() => Restore(("database", "mydb"), ("checksum", "x"), ("no_checksum", "x")));
        }

        [Fact]
        public void Restore_StopOnErrorAndContinueAfterError_Throws()
        {
            Assert.Throws<ArgumentException>(() => Restore(("database", "mydb"), ("stop_on_error", "x"), ("continue_after_error", "x")));
        }

        [Fact]
        public void Restore_RecoveryAndNoRecovery_Throws()
        {
            Assert.Throws<ArgumentException>(() => Restore(("database", "mydb"), ("recovery", "x"), ("norecovery", "x")));
        }

        [Theory]
        [InlineData("recovery")]
        [InlineData("norecovery")]
        public void Restore_StandbyWithRecovery_Throws(string other)
        {
            Assert.Throws<ArgumentException>(() => Restore(("database", "mydb"), ("standby", "s.bak"), (other, "x")));
        }

        [Fact]
        public void Restore_StandbyAlone_Works()
        {
            SqlCommand cmd = Restore(("database", "mydb"), ("standby", "s.bak"));
            Assert.Contains("STANDBY='s.bak'", cmd.CommandText);
        }

        [Theory]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData("''")]
        public void Restore_EmptyStandby_Throws(string standby)
        {
            Assert.Throws<ArgumentException>(() => Restore(("database", "mydb"), ("standby", standby)));
        }

        [Theory]
        [InlineData("'data' TO 'data2'")]
        [InlineData("'data' to 'data2'")]
        [InlineData("'data' To 'data2'")]
        public void Restore_Move_ToKeyword_CaseInsensitive(string move)
        {
            SqlCommand cmd = Restore(("database", "mydb"), ("move", move));
            Assert.Contains("MOVE 'data' TO 'data2'", cmd.CommandText);
        }

        [Theory]
        [InlineData("''TO'data2'")]
        [InlineData("'data'TO''")]
        [InlineData("'   ' TO 'data2'")]
        public void Restore_Move_EmptyFromOrTo_Throws(string move)
        {
            Assert.Throws<ArgumentException>(() => Restore(("database", "mydb"), ("move", move)));
        }
    }
}
