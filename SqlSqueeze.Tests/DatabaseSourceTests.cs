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

        [Theory]
        [InlineData("'   '")]
        [InlineData("' '")]
        public void Restore_QuotedBlankStandby_Throws(string standby)
        {
            Assert.Throws<ArgumentException>(() => Restore(("database", "mydb"), ("standby", standby)));
        }

        [Fact]
        public void Restore_StandbyWithStopAt_Works()
        {
            SqlCommand cmd = Restore(("database", "mydb"), ("standby", "s.bak"), ("stopat", "2024-05-01 13:30:00"));
            Assert.Contains("STANDBY='s.bak'", cmd.CommandText);
            Assert.Contains("STOPAT=@stopat", cmd.CommandText);
        }

        [Fact]
        public void Restore_StandbyWithReplace_Works()
        {
            SqlCommand cmd = Restore(("database", "mydb"), ("standby", "s.bak"), ("replace", "x"));
            Assert.Contains("STANDBY='s.bak'", cmd.CommandText);
            Assert.Contains("REPLACE", cmd.CommandText);
        }

        [Fact]
        public void Restore_NoRecoveryWithStopAt_Works()
        {
            SqlCommand cmd = Restore(("database", "mydb"), ("norecovery", "x"), ("stopat", "2024-05-01 13:30:00"));
            Assert.Contains("NORECOVERY", cmd.CommandText);
            Assert.Contains("STOPAT=@stopat", cmd.CommandText);
        }

        [Fact]
        public void Backup_ChecksumWithContinueAfterError_Works()
        {
            SqlCommand cmd = Backup(("database", "mydb"), ("checksum", "x"), ("continue_after_error", "x"));
            Assert.Contains("CHECKSUM", cmd.CommandText);
            Assert.Contains("CONTINUE_AFTER_ERROR", cmd.CommandText);
        }

        [Fact]
        public void Backup_CopyOnlyFull_Works()
        {
            SqlCommand cmd = Backup(("database", "mydb"), ("backuptype", "full"), ("copy_only", "x"));
            Assert.Contains("COPY_ONLY", cmd.CommandText);
        }

        [Fact]
        public void Backup_DifferentialWithChecksum_Works()
        {
            SqlCommand cmd = Backup(("database", "mydb"), ("backuptype", "differential"), ("checksum", "x"));
            Assert.Contains("DIFFERENTIAL", cmd.CommandText);
            Assert.Contains("CHECKSUM", cmd.CommandText);
        }

        [Fact]
        public void Restore_RecoveryAlone_Works()
        {
            SqlCommand cmd = Restore(("database", "mydb"), ("recovery", "x"));
            Assert.Contains("RECOVERY", cmd.CommandText);
            Assert.DoesNotContain("NORECOVERY", cmd.CommandText);
        }

        [Fact]
        public void Restore_NoChecksumAlone_Works()
        {
            SqlCommand cmd = Restore(("database", "mydb"), ("no_checksum", "x"));
            Assert.Contains("NO_CHECKSUM", cmd.CommandText);
        }

        [Fact]
        public void Restore_MultipleMove_Works()
        {
            SqlCommand cmd = Restore(("database", "mydb"), ("move", "'data' TO 'data2'"), ("move", "'log' TO 'log2'"));
            Assert.Contains("MOVE 'data' TO 'data2'", cmd.CommandText);
            Assert.Contains("MOVE 'log' TO 'log2'", cmd.CommandText);
        }

        [Fact]
        public void Restore_Move_PathWithSpaces_Works()
        {
            SqlCommand cmd = Restore(("database", "mydb"), ("move", "'my data' TO 'D:\\my backups\\data2.mdf'"));
            Assert.Contains("MOVE 'my data' TO 'D:\\my backups\\data2.mdf'", cmd.CommandText);
        }

        [Fact]
        public void Restore_Move_NonToKeyword_Throws()
        {
            Assert.Throws<ArgumentException>(() => Restore(("database", "mydb"), ("move", "'data' FROM 'data2'")));
        }
    }
}
