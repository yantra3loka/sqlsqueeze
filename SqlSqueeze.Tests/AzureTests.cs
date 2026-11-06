// Azure Shared Key canonicalization and blob flows.
// Live interop runs against Azurite; HTTP flows use a mocked handler.

using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using SqlSqueeze.Plugins.Storage;
using Xunit;

namespace SqlSqueeze.Tests
{
    public class AzureSignerTests
    {
        [Fact]
        public void CanonicalStringToSign_MatchesSharedKeyShape()
        {
            Dictionary<string, string> headers = new Dictionary<string, string>();
            headers["x-ms-date"] = "Wed, 01 Jan 2020 00:00:00 GMT";
            headers["x-ms-version"] = "2020-04-08";
            string canonical = AzureSigner.CanonicalStringToSign(
                "PUT", 8, "application/octet-stream",
                "devstoreaccount1", "c/b.bak",
                "comp=block&blockid=AAAAAA==", headers);
            Assert.Equal(
                "PUT\n\n\n8\n\napplication/octet-stream\n\n\n\n\n\n\n" +
                "x-ms-date:Wed, 01 Jan 2020 00:00:00 GMT\n" +
                "x-ms-version:2020-04-08\n" +
                "/devstoreaccount1/c/b.bak\nblockid:AAAAAA==\ncomp:block",
                canonical);
        }

        [Fact]
        public void Authorize_AddsWellformedHeader()
        {
            Dictionary<string, string> headers = AzureSigner.Authorize(
                "GET", null, null, "devstoreaccount1", "c/b.bak", null,
                "devstoreaccount1",
                "Eby8vdM02xNOcqFlqUwJPLlmEtlCDXJ1OUzFT50uSRZ6IFsuFq2UVErCz4I6tq/K1SZFPTOtr/KBHBeksoGMGw==",
                new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc));
            Assert.StartsWith("SharedKey devstoreaccount1:", headers["Authorization"]);
            Assert.Equal("Wed, 01 Jan 2020 00:00:00 GMT", headers["x-ms-date"]);
            Assert.Equal("2020-04-08", headers["x-ms-version"]);
        }
    }

    public class AzureStorageTests
    {
        private sealed class MockHandler : HttpMessageHandler
        {
            public readonly List<string> Seen = new List<string>();

            protected override Task<HttpResponseMessage> SendAsync(
                HttpRequestMessage request, CancellationToken cancellationToken)
            {
                Seen.Add(request.Method.Method + " " + request.RequestUri.PathAndQuery);
                string q = request.RequestUri.Query;
                if (request.Method.Method == "PUT" && q.Contains("comp=block"))
                {
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Created));
                }
                if (request.Method.Method == "PUT" && q.Contains("comp=blocklist"))
                {
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Created));
                }
                if (request.Method.Method == "PUT")
                {
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Created));
                }
                if (request.Method.Method == "GET" && q.Contains("comp=list"))
                {
                    HttpResponseMessage list = new HttpResponseMessage(HttpStatusCode.OK);
                    list.Content = new StringContent(
                        "<EnumerationResults><Blobs><Blob><Name>live/old.bak</Name>" +
                        "<Properties><Last-Modified>Wed, 01 Jan 2020 00:00:00 GMT</Last-Modified>" +
                        "</Properties></Blob></Blobs><NextMarker /></EnumerationResults>");
                    return Task.FromResult(list);
                }
                if (request.Method.Method == "GET")
                {
                    HttpResponseMessage get = new HttpResponseMessage(HttpStatusCode.OK);
                    get.Content = new ByteArrayContent(new byte[] { 1, 2, 3 });
                    return Task.FromResult(get);
                }
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent));
            }
        }

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
        public void BlockUpload_StagesBlocksAndCommits()
        {
            MockHandler handler = new MockHandler();
            AzureStorage storage = new AzureStorage(handler);
            Dictionary<string, List<string>> config = Config(
                "endpoint=http://127.0.0.1:10000/devstoreaccount1", "container=c",
                "blob=a.bak", "account=devstoreaccount1",
                "accountkey=QUJDREVGR0hJSktMTU5PUFFSU1RVVFVX", "blocksizeMB=1");
            using (System.IO.Stream writer = storage.GetBackupWriter(config)[0])
            {
                byte[] chunk = new byte[512 * 1024];
                for (int i = 0; i < 3; i++)
                {
                    writer.Write(chunk, 0, chunk.Length);
                }
            }
            Assert.Contains(handler.Seen, s => s.Contains("comp=block&"));
            Assert.Contains(handler.Seen, s => s.Contains("comp=blocklist"));
        }

        [Fact]
        public void Prune_DeletesOldMatchingBlobs()
        {
            MockHandler handler = new MockHandler();
            AzureStorage storage = new AzureStorage(handler);
            SqlSqueeze.Plugins.PruneResult result = storage.PruneOldFiles(Config(
                "endpoint=http://127.0.0.1:10000/devstoreaccount1", "container=c",
                "prefix=live/", "pattern=*.bak", "olderThanDays=30",
                "account=devstoreaccount1", "accountkey=QUJDREVGR0hJSktMTU5PUFFSU1RVVFVX"));
            Assert.Equal(1, result.DeletedFiles);
        }
    }
}
