using System;
using System.Globalization;
using System.IO;
using System.Text;
using BepInEx;

namespace DSH.Foreseeker
{
    /// <summary>
    /// Foretest-only side channel. Every journey starts a new file inside
    /// BepInEx\plugins\foretest\, so different playthroughs never mix.
    /// Only "S " lines are written; the noisy RNG trace stays in LogOutput.log.
    /// </summary>
    internal static class ForetestLog
    {
        private static readonly object Gate = new object();
        private static string _path;
        private static string _directory;

        internal static string Directory
        {
            get
            {
                if (_directory == null)
                {
                    _directory = System.IO.Path.Combine(Paths.PluginPath, "foretest");
                }

                return _directory;
            }
        }

        internal static string Path
        {
            get
            {
                if (_path == null)
                {
                    _path = System.IO.Path.Combine(Directory, "foretest.log");
                }

                return _path;
            }
        }

        /// <summary>Starts a fresh file; called on game start and when a campaign begins.</summary>
        internal static void StartNew(string reason)
        {
            lock (Gate)
            {
                try
                {
                    System.IO.Directory.CreateDirectory(Directory);

                    string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
                    string name = "journey-" + stamp + "-" + Sanitize(reason) + ".log";
                    _path = System.IO.Path.Combine(Directory, name);

                    File.WriteAllText(
                        _path,
                        "# foretest log " + reason + " started "
                        + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + "\r\n",
                        new UTF8Encoding(false));
                }
                catch
                {
                    _path = null;
                }
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
                    System.IO.Directory.CreateDirectory(Directory);

                    string builder = DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture)
                                     + " " + line + "\r\n";

                    File.AppendAllText(Path, builder, new UTF8Encoding(false));
                }
                catch
                {
                    // Logging must never break the battle.
                }
            }
        }

        private static string Sanitize(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return "run";
            }

            var builder = new StringBuilder();
            foreach (char c in text)
            {
                builder.Append(char.IsLetterOrDigit(c) || c == '-' || c == '_' ? c : '_');
            }

            return builder.ToString();
        }
    }
}
