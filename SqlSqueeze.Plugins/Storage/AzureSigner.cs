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
    /// Azure Storage Shared Key signing for Blob (Azurite-compatible).
    /// See AzureTests for the canonical-string contract.
    /// </summary>
    public static class AzureSigner
    {
        public const string ServiceVersion = "2020-04-08";

        public static Dictionary<string, string> Authorize(
            string method,
            long? contentLength,
            string contentType,
            string account,
            string path,
            string query,
            string keyName,
            string accountKeyBase64,
            DateTime dateUtc)
        {
            Dictionary<string, string> headers = new Dictionary<string, string>();
            headers["x-ms-date"] = dateUtc.ToString("R");
            headers["x-ms-version"] = ServiceVersion;
            string canonical = CanonicalStringToSign(method, contentLength, contentType,
                account, path, query, headers);
            byte[] key = Convert.FromBase64String(accountKeyBase64);
            string signature;
            using (HMACSHA256 hmac = new HMACSHA256(key))
            {
                signature = Convert.ToBase64String(
                    hmac.ComputeHash(Encoding.UTF8.GetBytes(canonical)));
            }
            Dictionary<string, string> result =
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            result["x-ms-date"] = headers["x-ms-date"];
            result["x-ms-version"] = headers["x-ms-version"];
            result["Authorization"] = "SharedKey " + keyName + ":" + signature;
            return result;
        }

        public static string CanonicalStringToSign(
            string method,
            long? contentLength,
            string contentType,
            string account,
            string path,
            string query,
            IDictionary<string, string> msHeaders)
        {
            StringBuilder canonical = new StringBuilder();
            canonical.Append(method.ToUpperInvariant()).Append('\n');
            canonical.Append('\n'); // Content-Encoding
            canonical.Append('\n'); // Content-Language
            // Azure treats a zero Content-Length as empty in the signature,
            // even though the header travels on the wire (Azurite-verified).
            canonical.Append(contentLength.HasValue && contentLength.Value != 0
                ? contentLength.Value.ToString() : string.Empty).Append('\n');
            canonical.Append('\n'); // Content-MD5
            canonical.Append(contentType ?? string.Empty).Append('\n');
            canonical.Append('\n'); // Date (x-ms-date carries it)
            canonical.Append('\n'); // If-Modified-Since
            canonical.Append('\n'); // If-Match
            canonical.Append('\n'); // If-None-Match
            canonical.Append('\n'); // If-Unmodified-Since
            canonical.Append('\n'); // Range

            SortedDictionary<string, string> sorted =
                new SortedDictionary<string, string>(StringComparer.Ordinal);
            if (msHeaders != null)
            {
                foreach (KeyValuePair<string, string> pair in msHeaders)
                {
                    sorted[pair.Key.ToLowerInvariant()] = pair.Value.Trim();
                }
            }
            foreach (KeyValuePair<string, string> pair in sorted)
            {
                canonical.Append(pair.Key).Append(':').Append(pair.Value).Append('\n');
            }

            canonical.Append('/').Append(account).Append(path.StartsWith("/") ? path : "/" + path);
            SortedDictionary<string, string> params_ =
                new SortedDictionary<string, string>(StringComparer.Ordinal);
            if (!string.IsNullOrEmpty(query))
            {
                foreach (string part in query.TrimStart('?').Split('&'))
                {
                    if (part.Length == 0)
                    {
                        continue;
                    }
                    int equals = part.IndexOf('=');
                    string name = (equals < 0 ? part : part.Substring(0, equals)).ToLowerInvariant();
                    string value = equals < 0 ? string.Empty : Uri.UnescapeDataString(part.Substring(equals + 1));
                    if (params_.ContainsKey(name))
                    {
                        params_[name] = params_[name] + "," + value;
                    }
                    else
                    {
                        params_[name] = value;
                    }
                }
            }
            foreach (KeyValuePair<string, string> pair in params_)
            {
                canonical.Append('\n').Append(pair.Key).Append(':').Append(pair.Value);
            }
            return canonical.ToString();
        }
    }
}
