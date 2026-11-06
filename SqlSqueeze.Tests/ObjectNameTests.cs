// Backup names are not always plain: a name with spaces, non-ASCII letters, + & %
// or # must reach the service as exactly that name (found by the cloud
// conformance run: Azure received "%41" as "A" and cut the name at "#").

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using SqlSqueeze.Plugins.Storage;
using Xunit;

namespace SqlSqueeze.Tests
{
    public class ObjectNameTests
    {
        private const string OddName = "odd name/ünï cødé+plus&and%41#hash.bak";

        private static Dictionary<string, List<string>> Config(params string[] pairs)
        {
            var d = new Dictionary<string, List<string>>(StringComparer.InvariantCultureIgnoreCase);
            foreach (string pair in pairs)
            {
                int eq = pair.IndexOf('=');
                string key = pair.Substring(0, eq);
                if (!d.ContainsKey(key)) { d[key] = new List<string>(); }
                d[key].Add(pair.Substring(eq + 1));
            }
            return d;
        }

        [Fact]
        public void Azure_BlobNameWithSpecialCharacters_IsStoredUnderThatExactName()
        {
            FakeAzure fake = new FakeAzure();
            Stream writer = new AzureStorage(fake).GetBackupWriter(Config(
                "endpoint=http://127.0.0.1:10000/devstoreaccount1", "container=cont", "account=devstoreaccount1",
                "accountkey=Eby8vdM02xNOcqFlqUwJPLlmEtlCDXJ1OUzFT50uSRZ6IFsuFq2UVErCz4I6tq/K1SZFPTOtr/KBHBeksoGMGw==",
                "blob=" + OddName))[0];
            writer.WriteByte(7);
            writer.Dispose();

            Assert.Equal(new[] { "/devstoreaccount1/cont/" + OddName }, fake.Objects.Keys.ToArray());
        }

        [Fact]
        public void S3_KeyWithSpecialCharacters_IsStoredUnderThatExactName()
        {
            FakeS3 fake = new FakeS3();
            Stream writer = new S3Storage(fake).GetBackupWriter(Config(
                "endpoint=http://127.0.0.1:9000", "bucket=bkt", "region=us-east-1", "accesskey=AK", "secretkey=SK",
                "key=" + OddName))[0];
            writer.WriteByte(7);
            writer.Dispose();

            Assert.Equal(new[] { "/bkt/" + OddName }, fake.Objects.Keys.ToArray());
        }
    }
}
