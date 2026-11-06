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
using System.Security.Cryptography;
using System.Text;

namespace SqlSqueeze.Plugins.Storage
{
    /// <summary>
    /// Minimal AWS Signature Version 4 for S3, no new dependencies.
    /// Covers signed header auth (PUT/GET/DELETE/LIST) and presigned URLs.
    /// Verified against the AWS documentation vectors in S3SignerTests.
    /// </summary>
    public static class S3Signer
    {
        public static Dictionary<string, string> Sign(
            string method,
            Uri uri,
            string payloadSha256Hex,
            string accessKey,
            string secretKey,
            string region,
            DateTime dateUtc,
            IDictionary<string, string> extraHeaders)
        {
            string amzDate = dateUtc.ToString("yyyyMMddTHHmmssZ");
            string dateStamp = dateUtc.ToString("yyyyMMdd");

            SortedDictionary<string, string> headers =
                new SortedDictionary<string, string>(StringComparer.Ordinal);
            headers["host"] = uri.IsDefaultPort
                ? uri.Host
                : uri.Host + ":" + uri.Port.ToString();
            headers["x-amz-date"] = amzDate;
            // S3 insists on the payload hash as a header, and signed: without it AWS answers
            // "Missing required header", and strict S3 servers reject the signature.
            headers["x-amz-content-sha256"] = payloadSha256Hex;
            if (extraHeaders != null)
            {
                foreach (KeyValuePair<string, string> pair in extraHeaders)
                {
                    headers[pair.Key.ToLowerInvariant()] = CollapseWhitespace(pair.Value);
                }
            }

            StringBuilder canonicalHeaders = new StringBuilder();
            List<string> signedNames = new List<string>();
            foreach (KeyValuePair<string, string> pair in headers)
            {
                canonicalHeaders.Append(pair.Key).Append(':').Append(pair.Value).Append('\n');
                signedNames.Add(pair.Key);
            }
            string signedHeaders = string.Join(";", signedNames.ToArray());

            string canonicalRequest = method + "\n"
                + CanonicalUri(uri) + "\n"
                + CanonicalQueryString(uri) + "\n"
                + canonicalHeaders.ToString() + "\n"
                + signedHeaders + "\n"
                + payloadSha256Hex;

            string scope = dateStamp + "/" + region + "/s3/aws4_request";
            string stringToSign = "AWS4-HMAC-SHA256\n" + amzDate + "\n" + scope + "\n"
                + Hex(Sha256(Encoding.UTF8.GetBytes(canonicalRequest)));

            byte[] signingKey = GetSigningKey(secretKey, dateStamp, region);
            string signature = Hex(Hmac(signingKey, stringToSign));

            Dictionary<string, string> result =
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            result["x-amz-date"] = amzDate;
            result["x-amz-content-sha256"] = payloadSha256Hex;
            result["Authorization"] = "AWS4-HMAC-SHA256 Credential=" + accessKey + "/"
                + scope + ", SignedHeaders=" + signedHeaders + ", Signature=" + signature;
            return result;
        }

        public static string PresignedGetUrl(
            string endpoint,
            string bucket,
            string key,
            string accessKey,
            string secretKey,
            string region,
            int expiresSeconds)
        {
            DateTime now = DateTime.UtcNow;
            string amzDate = now.ToString("yyyyMMddTHHmmssZ");
            string dateStamp = now.ToString("yyyyMMdd");
            string scope = dateStamp + "/" + region + "/s3/aws4_request";

            SortedDictionary<string, string> query =
                new SortedDictionary<string, string>(StringComparer.Ordinal);
            query["X-Amz-Algorithm"] = "AWS4-HMAC-SHA256";
            query["X-Amz-Credential"] = accessKey + "/" + scope;
            query["X-Amz-Date"] = amzDate;
            query["X-Amz-Expires"] = expiresSeconds.ToString();
            query["X-Amz-SignedHeaders"] = "host";

            string canonicalRequest = "GET\n"
                + CanonicalPath(bucket, key) + "\n"
                + EncodeQuery(query) + "\n"
                + "host:" + HostHeader(new Uri(endpoint)) + "\n"
                + "\nhost\n"
                + "UNSIGNED-PAYLOAD";

            string stringToSign = "AWS4-HMAC-SHA256\n" + amzDate + "\n" + scope + "\n"
                + Hex(Sha256(Encoding.UTF8.GetBytes(canonicalRequest)));
            string signature = Hex(Hmac(GetSigningKey(secretKey, dateStamp, region), stringToSign));
            return endpoint.TrimEnd('/') + CanonicalPath(bucket, key)
                + "?" + EncodeQuery(query) + "&X-Amz-Signature=" + signature;
        }

        public static string CanonicalUri(Uri uri)
        {
            return EncodePath(uri.AbsolutePath);
        }

        public static string CanonicalPath(string bucket, string key)
        {
            return "/" + EncodeSegment(bucket.TrimStart('/')) + "/" + EncodeKey(key);
        }

        public static string EncodeKey(string key)
        {
            string[] segments = key.Split('/');
            for (int i = 0; i < segments.Length; i++)
            {
                segments[i] = EncodeSegment(segments[i]);
            }
            return string.Join("/", segments);
        }

        public static string CanonicalUri(string bucket, string key)
        {
            return CanonicalPath(bucket, key);
        }

        private static string HostHeader(Uri uri)
        {
            return uri.IsDefaultPort ? uri.Host : uri.Host + ":" + uri.Port.ToString();
        }

        private static string EncodePath(string absolutePath)
        {
            string[] segments = absolutePath.Split('/');
            for (int i = 0; i < segments.Length; i++)
            {
                segments[i] = EncodeSegment(Uri.UnescapeDataString(segments[i]));
            }
            return string.Join("/", segments);
        }

        private static string EncodeSegment(string segment)
        {
            return Uri.EscapeDataString(segment).Replace("%7E", "~");
        }

        private static string CanonicalQueryString(Uri uri)
        {
            string query = uri.Query;
            if (string.IsNullOrEmpty(query) || query == "?")
            {
                return string.Empty;
            }
            SortedDictionary<string, string> pairs =
                new SortedDictionary<string, string>(StringComparer.Ordinal);
            foreach (string part in query.TrimStart('?').Split('&'))
            {
                if (part.Length == 0)
                {
                    continue;
                }
                int equals = part.IndexOf('=');
                string name = equals < 0 ? part : part.Substring(0, equals);
                string value = equals < 0 ? string.Empty : part.Substring(equals + 1);
                // decoded here, encoded once in EncodeQuery (encoding twice turned %2F into %252F)
                pairs[Uri.UnescapeDataString(name)] = Uri.UnescapeDataString(value);
            }
            return EncodeQuery(pairs);
        }

        private static string EncodeQuery(IDictionary<string, string> pairs)
        {
            List<string> parts = new List<string>();
            foreach (KeyValuePair<string, string> pair in pairs)
            {
                parts.Add(Uri.EscapeDataString(pair.Key) + "=" + Uri.EscapeDataString(pair.Value));
            }
            return string.Join("&", parts.ToArray());
        }

        private static string CollapseWhitespace(string value)
        {
            StringBuilder collapsed = new StringBuilder();
            bool inSpace = true;
            foreach (char c in value.Trim())
            {
                if (c == ' ' || c == '\t')
                {
                    if (!inSpace)
                    {
                        collapsed.Append(' ');
                        inSpace = true;
                    }
                }
                else
                {
                    collapsed.Append(c);
                    inSpace = false;
                }
            }
            return collapsed.ToString();
        }

        private static byte[] GetSigningKey(string secretKey, string dateStamp, string region)
        {
            byte[] kDate = Hmac(Encoding.UTF8.GetBytes("AWS4" + secretKey), dateStamp);
            byte[] kRegion = Hmac(kDate, region);
            byte[] kService = Hmac(kRegion, "s3");
            return Hmac(kService, "aws4_request");
        }

        private static byte[] Hmac(byte[] key, string data)
        {
            using (HMACSHA256 hmac = new HMACSHA256(key))
            {
                return hmac.ComputeHash(Encoding.UTF8.GetBytes(data));
            }
        }

        private static byte[] Sha256(byte[] data)
        {
            using (SHA256 sha = SHA256.Create())
            {
                return sha.ComputeHash(data);
            }
        }

        private static string Hex(byte[] bytes)
        {
            StringBuilder hex = new StringBuilder(bytes.Length * 2);
            foreach (byte b in bytes)
            {
                hex.Append(b.ToString("x2"));
            }
            return hex.ToString();
        }
    }
}
