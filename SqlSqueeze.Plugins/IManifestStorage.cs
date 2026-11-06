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

using System.Collections.Generic;

namespace SqlSqueeze.Plugins
{
    internal static class StorageConfigs
    {
        public static Dictionary<string, List<string>> Clone(Dictionary<string, List<string>> config)
        {
            Dictionary<string, List<string>> copy = new Dictionary<string, List<string>>(config.Comparer);
            foreach (KeyValuePair<string, List<string>> pair in config)
            {
                copy[pair.Key] = new List<string>(pair.Value);
            }
            return copy;
        }
    }

    /// <summary>
    /// Optional storage capability: keep a small JSON manifest (what was
    /// backed up, which pipeline, sizes and checksums) next to the backup
    /// files, so a backup describes itself and can be verified later.
    /// Storage plugins that do not implement this simply get no manifest.
    /// </summary>
    public interface IManifestStorage
    {
        /// <summary>
        /// The file names the backup occupies, in device order, with date
        /// tokens already expanded by the last GetBackupWriter call.
        /// </summary>
        IList<string> GetFileNames(Dictionary<string, List<string>> config);

        /// <summary>
        /// The config with date tokens (and anything else the writer expanded) replaced by the names
        /// the last GetBackupWriter call actually used, so a later step, such as copying the backup
        /// elsewhere, finds the files rather than re-expanding the tokens a moment later. Before
        /// anything was written it is just a copy of the config.
        /// </summary>
        Dictionary<string, List<string>> ResolveConfig(Dictionary<string, List<string>> config);

        void WriteManifest(Dictionary<string, List<string>> config, string json);

        /// <summary>Returns the manifest text, or null when there is none.</summary>
        string ReadManifest(Dictionary<string, List<string>> config);
    }
}
