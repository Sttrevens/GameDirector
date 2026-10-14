using System;

namespace GameDirector.Core.Dsl
{
    /// <summary>Non-loadable OS metadata allowed in a verified runtime tree.
    /// Only the undeclared-file walk uses this policy: declared files still
    /// require their hashes, and links are never exempted.</summary>
    public static class RuntimeFilePolicy
    {
        public static bool IsOsMetadata(string name) =>
            name == ".DS_Store" || name.StartsWith("._", StringComparison.Ordinal) ||
            name == "Thumbs.db" || name == "desktop.ini" || name == "Icon\r";

        /// <summary>Windows defines the NAME_SURROGATE bit for reparse tags
        /// whose paths name another entity (symlinks, junctions, mount points).
        /// Cloud placeholders do not set it and are ordinary verifiable files.</summary>
        public static bool IsWindowsLinkTag(uint tag) => (tag & 0x20000000u) != 0;
    }
}
