/*
 * This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
 * If a copy of the MPL was not distributed with this file, You can obtain one at
 * http://mozilla.org/MPL/2.0/.
 */

/*
 * SimSaveStore (reeims-eb9)
 *
 * Path-safe file store for Sim marshal snapshots. Anchors all save/load paths
 * under Content/Saves/ (resolved relative to the process CWD, mirroring how
 * Content/Houses/ is used elsewhere in the codebase).
 *
 * Path rules:
 *   - Filenames may contain letters, digits, dashes, underscores, dots, spaces.
 *   - Path separators (/ or \) are rejected.
 *   - Parent-directory tokens (..) are rejected.
 *   - Absolute paths (leading /) and tilde expansion (leading ~) are rejected.
 *   - Empty / null / whitespace-only filenames are rejected.
 *
 * This helper is pure (no VM references) so it is unit-testable without pulling
 * in MonoGame / VMContext.
 */

using System;
using System.IO;

namespace FSO.SimAntics.Diagnostics
{
    public static class SimSaveStore
    {
        /// <summary>
        /// Directory (relative to CWD) where sim save files are stored.
        /// Created lazily when a save is first written.
        /// </summary>
        public const string BaseDir = "Content/Saves";

        public enum PathError
        {
            Ok = 0,
            EmptyFilename = 1,
            AbsolutePath = 2,
            TildePath = 3,
            SeparatorInFilename = 4,
            ParentTraversal = 5,
        }

        /// <summary>
        /// Validates a filename for safety. Returns Ok and sets resolvedPath on success.
        /// resolvedPath is Path.Combine(BaseDir, filename), still relative to CWD.
        /// </summary>
        public static PathError ValidateFilename(string filename, out string resolvedPath)
        {
            resolvedPath = null;

            if (string.IsNullOrWhiteSpace(filename))
                return PathError.EmptyFilename;

            // Reject absolute paths (Unix or Windows style).
            if (filename[0] == '/' || filename[0] == '\\')
                return PathError.AbsolutePath;

            // Reject tilde expansion anywhere in the leading segment.
            if (filename[0] == '~')
                return PathError.TildePath;

            // Reject any path separator — save files must be flat.
            if (filename.IndexOf('/') >= 0 || filename.IndexOf('\\') >= 0)
                return PathError.SeparatorInFilename;

            // Reject parent traversal. Be conservative: any occurrence of ".."
            // anywhere in the filename is rejected.
            if (filename.IndexOf("..", StringComparison.Ordinal) >= 0)
                return PathError.ParentTraversal;

            resolvedPath = Path.Combine(BaseDir, filename);
            return PathError.Ok;
        }

        /// <summary>
        /// Returns a short error key for a PathError, suitable for the response
        /// payload. Never returns null.
        /// </summary>
        public static string ErrorKey(PathError err)
        {
            switch (err)
            {
                case PathError.EmptyFilename: return "empty_filename";
                case PathError.AbsolutePath: return "absolute_path_not_allowed";
                case PathError.TildePath: return "tilde_path_not_allowed";
                case PathError.SeparatorInFilename: return "separator_in_filename";
                case PathError.ParentTraversal: return "parent_traversal_not_allowed";
                default: return "unknown";
            }
        }

        /// <summary>
        /// Ensures BaseDir exists on disk. Called before writing a save file.
        /// </summary>
        public static void EnsureBaseDir()
        {
            if (!Directory.Exists(BaseDir))
                Directory.CreateDirectory(BaseDir);
        }
    }
}
