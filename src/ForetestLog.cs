using System;
using System.Globalization;
using System.IO;
using System.Text;
using BepInEx;

namespace DSH.Foreseeker
{
    /// <summary>
    /// Foretest-only side channel: appends the solver's decision/actual lines to a file next to
    /// Foretest.dll (BepInEx\plugins\foretest.log) so a whole run can be compared offline.
    /// Only "S " lines are written; the noisy RNG trace stays in LogOutput.log.
    /// </summary>
    internal static class ForetestLog
    {
        private static readonly object Gate = new object();
        private static string _path;
        private static bool _headerWritten;

        internal static string Path
        {
            get
            {
                if (_path == null)
                {
                    _path = System.IO.Path.Combine(Paths.PluginPath, "foretest.log");
                }

                return _path;
            }
        }

        internal static void Write(string line)
        {
            if (string.IsNullOrEmpty(line))
            {
                return;
            }

            lock (Gate)
            {
                try
                {
                    var builder = new StringBuilder();
                    if (!_headerWritten)
                    {
                        _headerWritten = true;
                        builder.AppendLine("# foretest log started "
                                           + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));
                    }

                    builder.Append(DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture));
                    builder.Append(' ');
                    builder.AppendLine(line);

                    File.AppendAllText(Path, builder.ToString(), new UTF8Encoding(false));
                }
                catch
                {
                    // Logging must never break the battle.
                }
            }
        }
    }
}
