/*
 * This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
 * If a copy of the MPL was not distributed with this file, You can obtain one at
 * http://mozilla.org/MPL/2.0/.
 */

/*
 * VMHouseXmlValidator (reeims-e54)
 *
 * Pure static helper — no MonoGame, no game-engine dependencies. Safe to include
 * directly in the test project via <Compile Include>.
 *
 * Validates house XML filenames supplied by external agents before the game engine
 * passes them to CoreGameScreen.RequestLotLoad. An agent-supplied path that
 * bypasses this check could read arbitrary files as IFF data (path traversal).
 */

using System.IO;

namespace FSO.SimAntics.NetPlay.Model.Commands
{
    /// <summary>
    /// Validates house XML filenames received from external agents (IPC / campfire).
    /// </summary>
    public static class VMHouseXmlValidator
    {
        /// <summary>
        /// Returns true if <paramref name="houseXml"/> is a safe bare filename for
        /// lot loading. Rejects anything that could escape the Content/Houses/
        /// directory.
        ///
        /// Rejection rules (any violation → false):
        ///   1. Null or empty → false.
        ///   2. Contains ".." (directory traversal) → false.
        ///   3. Path.IsPathRooted returns true (e.g. "/tmp/evil", "C:\...") → false.
        ///   4. Starts with '~' (shell home-directory shorthand) → false.
        ///   5. Contains '/' or '\' (path separator chars) → false.
        /// </summary>
        public static bool IsValidHouseXml(string houseXml)
        {
            if (string.IsNullOrEmpty(houseXml))
                return false;
            if (houseXml.Contains(".."))
                return false;
            if (Path.IsPathRooted(houseXml))
                return false;
            if (houseXml[0] == '~')
                return false;
            if (houseXml.IndexOf('/') >= 0 || houseXml.IndexOf('\\') >= 0)
                return false;
            return true;
        }
    }
}
