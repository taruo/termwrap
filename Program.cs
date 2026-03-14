using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Pipes;
using System.Reflection;
using System.Security.Principal;
using System.Text;
using System.Threading;

[assembly: AssemblyTitle("termwrap")]
[assembly: AssemblyDescription("Persistent ssh/telnet session wrapper")]
[assembly: AssemblyCompany("Codex")]
[assembly: AssemblyProduct("termwrap")]
namespace TermWrap
{
    internal static class Program
    {
        private const string AppVersion = "current";
        private const int TailPollIntervalMs = 2000;
        private const int StartReadyTimeoutMs = 8000;
        private const int StartReadyPollIntervalMs = 200;
        private const int StopExitTimeoutMs = 5000;
        private const int StopExitPollIntervalMs = 100;
        private static string _currentLogFolder;
        
        private sealed class GlobalOptions
        {
            public string LogFolder;
            public string[] RemainingArgs = new string[0];
        }

        private static int Main(string[] args)
        {
            Console.OutputEncoding = Encoding.UTF8;
            GlobalOptions globalOptions = ParseGlobalOptions(args);
            _currentLogFolder = globalOptions.LogFolder;
            Logger.Initialize(AppVersion, globalOptions.LogFolder);
            Logger.Info("process start args={0}", string.Join(" ", globalOptions.RemainingArgs));

            if (globalOptions.RemainingArgs.Length > 0 && globalOptions.RemainingArgs[0] == "--daemon")
            {
                return RunWithLogging(delegate { return DaemonMain(globalOptions.RemainingArgs); });
            }

            return RunWithLogging(delegate { return CliMain(globalOptions.RemainingArgs); });
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
            if (command == "tail") { return TailCommand(args); }
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
            if (string.IsNullOrEmpty(options.SessionName))
            {
                options.SessionName = GenerateSessionName(options.Protocol);
            }
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
            List<string> daemonParts = new List<string>();
            if (!string.IsNullOrEmpty(_currentLogFolder))
            {
                daemonParts.Add("--log-folder");
                daemonParts.Add(_currentLogFolder);
            }

            daemonParts.Add("--daemon");
            daemonParts.Add(options.SessionName);
            daemonParts.Add(options.Protocol.ToString().ToLowerInvariant());
            daemonParts.Add(options.Host);
            daemonParts.Add(EncodeOrEmpty(executablePath));
            daemonParts.Add(EncodeOrEmpty(transportArguments));
            daemonParts.Add(EncodeOrEmpty(options.UserName));
            daemonParts.Add(EncodeOrEmpty(options.Password));
            daemonParts.Add(EncodeOrEmpty(options.GetPromptConfig()));
            string daemonArgs = JoinList(daemonParts);

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

            DateTime deadline = DateTime.UtcNow.AddMilliseconds(StartReadyTimeoutMs);
            while (DateTime.UtcNow < deadline)
            {
                Thread.Sleep(StartReadyPollIntervalMs);
                SessionInfo info = SessionInfo.Load(options.SessionName);
                if (info.IsAlive() && IsCommandPipeReady(options.SessionName) && (!options.WaitReady || IsSessionReady(options)))
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

        private static bool IsCommandPipeReady(string sessionName)
        {
            try
            {
                return string.Equals(SendCommandAndReadResponse(sessionName, "PING", 500), "OK pong", StringComparison.Ordinal);
            }
            catch (InvalidOperationException ex)
            {
                Logger.Info("start readiness waiting session={0} detail={1}", sessionName, ex.Message);
                return false;
            }
        }

        private static bool IsSessionReady(StartOptions options)
        {
            try
            {
                TailSnapshot snapshot = ReadBufferSnapshot(options.SessionName, false);
                string text = Encoding.UTF8.GetString(snapshot.Data ?? new byte[0]);
                if (string.IsNullOrEmpty(text))
                {
                    return false;
                }

                if (options.Protocol == ConnectionProtocol.Ssh)
                {
                    return text.Contains("# ") || text.Contains("$ ");
                }

                return text.Contains("]#") || text.Contains(":/#") || text.Contains("@") && text.Contains("#");
            }
            catch (InvalidOperationException ex)
            {
                Logger.Info("start ready wait session={0} detail={1}", options.SessionName, ex.Message);
                return false;
            }
        }

        private static int StopCommand(string[] args)
        {
            StopOptions options = ParseStopOptions(args);
            if (options.Prune && string.IsNullOrEmpty(options.SessionName))
            {
                return PruneAllSessions(options.ClearStale);
            }

            return StopSingleSession(options);
        }

        private static int StopSingleSession(StopOptions options)
        {
            bool sessionExists = SessionPaths.SessionDirectoryExists(options.SessionName);
            SessionInfo info = SessionInfo.Load(options.SessionName);
            if (!options.ClearStale)
            {
                try
                {
                    SendSimpleCommand(options.SessionName, "STOP");
                    WaitForSessionExit(options.SessionName, StopExitTimeoutMs);
                }
                catch (InvalidOperationException ex)
                {
                    if (!ex.Message.StartsWith("session is not running:", StringComparison.Ordinal))
                    {
                        throw;
                    }
                }
            }
            else
            {
                if (info.IsAlive())
                {
                    try
                    {
                        SendSimpleCommand(options.SessionName, "STOP");
                        WaitForSessionExit(options.SessionName, StopExitTimeoutMs);
                    }
                    catch (Exception ex)
                    {
                        Logger.Error("stop clear-stale graceful stop failed session=" + options.SessionName + " " + ex);
                    }
                }

                SessionDaemon.ClearStaleProcesses(SessionInfo.Load(options.SessionName));
                Console.WriteLine("cleared stale " + options.SessionName);
            }

            if (options.Prune)
            {
                Thread.Sleep(200);
                SessionInfo refreshed = SessionInfo.Load(options.SessionName);
                if (refreshed.IsAlive())
                {
                    throw new InvalidOperationException("cannot prune running session: " + options.SessionName);
                }

                if (!sessionExists)
                {
                    Console.WriteLine("no session data");
                    return 0;
                }

                SessionPaths.DeleteSessionDirectory(options.SessionName);
                Console.WriteLine("pruned " + options.SessionName);
            }

            return 0;
        }

        private static void WaitForSessionExit(string sessionName, int timeoutMs)
        {
            DateTime deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            while (DateTime.UtcNow < deadline)
            {
                if (!SessionInfo.Load(sessionName).IsAlive())
                {
                    return;
                }

                Thread.Sleep(StopExitPollIntervalMs);
            }

            Logger.Info("stop wait timed out session={0}", sessionName);
        }

        private static int PruneAllSessions(bool clearStale)
        {
            string[] sessions = SessionPaths.GetKnownSessions(true);
            if (sessions.Length == 0)
            {
                Console.WriteLine("no sessions");
                return 0;
            }

            for (int i = 0; i < sessions.Length; i++)
            {
                StopOptions options = new StopOptions();
                options.SessionName = sessions[i];
                options.ClearStale = clearStale;
                options.Prune = true;
                StopSingleSession(options);
            }

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

        private static int TailCommand(string[] args)
        {
            SessionCommandOptions options = ParseSessionCommandOptions(args, "tail");
            string sessionName = ResolveTailSession(options);
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
            SessionCommandOptions options = ParseSessionCommandOptions(args, "read");
            TailSnapshot snapshot = ReadBufferSnapshot(options.SessionName, options.Clear);
            if (snapshot.Data.Length > 0)
            {
                Console.Write(Encoding.UTF8.GetString(snapshot.Data));
            }

            return 0;
        }

        private static int SendCommand(string[] args)
        {
            SessionCommandOptions options = ParseSessionCommandOptions(args, "send");
            string command = null;

            if (!string.IsNullOrEmpty(options.TextValue))
            {
                command = "SEND_TEXT " + Convert.ToBase64String(Encoding.UTF8.GetBytes(options.TextValue));
            }
            else if (!string.IsNullOrEmpty(options.HexValue))
            {
                command = "SEND_HEX " + options.HexValue;
            }
            else if (!string.IsNullOrEmpty(options.ControlValue))
            {
                command = "SEND_CONTROL " + options.ControlValue;
            }

            if (string.IsNullOrEmpty(command))
            {
                throw new InvalidOperationException("usage: send [--session SESSION] (--text TEXT | --hex HEX | --control CONTROL)");
            }

            return SendSimpleCommand(options.SessionName, command);
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
            return SendCommandAndReadResponse(sessionName, command, 5000);
        }

        private static string SendCommandAndReadResponse(string sessionName, string command, int connectTimeoutMs)
        {
            string pipeName = SessionPaths.GetCommandPipeName(sessionName);
            try
            {
                Logger.Info(
                    "pipe client connect begin session={0} pipe={1} command={2} identity={3}",
                    sessionName,
                    pipeName,
                    command,
                    GetCurrentIdentityForLog());
                using (NamedPipeClientStream pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut))
                {
                    pipe.Connect(connectTimeoutMs);
                    Logger.Info("pipe client connect ok session={0} pipe={1} isConnected={2}", sessionName, pipeName, pipe.IsConnected);
                    using (StreamWriter writer = new StreamWriter(pipe, Encoding.UTF8, 1024, true))
                    using (StreamReader reader = new StreamReader(pipe, Encoding.UTF8, false, 1024, true))
                    {
                        writer.NewLine = "\n";
                        writer.WriteLine(command);
                        writer.Flush();
                        string response = reader.ReadLine() ?? "ERR empty response";
                        Logger.Info("pipe client response session={0} pipe={1} response={2}", sessionName, pipeName, response);
                        return response;
                    }
                }
            }
            catch (TimeoutException)
            {
                Logger.Error("pipe client timeout session=" + sessionName + " pipe=" + pipeName + " command=" + command);
                throw new InvalidOperationException("session command pipe timed out: " + sessionName);
            }
            catch (UnauthorizedAccessException ex)
            {
                Logger.Error("pipe client unauthorized session=" + sessionName + " pipe=" + pipeName + " command=" + command + " identity=" + GetCurrentIdentityForLog() + " " + ex.Message);
                throw;
            }
            catch (IOException ex)
            {
                Logger.Error("pipe client io session=" + sessionName + " pipe=" + pipeName + " command=" + command + " " + ex.Message);
                throw new InvalidOperationException("session command pipe failed: " + sessionName + " " + ex.Message);
            }
        }

        private static string ResolveTailSession(SessionCommandOptions options)
        {
            if (!string.IsNullOrEmpty(options.SessionName))
            {
                if (!options.Wait || SessionInfo.Load(options.SessionName).IsAlive())
                {
                    return options.SessionName;
                }

                Console.WriteLine("waiting for session " + options.SessionName);
                while (true)
                {
                    if (SessionInfo.Load(options.SessionName).IsAlive())
                    {
                        return options.SessionName;
                    }

                    Thread.Sleep(TailPollIntervalMs);
                }
            }

            if (!options.Wait)
            {
                return ResolveTargetSessionFromRunningSessions();
            }

            Console.WriteLine("waiting for a running session");
            while (true)
            {
                string[] sessions = SessionPaths.GetKnownSessions(false);
                if (sessions.Length == 1)
                {
                    return sessions[0];
                }

                if (sessions.Length > 1)
                {
                    throw new InvalidOperationException("multiple running sessions; specify --session");
                }

                Thread.Sleep(TailPollIntervalMs);
            }
        }

        private static string GetCurrentIdentityForLog()
        {
            try
            {
                WindowsIdentity identity = WindowsIdentity.GetCurrent();
                if (identity == null)
                {
                    return Environment.UserDomainName + "\\" + Environment.UserName;
                }

                string sid = identity.User == null ? "-" : identity.User.Value;
                return identity.Name + " sid=" + sid;
            }
            catch
            {
                return Environment.UserDomainName + "\\" + Environment.UserName;
            }
        }

        private static void EnsureOk(string response)
        {
            if (!response.StartsWith("OK ", StringComparison.Ordinal))
            {
                throw new InvalidOperationException(response);
            }
        }

        private static GlobalOptions ParseGlobalOptions(string[] args)
        {
            GlobalOptions options = new GlobalOptions();
            List<string> remaining = new List<string>();
            int index = 0;
            while (index < args.Length)
            {
                if (args[index] == "--log-folder")
                {
                    if (index + 1 >= args.Length)
                    {
                        throw new InvalidOperationException("--log-folder requires a value");
                    }

                    options.LogFolder = args[index + 1];
                    index += 2;
                    continue;
                }

                break;
            }

            for (int i = index; i < args.Length; i++)
            {
                remaining.Add(args[i]);
            }

            options.RemainingArgs = remaining.ToArray();
            return options;
        }

        private static string GenerateSessionName(ConnectionProtocol protocol)
        {
            string prefix = protocol == ConnectionProtocol.Telnet ? "telnet-" : "ssh-";
            for (int i = 1; i <= 999; i++)
            {
                string candidate = prefix + i.ToString("D3", CultureInfo.InvariantCulture);
                if (!SessionPaths.SessionDirectoryExists(candidate))
                {
                    return candidate;
                }
            }

            throw new InvalidOperationException("no free session name available for prefix: " + prefix);
        }

        private static StartOptions ParseStartOptions(string[] args)
        {
            if (args.Length < 2)
            {
                throw new InvalidOperationException("usage: start [--session SESSION] --host HOST [--protocol ssh|telnet] [--port PORT] [--user USER] [--password PASSWORD] [--login-prompt TEXT] [--password-prompt TEXT] [--legacy-ssh] [--wait-ready] [ssh-args...]");
            }

            StartOptions options = new StartOptions();
            int index = 1;

            while (index < args.Length && args[index].StartsWith("-", StringComparison.Ordinal))
            {
                string option = args[index];
                if (option == "--protocol") { options.Protocol = ParseProtocol(ReadOptionValue(args, ref index, option)); continue; }
                if (option == "--host") { options.Host = ReadOptionValue(args, ref index, option); continue; }
                if (option == "--user") { options.UserName = ReadOptionValue(args, ref index, option); continue; }
                if (option == "--password") { options.Password = ReadOptionValue(args, ref index, option); continue; }
                if (option == "--session") { options.SessionName = ReadOptionValue(args, ref index, option); continue; }
                if (option == "--port") { options.Port = int.Parse(ReadOptionValue(args, ref index, option), CultureInfo.InvariantCulture); continue; }
                if (option == "--login-prompt") { options.LoginPrompt = ReadOptionValue(args, ref index, option); continue; }
                if (option == "--password-prompt") { options.PasswordPrompt = ReadOptionValue(args, ref index, option); continue; }
                if (option == "--legacy-ssh") { options.EnableLegacySsh = true; index++; continue; }
                if (option == "--wait-ready") { options.WaitReady = true; index++; continue; }
                break;
            }

            if (string.IsNullOrEmpty(options.Host))
            {
                throw new InvalidOperationException("--host is required");
            }

            List<string> extras = new List<string>();
            while (index < args.Length)
            {
                extras.Add(args[index]);
                index++;
            }

            options.ExtraArgs = extras.ToArray();
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

        private static bool ContainsPortOption(string[] args)
        {
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "-p") { return true; }
            }
            return false;
        }

        private static ConnectionProtocol ParseProtocol(string value)
        {
            if (string.Equals(value, "ssh", StringComparison.OrdinalIgnoreCase)) { return ConnectionProtocol.Ssh; }
            if (string.Equals(value, "telnet", StringComparison.OrdinalIgnoreCase)) { return ConnectionProtocol.Telnet; }
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
                parts.Add("StrictHostKeyChecking=no");
            }

            if (!HasSshOption(options.ExtraArgs, "UserKnownHostsFile"))
            {
                parts.Add("-o");
                parts.Add("UserKnownHostsFile=" + SessionPaths.GetSessionKnownHostsFile(options.SessionName));
            }

            if (!HasSshOption(options.ExtraArgs, "GlobalKnownHostsFile"))
            {
                parts.Add("-o");
                parts.Add("GlobalKnownHostsFile=" + SessionPaths.GetWindowsSharedKnownHostsFile());
            }

            return JoinList(parts);
        }

        private static StopOptions ParseStopOptions(string[] args)
        {
            StopOptions options = new StopOptions();
            for (int i = 1; i < args.Length; i++)
            {
                string value = args[i];
                if (value == "--session")
                {
                    options.SessionName = ReadOptionValue(args, ref i, value);
                    i--;
                    continue;
                }

                if (value == "--clear-stale")
                {
                    options.ClearStale = true;
                    continue;
                }

                if (value == "--prune")
                {
                    options.Prune = true;
                    continue;
                }

                throw new InvalidOperationException("usage: stop [--session SESSION] [--clear-stale] [--prune]");
            }

            if (string.IsNullOrEmpty(options.SessionName) && !options.Prune)
            {
                options.SessionName = ResolveTargetSessionFromRunningSessions();
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

        private static SessionCommandOptions ParseSessionCommandOptions(string[] args, string commandName)
        {
            SessionCommandOptions options = new SessionCommandOptions();

            for (int i = 1; i < args.Length; i++)
            {
                string value = args[i];
                if (value == "--session")
                {
                    options.SessionName = ReadOptionValue(args, ref i, value);
                    i--;
                    continue;
                }

                if (commandName == "read" && value == "--clear")
                {
                    options.Clear = true;
                    continue;
                }

                if (commandName == "tail" && value == "--wait")
                {
                    options.Wait = true;
                    continue;
                }

                if (commandName == "send" && value == "--text")
                {
                    options.TextValue = ReadOptionValue(args, ref i, value);
                    i--;
                    continue;
                }

                if (commandName == "send" && value == "--hex")
                {
                    options.HexValue = ReadOptionValue(args, ref i, value);
                    i--;
                    continue;
                }

                if (commandName == "send" && value == "--control")
                {
                    options.ControlValue = ReadOptionValue(args, ref i, value);
                    i--;
                    continue;
                }

                if (commandName == "tail")
                {
                    throw new InvalidOperationException("usage: tail [--session SESSION] [--wait]");
                }

                if (commandName == "read")
                {
                    throw new InvalidOperationException("usage: read [--session SESSION] [--clear]");
                }

                throw new InvalidOperationException("usage: send [--session SESSION] (--text TEXT | --hex HEX | --control CONTROL)");
            }

            if (string.IsNullOrEmpty(options.SessionName) && commandName != "tail")
            {
                options.SessionName = ResolveTargetSessionFromRunningSessions();
            }

            if (commandName == "send")
            {
                int specifiedValueCount = 0;
                if (!string.IsNullOrEmpty(options.TextValue)) { specifiedValueCount++; }
                if (!string.IsNullOrEmpty(options.HexValue)) { specifiedValueCount++; }
                if (!string.IsNullOrEmpty(options.ControlValue)) { specifiedValueCount++; }
                if (specifiedValueCount != 1)
                {
                    throw new InvalidOperationException("usage: send [--session SESSION] (--text TEXT | --hex HEX | --control CONTROL)");
                }
            }

            return options;
        }

        private static string ResolveTargetSessionFromRunningSessions()
        {
            string[] sessions = SessionPaths.GetKnownSessions(false);
            if (sessions.Length == 1)
            {
                return sessions[0];
            }

            if (sessions.Length == 0)
            {
                throw new InvalidOperationException("no running session; specify --session");
            }

            throw new InvalidOperationException("multiple running sessions; specify --session");
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
            Console.WriteLine("termwrap.exe [--log-folder PATH] <command> [options]");
            Console.WriteLine();
            Console.WriteLine("Start a session");
            Console.WriteLine("  termwrap.exe [--log-folder PATH] start [--session SESSION] --host HOST [--protocol ssh|telnet] [--port PORT] [--user USER] [--password PASSWORD] [--login-prompt TEXT] [--password-prompt TEXT] [--legacy-ssh] [--wait-ready] [ssh-args...]");
            Console.WriteLine("  --session     optional; auto-generates ssh-001 / telnet-001 when omitted");
            Console.WriteLine("  --host        target host or IP");
            Console.WriteLine("  --protocol    default: ssh");
            Console.WriteLine("  --port        default: ssh=22, telnet=23");
            Console.WriteLine("  --legacy-ssh  add ssh-rsa / hmac-sha1 compatibility options");
            Console.WriteLine("  --wait-ready  wait for the first shell prompt before returning");
            Console.WriteLine("  --log-folder  optional; write termwrap.log only when specified");
            Console.WriteLine();
            Console.WriteLine("Read session output");
            Console.WriteLine("  termwrap.exe read [--session SESSION] [--clear]");
            Console.WriteLine("  --clear       clear buffered output after reading");
            Console.WriteLine();
            Console.WriteLine("Follow session output");
            Console.WriteLine("  termwrap.exe tail [--session SESSION] [--wait]");
            Console.WriteLine("  --wait        wait until the target session appears");
            Console.WriteLine();
            Console.WriteLine("Send input");
            Console.WriteLine("  termwrap.exe send [--session SESSION] --text TEXT");
            Console.WriteLine("  termwrap.exe send [--session SESSION] --hex HEX");
            Console.WriteLine("  termwrap.exe send [--session SESSION] --control <ctrl-c|ctrl-d|ctrl-z|esc|tab|enter|up|down|left|right|backspace>");
            Console.WriteLine();
            Console.WriteLine("Stop and clean up");
            Console.WriteLine("  termwrap.exe stop [--session SESSION] [--clear-stale] [--prune]");
            Console.WriteLine("  --clear-stale force cleanup of stale processes");
            Console.WriteLine("  --prune       delete session data after stop");
            Console.WriteLine();
            Console.WriteLine("List sessions");
            Console.WriteLine("  termwrap.exe list [--all] [--verbose]");
            Console.WriteLine();
            Console.WriteLine("Session selection");
            Console.WriteLine("  read / tail / send / stop can omit --session when exactly one running session exists");
            Console.WriteLine("  start auto-generates a short session name when --session is omitted");
            Console.WriteLine();
            Console.WriteLine("Examples");
            Console.WriteLine("  termwrap.exe start --session ssh-001 --host HOST --user USER --password PASSWORD --wait-ready");
            Console.WriteLine("  termwrap.exe start --session telnet-001 --host HOST --protocol telnet --port 23 --user USER --password PASSWORD --wait-ready");
            Console.WriteLine("  termwrap.exe --log-folder LOGS start --host HOST --user USER --password PASSWORD");
            Console.WriteLine("  termwrap.exe send --text \"uname -a\"");
            Console.WriteLine("  termwrap.exe send --control enter");
            Console.WriteLine("  termwrap.exe read --clear");
            Console.WriteLine("  termwrap.exe stop --prune");
        }
    }
}






