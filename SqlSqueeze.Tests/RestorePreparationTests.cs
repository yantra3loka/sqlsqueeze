// Restore ergonomics: ending connections before a restore, and reporting
// orphaned users after one. The SQL and the decisions, with no SQL Server.

using System;
using System.Collections.Generic;
using Microsoft.Data.SqlClient;
using SqlSqueeze.Core;
using SqlSqueeze.Plugins.Database;
using Xunit;

namespace SqlSqueeze.Tests
{
    public class RestorePreparationTests
    {
        private static Dictionary<string, List<string>> Cfg(params string[] pairs)
        {
            var d = new Dictionary<string, List<string>>(StringComparer.InvariantCultureIgnoreCase);
            foreach (string pair in pairs)
            {
                int eq = pair.IndexOf('=');
                string key = eq < 0 ? pair : pair.Substring(0, eq);
                d[key] = new List<string> { eq < 0 ? null : pair.Substring(eq + 1) };
            }
            return d;
        }

        // ---- the SQL ----

        [Fact]
        public void KillConnections_RollsBackOtherSessionsButOnlyIfTheDatabaseExists()
        {
            Assert.Equal(
                "IF DB_ID(N'appdb') IS NOT NULL ALTER DATABASE [appdb] SET SINGLE_USER WITH ROLLBACK IMMEDIATE;",
                RestorePreparation.KillConnectionsSql("appdb"));
        }

        [Fact]
        public void TheDatabaseNameIsEscapedForBothTheBracketsAndTheLiteral()
        {
            string sql = RestorePreparation.KillConnectionsSql("we]ird'db");

            Assert.Equal("IF DB_ID(N'we]ird''db') IS NOT NULL ALTER DATABASE [we]]ird'db] SET SINGLE_USER WITH ROLLBACK IMMEDIATE;", sql);
        }

        [Fact]
        public void MultiUser_PutsAnOnlineDatabaseBack_AndLeavesARestoringOneAlone()
        {
            string sql = RestorePreparation.MultiUserSql("appdb");

            Assert.Contains("ALTER DATABASE [appdb] SET MULTI_USER", sql);
            // ONLINE only (state 0): a database left RESTORING cannot take ALTER DATABASE
            Assert.Contains("(SELECT state FROM sys.databases WHERE name = N'appdb') = 0", sql);
        }

        [Fact]
        public void OrphanedUsersQuery_LooksInTheRestoredDatabase_ForInstanceUsersWithNoLogin()
        {
            string sql = RestorePreparation.OrphanedUsersSql("appdb");

            Assert.Contains("[appdb].sys.database_principals", sql);
            Assert.Contains("sys.server_principals", sql);
            Assert.Contains("authentication_type = 1", sql);   // users that map to a login; contained-database users are not orphans
        }

        // ---- the decisions ----

        [Fact]
        public void KillConnections_IsOnlyWhenAsked()
        {
            Assert.False(RestorePreparation.WantsKillConnections(Cfg("database=app")));
            Assert.True(RestorePreparation.WantsKillConnections(Cfg("database=app", "KILL_CONNECTIONS")));
        }

        [Fact]
        public void Orphans_AreReportedAfterARestoreThatLeavesTheDatabaseUsable()
        {
            Assert.True(RestorePreparation.ShouldReportOrphans(Cfg("database=app")));
            Assert.True(RestorePreparation.ShouldReportOrphans(Cfg("database=app", "RECOVERY")));
            Assert.True(RestorePreparation.ShouldReportOrphans(Cfg("database=app", "restoretype=database")));
        }

        [Theory]
        [InlineData("NORECOVERY")]
        [InlineData("STANDBY=/var/opt/mssql/undo.sby")]
        public void Orphans_AreNotReportedWhileTheDatabaseIsStillBeingRestoredInSteps(string option)
        {
            Assert.False(RestorePreparation.ShouldReportOrphans(Cfg("database=app", option)));
        }

        [Fact]
        public void Orphans_AreNotReportedForAVerify()
        {
            Assert.False(RestorePreparation.ShouldReportOrphans(Cfg("database=app", "restoretype=verifyonly")));
        }

        [Fact]
        public void Orphans_AreReportedAfterTheLastLogOfAChainBringsTheDatabaseOnline()
        {
            Assert.True(RestorePreparation.ShouldReportOrphans(Cfg("database=app", "restoretype=log", "RECOVERY")));
            Assert.False(RestorePreparation.ShouldReportOrphans(Cfg("database=app", "restoretype=log", "NORECOVERY")));
        }

        // ---- the db plugin ----

        private static string RestoreSql(params string[] pairs)
        {
            SqlCommand cmd = new SqlCommand();
            new DatabaseSource().ConfigureRestoreCommand(Cfg(pairs), new[] { "dev0" }, cmd);
            return cmd.CommandText;
        }

        [Fact]
        public void KillConnections_IsNotARestoreOption_SoTheRestoreStatementIsUnchanged()
        {
            Assert.Equal(RestoreSql("database=app"), RestoreSql("database=app", "KILL_CONNECTIONS"));
        }

        [Fact]
        public void KillConnections_MakesNoSenseForAVerifyOnly()
        {
            Assert.Throws<ArgumentException>(() => RestoreSql("database=app", "restoretype=verifyonly", "KILL_CONNECTIONS"));
        }
    }
}
