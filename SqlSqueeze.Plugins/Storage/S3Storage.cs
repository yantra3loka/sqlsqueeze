/*
    This file is part of SqlSqueeze.

    SqlSqueeze is free software: you can redistribute it and/or modify
    it under the terms of the GNU General Public License as published by
    the Free Software Foundation, either version 3 of the License, or
    (at your option) any later version.

    SqlSqueeze is distributed in the hope that it will be useful,
    but WITHOUT ANY WARRANTY; without even the implied warranty of
    MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
    GNU General Public License for more details.

    You should have received a copy of the GNU General Public License
    along with SqlSqueeze.  If not, see <http://www.gnu.org/licenses/>.
*/

using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;

namespace SqlSqueeze.Plugins.Storage
{
    /// <summary>
    /// S3-compatible object storage (AWS S3, MinIO, and other S3 clones).
    /// Uploads always use multipart (buffered parts, so arbitrarily large
    /// backups stream), downloads stream. The bucket is created when
    /// missing. Secrets come from secretkeyenv=, never the command line.
    /// </summary>
    public class S3Storage : IBackupStorage, IPrunableStorage, IManifestStorage, IResumableStorage
    {
        private static Dictionary<string, ParameterInfo> mBackupParamSchema;
        private static Dictionary<string, ParameterInfo> mPruneParamSchema;

        static S3Storage()
        {
            mBackupParamSchema = new Dictionary<string, ParameterInfo>(StringComparer.InvariantCultureIgnoreCase);
            mBackupParamSchema.Add("endpoint", new ParameterInfo(false, true));
            mBackupParamSchema.Add("bucket", new ParameterInfo(false, true));
            mBackupParamSchema.Add("key", new ParameterInfo(true, true));
            mBackupParamSchema.Add("region", new ParameterInfo(false, false));
            mBackupParamSchema.Add("accesskey", new ParameterInfo(false, true));
            mBackupParamSchema.Add("secretkey", new ParameterInfo(false, false));
            mBackupParamSchema.Add("secretkeyenv", new ParameterInfo(false, false));
            mBackupParamSchema.Add("partsizeMB", new ParameterInfo(false, false));
            mBackupParamSchema.Add("writeretries", new ParameterInfo(false, false));
            mBackupParamSchema.Add("writeretrybackoffms", new ParameterInfo(false, false));

            mPruneParamSchema = new Dictionary<string, ParameterInfo>(StringComparer.InvariantCultureIgnoreCase);
            mPruneParamSchema.Add("endpoint", new ParameterInfo(false, true));
            mPruneParamSchema.Add("bucket", new ParameterInfo(false, true));
            mPruneParamSchema.Add("prefix", new ParameterInfo(false, false));
            mPruneParamSchema.Add("pattern", new ParameterInfo(false, false));
            mPruneParamSchema.Add("olderThanDays", new ParameterInfo(false, true));
            mPruneParamSchema.Add("region", new ParameterInfo(false, false));
            mPruneParamSchema.Add("accesskey", new ParameterInfo(false, true));
            mPruneParamSchema.Add("secretkey", new ParameterInfo(false, false));
            mPruneParamSchema.Add("secretkeyenv", new ParameterInfo(false, false));
        }

        private readonly HttpMessageHandler mHandler;
        private readonly List<string> mAbortUploads = new List<string>();
        private readonly List<string> mWrittenKeys = new List<string>();
        private readonly List<KeyValuePair<S3Endpoint, string>> mResumableKeys = new List<KeyValuePair<S3Endpoint, string>>();
        private const string ManifestSuffix = ".manifest.json";

        public S3Storage() : this(null)
        {
        }

        /// <summary>
        /// Test seam: inject the HTTP transport (mocked in S3StorageTests).
        /// </summary>
        public S3Storage(HttpMessageHandler handler)
        {
            mHandler = handler;
        }

        public string Name { get { return "s3"; } }

        public int GetNumberOfDevices(Dictionary<string, List<string>> config)
        {
            ParameterInfo.ValidateParams(mBackupParamSchema, config);
            return config["key"].Count;
        }

        public Stream[] GetBackupWriter(Dictionary<string, List<string>> config)
        {
            return CreateWriters(config, false);
        }

        /// <summary>
        /// Writers that continue an unfinished multipart upload of the same key if the server has
        /// one, skipping each part it already holds with identical content (by MD5). Use them only
        /// for a source that can be read again from the start.
        /// </summary>
        public Stream[] GetResumableBackupWriter(Dictionary<string, List<string>> config)
        {
            return CreateWriters(config, true);
        }

        private Stream[] CreateWriters(Dictionary<string, List<string>> config, bool resume)
        {
            ParameterInfo.ValidateParams(mBackupParamSchema, config);
            S3Endpoint endpoint = S3Endpoint.FromConfig(config);
            EnsureBucket(endpoint);

            int writeRetries = ResolveInt(config, "writeretries", 1, 1);
            int writeRetryBackoffMs = ResolveInt(config, "writeretrybackoffms", 1000, 0);
            int partSizeMB = ResolveInt(config, "partsizeMB", 8, 5);

            Console.WriteLine("s3:");
            Console.WriteLine("\tendpoint=" + endpoint.BaseUrl);
            Console.WriteLine("\tbucket=" + endpoint.Bucket);

            List<Stream> results = new List<Stream>(config["key"].Count);
            mWrittenKeys.Clear();
            foreach (string rawKey in config["key"])
            {
                string key = LocalStorage.ExpandDateTokens(rawKey);
                mWrittenKeys.Add(key);
                Console.WriteLine("\tkey=" + key);
                if (resume)
                {
                    mResumableKeys.Add(new KeyValuePair<S3Endpoint, string>(endpoint, key));
                }
                Stream writer = new S3MultipartUploadStream(
                    CreateClient(endpoint), endpoint, key, partSizeMB * 1024 * 1024, resume);
                if (writeRetries > 1)
                {
                    writer = new WriteRetryStream(writer, writeRetries, writeRetryBackoffMs);
                }
                results.Add(writer);
            }
            return results.ToArray();
        }

        public Stream[] GetRestoreReader(Dictionary<string, List<string>> config, out long estimatedTotalBytes)
        {
            ParameterInfo.ValidateParams(mBackupParamSchema, config);
            S3Endpoint endpoint = S3Endpoint.FromConfig(config);

            List<Stream> results = new List<Stream>(config["key"].Count);
            estimatedTotalBytes = 0;
            foreach (string key in config["key"])
            {
                HttpClient client = CreateClient(endpoint);
                HttpRequestMessage request = SignedRequest(endpoint, "GET", key, null,
                    "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855");
                HttpResponseMessage response = Send(client, request);
                if (response.Content.Headers.ContentLength.HasValue)
                {
                    estimatedTotalBytes += response.Content.Headers.ContentLength.Value;
                }
                results.Add(new S3DownloadStream(client, response));
            }
            return results.ToArray();
        }

        /// <summary>
        /// Removes the unfinished uploads that resumable writers left behind (a copy that gave up).
        /// Nothing to do after a plain backup: its failed uploads are aborted as they fail.
        /// </summary>
        public void CleanupOnAbort()
        {
            lock (mAbortUploads)
            {
                mAbortUploads.Clear();
            }
            foreach (KeyValuePair<S3Endpoint, string> pending in mResumableKeys)
            {
                try
                {
                    using (HttpClient client = CreateClient(pending.Key))
                    {
                        foreach (KeyValuePair<string, DateTime> upload in S3MultipartUploadStream.ListUploads(client, pending.Key, pending.Value))
                        {
                            HttpRequestMessage request = SignedRequest(pending.Key, "DELETE", pending.Value,
                                "uploadId=" + Uri.EscapeDataString(upload.Key), "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855");
                            using (Send(client, request, false))
                            {
                            }
                        }
                    }
                }
                catch (Exception)
                {
                    // best effort: an orphaned upload costs a little storage but is never an object
                }
            }
            mResumableKeys.Clear();
        }

        public PruneResult PruneOldFiles(Dictionary<string, List<string>> config)
        {
            ParameterInfo.ValidateParams(mPruneParamSchema, config);
            S3Endpoint endpoint = S3Endpoint.FromConfig(config);
            string prefix = FirstOr(config, "prefix", string.Empty);
            string pattern = FirstOr(config, "pattern", "*");
            int olderThanDays = ResolveInt(config, "olderThanDays", 0, 0);
            DateTime cutoff = DateTime.UtcNow.AddDays(-olderThanDays);
            Regex matcher = new Regex("^" + Regex.Escape(pattern)
                .Replace("\\*", ".*").Replace("\\?", ".") + "$");

            PruneResult result = new PruneResult();
            string continuation = null;
            do
            {
                string query = "list-type=2&prefix=" + Uri.EscapeDataString(prefix) + "&max-keys=1000"
                    + (continuation == null ? string.Empty : "&continuation-token=" + Uri.EscapeDataString(continuation));
                HttpResponseMessage list = SignedGet(endpoint, query);
                string xml = ReadBody(list);
                continuation = XmlInner(xml, "NextContinuationToken");
                foreach (XmlElement content in XmlElements(xml, "Contents"))
                {
                    string key = XmlInner(content.OuterXml, "Key");
                    if (key.EndsWith(ManifestSuffix, StringComparison.Ordinal))
                    {
                        // a manifest goes with the backup it describes; its backup removes it
                        continue;
                    }
                    DateTime modified = DateTime.Parse(XmlInner(content.OuterXml, "LastModified")).ToUniversalTime();
                    string name = key.Contains("/") ? key.Substring(key.LastIndexOf('/') + 1) : key;
                    if (matcher.IsMatch(name) && modified < cutoff)
                    {
                        DeleteKey(endpoint, key);
                        DeleteKey(endpoint, key + ManifestSuffix);
                        result.DeletedFiles++;
                        result.DeletedPaths.Add(endpoint.Bucket + "/" + key);
                    }
                }
            } while (!string.IsNullOrEmpty(continuation));
            return result;
        }

        #region IManifestStorage Members

        public IList<string> GetFileNames(Dictionary<string, List<string>> config)
        {
            List<string> keys = mWrittenKeys.Count > 0 ? mWrittenKeys : config["key"];
            List<string> names = new List<string>();
            foreach (string key in keys)
            {
                names.Add(key.Contains("/") ? key.Substring(key.LastIndexOf('/') + 1) : key);
            }
            return names;
        }

        public Dictionary<string, List<string>> ResolveConfig(Dictionary<string, List<string>> config)
        {
            Dictionary<string, List<string>> resolved = StorageConfigs.Clone(config);
            if (mWrittenKeys.Count > 0)
            {
                resolved["key"] = new List<string>(mWrittenKeys);
            }
            return resolved;
        }

        public void WriteManifest(Dictionary<string, List<string>> config, string json)
        {
            ParameterInfo.ValidateParams(mBackupParamSchema, config);
            S3Endpoint endpoint = S3Endpoint.FromConfig(config);
            byte[] bytes = System.Text.Encoding.UTF8.GetBytes(json);
            using (Stream writer = new S3MultipartUploadStream(CreateClient(endpoint), endpoint, ManifestKey(config), 5 * 1024 * 1024))
            {
                writer.Write(bytes, 0, bytes.Length);
            }
        }

        public string ReadManifest(Dictionary<string, List<string>> config)
        {
            ParameterInfo.ValidateParams(mBackupParamSchema, config);
            S3Endpoint endpoint = S3Endpoint.FromConfig(config);
            HttpRequestMessage request = SignedRequest(endpoint, "GET", ManifestKey(config), null,
                "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855");
            using (HttpResponseMessage response = Send(CreateClient(endpoint), request, false))
            {
                if (response.StatusCode == HttpStatusCode.NotFound)
                {
                    return null;
                }
                return ReadBody(response);
            }
        }

        // The manifest sits next to the first object. After GetBackupWriter the
        // date tokens in the configured key are already expanded.
        private string ManifestKey(Dictionary<string, List<string>> config)
        {
            return (mWrittenKeys.Count > 0 ? mWrittenKeys[0] : config["key"][0]) + ManifestSuffix;
        }

        #endregion

        public string CommandLineHelp
        {
            get
            {
                return @"s3 Usage:
    s3(endpoint=<url>;bucket=<name>;key=<object>;region=<r>;accesskey=<id>;secretkeyenv=<VAR>)
key= may repeat for striped backups and accepts {date} tokens like local path.
secretkeyenv= is recommended (command lines are visible in process lists).
Unsigned S3 clones (MinIO, moto) work: point endpoint at them, any region.
Uploads always use multipart (buffered 8 MB parts by default: partsizeMB).
Prune: s3(endpoint=...;bucket=...;prefix=...;pattern=*.bak.zst;olderThanDays=30).";
            }
        }

        internal HttpClient CreateClient(S3Endpoint endpoint)
        {
            HttpClient client = mHandler == null
                ? new HttpClient()
                : new HttpClient(mHandler, false);
            client.BaseAddress = new Uri(endpoint.BaseUrl + "/");
            client.Timeout = TimeSpan.FromMinutes(30);
            return client;
        }

        internal void EnsureBucket(S3Endpoint endpoint)
        {
            HttpClient client = CreateClient(endpoint);
            HttpRequestMessage head = SignedRequest(endpoint, "HEAD", string.Empty, null,
                "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855");
            using (HttpResponseMessage response = Send(client, head, false))
            {
                if (response.StatusCode == HttpStatusCode.OK)
                {
                    return;
                }
                if (response.StatusCode != HttpStatusCode.NotFound)
                {
                    ThrowForStatus("HeadBucket", response);
                }
            }
            HttpRequestMessage put = SignedRequest(endpoint, "PUT", string.Empty, null,
                "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855");
            using (HttpResponseMessage created = Send(client, put, false))
            {
                if (created.StatusCode != HttpStatusCode.OK
                    && created.StatusCode != HttpStatusCode.Conflict)
                {
                    ThrowForStatus("CreateBucket", created);
                }
            }
        }

        internal void DeleteKey(S3Endpoint endpoint, string key)
        {
            HttpClient client = CreateClient(endpoint);
            HttpRequestMessage request = SignedRequest(endpoint, "DELETE", key, null,
                "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855");
            using (HttpResponseMessage response = Send(client, request, false))
            {
                if (response.StatusCode != HttpStatusCode.OK
                    && response.StatusCode != HttpStatusCode.NoContent)
                {
                    ThrowForStatus("DeleteObject", response);
                }
            }
        }

        internal HttpResponseMessage SignedGet(S3Endpoint endpoint, string query)
        {
            HttpClient client = CreateClient(endpoint);
            Uri uri = new Uri(endpoint.BaseUrl + "/" + endpoint.Bucket + "?" + query);
            Dictionary<string, string> signed = S3Signer.Sign("GET", uri,
                "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855",
                endpoint.AccessKey, endpoint.SecretKey, endpoint.Region, DateTime.UtcNow, null);
            HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Get, uri);
            foreach (KeyValuePair<string, string> header in signed)
            {
                request.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }
            return Send(client, request);
        }

        internal static HttpRequestMessage SignedRequest(S3Endpoint endpoint, string method,
            string key, string query, string payloadHash)
        {
            string path = key == null ? "/" : S3Signer.CanonicalPath(endpoint.Bucket, key);
            Uri uri = new Uri(endpoint.BaseUrl + path
                + (string.IsNullOrEmpty(query) ? string.Empty : "?" + query));
            Dictionary<string, string> signed = S3Signer.Sign(method, uri, payloadHash,
                endpoint.AccessKey, endpoint.SecretKey, endpoint.Region, DateTime.UtcNow, null);
            HttpRequestMessage request = new HttpRequestMessage(new HttpMethod(method), uri);
            foreach (KeyValuePair<string, string> header in signed)
            {
                request.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }
            return request;
        }

        internal static HttpResponseMessage Send(HttpClient client, HttpRequestMessage request)
        {
            return Send(client, request, true);
        }

        internal static HttpResponseMessage Send(HttpClient client, HttpRequestMessage request, bool throwOnError)
        {
            try
            {
                HttpResponseMessage response = client.SendAsync(request,
                    HttpCompletionOption.ResponseHeadersRead).GetAwaiter().GetResult();
                if (throwOnError && !response.IsSuccessStatusCode)
                {
                    ThrowForStatus(request.Method + " " + request.RequestUri, response);
                }
                return response;
            }
            catch (HttpRequestException e)
            {
                throw new IOException("s3: network error for " + request.Method
                    + " " + request.RequestUri + ": " + e.Message, e);
            }
        }

        internal static void ThrowForStatus(string action, HttpResponseMessage response)
        {
            string body = string.Empty;
            try
            {
                body = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                if (body.Length > 300)
                {
                    body = body.Substring(0, 300);
                }
            }
            catch (Exception)
            {
            }
            string message = "s3: " + action + " failed: " + (int)response.StatusCode
                + " " + response.ReasonPhrase + " " + body.Trim();
            if ((int)response.StatusCode >= 500)
            {
                throw new IOException(message);
            }
            throw new InvalidOperationException(message);
        }

        internal static string ReadBody(HttpResponseMessage response)
        {
            if (!response.IsSuccessStatusCode)
            {
                ThrowForStatus("GET", response);
            }
            return response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
        }

        internal static string XmlInner(string xml, string tag)
        {
            XmlDocument document = new XmlDocument();
            document.LoadXml(xml);
            XmlNodeList nodes = document.GetElementsByTagName(tag);
            if (nodes.Count == 0)
            {
                return null;
            }
            return nodes[0].InnerText;
        }

        internal static List<XmlElement> XmlElements(string xml, string tag)
        {
            List<XmlElement> elements = new List<XmlElement>();
            XmlDocument document = new XmlDocument();
            document.LoadXml(xml);
            foreach (XmlNode node in document.GetElementsByTagName(tag))
            {
                if (node is XmlElement)
                {
                    elements.Add((XmlElement)node);
                }
            }
            return elements;
        }

        internal static string FirstOr(Dictionary<string, List<string>> config, string name, string fallback)
        {
            List<string> values;
            if (config.TryGetValue(name, out values) && values.Count > 0 && !string.IsNullOrEmpty(values[0]))
            {
                return values[0];
            }
            return fallback;
        }

        internal static int ResolveInt(Dictionary<string, List<string>> config, string name, int fallback, int minimum)
        {
            List<string> values;
            if (!config.TryGetValue(name, out values) || values.Count == 0)
            {
                return fallback;
            }
            int parsed;
            if (!int.TryParse(values[0], out parsed) || parsed < minimum)
            {
                throw new ArgumentException("s3: " + name + " must be an integer >= " + minimum + ".");
            }
            return parsed;
        }

        internal sealed class S3Endpoint
        {
            public string BaseUrl;
            public string Bucket;
            public string Region;
            public string AccessKey;
            public string SecretKey;

            public static S3Endpoint FromConfig(Dictionary<string, List<string>> config)
            {
                S3Endpoint endpoint = new S3Endpoint();
                endpoint.BaseUrl = config["endpoint"][0].TrimEnd('/');
                endpoint.Bucket = config["bucket"][0];
                endpoint.Region = FirstOr(config, "region", "us-east-1");
                endpoint.AccessKey = config["accesskey"][0];
                List<string> env;
                if (config.TryGetValue("secretkeyenv", out env) && env.Count > 0 && !string.IsNullOrEmpty(env[0]))
                {
                    endpoint.SecretKey = Environment.GetEnvironmentVariable(env[0]);
                    if (string.IsNullOrEmpty(endpoint.SecretKey))
                    {
                        throw new ArgumentException("s3: environment variable is empty or missing: " + env[0]);
                    }
                }
                else if (config.TryGetValue("secretkey", out env) && env.Count > 0)
                {
                    endpoint.SecretKey = env[0];
                }
                else
                {
                    throw new ArgumentException("s3: secretkeyenv= (recommended) or secretkey= is required.");
                }
                return endpoint;
            }
        }

        internal sealed class S3MultipartUploadStream : Stream, IResumableStream
        {
            private readonly HttpClient mClient;
            private readonly S3Endpoint mEndpoint;
            private readonly string mKey;
            private readonly int mPartSize;
            private readonly MemoryStream mBuffer = new MemoryStream();
            private string mUploadId;
            private readonly List<string> mPartETags = new List<string>();
            private bool mDisposed;
            private bool mAborted;
            private bool mServerAborted;
            private bool mSuspended;
            private long mBytesSkipped;
            private readonly bool mResume;
            private Dictionary<int, string> mExistingParts;   // part number -> MD5 of the part the server already holds

            public S3MultipartUploadStream(HttpClient client, S3Endpoint endpoint, string key, int partSizeBytes, bool resume = false)
            {
                mClient = client;
                mEndpoint = endpoint;
                mKey = key;
                mPartSize = partSizeBytes;
                mResume = resume;
            }

            public override bool CanRead { get { return false; } }
            public override bool CanSeek { get { return false; } }
            public override bool CanWrite { get { return true; } }
            public override long Length { get { throw new NotSupportedException(); } }
            public override long Position
            {
                get { throw new NotSupportedException(); }
                set { throw new NotSupportedException(); }
            }

            public override void Write(byte[] buffer, int offset, int count)
            {
                if (mAborted || mSuspended)
                {
                    // the pipeline flushes its last bytes into the stream as it unwinds after a failure
                    return;
                }
                try
                {
                    mBuffer.Write(buffer, offset, count);
                    while (mBuffer.Length >= mPartSize)
                    {
                        UploadBufferedPart(false);
                    }
                }
                catch
                {
                    // A write that failed leaves a hole: this upload must never be completed. A resumable
                    // upload keeps what the server has, for the next attempt; any other is thrown away.
                    if (mResume)
                    {
                        mSuspended = true;
                    }
                    else
                    {
                        mAborted = true;
                    }
                    throw;
                }
            }

            public override void Flush()
            {
            }

            /// <summary>Discard the upload: nothing is completed on dispose, and any parts already sent are deleted.</summary>
            public void Abort()
            {
                mAborted = true;
                AbortOnServer();
            }

            public long BytesSkipped { get { return mBytesSkipped; } }

            /// <summary>Keep what the server has for a later attempt: nothing is completed or deleted.</summary>
            public void Suspend()
            {
                if (!mAborted)
                {
                    mSuspended = true;
                    mBuffer.SetLength(0);
                }
            }

            private void AbortOnServer()
            {
                if (mUploadId == null || mServerAborted)
                {
                    return;
                }
                mServerAborted = true;
                try
                {
                    HttpRequestMessage request = SignedRequest(mEndpoint, "DELETE", mKey,
                        "uploadId=" + Uri.EscapeDataString(mUploadId), "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855");
                    using (Send(mClient, request, false))
                    {
                    }
                }
                catch (Exception)
                {
                    // best effort: an orphaned upload costs a little storage but is never an object
                }
            }

            protected override void Dispose(bool disposing)
            {
                if (disposing && !mDisposed)
                {
                    mDisposed = true;
                    try
                    {
                        if (mAborted)
                        {
                            AbortOnServer();
                        }
                        else if (!mSuspended)
                        {
                            UploadBufferedPart(true);
                            Complete();
                        }
                    }
                    finally
                    {
                        mBuffer.Dispose();
                        mClient.Dispose();
                    }
                }
                base.Dispose(disposing);
            }

            public override int Read(byte[] buffer, int offset, int count)
            {
                throw new NotSupportedException();
            }

            public override long Seek(long offset, SeekOrigin origin)
            {
                throw new NotSupportedException();
            }

            public override void SetLength(long value)
            {
                throw new NotSupportedException();
            }

            /// <summary>The unfinished uploads of exactly this key, newest first, as (upload id, when it started).</summary>
            internal static List<KeyValuePair<string, DateTime>> ListUploads(HttpClient client, S3Endpoint endpoint, string key)
            {
                List<KeyValuePair<string, DateTime>> uploads = new List<KeyValuePair<string, DateTime>>();
                HttpRequestMessage request = SignedRequest(endpoint, "GET", string.Empty,
                    "uploads=&prefix=" + Uri.EscapeDataString(key), "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855");
                using (HttpResponseMessage response = Send(client, request, false))
                {
                    if (!response.IsSuccessStatusCode)
                    {
                        return uploads;
                    }
                    string xml = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                    foreach (XmlElement upload in XmlElements(xml, "Upload"))
                    {
                        if (XmlInner(upload.OuterXml, "Key") == key)
                        {
                            DateTime initiated;
                            DateTime.TryParse(XmlInner(upload.OuterXml, "Initiated"), null, System.Globalization.DateTimeStyles.AdjustToUniversal, out initiated);
                            uploads.Add(new KeyValuePair<string, DateTime>(XmlInner(upload.OuterXml, "UploadId"), initiated));
                        }
                    }
                }
                uploads.Sort((a, b) => b.Value.CompareTo(a.Value));
                return uploads;
            }

            // The parts the server holds for an upload (number -> MD5 of the part), or false when it cannot say.
            private bool TryListParts(string uploadId, out Dictionary<int, string> parts)
            {
                parts = new Dictionary<int, string>();
                int marker = 0;
                while (true)
                {
                    HttpRequestMessage request = SignedRequest(mEndpoint, "GET", mKey,
                        "uploadId=" + Uri.EscapeDataString(uploadId) + "&max-parts=1000" + (marker > 0 ? "&part-number-marker=" + marker : string.Empty),
                        "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855");
                    using (HttpResponseMessage response = Send(mClient, request, false))
                    {
                        if (!response.IsSuccessStatusCode)
                        {
                            return false;
                        }
                        string xml = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                        foreach (XmlElement part in XmlElements(xml, "Part"))
                        {
                            parts[int.Parse(XmlInner(part.OuterXml, "PartNumber"))] = XmlInner(part.OuterXml, "ETag").Trim('"').ToLowerInvariant();
                        }
                        if (!string.Equals(XmlInner(xml, "IsTruncated"), "true", StringComparison.OrdinalIgnoreCase))
                        {
                            return true;
                        }
                        marker = int.Parse(XmlInner(xml, "NextPartNumberMarker"));
                    }
                }
            }

            private void EnsureUploadId()
            {
                if (mUploadId != null)
                {
                    return;
                }
                if (mResume)
                {
                    foreach (KeyValuePair<string, DateTime> candidate in ListUploads(mClient, mEndpoint, mKey))
                    {
                        Dictionary<int, string> parts;
                        if (TryListParts(candidate.Key, out parts))
                        {
                            mUploadId = candidate.Key;
                            mExistingParts = parts;
                            return;
                        }
                    }
                }
                HttpRequestMessage request = SignedRequest(mEndpoint, "POST", mKey,
                    "uploads=", "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855");
                using (HttpResponseMessage response = Send(mClient, request))
                {
                    string xml = ReadBody(response);
                    mUploadId = XmlInner(xml, "UploadId");
                    if (string.IsNullOrEmpty(mUploadId))
                    {
                        throw new InvalidOperationException("s3: CreateMultipartUpload returned no UploadId.");
                    }
                }
            }

            private void UploadBufferedPart(bool isLast)
            {
                int take = isLast ? (int)mBuffer.Length : mPartSize;
                if (take == 0)
                {
                    return;
                }
                EnsureUploadId();
                byte[] part = new byte[take];
                mBuffer.Position = 0;
                int read = 0;
                while (read < take)
                {
                    int n = mBuffer.Read(part, read, take - read);
                    if (n == 0)
                    {
                        break;
                    }
                    read += n;
                }
                byte[] rest = new byte[mBuffer.Length - mBuffer.Position];
                mBuffer.Read(rest, 0, rest.Length);
                mBuffer.SetLength(0);
                mBuffer.Write(rest, 0, rest.Length);

                string payloadHash = HexSha256(part);
                int partNumber = mPartETags.Count + 1;
                if (mExistingParts != null)
                {
                    string have;
                    string md5 = HexMd5(part);
                    if (mExistingParts.TryGetValue(partNumber, out have) && have == md5)
                    {
                        // the server already holds exactly these bytes as this part
                        mPartETags.Add(md5);
                        mBytesSkipped += part.Length;
                        return;
                    }
                }
                HttpRequestMessage request = SignedRequest(mEndpoint, "PUT", mKey,
                    "partNumber=" + partNumber + "&uploadId=" + Uri.EscapeDataString(mUploadId), payloadHash);
                request.Content = new ByteArrayContent(part);
                using (HttpResponseMessage response = Send(mClient, request))
                {
                    if (!response.IsSuccessStatusCode)
                    {
                        ThrowForStatus("UploadPart", response);
                    }
                    IEnumerable<string> values;
                    if (response.Headers.TryGetValues("ETag", out values))
                    {
                        foreach (string etag in values)
                        {
                            mPartETags.Add(etag.Trim('"'));
                        }
                    }
                    else
                    {
                        throw new InvalidOperationException("s3: UploadPart returned no ETag.");
                    }
                }
            }

            private void Complete()
            {
                EnsureUploadId();
                StringBuilder xml = new StringBuilder("<CompleteMultipartUpload>");
                for (int i = 0; i < mPartETags.Count; i++)
                {
                    xml.Append("<Part><PartNumber>").Append(i + 1).Append("</PartNumber><ETag>\"")
                        .Append(mPartETags[i]).Append("\"</ETag></Part>");
                }
                xml.Append("</CompleteMultipartUpload>");
                byte[] body = Encoding.UTF8.GetBytes(xml.ToString());
                HttpRequestMessage request = SignedRequest(mEndpoint, "POST", mKey,
                    "uploadId=" + Uri.EscapeDataString(mUploadId), HexSha256(body));
                request.Content = new ByteArrayContent(body);
                using (HttpResponseMessage response = Send(mClient, request))
                {
                    if (!response.IsSuccessStatusCode)
                    {
                        ThrowForStatus("CompleteMultipartUpload", response);
                    }
                }
            }

            private static string HexMd5(byte[] data)
            {
                using (MD5 md5 = MD5.Create())
                {
                    return Convert.ToHexString(md5.ComputeHash(data)).ToLowerInvariant();
                }
            }

            private static string HexSha256(byte[] data)
            {
                using (SHA256 sha = SHA256.Create())
                {
                    byte[] hash = sha.ComputeHash(data);
                    StringBuilder hex = new StringBuilder(hash.Length * 2);
                    foreach (byte b in hash)
                    {
                        hex.Append(b.ToString("x2"));
                    }
                    return hex.ToString();
                }
            }
        }

        internal sealed class S3DownloadStream : Stream
        {
            private readonly HttpClient mClient;
            private readonly HttpResponseMessage mResponse;
            private readonly Stream mInner;

            public S3DownloadStream(HttpClient client, HttpResponseMessage response)
            {
                mClient = client;
                mResponse = response;
                mInner = response.Content.ReadAsStreamAsync().GetAwaiter().GetResult();
            }

            public override bool CanRead { get { return true; } }
            public override bool CanSeek { get { return false; } }
            public override bool CanWrite { get { return false; } }
            public override long Length { get { throw new NotSupportedException(); } }
            public override long Position
            {
                get { throw new NotSupportedException(); }
                set { throw new NotSupportedException(); }
            }

            public override int Read(byte[] buffer, int offset, int count)
            {
                return mInner.Read(buffer, offset, count);
            }

            public override void Flush()
            {
            }

            public override long Seek(long offset, SeekOrigin origin)
            {
                throw new NotSupportedException();
            }

            public override void SetLength(long value)
            {
                throw new NotSupportedException();
            }

            public override void Write(byte[] buffer, int offset, int count)
            {
                throw new NotSupportedException();
            }

            protected override void Dispose(bool disposing)
            {
                if (disposing)
                {
                    mInner.Dispose();
                    mResponse.Dispose();
                    mClient.Dispose();
                }
                base.Dispose(disposing);
            }
        }
    }
}
