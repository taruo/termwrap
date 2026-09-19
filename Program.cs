using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Pipes;
using System.Reflection;
using System.Runtime.InteropServices;
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
        private const int StdInputHandle = -10;
        private const int StdOutputHandle = -11;
        private const int StdErrorHandle = -12;
        private const uint GenericRead = 0x80000000;
        private const uint GenericWrite = 0x40000000;
        private const uint FileShareRead = 0x00000001;
        private const uint FileShareWrite = 0x00000002;
        private const uint OpenExisting = 3;
        private static readonly IntPtr InvalidHandleValue = new IntPtr(-1);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr GetStdHandle(int handle);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetStdHandle(int handle, IntPtr value);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr CreateFile(
            string fileName, uint access, uint share, IntPtr security, uint creation,
            uint flags, IntPtr template);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr handle);

        private const string AppVersion = "current";
        private const int TailPollIntervalMs = 2000;
        private const int StartReadyTimeoutMs = 8000;
        private const int StartReadyPollIntervalMs = 200;
        private const int StartStableWindowMs = 1500;
        private const int StartStablePollIntervalMs = 200;
        private const int StopExitTimeoutMs = 5000;
        private const int StopExitPollIntervalMs = 100;
        private static string _currentLogFolder;
        
        private static int Main(string[] args)
        {
            Console.OutputEncoding = new UTF8Encoding(false);
            return RunWithLogging(delegate
            {
                if (args.Length == 2 && args[0] == "--askpass-secret")
                {
                    return PrintAskPassSecret(args[1]);
                }

                GlobalOptions globalOptions = CliOptions.ParseGlobal(args);
                _currentLogFolder = globalOptions.LogFolder;
                Logger.Initialize(AppVersion, globalOptions.LogFolder);
                // Do not log user input, SSH suffixes, or passwords in argv.
                Logger.Info("process start");
                if (globalOptions.RemainingArgs.Length > 0 && globalOptions.RemainingArgs[0] == "--daemon")
                {
                    return DaemonMain(globalOptions.RemainingArgs);
                }
                return CliMain(globalOptions.RemainingArgs);
            });
        }

        private static int PrintAskPassSecret(string path)
        {
            string fullPath = Path.GetFullPath(path);
            string root = Path.GetFullPath(SessionPaths.SessionsRoot);
            string rootPrefix = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!fullPath.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(Path.GetFileName(fullPath), "askpass.secret", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("invalid askpass secret path");
            }

            byte[] data = Encoding.UTF8.GetBytes(CredentialStore.Read(fullPath) + Environment.NewLine);
            using (Stream output = Console.OpenStandardOutput())
            {
                output.Write(data, 0, data.Length);
                output.Flush();
            }

            return 0;
        }

        private static int RunWithLogging(Func<int> action)
        {
            try
            {
                return action();
            }
            catch (Exception ex)
            {
                try { Logger.Error("fatal: " + ex); } catch { }
                return CliOutput.Error(ex);
            }
        }

        private static int CliMain(string[] args)
        {
            if (args.Length == 0)
            {
                throw CliException.Usage("a command is required; use termwrap help");
            }

            string command = args[0].ToLowerInvariant();
            if (command == "start") { return StartCommand(args); }
            if (command == "stop") { return StopCommand(args); }
            if (command == "prune") { return PruneCommand(args); }
            if (command == "list") { return ListCommand(args); }
            if (command == "tail") { return TailCommand(args); }
            if (command == "read") { return ReadCommand(args); }
            if (command == "send") { return SendCommand(args); }
            if (command == "help" || command == "--help" || command == "-h")
            {
                return HelpCommand(args);
            }

            throw CliException.Usage("unknown command; use termwrap help");
        }

        private static int DaemonMain(string[] args)
        {
            // A persistent daemon must not keep the caller's output pipe open.
            // Diagnostics remain in metadata / the explicitly enabled debug log.
            DetachDaemonStandardHandles();
            Console.SetIn(TextReader.Null);
            Console.SetOut(TextWriter.Null);
            Console.SetError(TextWriter.Null);
            if (args.Length < 9)
            {
                throw new InvalidOperationException("daemon arguments are incomplete");
            }

            string password = CredentialStore.Consume(args[1], DecodeOrEmpty(args[7]));
            SessionDaemon daemon = new SessionDaemon(
                args[1],
                args[2],
                args[3],
                DecodeOrEmpty(args[4]),
                DecodeOrEmpty(args[5]),
                DecodeOrEmpty(args[6]),
                password,
                DecodeOrEmpty(args[8]));
            return daemon.Run();
        }

        private static void DetachDaemonStandardHandles()
        {
            IntPtr oldInput = GetStdHandle(StdInputHandle);
            IntPtr oldOutput = GetStdHandle(StdOutputHandle);
            IntPtr oldError = GetStdHandle(StdErrorHandle);
            IntPtr nulInput = CreateFile("NUL", GenericRead, FileShareRead | FileShareWrite, IntPtr.Zero, OpenExisting, 0, IntPtr.Zero);
            IntPtr nulOutput = CreateFile("NUL", GenericWrite, FileShareRead | FileShareWrite, IntPtr.Zero, OpenExisting, 0, IntPtr.Zero);
            if (nulInput == InvalidHandleValue || nulOutput == InvalidHandleValue)
            {
                if (nulInput != InvalidHandleValue) { CloseHandle(nulInput); }
                if (nulOutput != InvalidHandleValue) { CloseHandle(nulOutput); }
                return;
            }

            SetStdHandle(StdInputHandle, nulInput);
            SetStdHandle(StdOutputHandle, nulOutput);
            SetStdHandle(StdErrorHandle, nulOutput);
            CloseInheritedHandle(oldInput, nulInput);
            CloseInheritedHandle(oldOutput, nulOutput);
            CloseInheritedHandle(oldError, nulOutput);
        }

        private static void CloseInheritedHandle(IntPtr handle, IntPtr replacement)
        {
            if (handle != IntPtr.Zero && handle != InvalidHandleValue && handle != replacement)
            {
                CloseHandle(handle);
            }
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
                throw CliException.Conflict("session already running: " + options.SessionName);
            }

            CliOptions.ReadPassword(options);

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
            // Only a protected one-time file path is placed on the command line;
            // the daemon consumes and deletes the credential immediately.
            string credentialPath = CredentialStore.Create(options.SessionName, options.Password);
            daemonParts.Add(EncodeOrEmpty(credentialPath));
            daemonParts.Add(EncodeOrEmpty(options.GetPromptConfig()));
            string daemonArgs = JoinList(daemonParts);

            Logger.Info(
                "start session={0} protocol={1} host={2} port={3}",
                options.SessionName,
                options.Protocol.ToString(),
                options.Host,
                options.Port.ToString(CultureInfo.InvariantCulture));

            ProcessStartInfo daemonStartInfo = new ProcessStartInfo
            {
                FileName = exePath,
                Arguments = daemonArgs,
                UseShellExecute = true,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden
            };
            Process process;
            try
            {
                process = Process.Start(daemonStartInfo);
            }
            catch
            {
                CredentialStore.Delete(credentialPath);
                throw;
            }
            if (process == null)
            {
                CredentialStore.Delete(credentialPath);
                throw new InvalidOperationException("failed to start daemon process");
            }

            DateTime deadline = DateTime.UtcNow.AddMilliseconds(StartReadyTimeoutMs);
            while (DateTime.UtcNow < deadline)
            {
                Thread.Sleep(StartReadyPollIntervalMs);
                SessionInfo info = SessionInfo.Load(options.SessionName);
                // Returning only after the pipe answers avoids a race where start
                // succeeds but the very first read/send still hits a timeout.
                if (info.IsAlive() && IsCommandPipeReady(options.SessionName) && (!options.WaitReady || IsSessionReady(options)))
                {
                    if (options.Protocol == ConnectionProtocol.Ssh && !WaitForSshStartupStability(options.SessionName, process))
                    {
                        throw new InvalidOperationException(BuildStartFailureMessage(options.SessionName, "ssh transport exited during startup stabilization"));
                    }

                    CliOutput.Write(CliOutput.Result("start", "session", options.SessionName,
                        "state", "running", "protocol", info.Protocol, "host", info.Host, "port", info.Port,
                        "daemonPid", info.DaemonPid, "remotePid", info.RemotePid), "started " + options.SessionName);
                    return 0;
                }

                if (process.HasExited)
                {
                    throw new InvalidOperationException(BuildStartFailureMessage(options.SessionName, "daemon exited before session became ready"));
                }
            }

            throw CliException.Timeout(BuildStartFailureMessage(options.SessionName, "session did not become ready in time"));
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

        private static bool WaitForSshStartupStability(string sessionName, Process daemonProcess)
        {
            DateTime deadline = DateTime.UtcNow.AddMilliseconds(StartStableWindowMs);
            while (DateTime.UtcNow < deadline)
            {
                Thread.Sleep(StartStablePollIntervalMs);
                SessionInfo info = SessionInfo.Load(sessionName);
                if (!info.IsAlive() || daemonProcess.HasExited)
                {
                    return false;
                }

                if (!IsCommandPipeReady(sessionName))
                {
                    return false;
                }

                if (info.RemotePid <= 0 || !IsProcessAlive(info.RemotePid))
                {
                    return false;
                }
            }

            return true;
        }

        private static bool IsProcessAlive(int pid)
        {
            if (pid <= 0)
            {
                return false;
            }

            try
            {
                return !Process.GetProcessById(pid).HasExited;
            }
            catch
            {
                return false;
            }
        }

        private static string BuildStartFailureMessage(string sessionName, string fallbackMessage)
        {
            SessionInfo info = SessionInfo.Load(sessionName);
            string detail = string.IsNullOrEmpty(info.LastError) ? string.Empty : info.LastError.Trim();
            string message = string.IsNullOrEmpty(detail) ? fallbackMessage : fallbackMessage + ": " + detail;
            if (LooksLikeSandboxLaunchFailure(detail))
            {
                message += " If an AI tool or sandbox launched this command, retry only the start command with sandbox escalation so ssh.exe can be started.";
            }

            return message;
        }

        private static bool LooksLikeSandboxLaunchFailure(string message)
        {
            if (string.IsNullOrEmpty(message))
            {
                return false;
            }

            string text = message.ToLowerInvariant();
            return text.Contains("access is denied")
                || text.Contains("permission denied")
                || text.Contains("operation not permitted")
                || text.Contains("requested operation requires elevation")
                || text.Contains("this program is blocked")
                || text.Contains("the system cannot execute");
        }

        private static int StopCommand(string[] args)
        {
            StopOptions options = CliOptions.ParseStop(args, false);
            if (!options.All && options.SessionName == null)
            {
                options.SessionName = ResolveTargetSessionFromRunningSessions();
            }
            string[] sessions = options.All ? SessionPaths.GetKnownSessions(true) : new[] { options.SessionName };
            List<object> results = new List<object>();
            List<string> messages = new List<string>();
            foreach (string session in sessions)
            {
                StopSingleSession(session, options);
                results.Add(CliOutput.Fields("session", session, "state", "stopped", "pruned", options.Prune));
                messages.Add((options.Prune ? "stopped and pruned " : "stopped ") + session);
            }
            object result = options.All
                ? CliOutput.Result("stop", "sessions", results)
                : CliOutput.Result("stop", "session", options.SessionName, "state", "stopped", "pruned", options.Prune);
            CliOutput.Write(result, messages.Count == 0 ? "no sessions" : string.Join(Environment.NewLine, messages.ToArray()));
            return 0;
        }

        private static void StopSingleSession(string sessionName, StopOptions options)
        {
            if (!SessionPaths.SessionDirectoryExists(sessionName))
            {
                throw CliException.NotFound("session not found: " + sessionName);
            }
            SessionInfo info = SessionInfo.Load(sessionName);
            if (info.IsAlive())
            {
                try
                {
                    SendSimpleCommand(sessionName, "STOP");
                    if (!WaitForSessionExit(sessionName, StopExitTimeoutMs))
                    {
                        throw CliException.Timeout("session did not stop in time: " + sessionName);
                    }
                }
                catch (InvalidOperationException)
                {
                    if (!options.ClearStale) { throw; }
                }
                catch (IOException)
                {
                    if (!options.ClearStale) { throw; }
                }
            }
            if (options.ClearStale)
            {
                SessionDaemon.ClearStaleProcesses(SessionInfo.Load(sessionName));
                if (!WaitForSessionExit(sessionName, StopExitTimeoutMs))
                {
                    throw CliException.Timeout("session still running after --force: " + sessionName);
                }
            }
            if (options.Prune) { PruneSingleSession(sessionName, true); }
        }

        private static bool WaitForSessionExit(string sessionName, int timeoutMs)
        {
            DateTime deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            while (DateTime.UtcNow < deadline)
            {
                if (!SessionInfo.Load(sessionName).IsAlive()) { return true; }
                Thread.Sleep(StopExitPollIntervalMs);
            }
            return false;
        }

        private static int PruneCommand(string[] args)
        {
            StopOptions options = CliOptions.ParseStop(args, true);
            if (options.SessionName == null)
            {
                options.SessionName = ResolveUniqueSession(true);
            }
            PruneSingleSession(options.SessionName, false);
            object result = CliOutput.Result("prune", "session", options.SessionName, "pruned", true);
            CliOutput.Write(result, "pruned " + options.SessionName);
            return 0;
        }

        private static void PruneSingleSession(string sessionName, bool waitForStop)
        {
            // The daemon owns this mutex during startup and shutdown as well.
            // Metadata alone must not authorize deletion while a daemon is active.
            DateTime deadline = DateTime.UtcNow.AddMilliseconds(waitForStop ? StopExitTimeoutMs : 0);
            while (true)
            {
                using (Mutex mutex = new Mutex(false, SessionPaths.GetMutexName(sessionName)))
                {
                    bool acquired = false;
                    try
                    {
                        try { acquired = mutex.WaitOne(0); }
                        catch (AbandonedMutexException) { acquired = true; }
                        if (acquired && !SessionInfo.Load(sessionName).IsAlive())
                        {
                            if (!SessionPaths.SessionDirectoryExists(sessionName))
                            {
                                throw CliException.NotFound("session not found: " + sessionName);
                            }
                            SessionPaths.DeleteSessionDirectory(sessionName);
                            return;
                        }

                    }
                    finally
                    {
                        if (acquired) { mutex.ReleaseMutex(); }
                    }

                    if (waitForStop && DateTime.UtcNow < deadline)
                    {
                        Thread.Sleep(50);
                        continue;
                    }
                    throw CliException.Conflict("cannot prune active session: " + sessionName + "; stop it first");
                }
            }
        }

        private static int ListCommand(string[] args)
        {
            bool showAll = false;
            bool verbose = false;
            for (int i = 1; i < args.Length; i++)
            {
                if (args[i] == "--all" || args[i] == "-a") { showAll = true; continue; }
                if (args[i] == "--verbose" || args[i] == "-v") { verbose = true; continue; }
                throw CliException.Usage("usage: list [--all] [--verbose] [--json]");
            }
            List<object> records = new List<object>();
            List<string> lines = new List<string>();
            foreach (string name in SessionPaths.GetKnownSessions(true))
            {
                SessionInfo info = SessionInfo.Load(name);
                bool alive = info.IsAlive();
                if (!showAll && !alive) { continue; }
                records.Add(CliOutput.Fields("session", name, "state", alive ? "running" : "stopped",
                    "daemonPid", info.DaemonPid, "remotePid", info.RemotePid,
                    "protocol", info.Protocol, "host", info.Host, "port", info.Port,
                    "startedAtUtc", info.StartedAtUtc, "authMode", info.AuthMode));
                lines.Add(verbose
                    ? string.Format(CultureInfo.InvariantCulture,
                        "{0}\t{1}\tpid={2}\tremotePid={3}\tprotocol={4}\thost={5}\tport={6}\tstarted={7}\tauth={8}\ttarget={9}",
                        name, alive ? "running" : "stopped", info.DaemonPid, info.RemotePid,
                        EmptyDash(info.Protocol), EmptyDash(info.Host), info.Port, EmptyDash(info.StartedAtUtc),
                        EmptyDash(info.AuthMode), EmptyDash(info.Target))
                    : string.Format(CultureInfo.InvariantCulture, "{0}\t{1}\t{2}\t{3}:{4}",
                        name, alive ? "running" : "stopped", EmptyDash(info.Protocol), EmptyDash(info.Host), info.Port));
            }
            CliOutput.Write(CliOutput.Result("list", "sessions", records),
                lines.Count == 0 ? "no sessions" : string.Join(Environment.NewLine, lines.ToArray()));
            return 0;
        }

        private static int TailCommand(string[] args)
        {
            SessionCommandOptions options = ParseSessionCommandOptions(args, "tail");
            string sessionName = ResolveTailSession(options);
            RequireRunningSession(sessionName);
            if (!CliOutput.Json) { CliOutput.Diagnostic("tailing " + sessionName + "  stop: Ctrl+C  interval: 2s"); }
            long nextOffset = -1;
            while (true)
            {
                TailSnapshot snapshot;
                try { snapshot = ReadTailSnapshot(sessionName, nextOffset); }
                catch (Exception)
                {
                    if (!SessionInfo.Load(sessionName).IsAlive()) { break; }
                    throw;
                }
                if (snapshot.Data.Length > 0) { WriteSnapshot("tail", sessionName, snapshot, false); }
                nextOffset = snapshot.EndOffset;
                if (!SessionInfo.Load(sessionName).IsAlive()) { break; }
                Thread.Sleep(TailPollIntervalMs);
            }
            CliOutput.Write(CliOutput.Result("tail", "session", sessionName, "event", "stopped"), null);
            return 0;
        }

        private static int ReadCommand(string[] args)
        {
            SessionCommandOptions options = ParseSessionCommandOptions(args, "read");
            RequireRunningSession(options.SessionName);
            TailSnapshot snapshot = ReadBufferSnapshot(options.SessionName, options.Clear);
            WriteSnapshot("read", options.SessionName, snapshot, options.Clear);
            return 0;
        }

        private static void WriteSnapshot(string command, string session, TailSnapshot snapshot, bool cleared)
        {
            string text = Encoding.UTF8.GetString(snapshot.Data);
            if (!CliOutput.Json) { Console.Write(text); return; }
            CliOutput.Write(CliOutput.Result(command, "session", session, "event", "data",
                "startOffset", snapshot.StartOffset, "endOffset", snapshot.EndOffset,
                "text", text, "dataBase64", Convert.ToBase64String(snapshot.Data), "cleared", cleared), null);
        }

        private static int SendCommand(string[] args)
        {
            SessionCommandOptions options = ParseSessionCommandOptions(args, "send");
            string command;
            string mode;
            if (options.LineValue != null)
            {
                // Use the existing raw-input operation, in one request, so older
                // running daemons also support --line without interleaved input.
                command = "SEND_TEXT " + Convert.ToBase64String(Encoding.UTF8.GetBytes(options.LineValue + "\r"));
                mode = "line";
            }
            else if (options.TextValue != null)
            {
                command = "SEND_TEXT " + Convert.ToBase64String(Encoding.UTF8.GetBytes(options.TextValue));
                mode = "text";
            }
            else if (options.HexValue != null) { command = "SEND_HEX " + options.HexValue; mode = "hex"; }
            else { command = "SEND_CONTROL " + options.ControlValue; mode = "key"; }
            SendSimpleCommand(options.SessionName, command);
            CliOutput.Write(CliOutput.Result("send", "session", options.SessionName, "mode", mode, "sent", true), "sent " + mode);
            return 0;
        }

        private static void SendSimpleCommand(string sessionName, string command)
        {
            RequireRunningSession(sessionName);
            EnsureOk(SendCommandAndReadResponse(sessionName, command));
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
                    SummarizePipeMessage(command),
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
                        Logger.Info("pipe client response session={0} pipe={1} response={2}", sessionName, pipeName, SummarizePipeMessage(response));
                        return response;
                    }
                }
            }
            catch (TimeoutException)
            {
                Logger.Error("pipe client timeout session=" + sessionName + " pipe=" + pipeName + " command=" + SummarizePipeMessage(command));
                throw CliException.Timeout("session command pipe timed out: " + sessionName);
            }
            catch (UnauthorizedAccessException ex)
            {
                Logger.Error("pipe client unauthorized session=" + sessionName + " pipe=" + pipeName + " command=" + SummarizePipeMessage(command) + " identity=" + GetCurrentIdentityForLog() + " " + ex.Message);
                throw;
            }
            catch (IOException ex)
            {
                Logger.Error("pipe client io session=" + sessionName + " pipe=" + pipeName + " command=" + SummarizePipeMessage(command) + " " + ex.Message);
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

                CliOutput.Write(CliOutput.Result("tail", "session", options.SessionName, "event", "waiting"), "waiting for session " + options.SessionName);
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

            CliOutput.Write(CliOutput.Result("tail", "session", null, "event", "waiting"), "waiting for a running session");
            while (true)
            {
                string[] sessions = SessionPaths.GetKnownSessions(false);
                if (sessions.Length == 1)
                {
                    return sessions[0];
                }

                if (sessions.Length > 1)
                {
                    throw CliException.Ambiguous("multiple running sessions; specify --session");
                }

                Thread.Sleep(TailPollIntervalMs);
            }
        }

        private static string SummarizePipeMessage(string message)
        {
            if (string.IsNullOrEmpty(message))
            {
                return string.Empty;
            }

            if (message.StartsWith("SEND_TEXT ", StringComparison.Ordinal) ||
                message.StartsWith("SEND_HEX ", StringComparison.Ordinal))
            {
                int separator = message.IndexOf(' ');
                return message.Substring(0, separator) + " payload=<redacted>";
            }

            if (message.StartsWith("OK READ ", StringComparison.Ordinal) ||
                message.StartsWith("OK READ_CLEAR ", StringComparison.Ordinal) ||
                message.StartsWith("OK TAIL ", StringComparison.Ordinal))
            {
                string[] parts = message.Split(new[] { ' ' }, 5);
                return parts.Length >= 4
                    ? string.Join(" ", parts, 0, 4) + " payload=<redacted>"
                    : "OK buffer payload=<redacted>";
            }

            return message;
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
            return CliOptions.ParseStart(args);
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

            parts.Add(options.Host);
            return JoinList(parts);
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
            SessionCommandOptions options = CliOptions.ParseSession(args, commandName);
            if (options.SessionName == null && commandName != "tail")
            {
                options.SessionName = ResolveTargetSessionFromRunningSessions();
            }
            return options;
        }

        private static string ResolveTargetSessionFromRunningSessions()
        {
            return ResolveUniqueSession(false);
        }

        private static string ResolveUniqueSession(bool includeStopped)
        {
            string[] sessions = SessionPaths.GetKnownSessions(includeStopped);
            if (sessions.Length == 1) { return sessions[0]; }
            if (sessions.Length == 0) { throw CliException.NotFound("no eligible session; specify --session"); }
            throw CliException.Ambiguous("multiple sessions; specify --session (stop also accepts --all)");
        }

        private static void RequireRunningSession(string sessionName)
        {
            if (!SessionInfo.Load(sessionName).IsAlive())
            {
                throw CliException.NotFound("session is not running: " + sessionName);
            }
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

        private static int HelpCommand(string[] args)
        {
            if (args.Length > 2) { throw CliException.Usage("usage: help [command]"); }
            string topic = args.Length == 2 ? args[1].ToLowerInvariant() : "";
            string text;
            switch (topic)
            {
                case "":
                case "help":
                    text = "termwrap [--json] [--log-folder PATH] <command> [options]\n\n" +
                        "  start --host HOST [-s SESSION] [connection options] [-- SSH-ARGS...]\n" +
                        "  list [--all] [--verbose]\n" +
                        "  send [-s SESSION] (--text TEXT | --line TEXT | --hex HEX | --key KEY)\n" +
                        "  read [-s SESSION] [--clear]\n" +
                        "  tail [-s SESSION] [--wait]\n" +
                        "  stop [-s SESSION | --all] [--force] [--prune]\n" +
                        "  prune [-s SESSION]\n" +
                        "  help [command]\n\n" +
                        "Global options work before or after the command (not inside a value or after --).\n" +
                        "-s is an alias for --session. Omission requires exactly one eligible session.\n" +
                        "stop --all stops all sessions but never deletes their data. Deletion is one session at a time.\n" +
                        "--json emits JSON; tail emits one JSON event per line. Errors go to stderr.\n" +
                        "Exit codes: 0 success, 1 failure, 2 usage, 3 not_found, 4 ambiguous, 5 timeout, 6 conflict.";
                    break;
                case "start":
                    text = "termwrap start --host HOST [-s SESSION] [options] [-- SSH-ARGS...]\n\n" +
                        "  --protocol ssh|telnet    Default: ssh\n" +
                        "  --port PORT             Default: 22 for SSH, 23 for Telnet\n" +
                        "  --user USER\n" +
                        "  --ask-password          Read without echo from the console\n" +
                        "  --password-stdin        Read one UTF-8 line from stdin (no trimming)\n" +
                        "  --password PASSWORD     Compatibility option; visible in caller arguments/history\n" +
                        "  --login-prompt TEXT     Telnet prompt (default: login:)\n" +
                        "  --password-prompt TEXT  Telnet prompt (default: password:)\n" +
                        "  --legacy-ssh            Enable legacy SSH algorithms\n" +
                        "  --wait-ready            Also wait for the existing prompt heuristic (8s deadline)\n\n" +
                        "Choose only one password input method. A session name is generated when omitted.\n" +
                        "SSH arguments require --, e.g.: start --host HOST -- -o ConnectTimeout=10";
                    break;
                case "send":
                    text = "termwrap send [-s SESSION] (--text TEXT | --line TEXT | --hex HEX | --key KEY)\n\n" +
                        "--text sends exact UTF-8 text, without Enter (empty text is allowed).\n" +
                        "--line sends one UTF-8 line followed by one CR in a single request.\n" +
                        "       Embedded CR/LF is rejected; an empty line sends only Enter.\n" +
                        "--hex sends raw bytes; spaces and hyphens between digits are accepted.\n" +
                        "--key sends ctrl-c|ctrl-d|ctrl-z|esc|tab|enter|up|down|left|right|backspace.\n" +
                        "--control is a compatibility alias for --key.\n" +
                        "Success means input was sent, not that a remote command completed.";
                    break;
                case "read":
                    text = "termwrap read [-s SESSION] [--clear]\n\n" +
                        "--clear consumes the unread buffer without clearing tail history.\n" +
                        "--json includes text, dataBase64, startOffset, endOffset and cleared.";
                    break;
                case "tail":
                    text = "termwrap tail [-s SESSION] [--wait]\n\n" +
                        "--wait waits for a session to appear. Ctrl+C stops following.\n" +
                        "--json emits newline-delimited events: waiting, data, stopped.\n" +
                        "Data events include text, dataBase64 and byte offsets. Poll interval: 2s.";
                    break;
                case "list":
                    text = "termwrap list [--all|-a] [--verbose|-v]\n\n" +
                        "--all includes stopped sessions. --verbose expands human-readable details.\n" +
                        "--json returns a sessions array, including an empty array when none exist.";
                    break;
                case "stop":
                    text = "termwrap stop [-s SESSION | --all] [--force] [--prune]\n\n" +
                        "Omission selects exactly one running session; --all explicitly targets all known sessions.\n" +
                        "--force allows verified-process cleanup after graceful stop fails.\n" +
                        "--clear-stale is a compatibility alias for --force.\n" +
                        "--prune also removes the selected stopped session data (compatibility option).\n" +
                        "--all cannot be combined with --prune; deletion is always limited to one session.";
                    break;
                case "prune":
                    text = "termwrap prune [-s SESSION]\n\n" +
                        "Delete one stopped session's data, including output logs. Running sessions are never stopped.\n" +
                        "An explicit active target returns conflict (exit 6). Omission requires exactly one known session.\n" +
                        "--all is intentionally unsupported; choose each session explicitly.";
                    break;
                default: throw CliException.Usage("unknown help topic");
            }
            CliOutput.Write(CliOutput.Result("help", "topic", topic, "text", text), text);
            return 0;
        }
    }
}






