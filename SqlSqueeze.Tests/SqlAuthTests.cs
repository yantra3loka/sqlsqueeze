// SQL-auth connection strings for the db plugin. No SQL Server needed.

using System;
using System.Collections.Generic;
using Microsoft.Data.SqlClient;
using SqlSqueeze.Core;
using Xunit;

namespace SqlSqueeze.Tests
{
    public class SqlAuthTests
    {
        private static Dictionary<string, List<string>> Config(params string[] pairs)
        {
            Dictionary<string, List<string>> config =
                new Dictionary<string, List<string>>(StringComparer.InvariantCultureIgnoreCase);
            foreach (string pair in pairs)
            {
                int equals = pair.IndexOf('=');
                config.Add(pair.Substring(0, equals),
                    new List<string> { pair.Substring(equals + 1) });
            }
            return config;
        }

        [Fact]
        public void NoUser_KeepsLegacySspiString()
        {
            Assert.Equal(
                "Data Source=.;Initial Catalog=master;Integrated Security=SSPI;TrustServerCertificate=true;",
                SqlThread.BuildConnectionString(".", Config()));
        }

        [Fact]
        public void User_Password_BuildsSqlAuth()
        {
            SqlConnectionStringBuilder parsed = new SqlConnectionStringBuilder(
                SqlThread.BuildConnectionString(".", Config("user=sa", "password=s3cret")));
            Assert.Equal(".", parsed.DataSource);
            Assert.Equal("master", parsed.InitialCatalog);
            Assert.Equal("sa", parsed.UserID);
            Assert.Equal("s3cret", parsed.Password);
            Assert.True(parsed.TrustServerCertificate);
            Assert.False(parsed.IntegratedSecurity);
        }

        [Fact]
        public void User_PasswordEnv_ResolvesEnv()
        {
            Environment.SetEnvironmentVariable("SQLSQUEEZE_TEST_PW", "env-secret");
            try
            {
                SqlConnectionStringBuilder parsed = new SqlConnectionStringBuilder(
                    SqlThread.BuildConnectionString(".", Config("user=sa", "passwordenv=SQLSQUEEZE_TEST_PW")));
                Assert.Equal("env-secret", parsed.Password);
            }
            finally
            {
                Environment.SetEnvironmentVariable("SQLSQUEEZE_TEST_PW", null);
            }
        }

        [Fact]
        public void PasswordEnv_PreferredOverPassword()
        {
            Environment.SetEnvironmentVariable("SQLSQUEEZE_TEST_PW", "env-secret");
            try
            {
                SqlConnectionStringBuilder parsed = new SqlConnectionStringBuilder(
                    SqlThread.BuildConnectionString(".", Config("user=sa", "password=plain", "passwordenv=SQLSQUEEZE_TEST_PW")));
                Assert.Equal("env-secret", parsed.Password);
            }
            finally
            {
                Environment.SetEnvironmentVariable("SQLSQUEEZE_TEST_PW", null);
            }
        }

        [Fact]
        public void RedactConnectionString_HidesPassword()
        {
            string redacted = SqlThread.RedactConnectionString(
                SqlThread.BuildConnectionString(".", Config("user=sa", "password=s3cret")));
            Assert.DoesNotContain("s3cret", redacted);
            Assert.Contains("********", redacted);
            Assert.Contains("sa", redacted);
        }

        [Fact]
        public void RedactConnectionString_LeavesSspiWithoutSecrets()
        {
            SqlConnectionStringBuilder parsed = new SqlConnectionStringBuilder(
                SqlThread.RedactConnectionString(SqlThread.BuildConnectionString(".", Config())));
            Assert.True(parsed.IntegratedSecurity);
            Assert.True(string.IsNullOrEmpty(parsed.Password));
            Assert.DoesNotContain("********", parsed.ConnectionString);
        }

        [Fact]
        public void User_WithoutPassword_Throws()
        {
            Assert.Throws<ArgumentException>(() =>
                SqlThread.BuildConnectionString(".", Config("user=sa")));
        }

        [Fact]
        public void User_MissingEnv_Throws()
        {
            Assert.Throws<ArgumentException>(() =>
                SqlThread.BuildConnectionString(".", Config("user=sa", "passwordenv=SQLSQUEEZE_TEST_MISSING")));
        }
    }
}
