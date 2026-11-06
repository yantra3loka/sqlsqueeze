// S3 SigV4 signing vectors (AWS documentation examples) and request
// building. No server needed; HTTP flows use a mocked handler.

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
    public class S3SignerTests
    {
        [Fact]
        public void SignGet_MatchesAwsDocumentationVector()
        {
            // https://docs.aws.amazon.com/AmazonS3/latest/API/sig-v4-authenticating-requests.html
            Dictionary<string, string> extra = new Dictionary<string, string>();
            extra["range"] = "bytes=0-9";
            Dictionary<string, string> signed = S3Signer.Sign(
                "GET",
                new Uri("https://examplebucket.s3.amazonaws.com/test.txt"),
                "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855",
                "AKIAIOSFODNN7EXAMPLE",
                "wJalrXUtnFEMI/K7MDENG/bPxRfiCYEXAMPLEKEY",
                "us-east-1",
                new DateTime(2013, 5, 24, 0, 0, 0, DateTimeKind.Utc),
                extra);
            Assert.Equal("20130524T000000Z", signed["x-amz-date"]);
            // S3 requires the payload hash as a signed header (x-amz-content-sha256), and the
            // signature below is the one printed on that AWS page for this request.
            Assert.Equal("e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855", signed["x-amz-content-sha256"]);
            Assert.Equal(
                "AWS4-HMAC-SHA256 Credential=AKIAIOSFODNN7EXAMPLE/20130524/us-east-1/s3/aws4_request, " +
                "SignedHeaders=host;range;x-amz-content-sha256;x-amz-date, " +
                "Signature=f0e8bdb87c964420e857bd35b5d6ed310bd44f0170aba48dd91039c6036bdb41",
                signed["Authorization"]);
        }

        [Fact]
        public void SignList_EncodesTheQueryOnce()
        {
            // A slash, a space and a plus in the prefix: the canonical query string must carry
            // %2F, %20 and %2B (encoded once). The expected signature was computed independently
            // (Python, from the SigV4 specification), not with this code.
            Dictionary<string, string> signed = S3Signer.Sign(
                "GET",
                new Uri("https://examplebucket.s3.amazonaws.com/?list-type=2&prefix=db%2Fapp%2F%20sp%2Bx&max-keys=1000"),
                "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855",
                "AKIAIOSFODNN7EXAMPLE",
                "wJalrXUtnFEMI/K7MDENG/bPxRfiCYEXAMPLEKEY",
                "us-east-1",
                new DateTime(2013, 5, 24, 0, 0, 0, DateTimeKind.Utc),
                null);
            Assert.EndsWith("Signature=af86fb9073155c88cf24c867a0796dfbc9ea361b08b27e10fcfc5a3d76cb8842", signed["Authorization"]);
        }

        [Fact]
        public void CanonicalUri_EncodesKeySegments()
        {
            Assert.Equal("/bkt/a%20b/c%2Bd/e.txt",
                S3Signer.CanonicalUri("/bkt", "a b/c+d/e.txt"));
        }

        [Fact]
        public void PresignedUrl_ContainsSignature()
        {
            string url = S3Signer.PresignedGetUrl(
                "http://127.0.0.1:9000", "bkt", "a/b.bak",
                "AKID", "SECRET", "us-east-1", 3600);
            Assert.StartsWith("http://127.0.0.1:9000/bkt/a/b.bak?", url);
            Assert.Contains("X-Amz-Signature=", url);
            Assert.Contains("X-Amz-Credential=", url);
        }
    }

    public class S3StorageTests
    {
        private sealed class MockHandler : HttpMessageHandler
        {
            public readonly List<HttpRequestMessage> Seen = new List<HttpRequestMessage>();

            protected override Task<HttpResponseMessage> SendAsync(
                HttpRequestMessage request, CancellationToken cancellationToken)
            {
                Seen.Add(request);
                string path = request.RequestUri.AbsolutePath;
                string method = request.Method.Method;
                HttpResponseMessage response;
                if (method == "POST" && request.RequestUri.Query.Contains("uploads"))
                {
                    response = new HttpResponseMessage(HttpStatusCode.OK);
                    response.Content = new StringContent(
                        "<InitiateMultipartUploadResult><UploadId>UPL1</UploadId></InitiateMultipartUploadResult>");
                }
                else if (method == "PUT")
                {
                    response = new HttpResponseMessage(HttpStatusCode.OK);
                    response.Headers.TryAddWithoutValidation("ETag", "\"E" + Seen.Count + "\"");
                }
                else if (method == "POST")
                {
                    response = new HttpResponseMessage(HttpStatusCode.OK);
                    response.Content = new StringContent("<CompleteMultipartUploadResult><ETag>\"DONE\"</ETag></CompleteMultipartUploadResult>");
                }
                else if (method == "DELETE")
                {
                    response = new HttpResponseMessage(HttpStatusCode.NoContent);
                }
                else
                {
                    response = new HttpResponseMessage(HttpStatusCode.NotFound);
                }
                return Task.FromResult(response);
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

        private sealed class ListHandler : HttpMessageHandler
        {
            public readonly List<string> Deleted = new List<string>();

            protected override Task<HttpResponseMessage> SendAsync(
                HttpRequestMessage request, CancellationToken cancellationToken)
            {
                if (request.Method.Method == "GET")
                {
                    HttpResponseMessage list = new HttpResponseMessage(HttpStatusCode.OK);
                    list.Content = new StringContent(
                        "<ListBucketResult><Contents><Key>live/old.bak</Key>" +
                        "<LastModified>2020-01-01T00:00:00.000Z</LastModified></Contents>" +
                        "<Contents><Key>live/new.bak</Key>" +
                        "<LastModified>2099-01-01T00:00:00.000Z</LastModified></Contents>" +
                        "<Contents><Key>live/notes.txt</Key>" +
                        "<LastModified>2020-01-01T00:00:00.000Z</LastModified></Contents>" +
                        "<NextContinuationToken /></ListBucketResult>");
                    return Task.FromResult(list);
                }
                Deleted.Add(request.RequestUri.AbsolutePath);
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent));
            }
        }

        [Fact]
        public void Prune_DeletesOnlyOldMatchingKeys()
        {
            ListHandler handler = new ListHandler();
            S3Storage storage = new S3Storage(handler);
            SqlSqueeze.Plugins.PruneResult result = storage.PruneOldFiles(Config(
                "endpoint=http://127.0.0.1:9000", "bucket=bkt", "prefix=live/",
                "pattern=*.bak", "olderThanDays=30",
                "region=us-east-1", "accesskey=AK", "secretkey=SK"));
            Assert.Equal(1, result.DeletedFiles);
            // the backup, then the manifest that goes with it (deleting a missing key is a no-op in S3)
            Assert.Equal(2, handler.Deleted.Count);
            Assert.Contains("/bkt/live/old.bak", handler.Deleted[0]);
            Assert.Contains("/bkt/live/old.bak.manifest.json", handler.Deleted[1]);
        }

        [Fact]
        public void MultipartUpload_UploadsPartsAndCompletes()
        {
            MockHandler handler = new MockHandler();
            S3Storage storage = new S3Storage(handler);
            Dictionary<string, List<string>> config = Config(
                "endpoint=http://127.0.0.1:9000", "bucket=bkt", "key=a.bak",
                "region=us-east-1", "accesskey=AK", "secretkey=SK", "partsizeMB=5");
            using (System.IO.Stream writer = storage.GetBackupWriter(config)[0])
            {
                byte[] chunk = new byte[1024 * 1024];
                for (int i = 0; i < 6; i++)
                {
                    writer.Write(chunk, 0, chunk.Length);
                }
            }
            Assert.True(handler.Seen.Count >= 4);
            Assert.Contains(handler.Seen, r =>
                r.Method.Method == "POST" && r.RequestUri.Query.Contains("uploads"));
            Assert.Contains(handler.Seen, r =>
                r.Method.Method == "POST" && r.RequestUri.Query.Contains("uploadId"));
        }
    }
}
