using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace TermWrap
{
    internal sealed class GlobalOptions
    {
        public string LogFolder;
        public string[] RemainingArgs = new string[0];
    }

    internal static class CliOptions
    {
        // Known value options are consumed as pairs. Values such as "--json"
        // remain data; the SSH suffix after -- is opaque to this parser.
        private static bool TakesValue(string option)
        {
            switch (option)
            {
                case "--session": case "-s": case "--host": case "--user":
                case "--password": case "--protocol": case "--port":
                case "--login-prompt": case "--password-prompt":
                case "--text": case "--line": case "--hex":
                case "--key": case "--control": return true;
                default: return false;
            }
        }

        public static GlobalOptions ParseGlobal(string[] args)
        {
            GlobalOptions options = new GlobalOptions();
            List<string> rest = new List<string>();
            bool help = false;
            for (int i = 0; i < args.Length; i++)
            {
                string value = args[i];
                if (value == "--" || value == "--daemon")
                {
                    for (; i < args.Length; i++) { rest.Add(args[i]); }
                    break;
                }
                if (value == "--json") { CliOutput.Json = true; continue; }
                if (value == "--log-folder")
                {
                    if (options.LogFolder != null) { throw CliException.Usage("duplicate --log-folder"); }
                    options.LogFolder = Value(args, ref i, value);
                    if (options.LogFolder.Length == 0) { throw CliException.Usage("--log-folder cannot be empty"); }
                    continue;
                }
                if (value == "--help" || value == "-h") { help = true; continue; }
                rest.Add(value);
                if (TakesValue(value)) { rest.Add(Value(args, ref i, value)); }
            }
            if (help)
            {
                options.RemainingArgs = rest.Count == 0
                    ? new[] { "help" } : new[] { "help", rest[0] };
            }
            else { options.RemainingArgs = rest.ToArray(); }
            return options;
        }

        private static string Value(string[] args, ref int index, string option)
        {
            if (++index >= args.Length) { throw CliException.Usage(option + " requires a value"); }
            return args[index];
        }

        public static void ValidateSession(string name)
        {
            try { SessionPaths.GetSessionDir(name); }
            catch (Exception ex)
            {
                if (ex is InvalidOperationException || ex is ArgumentException || ex is NotSupportedException)
                { throw CliException.Usage("invalid session name"); }
                throw;
            }
        }

        private static void Unique(HashSet<string> seen, string option)
        {
            if (!seen.Add(option)) { throw CliException.Usage("duplicate option: " + option); }
        }

        public static StartOptions ParseStart(string[] args)
        {
            StartOptions options = new StartOptions();
            HashSet<string> seen = new HashSet<string>(StringComparer.Ordinal);
            List<string> extras = new List<string>();
            for (int i = 1; i < args.Length; i++)
            {
                string option = args[i] == "-s" ? "--session" : args[i];
                if (option == "--")
                {
                    for (i++; i < args.Length; i++) { extras.Add(args[i]); }
                    break;
                }
                Unique(seen, option);
                switch (option)
                {
                    case "--host": options.Host = Value(args, ref i, option); break;
                    case "--user": options.UserName = Value(args, ref i, option); break;
                    case "--session":
                        options.SessionName = Value(args, ref i, option);
                        ValidateSession(options.SessionName); break;
                    case "--protocol":
                        string protocol = Value(args, ref i, option);
                        if (protocol.Equals("ssh", StringComparison.OrdinalIgnoreCase)) { options.Protocol = ConnectionProtocol.Ssh; }
                        else if (protocol.Equals("telnet", StringComparison.OrdinalIgnoreCase)) { options.Protocol = ConnectionProtocol.Telnet; }
                        else { throw CliException.Usage("--protocol must be ssh or telnet"); }
                        break;
                    case "--port":
                        if (!int.TryParse(Value(args, ref i, option), NumberStyles.None, CultureInfo.InvariantCulture, out options.Port)
                            || options.Port < 1 || options.Port > 65535)
                        { throw CliException.Usage("--port must be between 1 and 65535"); }
                        break;
                    case "--password": options.Password = Value(args, ref i, option); break;
                    case "--ask-password": options.AskPassword = true; break;
                    case "--password-stdin": options.PasswordStdin = true; break;
                    case "--login-prompt": options.LoginPrompt = Value(args, ref i, option); break;
                    case "--password-prompt": options.PasswordPrompt = Value(args, ref i, option); break;
                    case "--legacy-ssh": options.EnableLegacySsh = true; break;
                    case "--wait-ready": options.WaitReady = true; break;
                    default: throw CliException.Usage("unknown start argument; use start --help and put SSH arguments after --");
                }
            }
            if (string.IsNullOrWhiteSpace(options.Host) || options.Host.StartsWith("-", StringComparison.Ordinal)
                || options.Host.IndexOfAny(new[] { '\r', '\n', '\0' }) >= 0)
            { throw CliException.Usage("--host requires a valid host or IP address"); }
            int passwords = (seen.Contains("--password") ? 1 : 0) + (options.AskPassword ? 1 : 0) + (options.PasswordStdin ? 1 : 0);
            if (passwords > 1) { throw CliException.Usage("choose only one of --password, --ask-password, --password-stdin"); }
            if (options.Protocol == ConnectionProtocol.Telnet && (extras.Count > 0 || options.EnableLegacySsh))
            { throw CliException.Usage("SSH arguments and --legacy-ssh cannot be used with telnet"); }
            if (options.Port == 0) { options.Port = options.Protocol == ConnectionProtocol.Ssh ? 22 : 23; }
            if (options.Protocol == ConnectionProtocol.Ssh && options.EnableLegacySsh)
            {
                extras.InsertRange(0, new[] { "-o", "HostKeyAlgorithms=+ssh-rsa", "-o", "PubkeyAcceptedAlgorithms=+ssh-rsa", "-o", "MACs=hmac-sha1" });
            }
            bool portInExtras = extras.Exists(delegate(string value) { return value.StartsWith("-p", StringComparison.Ordinal); });
            if (seen.Contains("--port") && portInExtras)
            { throw CliException.Usage("specify the SSH port using --port or -p after --, not both"); }
            if (options.Protocol == ConnectionProtocol.Ssh && options.Port != 22 && !portInExtras)
            { extras.InsertRange(0, new[] { "-p", options.Port.ToString(CultureInfo.InvariantCulture) }); }
            options.ExtraArgs = extras.ToArray();
            return options;
        }

        public static void ReadPassword(StartOptions options)
        {
            if (options.PasswordStdin)
            {
                // One UTF-8 line, including a valid empty password. Never trim it.
                using (StreamReader reader = new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false, true)))
                {
                    options.Password = reader.ReadLine();
                }
                if (options.Password == null) { throw CliException.Usage("--password-stdin received no line"); }
            }
            else if (options.AskPassword)
            {
                if (Console.IsInputRedirected) { throw CliException.Usage("--ask-password requires a console; use --password-stdin for redirected input"); }
                Console.Error.Write("Password: ");
                StringBuilder secret = new StringBuilder();
                try
                {
                    while (true)
                    {
                        ConsoleKeyInfo key = Console.ReadKey(true);
                        if (key.Key == ConsoleKey.Enter) { break; }
                        if (key.Key == ConsoleKey.Backspace) { if (secret.Length > 0) { secret.Length--; } }
                        else if (!char.IsControl(key.KeyChar)) { secret.Append(key.KeyChar); }
                    }
                    options.Password = secret.ToString();
                }
                finally { Console.Error.WriteLine(); }
            }
        }

        public static StopOptions ParseStop(string[] args, bool pruneOnly)
        {
            StopOptions options = new StopOptions();
            HashSet<string> seen = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 1; i < args.Length; i++)
            {
                string option = args[i];
                if (option == "-s") { option = "--session"; }
                if (option == "--clear-stale") { option = "--force"; }
                Unique(seen, option);
                if (option == "--session")
                {
                    options.SessionName = Value(args, ref i, option);
                    ValidateSession(options.SessionName);
                }
                else if (option == "--all")
                {
                    if (pruneOnly)
                    {
                        throw CliException.Usage("prune does not support --all; specify --session");
                    }
                    options.All = true;
                }
                else if (!pruneOnly && option == "--force") { options.ClearStale = true; }
                else if (!pruneOnly && option == "--prune") { options.Prune = true; }
                else { throw CliException.Usage("unknown option; use " + args[0] + " --help"); }
            }
            if (options.All && options.SessionName != null)
            { throw CliException.Usage("--all and --session cannot be combined"); }
            if (options.All && options.Prune)
            { throw CliException.Usage("stop --all cannot be combined with --prune; prune one session at a time"); }
            return options;
        }

        public static SessionCommandOptions ParseSession(string[] args, string command)
        {
            SessionCommandOptions options = new SessionCommandOptions();
            HashSet<string> seen = new HashSet<string>(StringComparer.Ordinal);
            int inputs = 0;
            for (int i = 1; i < args.Length; i++)
            {
                string option = args[i];
                if (option == "-s") { option = "--session"; }
                if (option == "--control") { option = "--key"; }
                Unique(seen, option);
                if (option == "--session")
                {
                    options.SessionName = Value(args, ref i, option);
                    ValidateSession(options.SessionName);
                }
                else if (command == "read" && option == "--clear") { options.Clear = true; }
                else if (command == "tail" && option == "--wait") { options.Wait = true; }
                else if (command == "send" && (option == "--text" || option == "--line" || option == "--hex" || option == "--key"))
                {
                    string value = Value(args, ref i, option);
                    inputs++;
                    if (option == "--text") { options.TextValue = value; }
                    if (option == "--line")
                    {
                        if (value.IndexOfAny(new[] { '\r', '\n' }) >= 0)
                        { throw CliException.Usage("--line accepts one line without CR or LF; use --text for raw input"); }
                        options.LineValue = value;
                    }
                    if (option == "--hex") { options.HexValue = value; }
                    if (option == "--key") { options.ControlValue = value; }
                }
                else { throw CliException.Usage("unknown option; use " + command + " --help"); }
            }
            if (command == "send" && inputs != 1)
            { throw CliException.Usage("choose exactly one of --text, --line, --hex, --key"); }
            if (options.ControlValue != null)
            {
                string key = options.ControlValue.ToLowerInvariant();
                string[] keys = { "ctrl-c", "ctrl-d", "ctrl-z", "esc", "tab", "enter", "up", "down", "left", "right", "backspace" };
                if (Array.IndexOf(keys, key) < 0) { throw CliException.Usage("unknown key; see send --help"); }
                options.ControlValue = key;
            }
            if (options.HexValue != null)
            {
                string hex = options.HexValue.Replace(" ", "").Replace("-", "");
                if ((hex.Length % 2) != 0) { throw CliException.Usage("--hex requires an even number of hex digits"); }
                foreach (char c in hex) { if (!Uri.IsHexDigit(c)) { throw CliException.Usage("--hex contains a non-hex digit"); } }
            }
            return options;
        }
    }
}
