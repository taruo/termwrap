using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Pipes;
using System.Reflection;
using System.Text;
using System.Threading;

[assembly: AssemblyTitle("termwrap")]
[assembly: AssemblyDescription("Persistent ssh/telnet session wrapper")]
[assembly: AssemblyCompany("Codex")]
[assembly: AssemblyProduct("termwrap")]
[assembly: AssemblyVersion("1.0.8.0")]
[assembly: AssemblyFileVersion("1.0.8.0")]
// Version: 1.0.8

namespace TermWrap
{
    internal static class Program
    {
        private const string AppVersion = "1.0.8";
        private const int TailPollIntervalMs = 2000;

        private static int Main(string[] args)
        {
            Console.OutputEncoding = Encoding.UTF8;
            Logger.Initialize(AppVersion);
            Logger.Info("process start args={0}", string.Join(" ", args));

            if (args.Length > 0 && args[0] == "--daemon")
            {
                return RunWithLogging(delegate { return DaemonMain(args); });
            }

            return RunWithLogging(delegate { return CliMain(args); });
        }

        private static int RunWithLogging(Func<int> action)
        {
            try
            {
                return action();
            }
            catch (Exception ex)
            {
                Logger.Error("fatal: " + ex);
                Console.Error.WriteLine("error: " + ex.Message);
                return 1;
            }
        }

        private static int CliMain(string[] args)
        {
            if (args.Length == 0)
            {
                PrintUsage();
                return 1;
            }

            string command = args[0].ToLowerInvariant();
            if (command == "start") { return StartCommand(args); }
            if (command == "stop") { return StopCommand(args); }
            if (command == "list") { return ListCommand(args); }
            if (command == "tail") { return TailCommand(ResolveTargetSession(args, 1)); }
            if (command == "read") { return ReadCommand(args); }
            if (command == "send") { return SendCommand(args); }
            if (command == "help" || command == "--help" || command == "-h")
            {
                PrintUsage();
                return 0;
            }

            throw new InvalidOperationException("unknown command: " + args[0]);
        }

        private static int DaemonMain(string[] args)
        {
            if (args.Length < 9)
            {
                throw new InvalidOperationException("daemon arguments are incomplete");
            }

            SessionDaemon daemon = new SessionDaemon(
                args[1],
                args[2],
                args[3],
                DecodeOrEmpty(args[4]),
                DecodeOrEmpty(args[5]),
                DecodeOrEmpty(args[6]),
                DecodeOrEmpty(args[7]),
                DecodeOrEmpty(args[8]));
            return daemon.Run();
        }

        private static int StartCommand(string[] args)
        {
            StartOptions options = ParseStartOptions(args);
            SessionInfo existing = SessionInfo.Load(options.SessionName);
            if (existing.IsAlive())
            {
                throw new InvalidOperationException("session already running: " + options.SessionName);
            }

            string executablePath;
            string transportArguments;
            if (options.Protocol == ConnectionProtocol.Ssh)
            {
                executablePath = ResolveSshPath();
                transportArguments = BuildSshArguments(options);
            }
            else
            {
                executablePath = string.Empty;
                transportArguments = options.Port.ToString(CultureInfo.InvariantCulture);
            }

            string exePath = Process.GetCurrentProcess().MainModule.FileName;
            string daemonArgs = string.Format(
                CultureInfo.InvariantCulture,
                "--daemon {0} {1} {2} {3} {4} {5} {6} {7}",
                QuoteArg(options.SessionName),
                QuoteArg(options.Protocol.ToString().ToLowerInvariant()),
                QuoteArg(options.Host),
                QuoteArg(EncodeOrEmpty(executablePath)),
                QuoteArg(EncodeOrEmpty(transportArguments)),
                QuoteArg(EncodeOrEmpty(options.UserName)),
                QuoteArg(EncodeOrEmpty(options.Password)),
                QuoteArg(EncodeOrEmpty(options.GetPromptConfig())));

            Logger.Info(
                "start session={0} protocol={1} host={2} port={3}",
                options.SessionName,
                options.Protocol.ToString(),
                options.Host,
                options.Port.ToString(CultureInfo.InvariantCulture));

            Process process = Process.Start(new ProcessStartInfo
            {
                FileName = exePath,
                Arguments = daemonArgs,
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden
            });
            if (process == null)
            {
                throw new InvalidOperationException("failed to start daemon process");
            }

            DateTime deadline = DateTime.UtcNow.AddSeconds(8);
            while (DateTime.UtcNow < deadline)
            {
                Thread.Sleep(200);
                SessionInfo info = SessionInfo.Load(options.SessionName);
                if (info.IsAlive())
                {
                    Console.WriteLine("started " + options.SessionName);
                    return 0;
                }

                if (process.HasExited)
                {
                    throw new InvalidOperationException("daemon exited before session became ready");
                }
            }

            throw new InvalidOperationException("session did not become ready in time");
        }

        private static int StopCommand(string[] args)
        {
            StopOptions options = ParseStopOptions(args);
            if (!options.ClearStale)
            {
                return SendSimpleCommand(options.SessionName, "STOP");
            }

            SessionInfo info = SessionInfo.Load(options.SessionName);
            if (info.IsAlive())
            {
                try
                {
                    SendSimpleCommand(options.SessionName, "STOP");
                    Thread.Sleep(500);
                }
                catch (Exception ex)
                {
                    Logger.Error("stop clear-stale graceful stop failed session=" + options.SessionName + " " + ex);
                }
            }

            SessionDaemon.ClearStaleProcesses(SessionInfo.Load(options.SessionName));
            Console.WriteLine("cleared stale " + options.SessionName);
            return 0;
        }

        private static int ListCommand(string[] args)
        {
            bool showAll = false;
            bool verbose = false;
            for (int i = 1; i < args.Length; i++)
            {
                if (args[i] == "--all" || args[i] == "-a") { showAll = true; continue; }
                if (args[i] == "--verbose" || args[i] == "-v") { verbose = true; continue; }
                throw new InvalidOperationException("usage: list [--all] [--verbose]");
            }

            Directory.CreateDirectory(SessionPaths.SessionsRoot);
            string[] dirs = Directory.GetDirectories(SessionPaths.SessionsRoot);
            if (dirs.Length == 0)
            {
                Console.WriteLine("no sessions");
                return 0;
            }

            int shown = 0;
            foreach (string dir in dirs)
            {
                string sessionName = Path.GetFileName(dir);
                SessionInfo info = SessionInfo.Load(sessionName);
                bool alive = info.IsAlive();
                if (!showAll && !alive)
                {
                    continue;
                }

                if (verbose)
                {
                    Console.WriteLine(
                        "{0}\t{1}\tpid={2}\tremotePid={3}\tprotocol={4}\thost={5}\tport={6}\tstarted={7}\tauth={8}\ttarget={9}",
                        sessionName,
                        alive ? "running" : "stopped",
                        info.DaemonPid,
                        info.RemotePid,
                        EmptyDash(info.Protocol),
                        EmptyDash(info.Host),
                        info.Port <= 0 ? "-" : info.Port.ToString(CultureInfo.InvariantCulture),
                        EmptyDash(info.StartedAtUtc),
                        EmptyDash(info.AuthMode),
                        EmptyDash(info.Target));
                }
                else
                {
                    Console.WriteLine(
                        "{0}\t{1}\t{2}\t{3}:{4}",
                        sessionName,
                        alive ? "running" : "stopped",
                        EmptyDash(info.Protocol),
                        EmptyDash(info.Host),
                        info.Port <= 0 ? "-" : info.Port.ToString(CultureInfo.InvariantCulture));
                }
                shown++;
            }

            if (shown == 0)
            {
                Console.WriteLine("no sessions");
            }

            return 0;
        }

        private static int TailCommand(string sessionName)
        {
            Console.WriteLine("tailing " + sessionName + "  source: memory-buffer  stop: Ctrl+C  interval: 2s");
            long nextOffset = -1;
            while (true)
            {
                TailSnapshot snapshot;
                try
                {
                    snapshot = ReadTailSnapshot(sessionName, nextOffset);
                }
                catch (Exception ex)
                {
                    if (!SessionInfo.Load(sessionName).IsAlive())
                    {
                        return 0;
                    }

                    throw new InvalidOperationException("tail failed: " + ex.Message, ex);
                }

                if (snapshot.Data.Length > 0)
                {
                    Console.Write(Encoding.UTF8.GetString(snapshot.Data));
                }

                nextOffset = snapshot.EndOffset;
                if (!SessionInfo.Load(sessionName).IsAlive())
                {
                    return 0;
                }

                Thread.Sleep(TailPollIntervalMs);
            }
        }

        private static int ReadCommand(string[] args)
        {
            bool clear = false;
            string sessionName = null;
            for (int i = 1; i < args.Length; i++)
            {
                string value = args[i];
                if (value == "--clear")
                {
                    clear = true;
                    continue;
                }

                if (sessionName == null)
                {
                    sessionName = value;
                    continue;
                }

                throw new InvalidOperationException("usage: read [session] [--clear]");
            }

            if (string.IsNullOrEmpty(sessionName))
            {
                sessionName = ResolveTargetSession(new[] { "read" }, 1);
            }

            TailSnapshot snapshot = ReadBufferSnapshot(sessionName, clear);
            if (snapshot.Data.Length > 0)
            {
                Console.Write(Encoding.UTF8.GetString(snapshot.Data));
            }

            return 0;
        }

        private static int SendCommand(string[] args)
        {
            if (args.Length < 3)
            {
                throw new InvalidOperationException("usage: send [session] <text|hex|control|binary-file> <value>");
            }

            int modeIndex = args.Length >= 4 ? 2 : 1;
            string sessionName = ResolveTargetSession(args, 1, modeIndex);
            string mode = args[modeIndex].ToLowerInvariant();
            string value = JoinArgs(args, modeIndex + 1);

            if (string.IsNullOrEmpty(value))
            {
                throw new InvalidOperationException("send value is required");
            }

            if (mode == "text") { return SendSimpleCommand(sessionName, "SEND_TEXT " + Convert.ToBase64String(Encoding.UTF8.GetBytes(value))); }
            if (mode == "hex") { return SendSimpleCommand(sessionName, "SEND_HEX " + value); }
            if (mode == "binary-file")
            {
                byte[] data = File.ReadAllBytes(value);
                return SendSimpleCommand(sessionName, "SEND_HEX " + ToHex(data));
            }
            if (mode == "control") { return SendSimpleCommand(sessionName, "SEND_CONTROL " + value); }

            throw new InvalidOperationException("unknown send mode: " + mode);
        }

        private static int SendSimpleCommand(string sessionName, string command)
        {
            SessionInfo info = SessionInfo.Load(sessionName);
            if (!info.IsAlive())
            {
                if (string.Equals(command, "STOP", StringComparison.OrdinalIgnoreCase))
                {
                    Console.WriteLine("already stopped");
                    return 0;
                }

                throw new InvalidOperationException("session is not running: " + sessionName);
            }

            string response = SendCommandAndReadResponse(sessionName, command);
            EnsureOk(response);
            Console.WriteLine(response.Substring(3).Trim());
            return 0;
        }

        private static TailSnapshot ReadTailSnapshot(string sessionName, long offset)
        {
            string response = SendCommandAndReadResponse(sessionName, "TAIL " + offset.ToString(CultureInfo.InvariantCulture));
            return ParseBufferSnapshotResponse(response, "TAIL");
        }

        private static TailSnapshot ReadBufferSnapshot(string sessionName, bool clear)
        {
            string response = SendCommandAndReadResponse(sessionName, clear ? "READ_CLEAR" : "READ");
            return ParseBufferSnapshotResponse(response, clear ? "READ_CLEAR" : "READ");
        }

        private static TailSnapshot ParseBufferSnapshotResponse(string response, string expectedKind)
        {
            if (!response.StartsWith("OK ", StringComparison.Ordinal))
            {
                throw new InvalidOperationException(response);
            }

            string[] parts = response.Split(new[] { ' ' }, 5);
            if (parts.Length < 5 || parts[1] != expectedKind)
            {
                throw new InvalidOperationException("invalid buffer response: " + response);
            }

            TailSnapshot snapshot = new TailSnapshot();
            snapshot.StartOffset = long.Parse(parts[2], CultureInfo.InvariantCulture);
            snapshot.EndOffset = long.Parse(parts[3], CultureInfo.InvariantCulture);
            snapshot.Data = string.IsNullOrEmpty(parts[4]) || parts[4] == "-" ? new byte[0] : Convert.FromBase64String(parts[4]);
            return snapshot;
        }

        private static string SendCommandAndReadResponse(string sessionName, string command)
        {
            try
            {
                using (NamedPipeClientStream pipe = new NamedPipeClientStream(".", SessionPaths.GetCommandPipeName(sessionName), PipeDirection.InOut))
                {
                    pipe.Connect(5000);
                    using (StreamWriter writer = new StreamWriter(pipe, Encoding.UTF8, 1024, true))
                    using (StreamReader reader = new StreamReader(pipe, Encoding.UTF8, false, 1024, true))
                    {
                        writer.NewLine = "\n";
                        writer.WriteLine(command);
                        writer.Flush();
                        return reader.ReadLine() ?? "ERR empty response";
                    }
                }
            }
            catch (TimeoutException)
            {
                throw new InvalidOperationException("session command pipe timed out: " + sessionName);
            }
            catch (IOException ex)
            {
                throw new InvalidOperationException("session command pipe failed: " + sessionName + " " + ex.Message);
            }
        }

        private static void EnsureOk(string response)
        {
            if (!response.StartsWith("OK ", StringComparison.Ordinal))
            {
                throw new InvalidOperationException(response);
            }
        }

        private static StartOptions ParseStartOptions(string[] args)
        {
            if (args.Length < 2)
            {
                throw new InvalidOperationException("usage: start [session] [--protocol ssh|telnet|auto] [--user USER] [--password PASSWORD] [--port PORT] [--login-prompt TEXT] [--password-prompt TEXT] host[:port] [ssh-args...]");
            }

            StartOptions options = new StartOptions();
            int index = 1;

            if (index < args.Length && !args[index].StartsWith("-", StringComparison.Ordinal))
            {
                if (index + 1 < args.Length && !args[index + 1].StartsWith("-", StringComparison.Ordinal))
                {
                    options.SessionName = args[index];
                    index++;
                }
                else if (index + 1 < args.Length && args[index + 1].StartsWith("--", StringComparison.Ordinal))
                {
                    options.SessionName = args[index];
                    index++;
                }
            }

            while (index < args.Length && args[index].StartsWith("-", StringComparison.Ordinal))
            {
                string option = args[index];
                if (option == "--protocol" || option == "--proto") { options.Protocol = ParseProtocol(ReadOptionValue(args, ref index, option), options); continue; }
                if (option == "--user") { options.UserName = ReadOptionValue(args, ref index, option); continue; }
                if (option == "--password") { options.Password = ReadOptionValue(args, ref index, option); continue; }
                if (option == "--name") { options.SessionName = ReadOptionValue(args, ref index, option); continue; }
                if (option == "--port") { options.Port = int.Parse(ReadOptionValue(args, ref index, option), CultureInfo.InvariantCulture); continue; }
                if (option == "--login-prompt") { options.LoginPrompt = ReadOptionValue(args, ref index, option); continue; }
                if (option == "--password-prompt") { options.PasswordPrompt = ReadOptionValue(args, ref index, option); continue; }
                if (option == "--legacy-ssh") { options.EnableLegacySsh = true; index++; continue; }
                break;
            }

            if (index >= args.Length)
            {
                throw new InvalidOperationException("host is required");
            }

            ParseHostAndPort(args[index++], options);
            if (string.IsNullOrEmpty(options.SessionName))
            {
                options.SessionName = GenerateSessionName(options.Host);
            }

            List<string> extras = new List<string>();
            while (index < args.Length)
            {
                extras.Add(args[index]);
                index++;
            }

            options.ExtraArgs = extras.ToArray();
            if (options.Protocol == ConnectionProtocol.Auto)
            {
                options.Protocol = options.Port == 23 ? ConnectionProtocol.Telnet : ConnectionProtocol.Ssh;
            }

            if (options.Protocol == ConnectionProtocol.Telnet && options.Port <= 0)
            {
                options.Port = 23;
            }

            if (options.Protocol == ConnectionProtocol.Ssh && options.Port <= 0)
            {
                options.Port = 22;
            }

            if (options.Protocol == ConnectionProtocol.Ssh && options.EnableLegacySsh)
            {
                List<string> legacyExtras = new List<string>();
                legacyExtras.Add("-o");
                legacyExtras.Add("HostKeyAlgorithms=+ssh-rsa");
                legacyExtras.Add("-o");
                legacyExtras.Add("PubkeyAcceptedAlgorithms=+ssh-rsa");
                legacyExtras.Add("-o");
                legacyExtras.Add("MACs=hmac-sha1");
                legacyExtras.AddRange(options.ExtraArgs);
                options.ExtraArgs = legacyExtras.ToArray();
            }

            if (options.Protocol == ConnectionProtocol.Ssh && options.Port > 0 && !ContainsPortOption(options.ExtraArgs) && options.Port != 22)
            {
                List<string> sshExtras = new List<string>();
                sshExtras.Add("-p");
                sshExtras.Add(options.Port.ToString(CultureInfo.InvariantCulture));
                sshExtras.AddRange(options.ExtraArgs);
                options.ExtraArgs = sshExtras.ToArray();
            }

            return options;
        }

        private static void ParseHostAndPort(string input, StartOptions options)
        {
            string host = input;
            if (input.StartsWith("[", StringComparison.Ordinal) && input.Contains("]:"))
            {
                int end = input.IndexOf("]:", StringComparison.Ordinal);
                host = input.Substring(1, end - 1);
                options.Port = int.Parse(input.Substring(end + 2), CultureInfo.InvariantCulture);
            }
            else
            {
                int firstColon = input.IndexOf(':');
                int lastColon = input.LastIndexOf(':');
                if (firstColon > 0 && firstColon == lastColon)
                {
                    string maybePort = input.Substring(firstColon + 1);
                    int parsedPort;
                    if (int.TryParse(maybePort, NumberStyles.Integer, CultureInfo.InvariantCulture, out parsedPort))
                    {
                        host = input.Substring(0, firstColon);
                        options.Port = parsedPort;
                    }
                }
            }

            options.Host = host;
        }

        private static bool ContainsPortOption(string[] args)
        {
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "-p") { return true; }
            }
            return false;
        }

        private static ConnectionProtocol ParseProtocol(string value, StartOptions options)
        {
            if (string.Equals(value, "ssh", StringComparison.OrdinalIgnoreCase)) { return ConnectionProtocol.Ssh; }
            if (string.Equals(value, "telnet", StringComparison.OrdinalIgnoreCase)) { return ConnectionProtocol.Telnet; }
            if (string.Equals(value, "auto", StringComparison.OrdinalIgnoreCase)) { return ConnectionProtocol.Auto; }
            throw new InvalidOperationException("unsupported protocol: " + value);
        }

        private static string ReadOptionValue(string[] args, ref int index, string option)
        {
            index++;
            if (index >= args.Length)
            {
                throw new InvalidOperationException(option + " requires a value");
            }

            string value = args[index];
            index++;
            return value;
        }

        private static string BuildSshArguments(StartOptions options)
        {
            List<string> parts = new List<string>();
            if (!string.IsNullOrEmpty(options.UserName))
            {
                parts.Add("-l");
                parts.Add(options.UserName);
            }

            if (!HasTtyOption(options.ExtraArgs))
            {
                parts.Add("-tt");
            }

            parts.Add(options.Host);
            for (int i = 0; i < options.ExtraArgs.Length; i++)
            {
                parts.Add(options.ExtraArgs[i]);
            }

            if (!HasSshOption(options.ExtraArgs, "StrictHostKeyChecking"))
            {
                parts.Add("-o");
                parts.Add("StrictHostKeyChecking=accept-new");
            }

            return JoinList(parts);
        }

        private static StopOptions ParseStopOptions(string[] args)
        {
            StopOptions options = new StopOptions();
            for (int i = 1; i < args.Length; i++)
            {
                string value = args[i];
                if (value == "--clear-stale")
                {
                    options.ClearStale = true;
                    continue;
                }

                if (options.SessionName == null)
                {
                    options.SessionName = value;
                    continue;
                }

                throw new InvalidOperationException("usage: stop [session] [--clear-stale]");
            }

            if (string.IsNullOrEmpty(options.SessionName))
            {
                options.SessionName = ResolveTargetSession(new[] { "stop" }, 1);
            }

            return options;
        }

        private static bool HasSshOption(string[] args, string optionName)
        {
            for (int i = 0; i < args.Length; i++)
            {
                if (string.Equals(args[i], "-o", StringComparison.OrdinalIgnoreCase) &&
                    i + 1 < args.Length &&
                    args[i + 1].StartsWith(optionName + "=", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool HasTtyOption(string[] args)
        {
            for (int i = 0; i < args.Length; i++)
            {
                string value = args[i];
                if (string.Equals(value, "-T", StringComparison.OrdinalIgnoreCase)) { return true; }
                if (value.StartsWith("-t", StringComparison.OrdinalIgnoreCase)) { return true; }
            }

            return false;
        }

        private static string ResolveTargetSession(string[] args, int defaultArgIndex)
        {
            return ResolveTargetSession(args, defaultArgIndex, args.Length);
        }

        private static string ResolveTargetSession(string[] args, int defaultArgIndex, int modeIndex)
        {
            if (args.Length > defaultArgIndex && defaultArgIndex < modeIndex)
            {
                return args[defaultArgIndex];
            }

            string[] sessions = SessionPaths.GetKnownSessions(false);
            if (sessions.Length == 1)
            {
                return sessions[0];
            }

            throw new InvalidOperationException("session name is required");
        }

        private static string ResolveSshPath()
        {
            string preferred = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "OpenSSH", "ssh.exe");
            if (File.Exists(preferred))
            {
                return preferred;
            }

            try
            {
                Process process = Process.Start(new ProcessStartInfo
                {
                    FileName = "where.exe",
                    Arguments = "ssh.exe",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true
                });
                if (process != null)
                {
                    string output = process.StandardOutput.ReadToEnd();
                    process.WaitForExit(1000);
                    string[] lines = output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                    if (lines.Length > 0)
                    {
                        return lines[0];
                    }
                }
            }
            catch
            {
            }

            throw new InvalidOperationException("ssh.exe not found");
        }

        private static string GenerateSessionName(string host)
        {
            return SessionPaths.Sanitize(host + "-" + DateTime.UtcNow.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture));
        }

        private static string JoinList(List<string> parts)
        {
            StringBuilder builder = new StringBuilder();
            for (int i = 0; i < parts.Count; i++)
            {
                if (i > 0) { builder.Append(' '); }
                builder.Append(QuoteArg(parts[i]));
            }
            return builder.ToString();
        }

        private static string JoinArgs(string[] args, int startIndex)
        {
            if (startIndex >= args.Length) { return string.Empty; }

            StringBuilder builder = new StringBuilder();
            for (int i = startIndex; i < args.Length; i++)
            {
                if (builder.Length > 0) { builder.Append(' '); }
                builder.Append(args[i]);
            }
            return builder.ToString();
        }

        private static string EncodeOrEmpty(string value)
        {
            return string.IsNullOrEmpty(value) ? "-" : Convert.ToBase64String(Encoding.UTF8.GetBytes(value));
        }

        private static string DecodeOrEmpty(string value)
        {
            if (string.IsNullOrEmpty(value) || value == "-") { return string.Empty; }
            return Encoding.UTF8.GetString(Convert.FromBase64String(value));
        }

        private static string QuoteArg(string value)
        {
            if (string.IsNullOrEmpty(value)) { return "\"\""; }
            bool needsQuotes = false;
            for (int i = 0; i < value.Length; i++)
            {
                char c = value[i];
                if (char.IsWhiteSpace(c) || c == '"')
                {
                    needsQuotes = true;
                    break;
                }
            }

            if (!needsQuotes)
            {
                return value;
            }

            return "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
        }

        private static string ToHex(byte[] data)
        {
            StringBuilder builder = new StringBuilder(data.Length * 2);
            for (int i = 0; i < data.Length; i++)
            {
                builder.Append(data[i].ToString("X2", CultureInfo.InvariantCulture));
            }
            return builder.ToString();
        }

        private static string EmptyDash(string value)
        {
            return string.IsNullOrEmpty(value) ? "-" : value;
        }

        private static void PrintUsage()
        {
            Console.WriteLine("termwrap.exe start [session] [--protocol ssh|telnet|auto] [--user USER] [--password PASSWORD] [--port PORT] [--login-prompt TEXT] [--password-prompt TEXT] [--legacy-ssh] host[:port] [ssh-args...]");
            Console.WriteLine("termwrap.exe stop [session] [--clear-stale]");
            Console.WriteLine("termwrap.exe list [--all] [--verbose]");
            Console.WriteLine("termwrap.exe tail [session]");
            Console.WriteLine("termwrap.exe read [session] [--clear]");
            Console.WriteLine("termwrap.exe send [session] text <text>");
            Console.WriteLine("termwrap.exe send [session] hex <hex-bytes>");
            Console.WriteLine("termwrap.exe send [session] binary-file <path>");
            Console.WriteLine("termwrap.exe send [session] control <ctrl-c|ctrl-d|ctrl-z|esc|tab|enter|up|down|left|right|backspace>");
            Console.WriteLine("default protocol: ssh");
            Console.WriteLine("protocol=auto: port 23 -> telnet, otherwise ssh");
            Console.WriteLine("--legacy-ssh: add ssh-rsa / hmac-sha1 compatibility options");
        }
    }
}






