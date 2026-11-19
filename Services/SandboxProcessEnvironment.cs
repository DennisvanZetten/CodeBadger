namespace CodeBadger.Services
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using System.Text;

    // A process environment is not an isolation boundary. This only prevents
    // accidental inheritance; the sandbox must separately restrict filesystem,
    // network and credential access before starting an untrusted process.
    public static class SandboxProcessEnvironment
    {
        public static byte[] CreateWindowsBlock(string windowsDirectory, string sandboxHome)
        {
            ValidatePath(windowsDirectory, nameof(windowsDirectory));
            ValidatePath(sandboxHome, nameof(sandboxHome));

            var systemDirectory = Path.Combine(windowsDirectory, "System32");
            var values = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["APPDATA"] = Path.Combine(sandboxHome, "AppData", "Roaming"),
                ["COMSPEC"] = Path.Combine(systemDirectory, "cmd.exe"),
                ["HOME"] = sandboxHome,
                ["LOCALAPPDATA"] = Path.Combine(sandboxHome, "AppData", "Local"),
                // V2 configuration discovery otherwise walks above the private
                // profile and fails on inaccessible host directories. Supply
                // only the configuration staged by CodeBadger in this profile.
                ["OPENCODE_CONFIG_PROJECT_DISABLE"] = "1",
                ["OPENCODE_CONFIG"] = Path.Combine(sandboxHome, "opencode.json"),
                ["OPENCODE_DISABLE_MODELS_FETCH"] = "1",
                ["PATH"] = systemDirectory,
                ["PATHEXT"] = ".COM;.EXE;.BAT;.CMD",
                ["SYSTEMROOT"] = windowsDirectory,
                ["TEMP"] = Path.Combine(sandboxHome, "Temp"),
                ["TMP"] = Path.Combine(sandboxHome, "Temp"),
                ["USERPROFILE"] = sandboxHome,
                ["WINDIR"] = windowsDirectory
            };

            // CreateProcessW requires UTF-16 entries terminated by NUL and
            // an additional NUL after the last entry, sorted by variable name.
            return Encoding.Unicode.GetBytes(string.Concat(values.Select(value => value.Key + "=" + value.Value + "\0")) + "\0");
        }

        private static void ValidatePath(string path, string parameterName)
        {
            if (string.IsNullOrWhiteSpace(path) || path.IndexOf('\0') >= 0 || !Path.IsPathFullyQualified(path))
                throw new ArgumentException("A fully qualified path without NUL characters is required.", parameterName);
        }
    }
}
