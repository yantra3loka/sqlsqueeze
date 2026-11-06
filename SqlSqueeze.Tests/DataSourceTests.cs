// How instancename= / clusternetworkname= become a SQL Server data source.

using SqlSqueeze.Core;
using Xunit;

namespace SqlSqueeze.Tests
{
    public class DataSourceTests
    {
        [Fact]
        public void NoInstanceNoCluster_IsTheLocalDefaultInstance()
        {
            Assert.Equal(".", SqlThread.ResolveDataSource(null, null));
            Assert.Equal(".", SqlThread.ResolveDataSource(null, ""));
        }

        [Fact]
        public void ANamedInstance_IsLocalBackslashInstance()
        {
            Assert.Equal(@".\SQL2019", SqlThread.ResolveDataSource(null, "SQL2019"));
        }

        [Fact]
        public void AClusterNetworkName_ReplacesTheDot()
        {
            Assert.Equal("clusterA", SqlThread.ResolveDataSource("clusterA", null));
            Assert.Equal(@"clusterA\SQL2019", SqlThread.ResolveDataSource("clusterA", "SQL2019"));
        }

        [Fact]
        public void ALocalDbInstance_IsAlreadyAFullDataSource()
        {
            Assert.Equal(@"(localdb)\MSSQLLocalDB", SqlThread.ResolveDataSource(null, @"(localdb)\MSSQLLocalDB"));
        }
    }
}
