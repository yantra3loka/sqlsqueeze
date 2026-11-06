// Pieces of "verify --sql" that need no SQL Server: reading an error's real
// message, and building the VERIFYONLY config from the user's db options.

using System;
using System.Collections.Generic;
using SqlSqueeze.Core;
using SqlSqueeze.Plugins.Database;
using Xunit;

namespace SqlSqueeze.Tests
{
    public class VerifySqlTests
    {
        [Fact]
        public void ErrorText_UsesTheInnermostMessage()
        {
            Exception e = new InvalidOperationException("outer", new ArgumentException("middle", new System.IO.IOException("disk full")));

            Assert.Equal("disk full", ErrorText.Innermost(e));
        }

        [Fact]
        public void ErrorText_LooksInsideAParallelExecutionException()
        {
            ParallelExecutionException many = new ParallelExecutionException();
            many.Exceptions.Add(new Exception("wrapper", new Exception("Msg 3241: media family incorrectly formed")));
            many.Exceptions.Add(new Exception("second problem"));

            Assert.Equal("Msg 3241: media family incorrectly formed", ErrorText.Innermost(many));
        }

        private static ConfigPair Db(params string[] pairs)
        {
            var p = new Dictionary<string, List<string>>(StringComparer.InvariantCultureIgnoreCase);
            foreach (string pair in pairs)
            {
                int eq = pair.IndexOf('=');
                p[eq < 0 ? pair : pair.Substring(0, eq)] = new List<string> { eq < 0 ? null : pair.Substring(eq + 1) };
            }
            return new ConfigPair { TransformationType = typeof(DatabaseSource), Parameters = p };
        }

        [Fact]
        public void VerifyOnlyConfig_KeepsTheLoginOptionsAndAsksForVerifyOnly()
        {
            ConfigPair original = Db("user=sa", "passwordenv=PW", "instancename=SQL2019");

            ConfigPair verify = BackupPipeSystem.VerifyOnlyConfig(original);

            Assert.Equal("verifyonly", verify.Parameters["restoretype"][0]);
            Assert.Equal("sa", verify.Parameters["user"][0]);
            Assert.Equal("PW", verify.Parameters["passwordenv"][0]);
            Assert.Equal("SQL2019", verify.Parameters["instancename"][0]);
            Assert.Equal(typeof(DatabaseSource), verify.TransformationType);
        }

        [Fact]
        public void VerifyOnlyConfig_DoesNotChangeTheCallersConfig()
        {
            ConfigPair original = Db("user=sa", "restoretype=database");

            BackupPipeSystem.VerifyOnlyConfig(original);

            Assert.Equal("database", original.Parameters["restoretype"][0]);
        }

        [Fact]
        public void VerifyOnlyConfig_PassesTheBackupsDatabaseNameWhenKnown_ForMessages()
        {
            ConfigPair verify = BackupPipeSystem.VerifyOnlyConfig(Db("user=sa"), "appdb");

            Assert.Equal("appdb", verify.Parameters["database"][0]);
        }
    }
}
