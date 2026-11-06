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
using System.Threading;

namespace SqlSqueeze.Core
{
    /// <summary>
    /// Graceful Ctrl-C: the first press aborts the in-flight VDI operation
    /// (when there is one) so the normal failure path runs — partial files
    /// are cleaned up and a KILL_CONNECTIONS restore is put back to
    /// multi-user — instead of dying mid-stream and stranding server-side
    /// state. A second press kills the process immediately, as before.
    /// </summary>
    public static class OperationCanceller
    {
        private static int sPresses;

        public static void Reset()
        {
            Interlocked.Exchange(ref sPresses, 0);
        }

        /// <summary>
        /// Returns true to swallow the key press (an abort was signalled and
        /// the operation will fail through the normal path), false to let
        /// the process die: second press, or nothing in flight.
        /// </summary>
        public static bool OnCancelKeyPress()
        {
            if (Interlocked.Increment(ref sPresses) > 1)
            {
                return false;
            }
            return BackupPipeSystem.RequestCancel();
        }
    }
}
