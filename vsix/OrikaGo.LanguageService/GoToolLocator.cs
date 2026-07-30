using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;

namespace OrikaGo.LanguageService
{
    /// <summary>
    /// Locates Go ecosystem executables (gopls.exe, dlv.exe). Probe order:
    /// every directory on PATH, then GOBIN, then each GOPATH entry's bin\,
    /// then %USERPROFILE%\go\bin. GOBIN/GOPATH are read from the process
    /// environment AND from `go env` - `go env -w` persists them into Go's
    /// env file, invisible to GetEnvironmentVariable, and `go install`
    /// honours exactly those values, so the probe must too or an
    /// extension-suggested "go install ..." would land somewhere this
    /// class never looks.
    /// </summary>
    internal static class GoToolLocator
    {
        /// <param name="exeName">File name including .exe, e.g. "dlv.exe".</param>
        /// <returns>Full path, or null when not found.</returns>
        public static string Find(string exeName)
        {
            foreach (string dir in CandidateDirectories())
            {
                if (string.IsNullOrEmpty(dir))
                {
                    continue;
                }
                try
                {
                    string candidate = Path.Combine(dir, exeName);
                    if (File.Exists(candidate))
                    {
                        return candidate;
                    }
                }
                catch (ArgumentException)
                {
                    // Malformed entry (invalid characters) - skip it.
                }
            }
            return null;
        }

        /// <summary>The probe list shown in "not found" error messages.</summary>
        public const string ProbeDescription = "PATH、GOBIN、GOPATH\\bin（含 go env -w 持久化的值）或 %USERPROFILE%\\go\\bin";

        private static IEnumerable<string> CandidateDirectories()
        {
            string pathVariable = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
            foreach (string rawDir in pathVariable.Split(Path.PathSeparator))
            {
                yield return rawDir.Trim().Trim('"');
            }

            string envGobin = Environment.GetEnvironmentVariable("GOBIN");
            if (!string.IsNullOrEmpty(envGobin))
            {
                yield return envGobin.Trim();
            }
            string envGopath = Environment.GetEnvironmentVariable("GOPATH");
            if (!string.IsNullOrEmpty(envGopath))
            {
                foreach (string entry in envGopath.Split(Path.PathSeparator))
                {
                    if (entry.Trim().Length > 0)
                    {
                        yield return Path.Combine(entry.Trim(), "bin");
                    }
                }
            }

            string[] goEnv = RunGoEnv("GOBIN", "GOPATH");
            if (goEnv.Length > 0 && goEnv[0].Length > 0)
            {
                yield return goEnv[0];
            }
            if (goEnv.Length > 1 && goEnv[1].Length > 0)
            {
                foreach (string entry in goEnv[1].Split(Path.PathSeparator))
                {
                    if (entry.Trim().Length > 0)
                    {
                        yield return Path.Combine(entry.Trim(), "bin");
                    }
                }
            }

            string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (!string.IsNullOrEmpty(userProfile))
            {
                yield return Path.Combine(userProfile, "go", "bin");
            }
        }

        /// <summary>
        /// Runs `go env <names>` and returns one trimmed line per requested name
        /// (positional; unset values come back as empty strings). Returns an empty
        /// array when the go command is unavailable or misbehaves.
        /// </summary>
        private static string[] RunGoEnv(params string[] names)
        {
            try
            {
                var startInfo = new ProcessStartInfo
                {
                    FileName = "go",
                    Arguments = "env " + string.Join(" ", names),
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                };
                using (var process = Process.Start(startInfo))
                {
                    string output = process.StandardOutput.ReadToEnd();
                    if (!process.WaitForExit(5000))
                    {
                        try { process.Kill(); } catch (Exception) { }
                        return Array.Empty<string>();
                    }
                    if (process.ExitCode != 0)
                    {
                        return Array.Empty<string>();
                    }

                    // Positional lines; an unset variable is an EMPTY line, so no
                    // RemoveEmptyEntries here or the mapping would shift.
                    string[] lines = output.Replace("\r", string.Empty).Split('\n');
                    var values = new string[names.Length];
                    for (int i = 0; i < names.Length; i++)
                    {
                        values[i] = i < lines.Length ? lines[i].Trim() : string.Empty;
                    }
                    return values;
                }
            }
            catch (Exception)
            {
                return Array.Empty<string>();
            }
        }
    }
}
