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
using System.Reflection;
using System.Text;
using System.Text.Json;

namespace SqlSqueeze.Core
{
    /// <summary>
    /// One backup file as it was stored (after compression/encryption) and as
    /// SQL Server produced it (the "raw" stream before any pipeline stage).
    /// </summary>
    public class ManifestFile
    {
        public string Name { get; set; }
        public long StoredBytes { get; set; }
        public string StoredSha256 { get; set; }
        public long RawBytes { get; set; }
        public string RawSha256 { get; set; }
    }

    /// <summary>
    /// What a backup contains and how to read it back: written next to the
    /// backup files so a backup describes itself and can be verified without
    /// SQL Server. Secrets are never stored; env var names are.
    /// </summary>
    public class BackupManifest
    {
        public const int CurrentFormatVersion = 1;

        private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true,
        };

        public int FormatVersion { get; set; } = CurrentFormatVersion;
        public string Tool { get; set; }
        public DateTime CreatedUtc { get; set; }
        public string Database { get; set; }
        public string BackupType { get; set; }

        /// <summary>A copy-only backup: taken outside the normal chain (it does not become a differential base).</summary>
        public bool CopyOnly { get; set; }

        public string Storage { get; set; }
        public List<string> Pipeline { get; set; } = new List<string>();
        public List<ManifestFile> Files { get; set; } = new List<ManifestFile>();

        /// <summary>
        /// The database's files when it was backed up (logical name and type), so a
        /// test restore can place them. Null in manifests written before this existed.
        /// </summary>
        public List<DatabaseFileInfo> DatabaseFiles { get; set; }

        public string ToJson()
        {
            return JsonSerializer.Serialize(this, JsonOptions);
        }

        public static BackupManifest FromJson(string json)
        {
            BackupManifest manifest = JsonSerializer.Deserialize<BackupManifest>(json, JsonOptions);
            if (manifest == null)
            {
                throw new ArgumentException("The manifest is empty.");
            }
            if (manifest.FormatVersion > CurrentFormatVersion)
            {
                throw new ArgumentException(string.Format(
                    "The manifest is format {0}; this build understands up to format {1}. Upgrade sqlsqueeze.",
                    manifest.FormatVersion, CurrentFormatVersion));
            }
            return manifest;
        }

        /// <summary>
        /// "name" or "name(key=value;key=value)" for one pipeline stage, with
        /// secret values replaced by ***. A key holds a secret when it names a
        /// password, secret, token or access/account key; keys ending in "env"
        /// only name an environment variable and are kept.
        /// </summary>
        public static string DescribeStage(string name, Dictionary<string, List<string>> parameters)
        {
            if (parameters == null || parameters.Count == 0)
            {
                return name;
            }
            List<string> keys = new List<string>(parameters.Keys);
            keys.Sort(StringComparer.OrdinalIgnoreCase);

            StringBuilder spec = new StringBuilder(name).Append('(');
            bool first = true;
            foreach (string key in keys)
            {
                foreach (string value in parameters[key])
                {
                    if (!first)
                    {
                        spec.Append(';');
                    }
                    first = false;
                    // ";;" is how a literal semicolon is written in a pipeline spec
                    spec.Append(key).Append('=').Append(IsSecret(key) ? "***" : (value ?? string.Empty).Replace(";", ";;"));
                }
            }
            return spec.Append(')').ToString();
        }

        /// <summary>
        /// The pipeline stages as spec strings (name or name(key=value;...)) that can be
        /// parsed and used to read this backup. Throws when a stage had a secret the
        /// manifest, by design, did not keep: that stage must be given by hand.
        /// </summary>
        public List<string> PipelineSpecsForRestore()
        {
            List<string> specs = new List<string>();
            foreach (string stage in Pipeline ?? new List<string>())
            {
                System.Text.RegularExpressions.Match secret = System.Text.RegularExpressions.Regex.Match(stage, @"([A-Za-z0-9_]+)=\*\*\*");
                if (secret.Success)
                {
                    string name = stage.IndexOf('(') > 0 ? stage.Substring(0, stage.IndexOf('(')) : stage;
                    throw new ArgumentException(string.Format(
                        "The {0} stage was run with {1}=..., and a manifest never keeps secrets. Give the pipeline yourself (for {0}, use passwordEnv=<variable>) instead of --from-manifest.",
                        name, secret.Groups[1].Value));
                }
                specs.Add(stage);
            }
            return specs;
        }

        private static bool IsSecret(string key)
        {
            string k = key.ToLowerInvariant();
            if (k.EndsWith("env"))
            {
                return false;
            }
            return k == "key" || k.Contains("password") || k.Contains("secret") || k.Contains("token")
                || k.Contains("accesskey") || k.Contains("accountkey");
        }

        internal static string ToolName()
        {
            Version v = Assembly.GetExecutingAssembly().GetName().Version;
            return "sqlsqueeze " + v;
        }
    }

    /// <summary>
    /// Wraps the streams of a pipeline at its two ends, the file as stored and
    /// the raw stream SQL Server sees, and remembers their hashes and sizes.
    /// Used both when writing a backup and when verifying one.
    /// </summary>
    public class ManifestRecorder
    {
        private readonly List<HashingStream> mStored = new List<HashingStream>();
        private readonly List<HashingStream> mRaw = new List<HashingStream>();

        public Stream WrapStored(Stream fileStream)
        {
            HashingStream hashing = new HashingStream(fileStream, leaveOpen: false);
            mStored.Add(hashing);
            return hashing;
        }

        public Stream WrapRaw(Stream topStream)
        {
            HashingStream hashing = new HashingStream(topStream, leaveOpen: false);
            mRaw.Add(hashing);
            return hashing;
        }

        /// <summary>
        /// Reads whatever the pipeline left unread, so the stored hash covers
        /// the whole file (trailing bytes after a valid stream still count).
        /// </summary>
        public void DrainStored()
        {
            byte[] buffer = new byte[81920];
            foreach (HashingStream stored in mStored)
            {
                try
                {
                    while (stored.Read(buffer, 0, buffer.Length) > 0)
                    {
                    }
                }
                catch (ObjectDisposedException)
                {
                }
            }
        }

        public BackupManifest Build(string database, string backupType, string storage, IList<string> pipeline, IList<string> fileNames)
        {
            BackupManifest manifest = new BackupManifest
            {
                Tool = BackupManifest.ToolName(),
                CreatedUtc = DateTime.UtcNow,
                Database = database,
                BackupType = backupType,
                Storage = storage,
                Pipeline = new List<string>(pipeline),
                Files = Observed(fileNames),
            };
            return manifest;
        }

        internal List<ManifestFile> Observed(IList<string> fileNames)
        {
            List<ManifestFile> files = new List<ManifestFile>();
            for (int i = 0; i < mStored.Count; i++)
            {
                files.Add(new ManifestFile
                {
                    Name = fileNames != null && i < fileNames.Count ? fileNames[i] : "file " + (i + 1),
                    StoredBytes = mStored[i].Length,
                    StoredSha256 = mStored[i].HexDigest(),
                    RawBytes = i < mRaw.Count ? mRaw[i].Length : 0,
                    RawSha256 = i < mRaw.Count ? mRaw[i].HexDigest() : null,
                });
            }
            return files;
        }
    }
}
