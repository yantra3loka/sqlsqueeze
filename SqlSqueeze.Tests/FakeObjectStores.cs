// In-memory stand-ins for the parts of S3 and Azure Blob that the storage tests touch.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace SqlSqueeze.Tests
{
    /// <summary>An object store keyed by URL path, with the bits of S3 and Azure these tests touch.</summary>
    internal abstract class FakeStore : HttpMessageHandler
    {
        public readonly Dictionary<string, byte[]> Objects = new Dictionary<string, byte[]>();
        public readonly Dictionary<string, DateTime> Modified = new Dictionary<string, DateTime>();
        public readonly List<string> Deleted = new List<string>();

        public void Add(string path, string text, DateTime modifiedUtc)
        {
            Objects[path] = Encoding.UTF8.GetBytes(text);
            Modified[path] = modifiedUtc;
        }

        protected static HttpResponseMessage Status(HttpStatusCode code, string body = null)
        {
            HttpResponseMessage r = new HttpResponseMessage(code);
            if (body != null)
            {
                r.Content = new StringContent(body);
            }
            return r;
        }

        protected static byte[] Body(HttpRequestMessage request)
        {
            return request.Content == null ? new byte[0] : request.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult();
        }
    }

    internal sealed class FakeS3 : FakeStore
    {
        public readonly List<string> Completed = new List<string>();
        public readonly List<string> AbortedUploads = new List<string>();
        public readonly List<int> PartPuts = new List<int>();     // every part number PUT, in order
        public int UploadsStarted;
        public int FailPartNumber;   // answer this part number with a 500

        private class Upload
        {
            public string Path;
            public DateTime Initiated;
            public SortedDictionary<int, byte[]> Parts = new SortedDictionary<int, byte[]>();
        }

        private readonly Dictionary<string, Upload> mUploads = new Dictionary<string, Upload>();

        /// <summary>Uploads that were started and neither completed nor aborted.</summary>
        public int OpenUploads { get { return mUploads.Count; } }

        private static string Md5(byte[] b)
        {
            return Convert.ToHexString(System.Security.Cryptography.MD5.HashData(b)).ToLowerInvariant();
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string path = Uri.UnescapeDataString(request.RequestUri.AbsolutePath);
            string query = request.RequestUri.Query;
            string method = request.Method.Method;
            bool bucketLevel = path.TrimEnd('/').Count(c => c == '/') == 1;
            HttpResponseMessage r;

            if (method == "HEAD" && bucketLevel)
            {
                r = Status(HttpStatusCode.OK);
            }
            else if (method == "POST" && query.Contains("uploads"))
            {
                UploadsStarted++;
                string id = "U" + UploadsStarted;
                mUploads[id] = new Upload { Path = path, Initiated = DateTime.UtcNow.AddSeconds(UploadsStarted) };
                r = Status(HttpStatusCode.OK, "<InitiateMultipartUploadResult><UploadId>" + id + "</UploadId></InitiateMultipartUploadResult>");
            }
            else if (method == "GET" && bucketLevel && query.Contains("uploads"))
            {
                StringBuilder xml = new StringBuilder("<ListMultipartUploadsResult>");
                string prefix = Regex.Match(query, @"prefix=([^&]*)").Success ? Uri.UnescapeDataString(Regex.Match(query, @"prefix=([^&]*)").Groups[1].Value) : string.Empty;
                string bucketPrefix = "/" + path.Trim('/') + "/";
                foreach (KeyValuePair<string, Upload> u in mUploads.Where(u => u.Value.Path.StartsWith(bucketPrefix + prefix)))
                {
                    xml.Append("<Upload><Key>").Append(u.Value.Path.Substring(bucketPrefix.Length)).Append("</Key><UploadId>").Append(u.Key)
                       .Append("</UploadId><Initiated>").Append(u.Value.Initiated.ToString("o")).Append("</Initiated></Upload>");
                }
                xml.Append("<IsTruncated>false</IsTruncated></ListMultipartUploadsResult>");
                r = Status(HttpStatusCode.OK, xml.ToString());
            }
            else if (method == "GET" && query.Contains("uploadId"))
            {
                string id = Regex.Match(query, @"uploadId=([^&]+)").Groups[1].Value;
                if (!mUploads.ContainsKey(id))
                {
                    r = Status(HttpStatusCode.NotFound, "<Error><Code>NoSuchUpload</Code></Error>");
                }
                else
                {
                    StringBuilder xml = new StringBuilder("<ListPartsResult>");
                    foreach (KeyValuePair<int, byte[]> part in mUploads[id].Parts)
                    {
                        xml.Append("<Part><PartNumber>").Append(part.Key).Append("</PartNumber><ETag>\"").Append(Md5(part.Value))
                           .Append("\"</ETag><Size>").Append(part.Value.Length).Append("</Size></Part>");
                    }
                    xml.Append("<IsTruncated>false</IsTruncated></ListPartsResult>");
                    r = Status(HttpStatusCode.OK, xml.ToString());
                }
            }
            else if (method == "PUT" && query.Contains("partNumber"))
            {
                int part = int.Parse(Regex.Match(query, @"partNumber=(\d+)").Groups[1].Value);
                string id = Regex.Match(query, @"uploadId=([^&]+)").Groups[1].Value;
                PartPuts.Add(part);
                if (FailPartNumber == part)
                {
                    return Task.FromResult(Status(HttpStatusCode.InternalServerError, "boom"));
                }
                if (!mUploads.ContainsKey(id))
                {
                    return Task.FromResult(Status(HttpStatusCode.NotFound, "<Error><Code>NoSuchUpload</Code></Error>"));
                }
                byte[] body = Body(request);
                mUploads[id].Parts[part] = body;
                r = Status(HttpStatusCode.OK);
                r.Headers.TryAddWithoutValidation("ETag", "\"" + Md5(body) + "\"");
            }
            else if (method == "POST" && query.Contains("uploadId"))
            {
                string id = Regex.Match(query, @"uploadId=([^&]+)").Groups[1].Value;
                Upload upload = mUploads[id];
                Objects[upload.Path] = upload.Parts.Values.SelectMany(b => b).ToArray();
                Modified[upload.Path] = DateTime.UtcNow;
                Completed.Add(upload.Path);
                mUploads.Remove(id);
                r = Status(HttpStatusCode.OK, "<CompleteMultipartUploadResult><ETag>\"DONE\"</ETag></CompleteMultipartUploadResult>");
            }
            else if (method == "GET" && query.Contains("list-type=2"))
            {
                StringBuilder xml = new StringBuilder("<ListBucketResult>");
                string bucketPrefix = "/" + path.Trim('/') + "/";
                foreach (KeyValuePair<string, byte[]> o in Objects.Where(o => o.Key.StartsWith(bucketPrefix)).OrderBy(o => o.Key))
                {
                    xml.Append("<Contents><Key>").Append(o.Key.Substring(bucketPrefix.Length)).Append("</Key><LastModified>")
                       .Append(Modified[o.Key].ToString("o")).Append("</LastModified></Contents>");
                }
                xml.Append("<NextContinuationToken /></ListBucketResult>");
                r = Status(HttpStatusCode.OK, xml.ToString());
            }
            else if (method == "GET")
            {
                r = Objects.ContainsKey(path)
                    ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Objects[path]) }
                    : Status(HttpStatusCode.NotFound, "<Error><Code>NoSuchKey</Code></Error>");
            }
            else if (method == "DELETE" && query.Contains("uploadId"))
            {
                string id = Regex.Match(query, @"uploadId=([^&]+)").Groups[1].Value;
                AbortedUploads.Add(path);
                mUploads.Remove(id);
                r = Status(HttpStatusCode.NoContent);
            }
            else if (method == "DELETE")
            {
                Deleted.Add(path);
                Objects.Remove(path);
                r = Status(HttpStatusCode.NoContent);
            }
            else
            {
                r = Status(HttpStatusCode.BadRequest, "unexpected " + method + " " + path);
            }
            return Task.FromResult(r);
        }
    }

    internal sealed class FakeAzure : FakeStore
    {
        public int FailBlockNumber;   // answer the Nth staged block with a 500
        public int StagedBlocks { get { return mBlocksSeen; } }
        public readonly List<string> BlockPuts = new List<string>();   // the id of every block PUT, in order
        private int mBlocksSeen;
        private readonly Dictionary<string, Dictionary<string, byte[]>> mBlocks = new Dictionary<string, Dictionary<string, byte[]>>();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string path = Uri.UnescapeDataString(request.RequestUri.AbsolutePath);
            string query = Uri.UnescapeDataString(request.RequestUri.Query);
            string method = request.Method.Method;
            HttpResponseMessage r;

            if (method == "PUT" && query.Contains("restype=container"))
            {
                r = Status(HttpStatusCode.Created);
            }
            else if (method == "PUT" && query.Contains("comp=blocklist"))
            {
                string xml = Encoding.UTF8.GetString(Body(request));
                List<string> ids = Regex.Matches(xml, "<Latest>([^<]+)</Latest>").Cast<Match>().Select(m => m.Groups[1].Value).ToList();
                Objects[path] = ids.SelectMany(id => mBlocks[path][id]).ToArray();
                Modified[path] = DateTime.UtcNow;
                r = Status(HttpStatusCode.Created);
            }
            else if (method == "PUT" && query.Contains("comp=block"))
            {
                mBlocksSeen++;
                BlockPuts.Add(Regex.Match(query, @"blockid=([^&]+)").Groups[1].Value);
                if (FailBlockNumber == mBlocksSeen)
                {
                    return Task.FromResult(Status(HttpStatusCode.InternalServerError, "boom"));
                }
                string id = Regex.Match(query, @"blockid=([^&]+)").Groups[1].Value;
                if (!mBlocks.ContainsKey(path))
                {
                    mBlocks[path] = new Dictionary<string, byte[]>();
                }
                mBlocks[path][id] = Body(request);
                r = Status(HttpStatusCode.Created);
            }
            else if (method == "GET" && query.Contains("comp=blocklist"))
            {
                StringBuilder xml = new StringBuilder("<BlockList><UncommittedBlocks>");
                if (mBlocks.ContainsKey(path))
                {
                    foreach (KeyValuePair<string, byte[]> block in mBlocks[path])
                    {
                        xml.Append("<Block><Name>").Append(block.Key).Append("</Name><Size>").Append(block.Value.Length).Append("</Size></Block>");
                    }
                }
                xml.Append("</UncommittedBlocks></BlockList>");
                r = Status(HttpStatusCode.OK, xml.ToString());
            }
            else if (method == "GET" && query.Contains("comp=list"))
            {
                StringBuilder xml = new StringBuilder("<EnumerationResults><Blobs>");
                string containerPrefix = "/" + path.Trim('/') + "/";
                foreach (KeyValuePair<string, byte[]> o in Objects.Where(o => o.Key.StartsWith(containerPrefix)).OrderBy(o => o.Key))
                {
                    xml.Append("<Blob><Name>").Append(o.Key.Substring(containerPrefix.Length)).Append("</Name><Properties><Last-Modified>")
                       .Append(Modified[o.Key].ToString("R")).Append("</Last-Modified></Properties></Blob>");
                }
                xml.Append("</Blobs><NextMarker /></EnumerationResults>");
                r = Status(HttpStatusCode.OK, xml.ToString());
            }
            else if (method == "GET")
            {
                r = Objects.ContainsKey(path)
                    ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Objects[path]) }
                    : Status(HttpStatusCode.NotFound, "<Error><Code>BlobNotFound</Code></Error>");
            }
            else if (method == "DELETE")
            {
                Deleted.Add(path);
                Objects.Remove(path);
                r = Status(HttpStatusCode.Accepted);
            }
            else
            {
                r = Status(HttpStatusCode.BadRequest, "unexpected " + method + " " + path);
            }
            return Task.FromResult(r);
        }
    }


    internal sealed class AlwaysStatus : HttpMessageHandler
    {
        private readonly HttpStatusCode mCode;
        public AlwaysStatus(HttpStatusCode code) { mCode = code; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromResult(new HttpResponseMessage(mCode) { Content = new StringContent("denied") });
        }
    }
}
