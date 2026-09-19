using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Threading;

namespace TermWrap
{
    internal enum ConnectionProtocol
    {
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
        public bool EnableLegacySsh;
        public bool WaitReady;
        public bool AskPassword;
        public bool PasswordStdin;

        public string GetPromptConfig()
        {
            return (LoginPrompt ?? string.Empty) + "\n" + (PasswordPrompt ?? string.Empty);
        }
    }

    internal sealed class StopOptions
    {
        public string SessionName;
        public bool ClearStale;
        public bool Prune;
        public bool All;
    }

    internal sealed class SessionCommandOptions
    {
        public string SessionName;
        public bool Clear;
        public bool Wait;
        public string TextValue;
        public string LineValue;
        public string HexValue;
        public string ControlValue;
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
        private static readonly string Sessions = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, ".termwrap-sessions");

        public static string SessionsRoot { get { return Sessions; } }
        public static string DefaultKnownHostsFile { get { return GetWindowsSharedKnownHostsFile(); } }

        public static string GetWindowsSharedKnownHostsFile()
        {
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "ssh", "ssh_known_hosts");
        }

        public static string GetSessionKnownHostsFile(string sessionName)
        {
            return Path.Combine(GetSessionDir(sessionName), "known_hosts");
        }

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
            string sanitized = Sanitize(sessionName);
            if (sanitized == "." || sanitized == "..")
            {
                throw new InvalidOperationException("session name cannot be '.' or '..'");
            }

            string root = Path.GetFullPath(SessionsRoot);
            string candidate = Path.GetFullPath(Path.Combine(root, sanitized));
            string rootPrefix = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!candidate.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("session path escapes the sessions directory");
            }

            return candidate;
        }

        public static bool SessionDirectoryExists(string sessionName)
        {
            return Directory.Exists(GetSessionDir(sessionName));
        }

        public static void DeleteSessionDirectory(string sessionName)
        {
            string dir = GetSessionDir(sessionName);
            if (Directory.Exists(dir))
            {
                RejectReparsePoint(dir);
                Directory.Delete(dir, true);
            }
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
            if (string.IsNullOrEmpty(value))
            {
                throw new InvalidOperationException("session name cannot be empty");
            }

            StringBuilder builder = new StringBuilder(value.Length);
            for (int i = 0; i < value.Length; i++)
            {
                char c = value[i];
                if (!char.IsLetterOrDigit(c) && c != '-' && c != '_' && c != '.')
                {
                    throw new InvalidOperationException("session name contains an unsupported character: " + c);
                }

                builder.Append(c);
            }

            return builder.ToString();
        }

        public static void EnsureSecureSessionDirectory(string sessionName)
        {
            EnsureSecureDirectory(SessionsRoot);
            EnsureSecureDirectory(GetSessionDir(sessionName));
        }

        private static void EnsureSecureDirectory(string path)
        {
            Directory.CreateDirectory(path);
            RejectReparsePoint(path);
            DirectorySecurity security = new DirectorySecurity();
            security.SetAccessRuleProtection(true, false);
            AddFullControlRules(security, true);
            Directory.SetAccessControl(path, security);
        }

        private static void RejectReparsePoint(string path)
        {
            FileAttributes attributes = File.GetAttributes(path);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidOperationException("session paths cannot be reparse points: " + path);
            }
        }

        internal static void AddFullControlRules(FileSystemSecurity security, bool inheritToChildren)
        {
            InheritanceFlags inheritance = inheritToChildren
                ? InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit
                : InheritanceFlags.None;
            SecurityIdentifier currentUser = WindowsIdentity.GetCurrent().User;
            if (currentUser != null)
            {
                security.AddAccessRule(new FileSystemAccessRule(currentUser, FileSystemRights.FullControl, inheritance, PropagationFlags.None, AccessControlType.Allow));
            }

            security.AddAccessRule(new FileSystemAccessRule(
                new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
                FileSystemRights.FullControl,
                inheritance,
                PropagationFlags.None,
                AccessControlType.Allow));
            security.AddAccessRule(new FileSystemAccessRule(
                new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
                FileSystemRights.FullControl,
                inheritance,
                PropagationFlags.None,
                AccessControlType.Allow));
        }
    }

    internal static class CredentialStore
    {
        public static string Create(string sessionName, string password)
        {
            if (password == null)
            {
                return string.Empty;
            }

            SessionPaths.EnsureSecureSessionDirectory(sessionName);
            string path = Path.Combine(SessionPaths.GetSessionDir(sessionName), "credential.secret");
            WriteProtected(path, password, new UTF8Encoding(false));
            return path;
        }

        public static void WriteProtected(string path, string content, Encoding encoding)
        {
            File.WriteAllText(path, content, encoding);
            FileSecurity security = new FileSecurity();
            security.SetAccessRuleProtection(true, false);
            SessionPaths.AddFullControlRules(security, false);
            File.SetAccessControl(path, security);
        }

        public static string Consume(string sessionName, string path)
        {
            if (string.IsNullOrEmpty(path))
            {
                return null;
            }

            string expectedPath = Path.GetFullPath(Path.Combine(SessionPaths.GetSessionDir(sessionName), "credential.secret"));
            string fullPath = Path.GetFullPath(path);
            if (!string.Equals(fullPath, expectedPath, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("invalid credential file path");
            }

            try
            {
                return File.ReadAllText(fullPath, Encoding.UTF8);
            }
            finally
            {
                if (File.Exists(fullPath))
                {
                    File.Delete(fullPath);
                }
            }
        }

        public static void Delete(string path)
        {
            if (!string.IsNullOrEmpty(path) && File.Exists(path))
            {
                File.Delete(path);
            }
        }

        public static string Read(string path)
        {
            return File.ReadAllText(path, Encoding.UTF8);
        }
    }

    internal static class Logger
    {
        private static readonly object Sync = new object();
        private static string _version;
        private static string _appLogFile;

        public static void Initialize(string version, string logFolder)
        {
            _version = version;
            if (!string.IsNullOrEmpty(logFolder))
            {
                Directory.CreateDirectory(logFolder);
                _appLogFile = Path.Combine(logFolder, "termwrap.log");
            }

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
            if (string.IsNullOrEmpty(_appLogFile))
            {
                return;
            }

            lock (Sync)
            {
                string line = string.Format(
                    CultureInfo.InvariantCulture,
                    "{0} [{1}] v{2} pid={3} {4}{5}",
                    DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture),
                    level,
                    _version ?? "unknown",
                    Process.GetCurrentProcess().Id,
                    message,
                    Environment.NewLine);
                byte[] data = Encoding.UTF8.GetBytes(line);
                using (FileStream stream = new FileStream(_appLogFile, FileMode.Append, FileAccess.Write, FileShare.ReadWrite))
                {
                    stream.Write(data, 0, data.Length);
                    stream.Flush();
                }
            }
        }
    }

    internal sealed class SessionInfo
    {
        public string SessionName;
        public int DaemonPid;
        public int RemotePid;
        public string RemoteStartedAtUtc;
        public string Protocol;
        public string Host;
        public int Port;
        public string StartedAtUtc;
        public string Target;
        public string AuthMode;
        public string Status;
        public string LastError;
        public string ExitReason;
        public string StderrTail;

        public bool IsAlive()
        {
            if (DaemonPid <= 0)
            {
                return false;
            }

            try
            {
                Process process = Process.GetProcessById(DaemonPid);
                string expectedName = Path.GetFileNameWithoutExtension(Process.GetCurrentProcess().MainModule.FileName);
                if (process.HasExited || !string.Equals(process.ProcessName, expectedName, StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }

                DateTime recordedStart;
                if (!DateTime.TryParse(
                    StartedAtUtc,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind,
                    out recordedStart))
                {
                    return false;
                }

                // A PID can be reused long after a session daemon exits.  The
                // metadata timestamp is captured in the daemon constructor,
                // so allow a small startup margin but reject another process.
                TimeSpan difference = process.StartTime.ToUniversalTime() - recordedStart.ToUniversalTime();
                if (Math.Abs(difference.TotalSeconds) > 30)
                {
                    return false;
                }

                return true;
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
                else if (key == "remoteStartedAtUtc") { info.RemoteStartedAtUtc = value; }
                else if (key == "protocol") { info.Protocol = value; }
                else if (key == "host") { info.Host = value; }
                else if (key == "port") { int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out info.Port); }
                else if (key == "startedAtUtc") { info.StartedAtUtc = value; }
                else if (key == "target") { info.Target = value; }
                else if (key == "authMode") { info.AuthMode = value; }
                else if (key == "status") { info.Status = value; }
                else if (key == "lastError") { info.LastError = value; }
                else if (key == "exitReason") { info.ExitReason = value; }
                else if (key == "stderrTail") { info.StderrTail = value; }
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
        string RemoteStartedAtUtc { get; }
        bool HasExited { get; }
        string DescribeState();
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
        private string _askPassSecretPath;

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
        public string RemoteStartedAtUtc
        {
            get
            {
                return _process == null
                    ? string.Empty
                    : _process.StartTime.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture);
            }
        }
        public bool HasExited { get { return _process == null || _process.HasExited; } }
        public string DescribeState()
        {
            if (_process == null)
            {
                return "process=null";
            }

            if (!_process.HasExited)
            {
                return "process=running";
            }

            return "process=exited exitCode=" + _process.ExitCode.ToString(CultureInfo.InvariantCulture);
        }
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

            if (_password != null)
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
            _askPassSecretPath = Path.Combine(_sessionDir, "askpass.secret");
            CredentialStore.WriteProtected(_askPassSecretPath, _password, new UTF8Encoding(false));
            string executablePath = Process.GetCurrentProcess().MainModule.FileName;
            CredentialStore.WriteProtected(
                path,
                "@echo off" + Environment.NewLine +
                QuoteForBatch(executablePath) + " --askpass-secret " + QuoteForBatch(_askPassSecretPath) + Environment.NewLine,
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

                CredentialStore.Delete(_askPassSecretPath);
            }
            catch (Exception ex)
            {
                Logger.Error("askpass cleanup " + ex);
            }
        }

        private static string QuoteForBatch(string value)
        {
            return "\"" + value.Replace("\"", "\"\"") + "\"";
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
        private readonly string _sessionKnownHostsFile;
        private readonly TailBuffer _tailBuffer;
        private readonly TailBuffer _readBuffer;
        private readonly object _sync = new object();
        private readonly object _metadataSync = new object();
        private readonly DateTime _startedAtUtc;
        private Mutex _mutex;
        private volatile bool _stopping;
        private FileStream _outputFile;
        private ISessionTransport _transport;
        private int _promptStage;
        private string _loginPrompt;
        private string _passwordPrompt;
        private string _recentText = string.Empty;
        private string _lastError = string.Empty;
        private string _exitReason = string.Empty;
        private string _stderrTail = string.Empty;

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
            _sessionKnownHostsFile = SessionPaths.GetSessionKnownHostsFile(_sessionName);
            _tailBuffer = new TailBuffer(1024 * 1024);
            _readBuffer = new TailBuffer(1024 * 1024);
            _startedAtUtc = DateTime.UtcNow;
            ParsePromptConfig(promptConfig);
        }

        public int Run()
        {
            SessionPaths.EnsureSecureSessionDirectory(_sessionName);
            bool createdNew;
            _mutex = new Mutex(false, SessionPaths.GetMutexName(_sessionName), out createdNew);
            if (!_mutex.WaitOne(0))
            {
                throw new InvalidOperationException("session already active: " + _sessionName);
            }

            try
            {
                EnsureSessionKnownHostsFile();
                _outputFile = new FileStream(_outputLog, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
                _transport = CreateTransport();
                _transport.Start();
                WriteMetadata();

                Thread stdoutThread = StartPumpThread(_transport.OutputStream, "stdout");
                Thread stderrThread = _transport.ErrorStream == null ? null : StartPumpThread(_transport.ErrorStream, "stderr");
                Thread commandThread = StartCommandServerThread();
                Thread transportMonitorThread = StartTransportMonitorThread();
                Logger.Info("daemon loop start session={0} transport={1} state={2}", _sessionName, _transport.ProtocolName, _transport.DescribeState());

                while (!_stopping && !_transport.HasExited)
                {
                    Thread.Sleep(250);
                }

                Logger.Info(
                    "daemon loop exit session={0} stopping={1} transportExited={2} state={3}",
                    _sessionName,
                    _stopping,
                    _transport.HasExited,
                    _transport.DescribeState());
                _exitReason = _stopping ? "stop requested" : "transport exited";
                _stopping = true;
                _transport.Stop();
                if (stdoutThread != null) { stdoutThread.Join(1000); }
                if (stderrThread != null) { stderrThread.Join(1000); }
                if (commandThread != null) { commandThread.Join(1000); }
                if (transportMonitorThread != null) { transportMonitorThread.Join(1000); }
                WriteMetadata();
                Logger.Info("daemon shutdown complete session={0}", _sessionName);
                return 0;
            }
            catch (Exception ex)
            {
                _lastError = ex.Message;
                _exitReason = "exception";
                WriteMetadata();
                Logger.Error("daemon startup/run failed session=" + _sessionName + " " + ex);
                throw;
            }
            finally
            {
                Logger.Info("daemon finally session={0}", _sessionName);
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

        private Thread StartTransportMonitorThread()
        {
            Thread thread = new Thread(new ThreadStart(MonitorTransportExit));
            thread.IsBackground = true;
            thread.Start();
            return thread;
        }

        private void MonitorTransportExit()
        {
            while (!_stopping && _transport != null && !_transport.HasExited)
            {
                Thread.Sleep(100);
            }

            if (_transport == null || _stopping)
            {
                return;
            }

            _exitReason = "transport exited: " + _transport.DescribeState();
            WriteMetadata();
            Logger.Info("transport exit monitor session={0} state={1}", _sessionName, _transport.DescribeState());
        }

        private void RunCommandServer()
        {
            while (!_stopping)
            {
                NamedPipeServerStream pipe = null;
                try
                {
                    string pipeName = SessionPaths.GetCommandPipeName(_sessionName);
                    pipe = new NamedPipeServerStream(
                        pipeName,
                        PipeDirection.InOut,
                        1,
                        PipeTransmissionMode.Byte,
                        PipeOptions.None,
                        4096,
                        4096,
                        PipeSecurityFactory.Create());

                    Logger.Info("command server listening session={0} pipe={1} identity={2}", _sessionName, pipeName, GetProcessIdentityForLog());
                    pipe.WaitForConnection();
                    Logger.Info("command server connected session={0} pipe={1} identity={2}", _sessionName, pipeName, GetProcessIdentityForLog());
                    using (pipe)
                    using (StreamReader reader = new StreamReader(pipe, Encoding.UTF8, false, 1024, true))
                    using (StreamWriter writer = new StreamWriter(pipe, Encoding.UTF8, 1024, true))
                    {
                        writer.NewLine = "\n";
                        string command = reader.ReadLine() ?? string.Empty;
                        Logger.Info("command server request session={0} pipe={1} command={2}", _sessionName, pipeName, SummarizePipeMessage(command));
                        string response = HandleCommand(command);
                        writer.WriteLine(response);
                        writer.Flush();
                        Logger.Info("command server response session={0} pipe={1} response={2}", _sessionName, pipeName, SummarizePipeMessage(response));
                    }
                }
                catch (Exception ex)
                {
                    if (!_stopping)
                    {
                        Logger.Error("command server " + ex.Message);
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

                if (string.Equals(command, "PING", StringComparison.Ordinal))
                {
                    return "OK pong";
                }

                if (string.Equals(command, "READ", StringComparison.Ordinal)) { return EncodeSnapshot("READ", _readBuffer.ReadAll()); }
                if (string.Equals(command, "READ_CLEAR", StringComparison.Ordinal)) { return EncodeSnapshot("READ_CLEAR", _readBuffer.ReadAllAndClear()); }
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
                Logger.Error("command failed session=" + _sessionName + " command=" + SummarizePipeMessage(command) + " " + ex.Message);
                return "ERR " + ex.Message.Replace('\r', ' ').Replace('\n', ' ');
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
                        Logger.Info("stream session={0} source={1} eof", _sessionName, sourceName);
                        break;
                    }

                    lock (_sync)
                    {
                        _outputFile.Write(buffer, 0, read);
                        _outputFile.Flush();
                        _tailBuffer.Append(buffer, read);
                        _readBuffer.Append(buffer, read);
                        if (sourceName == "stderr")
                        {
                            AppendStderrTail(buffer, read);
                        }
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
                    Logger.Error("stream pump error session=" + _sessionName + " source=" + sourceName + " " + ex.Message);
                }
            }
        }

        private void AppendStderrTail(byte[] buffer, int count)
        {
            string chunk = Encoding.UTF8.GetString(buffer, 0, count);
            _stderrTail = _stderrTail + chunk;
            if (_stderrTail.Length > 512)
            {
                _stderrTail = _stderrTail.Substring(_stderrTail.Length - 512);
            }
        }

        private void MaybeHandleTelnetAutoLogin(byte[] buffer, int count)
        {
            if (string.IsNullOrEmpty(_userName) && _password == null)
            {
                return;
            }

            bool sentUsernameThisCall = false;
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
                QueueWriteToTransport(data, 150);
                _promptStage = 1;
                sentUsernameThisCall = true;
                Logger.Info("telnet auto-login sent username session={0}", _sessionName);
                normalized = _recentText.ToLowerInvariant();
            }

            if (_promptStage <= 1 && _password != null && normalized.Contains((_passwordPrompt ?? string.Empty).ToLowerInvariant()))
            {
                byte[] data = Encoding.ASCII.GetBytes(_password + "\r\n");
                QueueWriteToTransport(data, 150);
                _promptStage = 2;
                Logger.Info("telnet auto-login sent password session={0}", _sessionName);
                return;
            }

            if (!sentUsernameThisCall && _promptStage == 1 && _password != null && count > 0)
            {
                byte[] data = Encoding.ASCII.GetBytes(_password + "\r\n");
                QueueWriteToTransport(data, 250);
                _promptStage = 2;
                Logger.Info("telnet auto-login sent password fallback session={0}", _sessionName);
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

        private void QueueWriteToTransport(byte[] data, int delayMilliseconds)
        {
            ThreadPool.QueueUserWorkItem(
                delegate
                {
                    if (delayMilliseconds > 0)
                    {
                        Thread.Sleep(delayMilliseconds);
                    }

                    if (_stopping)
                    {
                        return;
                    }

                    try
                    {
                        WriteToTransport(data);
                    }
                    catch (Exception ex)
                    {
                        if (!_stopping)
                        {
                            Logger.Error("transport write failed session=" + _sessionName + " " + ex.Message);
                        }
                    }
                });
        }

        private void WriteMetadata()
        {
            lock (_metadataSync)
            {
                string content = string.Join(
                    Environment.NewLine,
                    "version=current",
                    "session=" + CleanMetadataValue(_sessionName),
                    "daemonPid=" + Process.GetCurrentProcess().Id.ToString(CultureInfo.InvariantCulture),
                    "remotePid=" + (_transport == null ? "0" : _transport.RemotePid.ToString(CultureInfo.InvariantCulture)),
                    "remoteStartedAtUtc=" + (_transport == null ? string.Empty : _transport.RemoteStartedAtUtc),
                    "protocol=" + CleanMetadataValue(_protocol),
                    "host=" + CleanMetadataValue(_host),
                    "port=" + CleanMetadataValue(ExtractPort()),
                    "userName=" + CleanMetadataValue(_userName),
                    "authMode=" + GetAuthMode(),
                    "knownHostsFile=" + CleanMetadataValue(_sessionKnownHostsFile),
                    "target=" + CleanMetadataValue(_transport == null ? _transportArguments : _transport.TargetDescription),
                    "startedAtUtc=" + _startedAtUtc.ToString("o", CultureInfo.InvariantCulture),
                    "status=" + (_stopping ? "stopping" : (_transport != null && !_transport.HasExited ? "running" : "stopped")),
                    "lastError=" + CleanMetadataValue(_lastError),
                    "exitReason=" + CleanMetadataValue(_exitReason),
                    "stderrTail=" + CleanMetadataValue(_stderrTail));
                string tempFile = _metaFile + ".tmp";
                File.WriteAllText(tempFile, content, Encoding.UTF8);
                if (File.Exists(_metaFile))
                {
                    File.Replace(tempFile, _metaFile, null);
                }
                else
                {
                    File.Move(tempFile, _metaFile);
                }
            }
        }

        private static string CleanMetadataValue(string value)
        {
            return (value ?? string.Empty).Replace("\r", " ").Replace("\n", " ");
        }

        private void EnsureSessionKnownHostsFile()
        {
            if (File.Exists(_sessionKnownHostsFile))
            {
                return;
            }

            string sharedFile = SessionPaths.GetWindowsSharedKnownHostsFile();
            if (File.Exists(sharedFile))
            {
                File.Copy(sharedFile, _sessionKnownHostsFile, false);
                return;
            }

            using (FileStream stream = new FileStream(_sessionKnownHostsFile, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite))
            {
            }
        }

        private string GetAuthMode()
        {
            if (string.Equals(_protocol, "telnet", StringComparison.OrdinalIgnoreCase))
            {
                if (!string.IsNullOrEmpty(_userName) || _password != null)
                {
                    return "telnet_prompt_auto";
                }

                return "none";
            }

            return _password == null ? "none" : "ssh_askpass";
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

            string daemonName = Path.GetFileNameWithoutExtension(Process.GetCurrentProcess().MainModule.FileName);
            TryKillTrackedProcess(info.DaemonPid, daemonName, info.StartedAtUtc);
            if (string.Equals(info.Protocol, "ssh", StringComparison.OrdinalIgnoreCase))
            {
                TryKillTrackedProcess(info.RemotePid, "ssh", info.RemoteStartedAtUtc);
            }

            TryDeleteAskPassFile(info);
        }

        private static void TryKillTrackedProcess(int pid, string expectedProcessName, string expectedStartedAtUtc)
        {
            DateTime expectedStart;
            if (pid <= 0 || !DateTime.TryParse(expectedStartedAtUtc, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out expectedStart))
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

                TimeSpan difference = process.StartTime.ToUniversalTime() - expectedStart.ToUniversalTime();
                if (Math.Abs(difference.TotalSeconds) > 30)
                {
                    Logger.Info("clear-stale skipped pid={0} expectedStart={1} actualStart={2}", pid.ToString(CultureInfo.InvariantCulture), expectedStartedAtUtc, process.StartTime.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture));
                    return;
                }

                process.Kill();
                process.WaitForExit(2000);
                Logger.Info("clear-stale killed pid={0} name={1}", pid.ToString(CultureInfo.InvariantCulture), expectedProcessName);
            }
            catch (Exception ex)
            {
                Logger.Error("clear-stale pid=" + pid.ToString(CultureInfo.InvariantCulture) + " " + ex.Message);
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
                Logger.Error("clear-stale askpass session=" + info.SessionName + " " + ex.Message);
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

        private static string GetProcessIdentityForLog()
        {
            try
            {
                System.Security.Principal.WindowsIdentity identity = System.Security.Principal.WindowsIdentity.GetCurrent();
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





