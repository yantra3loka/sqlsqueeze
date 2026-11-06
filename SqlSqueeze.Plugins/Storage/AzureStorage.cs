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
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;

namespace SqlSqueeze.Plugins.Storage
{
    /// <summary>
    /// Azure Blob storage (real Azure or the Azurite emulator).
    /// Uploads stage blocks and commit a block list, so arbitrarily large
    /// backups stream. The container is created when missing. The account
    /// key comes from accountkeyenv=, never the command line.
    /// </summary>
    public class AzureStorage : IBackupStorage, IPrunableStorage, IManifestStorage, IResumableStorage
    {
        private static Dictionary<string, ParameterInfo> mBackupParamSchema;
        private static Dictionary<string, ParameterInfo> mPruneParamSchema;

        static AzureStorage()
        {
            mBackupParamSchema = new Dictionary<string, ParameterInfo>(StringComparer.InvariantCultureIgnoreCase);
            mBackupParamSchema.Add("endpoint", new ParameterInfo(false, true));
            mBackupParamSchema.Add("container", new ParameterInfo(false, true));
            mBackupParamSchema.Add("blob", new ParameterInfo(true, true));
            mBackupParamSchema.Add("account", new ParameterInfo(false, true));
            mBackupParamSchema.Add("accountkey", new ParameterInfo(false, false));
            mBackupParamSchema.Add("accountkeyenv", new ParameterInfo(false, false));
            mBackupParamSchema.Add("blocksizeMB", new ParameterInfo(false, false));
            mBackupParamSchema.Add("writeretries", new ParameterInfo(false, false));
            mBackupParamSchema.Add("writeretrybackoffms", new ParameterInfo(false, false));

            mPruneParamSchema = new Dictionary<string, ParameterInfo>(StringComparer.InvariantCultureIgnoreCase);
            mPruneParamSchema.Add("endpoint", new ParameterInfo(false, true));
            mPruneParamSchema.Add("container", new ParameterInfo(false, true));
            mPruneParamSchema.Add("prefix", new ParameterInfo(false, false));
            mPruneParamSchema.Add("pattern", new ParameterInfo(false, false));
            mPruneParamSchema.Add("olderThanDays", new ParameterInfo(false, true));
            mPruneParamSchema.Add("account", new ParameterInfo(false, true));
            mPruneParamSchema.Add("accountkey", new ParameterInfo(false, false));
            mPruneParamSchema.Add("accountkeyenv", new ParameterInfo(false, false));
        }

        private readonly HttpMessageHandler mHandler;
        private readonly List<string> mWrittenBlobs = new List<string>();
        private const string ManifestSuffix = ".manifest.json";

        public AzureStorage() : this(null)
        {
        }

        /// <summary>
        /// Test seam: inject the HTTP transport (mocked in AzureStorageTests).
        /// </summary>
        public AzureStorage(HttpMessageHandler handler)
        {
            mHandler = handler;
        }

        public string Name { get { return "azure"; } }

        public int GetNumberOfDevices(Dictionary<string, List<string>> config)
        {
            ParameterInfo.ValidateParams(mBackupParamSchema, config);
            return config["blob"].Count;
        }

        public Stream[] GetBackupWriter(Dictionary<string, List<string>> config)
        {
            return CreateWriters(config, false);
        }

        /// <summary>
        /// Writers that continue an unfinished upload of the same blob: blocks the server already
        /// staged with identical content (the block id carries a hash of it) are not sent again.
        /// Use them only for a source that can be read again from the start.
        /// </summary>
        public Stream[] GetResumableBackupWriter(Dictionary<string, List<string>> config)
        {
            return CreateWriters(config, true);
        }

        private Stream[] CreateWriters(Dictionary<string, List<string>> config, bool resume)
        {
            ParameterInfo.ValidateParams(mBackupParamSchema, config);
            AzureEndpoint endpoint = AzureEndpoint.FromConfig(config);
            EnsureContainer(endpoint);

            int writeRetries = ResolveInt(config, "writeretries", 1, 1);
            int writeRetryBackoffMs = ResolveInt(config, "writeretrybackoffms", 1000, 0);
            int blockSizeMB = ResolveInt(config, "blocksizeMB", 8, 1);

            Console.WriteLine("azure:");
            Console.WriteLine("\tendpoint=" + endpoint.BaseUrl);
            Console.WriteLine("\tcontainer=" + endpoint.Container);

            List<Stream> results = new List<Stream>(config["blob"].Count);
            mWrittenBlobs.Clear();
            foreach (string rawBlob in config["blob"])
            {
                string blob = LocalStorage.ExpandDateTokens(rawBlob);
                mWrittenBlobs.Add(blob);
                Console.WriteLine("\tblob=" + blob);
                Stream writer = new AzureBlockUploadStream(
                    CreateClient(), endpoint, blob, blockSizeMB * 1024 * 1024, resume);
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
            AzureEndpoint endpoint = AzureEndpoint.FromConfig(config);

            List<Stream> results = new List<Stream>(config["blob"].Count);
            estimatedTotalBytes = 0;
            foreach (string blob in config["blob"])
            {
                HttpClient client = CreateClient();
                HttpResponseMessage response = Send(client,
                    Signed(endpoint, "GET", BlobPath(endpoint, blob), null, null, null));
                if (response.Content.Headers.ContentLength.HasValue)
                {
                    estimatedTotalBytes += response.Content.Headers.ContentLength.Value;
                }
                results.Add(new AzureDownloadStream(client, response));
            }
            return results.ToArray();
        }

        public void CleanupOnAbort()
        {
        }

        public PruneResult PruneOldFiles(Dictionary<string, List<string>> config)
        {
            ParameterInfo.ValidateParams(mPruneParamSchema, config);
            AzureEndpoint endpoint = AzureEndpoint.FromConfig(config);
            string prefix = FirstOr(config, "prefix", string.Empty);
            string pattern = FirstOr(config, "pattern", "*");
            int olderThanDays = ResolveInt(config, "olderThanDays", 0, 0);
            DateTime cutoff = DateTime.UtcNow.AddDays(-olderThanDays);
            Regex matcher = new Regex("^" + Regex.Escape(pattern)
                .Replace("\\*", ".*").Replace("\\?", ".") + "$");

            PruneResult result = new PruneResult();
            string marker = null;
            do
            {
                string query = "restype=container&comp=list&prefix=" + Uri.EscapeDataString(prefix)
                    + (marker == null ? string.Empty : "&marker=" + Uri.EscapeDataString(marker));
                HttpClient client = CreateClient();
                HttpResponseMessage list = Send(client,
                    Signed(endpoint, "GET", "/" + endpoint.Container, query, null, null));
                string xml = ReadBody(list);
                marker = XmlInner(xml, "NextMarker");
                foreach (XmlElement blob in XmlElements(xml, "Blob"))
                {
                    string name = XmlInner(blob.OuterXml, "Name");
                    if (name.EndsWith(ManifestSuffix, StringComparison.Ordinal))
                    {
                        // a manifest goes with the backup it describes; its backup removes it
                        continue;
                    }
                    DateTime modified = DateTime.Parse(XmlInner(blob.OuterXml, "Last-Modified")).ToUniversalTime();
                    string leaf = name.Contains("/") ? name.Substring(name.LastIndexOf('/') + 1) : name;
                    if (matcher.IsMatch(leaf) && modified < cutoff)
                    {
                        DeleteBlob(endpoint, name);
                        DeleteBlob(endpoint, name + ManifestSuffix, ignoreMissing: true);
                        result.DeletedFiles++;
                        result.DeletedPaths.Add(endpoint.Container + "/" + name);
                    }
                }
            } while (!string.IsNullOrEmpty(marker));
            return result;
        }

        #region IManifestStorage Members

        public IList<string> GetFileNames(Dictionary<string, List<string>> config)
        {
            List<string> blobs = mWrittenBlobs.Count > 0 ? mWrittenBlobs : config["blob"];
            List<string> names = new List<string>();
            foreach (string blob in blobs)
            {
                names.Add(blob.Contains("/") ? blob.Substring(blob.LastIndexOf('/') + 1) : blob);
            }
            return names;
        }

        public Dictionary<string, List<string>> ResolveConfig(Dictionary<string, List<string>> config)
        {
            Dictionary<string, List<string>> resolved = StorageConfigs.Clone(config);
            if (mWrittenBlobs.Count > 0)
            {
                resolved["blob"] = new List<string>(mWrittenBlobs);
            }
            return resolved;
        }

        public void WriteManifest(Dictionary<string, List<string>> config, string json)
        {
            ParameterInfo.ValidateParams(mBackupParamSchema, config);
            AzureEndpoint endpoint = AzureEndpoint.FromConfig(config);
            byte[] bytes = System.Text.Encoding.UTF8.GetBytes(json);
            using (Stream writer = new AzureBlockUploadStream(CreateClient(), endpoint, ManifestBlob(config), 1024 * 1024))
            {
                writer.Write(bytes, 0, bytes.Length);
            }
        }

        public string ReadManifest(Dictionary<string, List<string>> config)
        {
            ParameterInfo.ValidateParams(mBackupParamSchema, config);
            AzureEndpoint endpoint = AzureEndpoint.FromConfig(config);
            using (HttpResponseMessage response = Send(CreateClient(),
                Signed(endpoint, "GET", BlobPath(endpoint, ManifestBlob(config)), null, null, null), false))
            {
                if (response.StatusCode == HttpStatusCode.NotFound)
                {
                    return null;
                }
                return ReadBody(response);
            }
        }

        // The manifest sits next to the first blob. After GetBackupWriter the
        // date tokens in the configured blob name are already expanded.
        private string ManifestBlob(Dictionary<string, List<string>> config)
        {
            return (mWrittenBlobs.Count > 0 ? mWrittenBlobs[0] : config["blob"][0]) + ManifestSuffix;
        }

        #endregion

        public string CommandLineHelp
        {
            get
            {
                return @"azure Usage:
    azure(endpoint=<url>;container=<name>;blob=<object>;account=<name>;accountkeyenv=<VAR>)
blob= may repeat for striped backups and accepts {date} tokens like local path.
accountkeyenv= is recommended (command lines are visible in process lists).
Works against real Azure and the Azurite emulator.
Uploads stage blocks (blocksizeMB, default 8) and commit a block list.
Prune: azure(endpoint=...;container=...;prefix=...;pattern=*.bak.zst;olderThanDays=30).";
            }
        }

        internal HttpClient CreateClient()
        {
            HttpClient client = mHandler == null
                ? new HttpClient()
                : new HttpClient(mHandler, false);
            client.Timeout = TimeSpan.FromMinutes(30);
            return client;
        }

        internal void EnsureContainer(AzureEndpoint endpoint)
        {
            HttpClient client = CreateClient();
            using (HttpResponseMessage response = Send(client,
                Signed(endpoint, "PUT", "/" + endpoint.Container, "restype=container", null, null), false))
            {
                if (response.StatusCode != HttpStatusCode.Created
                    && response.StatusCode != HttpStatusCode.Conflict)
                {
                    ThrowForStatus("CreateContainer", response);
                }
            }
        }

        internal void DeleteBlob(AzureEndpoint endpoint, string blob, bool ignoreMissing = false)
        {
            HttpClient client = CreateClient();
            using (HttpResponseMessage response = Send(client,
                Signed(endpoint, "DELETE", BlobPath(endpoint, blob), null, null, null), false))
            {
                if (ignoreMissing && response.StatusCode == HttpStatusCode.NotFound)
                {
                    return;
                }
                if (response.StatusCode != HttpStatusCode.OK
                    && response.StatusCode != HttpStatusCode.Accepted
                    && response.StatusCode != HttpStatusCode.NoContent)
                {
                    ThrowForStatus("DeleteBlob", response);
                }
            }
        }

        internal static string BlobPath(AzureEndpoint endpoint, string blob)
        {
            // Percent-encoded, as it goes on the wire and into the signature: a name with a
            // space, "%" or "#" would otherwise be rewritten or cut short when the URI is built.
            return "/" + endpoint.Container + "/" + S3Signer.EncodeKey(blob);
        }

        internal static HttpRequestMessage Signed(AzureEndpoint endpoint, string method,
            string path, string query, byte[] content, string contentType)
        {
            if (content == null && (method == "PUT" || method == "POST"))
            {
                content = new byte[0];
            }
            if (content != null && contentType == null)
            {
                contentType = "application/octet-stream";
            }
            Uri uri = new Uri(endpoint.BaseUrl + path
                + (string.IsNullOrEmpty(query) ? string.Empty : "?" + query));
            // The canonical resource is the account plus the FULL URL path:
            // devstore-style URLs already contain /account/... in the path.
            string basePath = new Uri(endpoint.BaseUrl).AbsolutePath.TrimEnd('/');
            Dictionary<string, string> headers = AzureSigner.Authorize(method,
                content == null ? (long?)null : (long)content.Length,
                contentType, endpoint.Account, basePath + path, query,
                endpoint.Account, endpoint.AccountKey, DateTime.UtcNow);
            HttpRequestMessage request = new HttpRequestMessage(new HttpMethod(method), uri);
            foreach (KeyValuePair<string, string> header in headers)
            {
                request.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }
            if (content != null)
            {
                request.Content = new ByteArrayContent(content);
                request.Content.Headers.ContentType =
                    new System.Net.Http.Headers.MediaTypeHeaderValue(contentType ?? "application/octet-stream");
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
                throw new IOException("azure: network error for " + request.Method
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
            string message = "azure: " + action + " failed: " + (int)response.StatusCode
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
                throw new ArgumentException("azure: " + name + " must be an integer >= " + minimum + ".");
            }
            return parsed;
        }

        internal sealed class AzureEndpoint
        {
            public string BaseUrl;
            public string Container;
            public string Account;
            public string AccountKey;

            public static AzureEndpoint FromConfig(Dictionary<string, List<string>> config)
            {
                AzureEndpoint endpoint = new AzureEndpoint();
                endpoint.BaseUrl = config["endpoint"][0].TrimEnd('/');
                endpoint.Container = config["container"][0].ToLowerInvariant();
                endpoint.Account = config["account"][0];
                List<string> env;
                if (config.TryGetValue("accountkeyenv", out env) && env.Count > 0 && !string.IsNullOrEmpty(env[0]))
                {
                    endpoint.AccountKey = Environment.GetEnvironmentVariable(env[0]);
                    if (string.IsNullOrEmpty(endpoint.AccountKey))
                    {
                        throw new ArgumentException("azure: environment variable is empty or missing: " + env[0]);
                    }
                }
                else if (config.TryGetValue("accountkey", out env) && env.Count > 0)
                {
                    endpoint.AccountKey = env[0];
                }
                else
                {
                    throw new ArgumentException("azure: accountkeyenv= (recommended) or accountkey= is required.");
                }
                return endpoint;
            }
        }

        internal sealed class AzureBlockUploadStream : Stream, IResumableStream
        {
            private readonly HttpClient mClient;
            private readonly AzureEndpoint mEndpoint;
            private readonly string mBlob;
            private readonly int mBlockSize;
            private readonly MemoryStream mBuffer = new MemoryStream();
            private readonly List<string> mBlockIds = new List<string>();
            private bool mDisposed;
            private bool mAborted;
            private readonly bool mResume;
            private long mBytesSkipped;
            private HashSet<string> mStagedBlockIds;   // blocks the server already holds for this blob, when resuming

            public AzureBlockUploadStream(HttpClient client, AzureEndpoint endpoint, string blob, int blockSizeBytes, bool resume = false)
            {
                mClient = client;
                mEndpoint = endpoint;
                mBlob = blob;
                mBlockSize = blockSizeBytes;
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
                if (mAborted)
                {
                    // the pipeline flushes its last bytes into the stream as it unwinds after a failure
                    return;
                }
                try
                {
                    mBuffer.Write(buffer, offset, count);
                    while (mBuffer.Length >= mBlockSize)
                    {
                        StageBufferedBlock(false);
                    }
                }
                catch
                {
                    // A write that failed leaves a hole: this blob must never be committed.
                    mAborted = true;
                    throw;
                }
            }

            public override void Flush()
            {
            }

            /// <summary>
            /// Discard the upload: the block list is never committed, so no blob appears. Staged
            /// blocks that were never committed are removed by Azure on its own after a week.
            /// </summary>
            public void Abort()
            {
                mAborted = true;
            }

            public long BytesSkipped { get { return mBytesSkipped; } }

            /// <summary>Keep the staged blocks for a later attempt: the block list is not committed.</summary>
            public void Suspend()
            {
                mAborted = true;
            }

            protected override void Dispose(bool disposing)
            {
                if (disposing && !mDisposed)
                {
                    mDisposed = true;
                    try
                    {
                        if (!mAborted)
                        {
                            StageBufferedBlock(true);
                            Commit();
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

            private void StageBufferedBlock(bool isLast)
            {
                int take = isLast ? (int)mBuffer.Length : mBlockSize;
                if (take == 0)
                {
                    return;
                }
                byte[] block = new byte[take];
                mBuffer.Position = 0;
                int read = 0;
                while (read < take)
                {
                    int n = mBuffer.Read(block, read, take - read);
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

                // The id is the position plus a hash of the content, so an id the server already holds
                // means exactly these bytes are there, and a block that changed never matches.
                string blockId = Convert.ToBase64String(
                    Encoding.UTF8.GetBytes(mBlockIds.Count.ToString("D8") + "-" + HexMd5(block)));
                mBlockIds.Add(blockId);
                if (mResume)
                {
                    if (mStagedBlockIds == null)
                    {
                        mStagedBlockIds = LoadStagedBlockIds();
                    }
                    if (mStagedBlockIds.Contains(blockId))
                    {
                        mBytesSkipped += block.Length;
                        return;
                    }
                }
                HttpRequestMessage request = Signed(
                    mEndpoint, "PUT", BlobPath(mEndpoint, mBlob),
                    "comp=block&blockid=" + Uri.EscapeDataString(blockId),
                    block, "application/octet-stream");
                using (HttpResponseMessage response = Send(mClient, request))
                {
                    if (response.StatusCode != HttpStatusCode.Created)
                    {
                        ThrowForStatus("PutBlock", response);
                    }
                }
            }

            private static string HexMd5(byte[] data)
            {
                using (System.Security.Cryptography.MD5 md5 = System.Security.Cryptography.MD5.Create())
                {
                    return Convert.ToHexString(md5.ComputeHash(data)).ToLowerInvariant();
                }
            }

            // The ids of the uncommitted blocks the server holds for this blob; none when it cannot say.
            private HashSet<string> LoadStagedBlockIds()
            {
                HashSet<string> ids = new HashSet<string>();
                try
                {
                    HttpRequestMessage request = Signed(mEndpoint, "GET", BlobPath(mEndpoint, mBlob),
                        "blocklisttype=uncommitted&comp=blocklist", null, null);
                    using (HttpResponseMessage response = Send(mClient, request, false))
                    {
                        if (response.IsSuccessStatusCode)
                        {
                            string xml = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                            foreach (XmlElement block in XmlElements(xml, "Block"))
                            {
                                ids.Add(XmlInner(block.OuterXml, "Name"));
                            }
                        }
                    }
                }
                catch (Exception)
                {
                    // can't tell what is staged: send everything, which is always correct
                }
                return ids;
            }

            private void Commit()
            {
                StringBuilder xml = new StringBuilder("<BlockList>");
                foreach (string blockId in mBlockIds)
                {
                    xml.Append("<Latest>").Append(blockId).Append("</Latest>");
                }
                xml.Append("</BlockList>");
                byte[] body = Encoding.UTF8.GetBytes(xml.ToString());
                HttpRequestMessage request = Signed(
                    mEndpoint, "PUT", BlobPath(mEndpoint, mBlob),
                    "comp=blocklist", body, "application/xml");
                using (HttpResponseMessage response = Send(mClient, request))
                {
                    if (response.StatusCode != HttpStatusCode.Created)
                    {
                        ThrowForStatus("PutBlockList", response);
                    }
                }
            }
        }

        internal sealed class AzureDownloadStream : Stream
        {
            private readonly HttpClient mClient;
            private readonly HttpResponseMessage mResponse;
            private readonly Stream mInner;

            public AzureDownloadStream(HttpClient client, HttpResponseMessage response)
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
