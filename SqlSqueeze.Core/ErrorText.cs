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

namespace SqlSqueeze.Core
{
    internal static class ErrorText
    {
        /// <summary>
        /// The message that says what actually went wrong: the innermost one, and
        /// for a ParallelExecutionException the innermost one of its first failure.
        /// </summary>
        public static string Innermost(Exception e)
        {
            ParallelExecutionException parallel = e as ParallelExecutionException;
            if (parallel != null && parallel.Exceptions.Count > 0)
            {
                e = parallel.Exceptions[0];
            }
            while (e.InnerException != null)
            {
                e = e.InnerException;
            }
            return e.Message;
        }
    }
}
