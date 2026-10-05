using System.Collections.Generic;

namespace ModUpdater
{
    public partial class Plugin
    {
        private readonly HashSet<string> _expanded = new HashSet<string>(); // mods whose details are open

        /// <summary>
        /// A mod's description is its DESCRIPTION.txt: the first paragraph is the summary shown on the card,
        /// anything after the first blank line is the details shown under "Details".
        /// </summary>
        private static void SplitDescription(string text, out string summary, out string details)
        {
            text = (text ?? "").Replace("\r\n", "\n").Trim();
            int split = text.IndexOf("\n\n");
            summary = (split < 0 ? text : text.Substring(0, split)).Replace("\n", " ").Trim();
            details = split < 0 ? "" : text.Substring(split + 2).Trim();
        }
    }
}
