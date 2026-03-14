using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Threading;

namespace TermWrap
{
    internal enum ConnectionProtocol
    {
        Auto,
        Ssh,
        Telnet
    }

    internal sealed class StartOptions
    {
        public string SessionName;
        public ConnectionProtocol Protocol = ConnectionProtocol.Ssh;
        public string UserName;
        public string Password;
        public string Host;
        public int Port;
        public string[] ExtraArgs = new string[0];
        public string LoginPrompt = "login:";
        public string PasswordPrompt = "password:";

        public string GetPromptConfig()
        {
            return (LoginPrompt ?? string.Empty) + "\n" + (PasswordPrompt ?? string.Empty);
        }
    }

    internal sealed class StopOptions
    {
        public string SessionName;
        public bool ClearStale;
    }

    internal sealed class TailSnapshot
    {
        public long StartOffset;
        public long EndOffset;
        public byte[] Data = new byte[0];
    }

    internal sealed class TailBuffer
    {
        private readonly int _capacity;
        private readonly object _sync = new object();
        private long _startOffset;
        private byte[] _data = new byte[0];

        public TailBuffer(int capacity)
        {
            _capacity = capacity;
        }

        public void Append(byte[] source, int count)
        {
            lock (_sync)
            {
                int copyCount = Math.Min(count, _capacity);
                byte[] next = new byte[Math.Min(_data.Length + copyCount, _capacity)];
                int preserved = Math.Min(_data.Length, _capacity - copyCount);
                if (preserved > 0)
                {
                    Buffer.BlockCopy(_data, _data.Length - preserved, next, 0, preserved);
                }

                Buffer.BlockCopy(source, count - copyCount, next, preserved, copyCount);
                _startOffset += _data.Length - preserved;
                _data = next;
            }
        }

        public TailSnapshot Read(long requestedOffset)
        {
            lock (_sync)
            {
                long endOffset = _startOffset + _data.Length;
                long effectiveOffset = requestedOffset < 0 ? _startOffset : Math.Max(requestedOffset, _startOffset);
                int byteCount = (int)(endOffset - effectiveOffset);
                TailSnapshot snapshot = new TailSnapshot();
                snapshot.StartOffset = effectiveOffset;
                snapshot.EndOffset = endOffset;
                if (byteCount <= 0)
                {
                    return snapshot;
                }

                byte[] data = new byte[byteCount];
                int startIndex = (int)(effectiveOffset - _startOffset);
                Buffer.BlockCopy(_data, startIndex, data, 0, byteCount);
                snapshot.Data = data;
                return snapshot;
            }
        }

        public TailSnapshot ReadAll()
        {
            return Read(-1);
        }

        public TailSnapshot ReadAllAndClear()
        {
            lock (_sync)
            {
                TailSnapshot snapshot = new TailSnapshot();
                snapshot.StartOffset = _startOffset;
                snapshot.EndOffset = _startOffset + _data.Length;
                snapshot.Data = new byte[_data.Length];
                if (_data.Length > 0)
                {
                    Buffer.BlockCopy(_data, 0, snapshot.Data, 0, _data.Length);
                }

                _startOffset = snapshot.EndOffset;
                _data = new byte[0];
                return snapshot;
            }
        }
    }

    internal static class SessionPaths
    {
        private static readonly string Root = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, ".termwrap-data");

        public static string RootDir { get { return Root; } }
        public static string SessionsRoot { get { return Path.Combine(Root, "sessions"); } }
        public static string LogsRoot { get { return Path.Combine(Root, "logs"); } }
        public static string AppLogFile { get { return Path.Combine(LogsRoot, "termwrap.log"); } }
        public static string DefaultKnownHostsFile { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ssh", "known_hosts"); } }

        public static string[] GetKnownSessions(bool includeStopped)
        {
            Directory.CreateDirectory(SessionsRoot);
            string[] dirs = Directory.GetDirectories(SessionsRoot);
            List<string> names = new List<string>();
            for (int i = 0; i < dirs.Length; i++)
            {
                string sessionName = Path.GetFileName(dirs[i]);
                if (!includeStopped)
                {
                    SessionInfo info = SessionInfo.Load(sessionName);
                    if (!info.IsAlive())
                    {
                        continue;
                    }
                }

                names.Add(sessionName);
            }

            return names.ToArray();
        }

        public static string GetSessionDir(string sessionName)
        {
            return Path.Combine(SessionsRoot, Sanitize(sessionName));
        }

        public static string GetCommandPipeName(string sessionName)
        {
            return "termwrap.cmd." + Sanitize(sessionName);
        }

        public static string GetMutexName(string sessionName)
        {
            return "Local\\termwrap.mutex." + Sanitize(sessionName);
        }

        public static string Sanitize(string value)
        {
            StringBuilder builder = new StringBuilder(value.Length);
            for (int i = 0; i < value.Length; i++)
            {
                char c = value[i];
                builder.Append(char.IsLetterOrDigit(c) || c == '-' || c == '_' || c == '.' ? c : '_');
            }

            return builder.ToString();
        }
    }

    internal static class Logger
    {
        private static readonly object Sync = new object();
        private static string _version;

        public static void Initialize(string version)
        {
            _version = version;
            Directory.CreateDirectory(SessionPaths.RootDir);
            Directory.CreateDirectory(SessionPaths.LogsRoot);
            Info("logger initialized version={0}", version);
        }

        public static void Info(string format, params object[] args)
        {
            Write("INFO", string.Format(CultureInfo.InvariantCulture, format, args));
        }

        public static void Error(string message)
        {
            Write("ERROR", message);
        }

        private static void Write(string level, string message)
        {
            lock (Sync)
            {
                File.AppendAllText(
                    SessionPaths.AppLogFile,
                    string.Format(
                        CultureInfo.InvariantCulture,
                        "{0} [{1}] v{2} pid={3} {4}{5}",
                        DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture),
                        level,
                        _version ?? "unknown",
                        Process.GetCurrentProcess().Id,
                        message,
                        Environment.NewLine),
                    Encoding.UTF8);
            }
        }
    }

    internal sealed class SessionInfo
    {
        public string SessionName;
        public int DaemonPid;
        public int RemotePid;
        public string Protocol;
        public string Host;
        public int Port;
        public string StartedAtUtc;
        public string Target;
        public string AuthMode;
        public string Status;

        public bool IsAlive()
        {
            if (DaemonPid <= 0)
            {
                return false;
            }

            try
            {
                return !Process.GetProcessById(DaemonPid).HasExited;
            }
            catch
            {
                return false;
            }
        }

        public static SessionInfo Load(string sessionName)
        {
            SessionInfo info = new SessionInfo();
            info.SessionName = sessionName;
            string file = Path.Combine(SessionPaths.GetSessionDir(sessionName), "session.info");
            if (!File.Exists(file))
            {
                return info;
            }

            foreach (string line in ReadAllLinesShared(file))
            {
                int index = line.IndexOf('=');
                if (index <= 0)
                {
                    continue;
                }

                string key = line.Substring(0, index);
                string value = line.Substring(index + 1);
                if (key == "daemonPid") { int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out info.DaemonPid); }
                else if (key == "remotePid") { int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out info.RemotePid); }
                else if (key == "protocol") { info.Protocol = value; }
                else if (key == "host") { info.Host = value; }
                else if (key == "port") { int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out info.Port); }
                else if (key == "startedAtUtc") { info.StartedAtUtc = value; }
                else if (key == "target") { info.Target = value; }
                else if (key == "authMode") { info.AuthMode = value; }
                else if (key == "status") { info.Status = value; }
            }

            return info;
        }

        private static string[] ReadAllLinesShared(string file)
        {
            for (int attempt = 0; attempt < 5; attempt++)
            {
                try
                {
                    List<string> lines = new List<string>();
                    using (FileStream stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                    using (StreamReader reader = new StreamReader(stream, Encoding.UTF8))
                    {
                        while (!reader.EndOfStream)
                        {
                            lines.Add(reader.ReadLine());
                        }
                    }

                    return lines.ToArray();
                }
                catch (IOException)
                {
                    if (attempt == 4)
                    {
                        throw;
                    }

                    Thread.Sleep(100);
                }
            }

            return new string[0];
        }
    }

    internal interface ISessionTransport : IDisposable
    {
        string ProtocolName { get; }
        string TargetDescription { get; }
        int RemotePid { get; }
        bool HasExited { get; }
        Stream InputStream { get; }
        Stream OutputStream { get; }
        Stream ErrorStream { get; }
        void Start();
        void Stop();
        bool WaitForExit(int milliseconds);
    }

    internal sealed class SshSessionTransport : ISessionTransport
    {
        private readonly string _sshPath;
        private readonly string _sshArguments;
        private readonly string _password;
        private readonly string _sessionDir;
        private Process _process;
        private string _askPassScriptPath;

        public SshSessionTransport(string sshPath, string sshArguments, string password, string sessionDir)
        {
            _sshPath = sshPath;
            _sshArguments = sshArguments;
            _password = password;
            _sessionDir = sessionDir;
        }

        public string ProtocolName { get { return "ssh"; } }
        public string TargetDescription { get { return _sshArguments; } }
        public int RemotePid { get { return _process == null ? 0 : _process.Id; } }
        public bool HasExited { get { return _process == null || _process.HasExited; } }
        public Stream InputStream { get { return _process.StandardInput.BaseStream; } }
        public Stream OutputStream { get { return _process.StandardOutput.BaseStream; } }
        public Stream ErrorStream { get { return _process.StandardError.BaseStream; } }

        public void Start()
        {
            ProcessStartInfo startInfo = new ProcessStartInfo
            {
                FileName = _sshPath,
                Arguments = _sshArguments,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            if (!string.IsNullOrEmpty(_password))
            {
                _askPassScriptPath = CreateAskPassScript();
                startInfo.EnvironmentVariables["SSH_ASKPASS"] = _askPassScriptPath;
                startInfo.EnvironmentVariables["SSH_ASKPASS_REQUIRE"] = "force";
                startInfo.EnvironmentVariables["DISPLAY"] = "termwrap";
            }

            _process = Process.Start(startInfo);
            if (_process == null)
            {
                throw new InvalidOperationException("failed to start ssh transport");
            }
        }

        public void Stop()
        {
            try
            {
                if (_process != null && !_process.HasExited)
                {
                    _process.StandardInput.Close();
                    if (!_process.WaitForExit(2000))
                    {
                        _process.Kill();
                    }
                }
            }
            finally
            {
                DeleteAskPassScript();
            }
        }

        public bool WaitForExit(int milliseconds)
        {
            return _process == null || _process.WaitForExit(milliseconds);
        }

        public void Dispose()
        {
            Stop();
            if (_process != null)
            {
                _process.Dispose();
                _process = null;
            }
        }

        private string CreateAskPassScript()
        {
            string path = Path.Combine(_sessionDir, "askpass.cmd");
            File.WriteAllText(
                path,
                "@echo off" + Environment.NewLine +
                "setlocal disableDelayedExpansion" + Environment.NewLine +
                "echo " + EscapeForCmd(_password) + Environment.NewLine,
                Encoding.ASCII);
            return path;
        }

        private void DeleteAskPassScript()
        {
            try
            {
                if (!string.IsNullOrEmpty(_askPassScriptPath) && File.Exists(_askPassScriptPath))
                {
                    File.Delete(_askPassScriptPath);
                }
            }
            catch (Exception ex)
            {
                Logger.Error("askpass cleanup " + ex);
            }
        }

        private static string EscapeForCmd(string value)
        {
            StringBuilder builder = new StringBuilder(value.Length * 2);
            for (int i = 0; i < value.Length; i++)
            {
                char c = value[i];
                if (c == '^' || c == '&' || c == '|' || c == '<' || c == '>' || c == '(' || c == ')' || c == '%')
                {
                    builder.Append('^');
                }

                builder.Append(c);
            }

            return builder.ToString();
        }
    }

    internal sealed class SessionDaemon
    {
        private readonly string _sessionName;
        private readonly string _protocol;
        private readonly string _host;
        private readonly string _transportPath;
        private readonly string _transportArguments;
        private readonly string _userName;
        private readonly string _password;
        private readonly string _sessionDir;
        private readonly string _metaFile;
        private readonly string _outputLog;
        private readonly TailBuffer _tailBuffer;
        private readonly object _sync = new object();
        private readonly DateTime _startedAtUtc;
        private Mutex _mutex;
        private volatile bool _stopping;
        private FileStream _outputFile;
        private ISessionTransport _transport;
        private int _promptStage;
        private string _loginPrompt;
        private string _passwordPrompt;
        private string _recentText = string.Empty;

        public SessionDaemon(string sessionName, string protocol, string host, string transportPath, string transportArguments, string userName, string password, string promptConfig)
        {
            _sessionName = SessionPaths.Sanitize(sessionName);
            _protocol = protocol;
            _host = host;
            _transportPath = transportPath;
            _transportArguments = transportArguments;
            _userName = userName;
            _password = password;
            _sessionDir = SessionPaths.GetSessionDir(_sessionName);
            _metaFile = Path.Combine(_sessionDir, "session.info");
            _outputLog = Path.Combine(_sessionDir, "output.log");
            _tailBuffer = new TailBuffer(1024 * 1024);
            _startedAtUtc = DateTime.UtcNow;
            ParsePromptConfig(promptConfig);
        }

        public int Run()
        {
            Directory.CreateDirectory(_sessionDir);
            bool createdNew;
            _mutex = new Mutex(false, SessionPaths.GetMutexName(_sessionName), out createdNew);
            if (!_mutex.WaitOne(0))
            {
                throw new InvalidOperationException("session already active: " + _sessionName);
            }

            try
            {
                _outputFile = new FileStream(_outputLog, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
                _transport = CreateTransport();
                _transport.Start();
                WriteMetadata();

                Thread stdoutThread = StartPumpThread(_transport.OutputStream, "stdout");
                Thread stderrThread = _transport.ErrorStream == null ? null : StartPumpThread(_transport.ErrorStream, "stderr");
                Thread commandThread = StartCommandServerThread();

                while (!_stopping && !_transport.HasExited)
                {
                    Thread.Sleep(250);
                }

                _stopping = true;
                _transport.Stop();
                if (stdoutThread != null) { stdoutThread.Join(1000); }
                if (stderrThread != null) { stderrThread.Join(1000); }
                if (commandThread != null) { commandThread.Join(1000); }
                WriteMetadata();
                return 0;
            }
            finally
            {
                if (_transport != null) { _transport.Dispose(); }
                if (_outputFile != null) { _outputFile.Dispose(); }
                ReleaseMutex();
            }
        }

        private void ParsePromptConfig(string promptConfig)
        {
            string[] parts = (promptConfig ?? string.Empty).Split(new[] { '\n' }, 2);
            _loginPrompt = parts.Length > 0 && !string.IsNullOrEmpty(parts[0]) ? parts[0] : "login:";
            _passwordPrompt = parts.Length > 1 && !string.IsNullOrEmpty(parts[1]) ? parts[1] : "password:";
        }

        private ISessionTransport CreateTransport()
        {
            if (string.Equals(_protocol, "ssh", StringComparison.OrdinalIgnoreCase))
            {
                return new SshSessionTransport(_transportPath, _transportArguments, _password, _sessionDir);
            }

            if (string.Equals(_protocol, "telnet", StringComparison.OrdinalIgnoreCase))
            {
                int port = int.Parse(_transportArguments, CultureInfo.InvariantCulture);
                return new TelnetSessionTransport(_host, port);
            }

            throw new InvalidOperationException("unsupported protocol: " + _protocol);
        }

        private Thread StartPumpThread(Stream source, string sourceName)
        {
            Thread thread = new Thread(new ThreadStart(delegate { PumpStream(source, sourceName); }));
            thread.IsBackground = true;
            thread.Start();
            return thread;
        }

        private Thread StartCommandServerThread()
        {
            Thread thread = new Thread(new ThreadStart(RunCommandServer));
            thread.IsBackground = true;
            thread.Start();
            return thread;
        }

        private void RunCommandServer()
        {
            while (!_stopping)
            {
                NamedPipeServerStream pipe = null;
                try
                {
                    pipe = new NamedPipeServerStream(
                        SessionPaths.GetCommandPipeName(_sessionName),
                        PipeDirection.InOut,
                        1,
                        PipeTransmissionMode.Byte,
                        PipeOptions.Asynchronous,
                        4096,
                        4096,
                        PipeSecurityFactory_v1_0_1.Create());

                    IAsyncResult result = pipe.BeginWaitForConnection(null, null);
                    while (!_stopping && !result.AsyncWaitHandle.WaitOne(250))
                    {
                    }

                    if (_stopping)
                    {
                        pipe.Dispose();
                        return;
                    }

                    pipe.EndWaitForConnection(result);
                    using (pipe)
                    using (StreamReader reader = new StreamReader(pipe, Encoding.UTF8, false, 1024, true))
                    using (StreamWriter writer = new StreamWriter(pipe, Encoding.UTF8, 1024, true))
                    {
                        writer.NewLine = "\n";
                        string command = reader.ReadLine() ?? string.Empty;
                        string response = HandleCommand(command);
                        writer.WriteLine(response);
                        writer.Flush();
                    }
                }
                catch (Exception ex)
                {
                    if (!_stopping)
                    {
                        Logger.Error("command server " + ex);
                    }

                    if (pipe != null)
                    {
                        pipe.Dispose();
                    }
                }
            }
        }

        private string HandleCommand(string command)
        {
            try
            {
                if (string.Equals(command, "STOP", StringComparison.Ordinal))
                {
                    _stopping = true;
                    return "OK stopped";
                }

                if (string.Equals(command, "READ", StringComparison.Ordinal)) { return EncodeSnapshot("READ", _tailBuffer.ReadAll()); }
                if (string.Equals(command, "READ_CLEAR", StringComparison.Ordinal)) { return EncodeSnapshot("READ_CLEAR", _tailBuffer.ReadAllAndClear()); }
                if (command.StartsWith("TAIL ", StringComparison.Ordinal))
                {
                    long offset = long.Parse(command.Substring(5), CultureInfo.InvariantCulture);
                    return EncodeSnapshot("TAIL", _tailBuffer.Read(offset));
                }
                if (command.StartsWith("SEND_TEXT ", StringComparison.Ordinal))
                {
                    byte[] data = Convert.FromBase64String(command.Substring(10));
                    WriteToTransport(data);
                    return "OK sent text";
                }
                if (command.StartsWith("SEND_HEX ", StringComparison.Ordinal))
                {
                    byte[] data = HexToBytes(command.Substring(9));
                    WriteToTransport(data);
                    return "OK sent hex";
                }
                if (command.StartsWith("SEND_CONTROL ", StringComparison.Ordinal))
                {
                    byte[] data = ControlNameToBytes(command.Substring(13));
                    WriteToTransport(data);
                    return "OK sent control";
                }

                return "ERR unknown command";
            }
            catch (Exception ex)
            {
                Logger.Error("command failed session=" + _sessionName + " command=" + command + " " + ex);
                return "ERR " + ex.Message.Replace('\r', ' ').Replace('\n', ' ');
            }
        }

        private string EncodeSnapshot(string kind, TailSnapshot snapshot)
        {
            string payload = snapshot.Data.Length == 0 ? "-" : Convert.ToBase64String(snapshot.Data);
            return string.Format(CultureInfo.InvariantCulture, "OK {0} {1} {2} {3}", kind, snapshot.StartOffset, snapshot.EndOffset, payload);
        }

        private void PumpStream(Stream source, string sourceName)
        {
            byte[] buffer = new byte[4096];
            try
            {
                while (!_stopping)
                {
                    int read = source.Read(buffer, 0, buffer.Length);
                    if (read <= 0)
                    {
                        break;
                    }

                    lock (_sync)
                    {
                        _outputFile.Write(buffer, 0, read);
                        _outputFile.Flush();
                        _tailBuffer.Append(buffer, read);
                    }

                    if (string.Equals(_protocol, "telnet", StringComparison.OrdinalIgnoreCase))
                    {
                        MaybeHandleTelnetAutoLogin(buffer, read);
                    }

                    Logger.Info("stream session={0} source={1} bytes={2}", _sessionName, sourceName, read);
                }
            }
            catch (Exception ex)
            {
                if (!_stopping)
                {
                    Logger.Error("stream pump error session=" + _sessionName + " source=" + sourceName + " " + ex);
                }
            }
        }

        private void MaybeHandleTelnetAutoLogin(byte[] buffer, int count)
        {
            if (string.IsNullOrEmpty(_userName) && string.IsNullOrEmpty(_password))
            {
                return;
            }

            string chunk = Encoding.ASCII.GetString(buffer, 0, count);
            _recentText = _recentText + chunk;
            if (_recentText.Length > 256)
            {
                _recentText = _recentText.Substring(_recentText.Length - 256);
            }

            string normalized = _recentText.ToLowerInvariant();
            if (_promptStage == 0 && !string.IsNullOrEmpty(_userName) && normalized.Contains((_loginPrompt ?? string.Empty).ToLowerInvariant()))
            {
                byte[] data = Encoding.ASCII.GetBytes(_userName + "\r\n");
                WriteToTransport(data);
                _promptStage = 1;
                Logger.Info("telnet auto-login sent username session={0}", _sessionName);
                return;
            }

            if (_promptStage <= 1 && !string.IsNullOrEmpty(_password) && normalized.Contains((_passwordPrompt ?? string.Empty).ToLowerInvariant()))
            {
                byte[] data = Encoding.ASCII.GetBytes(_password + "\r\n");
                WriteToTransport(data);
                _promptStage = 2;
                Logger.Info("telnet auto-login sent password session={0}", _sessionName);
            }
        }

        private void WriteToTransport(byte[] data)
        {
            lock (_sync)
            {
                _transport.InputStream.Write(data, 0, data.Length);
                _transport.InputStream.Flush();
            }
        }

        private void WriteMetadata()
        {
            File.WriteAllText(
                _metaFile,
                string.Join(
                    Environment.NewLine,
                    "version=1.0.1",
                    "session=" + _sessionName,
                    "daemonPid=" + Process.GetCurrentProcess().Id.ToString(CultureInfo.InvariantCulture),
                    "remotePid=" + (_transport == null ? "0" : _transport.RemotePid.ToString(CultureInfo.InvariantCulture)),
                    "protocol=" + _protocol,
                    "host=" + _host,
                    "port=" + ExtractPort(),
                    "userName=" + _userName,
                    "authMode=" + GetAuthMode(),
                    "knownHostsFile=" + SessionPaths.DefaultKnownHostsFile,
                    "target=" + (_transport == null ? _transportArguments : _transport.TargetDescription),
                    "startedAtUtc=" + _startedAtUtc.ToString("o", CultureInfo.InvariantCulture),
                    "status=" + (_stopping ? "stopping" : (_transport != null && !_transport.HasExited ? "running" : "stopped"))),
                Encoding.UTF8);
        }

        private string GetAuthMode()
        {
            if (string.Equals(_protocol, "telnet", StringComparison.OrdinalIgnoreCase))
            {
                if (!string.IsNullOrEmpty(_userName) || !string.IsNullOrEmpty(_password))
                {
                    return "telnet_prompt_auto";
                }

                return "none";
            }

            return string.IsNullOrEmpty(_password) ? "none" : "ssh_askpass";
        }

        private string ExtractPort()
        {
            if (string.Equals(_protocol, "telnet", StringComparison.OrdinalIgnoreCase))
            {
                return _transportArguments;
            }

            string[] parts = _transportArguments.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < parts.Length - 1; i++)
            {
                if (parts[i] == "-p")
                {
                    return parts[i + 1].Trim('"');
                }
            }

            return "22";
        }

        public static void ClearStaleProcesses(SessionInfo info)
        {
            if (info == null)
            {
                return;
            }

            TryKillTrackedProcess(info.DaemonPid, "termwrap");
            if (string.Equals(info.Protocol, "ssh", StringComparison.OrdinalIgnoreCase))
            {
                TryKillTrackedProcess(info.RemotePid, "ssh");
            }

            TryDeleteAskPassFile(info);
        }

        private static void TryKillTrackedProcess(int pid, string expectedProcessName)
        {
            if (pid <= 0)
            {
                return;
            }

            try
            {
                Process process = Process.GetProcessById(pid);
                if (process.HasExited)
                {
                    return;
                }

                if (!string.Equals(process.ProcessName, expectedProcessName, StringComparison.OrdinalIgnoreCase))
                {
                    Logger.Info("clear-stale skipped pid={0} expected={1} actual={2}", pid.ToString(CultureInfo.InvariantCulture), expectedProcessName, process.ProcessName);
                    return;
                }

                process.Kill();
                process.WaitForExit(2000);
                Logger.Info("clear-stale killed pid={0} name={1}", pid.ToString(CultureInfo.InvariantCulture), expectedProcessName);
            }
            catch (Exception ex)
            {
                Logger.Error("clear-stale pid=" + pid.ToString(CultureInfo.InvariantCulture) + " " + ex);
            }
        }

        private static void TryDeleteAskPassFile(SessionInfo info)
        {
            try
            {
                string path = Path.Combine(SessionPaths.GetSessionDir(info.SessionName), "askpass.cmd");
                if (File.Exists(path))
                {
                    File.Delete(path);
                    Logger.Info("clear-stale deleted askpass session={0}", info.SessionName);
                }
            }
            catch (Exception ex)
            {
                Logger.Error("clear-stale askpass session=" + info.SessionName + " " + ex);
            }
        }

        private void ReleaseMutex()
        {
            if (_mutex == null)
            {
                return;
            }

            try
            {
                _mutex.ReleaseMutex();
            }
            catch
            {
            }

            _mutex.Dispose();
            _mutex = null;
        }

        private static byte[] HexToBytes(string hex)
        {
            string compact = hex.Replace(" ", string.Empty).Replace("-", string.Empty);
            if (compact.Length % 2 != 0)
            {
                throw new InvalidOperationException("hex string must have even length");
            }

            byte[] data = new byte[compact.Length / 2];
            for (int i = 0; i < data.Length; i++)
            {
                data[i] = byte.Parse(compact.Substring(i * 2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            }

            return data;
        }

        private static byte[] ControlNameToBytes(string name)
        {
            switch (name.ToLowerInvariant())
            {
                case "ctrl-c": return new byte[] { 3 };
                case "ctrl-d": return new byte[] { 4 };
                case "ctrl-z": return new byte[] { 26 };
                case "esc": return new byte[] { 27 };
                case "tab": return new byte[] { 9 };
                case "enter": return new byte[] { 13 };
                case "backspace": return new byte[] { 8 };
                case "up": return Encoding.ASCII.GetBytes("\u001b[A");
                case "down": return Encoding.ASCII.GetBytes("\u001b[B");
                case "right": return Encoding.ASCII.GetBytes("\u001b[C");
                case "left": return Encoding.ASCII.GetBytes("\u001b[D");
                default: throw new InvalidOperationException("unsupported control: " + name);
            }
        }
    }
}
