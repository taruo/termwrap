using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;

// Black-box CLI tests. Every process executes a copy named termwrap.exe inside
// a fresh sandbox; GUID session names also isolate the global named pipes.
internal static class CliRegression
{
    private static readonly JavaScriptSerializer Json = new JavaScriptSerializer();
    private static string _application;
    private static string _caseRoot;
    private static int _assertions;
    private static int _failures;

    private static int Main(string[] args)
    {
        if (args.Length != 2 || Path.GetFileName(args[0]) != "termwrap.exe")
        {
            Console.Error.WriteLine("usage: CliRegression.exe BUILD/termwrap.exe CASE_DIRECTORY");
            return 2;
        }
        _application = Path.GetFullPath(args[0]);
        _caseRoot = Path.GetFullPath(args[1]);
        Directory.CreateDirectory(_caseRoot);
        RunGroup("arguments, exit codes, and common options", Arguments);
        RunGroup("Telnet input, output, JSON, and tail", RoundTrip);
        RunGroup("safe session selection and pruning", Selection);
        RunGroup("explicit stop --all and single-session cleanup", StopAll);
        RunGroup("password from stdin", PasswordStdin);
        RunGroup("readiness timeout reports exit 5", ReadinessTimeout);
        RunGroup("connection refusal returns one JSON error", ConnectionRefused);
        Console.WriteLine("{0} assertions; {1} failed groups", _assertions, _failures);
        return _failures == 0 ? 0 : 1;
    }

    private static void RunGroup(string name, Action<Sandbox> action)
    {
        Console.WriteLine("RUN  " + name);
        try
        {
            using (Sandbox sandbox = new Sandbox(_application, _caseRoot))
            {
                action(sandbox);
            }
            Console.WriteLine("PASS " + name);
        }
        catch (Exception ex)
        {
            _failures++;
            Console.Error.WriteLine("FAIL " + name + ": " + ex);
        }
    }

    private static void Arguments(Sandbox box)
    {
        Assert(List(box, false).Length == 0, "a new sandbox has no sessions");
        Success(box.Run("list", "--json"), "list");
        string prefixLog = Path.Combine(box.Root, "prefix logs");
        Success(box.Run("--log-folder", prefixLog, "--json", "list"), "list");
        Assert(File.Exists(Path.Combine(prefixLog, "termwrap.log")), "prefix log folder is honored");
        string suffixLog = Path.Combine(box.Root, "suffix logs");
        Success(box.Run("list", "--log-folder", suffixLog, "--json"), "list");
        Assert(File.Exists(Path.Combine(suffixLog, "termwrap.log")), "suffix log folder is honored");

        Result literalValue = box.Run("list", "--log-folder", "--json");
        Assert(literalValue.ExitCode == 0, "--json is accepted as an option value");
        Assert(!literalValue.Out.TrimStart().StartsWith("{"), "an option value does not enable JSON");
        Assert(File.Exists(Path.Combine(box.Root, "--json", "termwrap.log")), "literal --json log folder exists");
        Success(box.Run("--json", "list", "--log-folder", "--json"), "list");

        string[][] invalid = {
            new[] { "unknown-command" },
            new[] { "list", "--unknown" },
            new[] { "list", "--unknown\"\\\n\u65e5\u672c\u8a9e" },
            new[] { "list", "--log-folder" },
            new[] { "start", "--host", "127.0.0.1", "--protocol", "telnet", "--typo" },
            new[] { "start", "--host", "127.0.0.1", "--protocol", "telnet", "bare-argument" },
            new[] { "start", "--protocol", "telnet" },
            new[] { "start", "--host", "127.0.0.1", "--protocol", "invalid" },
            new[] { "start", "--host", "127.0.0.1", "--protocol", "telnet", "--port", "0" },
            new[] { "start", "--host", "127.0.0.1", "--protocol", "telnet", "--port", "65536" },
            new[] { "start", "--host", "127.0.0.1", "--protocol", "telnet", "--port", "abc" },
            new[] { "start", "--host", "127.0.0.1", "--protocol", "telnet", "-s" },
            new[] { "start", "--host", "127.0.0.1", "--protocol", "telnet", "--password", "dummy", "--ask-password" },
            new[] { "start", "--host", "127.0.0.1", "--protocol", "telnet", "--password", "dummy", "--password-stdin" },
            new[] { "start", "--host", "127.0.0.1", "--protocol", "telnet", "--ask-password", "--password-stdin" },
            new[] { "send" },
            new[] { "send", "--line" },
            new[] { "send", "--line", "first\rsecond" },
            new[] { "send", "--line", "first\nsecond" },
            new[] { "send", "--key" },
            new[] { "stop", "--all", "-s", box.Name("missing") },
            new[] { "prune", "--all" },
            new[] { "stop", "--all", "--prune" },
            new[] { "read", "--unknown" },
            new[] { "tail", "--unknown" }
        };
        foreach (string[] test in invalid)
        {
            Error(box.Run(Prepend("--json", test)), 2, "usage");
        }
        string[] modes = { "--text", "--hex", "--line", "--key" };
        string[] values = { "text", "41", "", "enter" };
        for (int left = 0; left < modes.Length; left++)
        {
            for (int right = left + 1; right < modes.Length; right++)
            {
                Error(box.Run("--json", "send", modes[left], values[left], modes[right], values[right]), 2, "usage");
            }
        }
        Error(box.Run("--json", "send", "--text", "a", "--control", "enter"), 2, "usage");
        Error(box.Run("--json", "read", "-s", box.Name("missing")), 3, "not_found");
        Error(box.Run("--json", "send", "-s", box.Name("missing"), "--line", ""), 3, "not_found");
        Error(box.Run("--json", "stop", "-s", box.Name("missing")), 3, "not_found");
        Error(box.Run("--json", "prune", "-s", box.Name("missing")), 3, "not_found");
        Error(box.Run("--json", "stop"), 3, "not_found");
        Error(box.Run("--json", "prune"), 3, "not_found");
        Result separator = box.Run("list", "--", "--json");
        Assert(separator.ExitCode == 2, "unexpected separator suffix is usage error");
        Assert(!separator.Err.TrimStart().StartsWith("{"), "--json after -- is not consumed");
        Assert(List(box, true).Length == 0, "invalid commands create no session records");
    }

    private static void RoundTrip(Sandbox box)
    {
        string name = box.Name("roundtrip");
        string banner = "ready \"quoted\" \\path\r\n\u65e5\u672c\u8a9e\t\u0001\r\ntester@dummy:/# ";
        using (DummyTelnet peer = new DummyTelnet(banner))
        {
            Start(box, peer, name, true);
            Dictionary<string, object>[] listed = List(box, false);
            Assert(listed.Length == 1, "one active session is listed");
            Dictionary<string, object> session = listed[0];
            Assert(Text(session, "session") == name, "list includes session name");
            Assert(Text(session, "state") == "running", "list includes running state");
            Assert(Text(session, "host") == "127.0.0.1", "list includes host");
            Assert(Text(session, "protocol") == "telnet", "list includes protocol");
            Assert(Number(session, "port") == peer.Port, "list includes numeric port");
            Assert(Number(session, "daemonPid") > 0 && Number(session, "remotePid") == 0, "list includes numeric process IDs");

            List<byte> expected = new List<byte>();
            string line = "hello \"\u4e16\u754c\" \\";
            Sent(box, name, "--line", line);
            Add(expected, line + "\r");
            peer.Expect(expected, "--line appends exactly one CR");
            Sent(box, name, "--line", "");
            Add(expected, "\r");
            peer.Expect(expected, "empty --line sends a CR");
            Sent(box, name, "--key", "ctrl-c");
            expected.Add(3);
            peer.Expect(expected, "--key sends control bytes");
            Sent(box, name, "--control", "enter");
            expected.Add(13);
            peer.Expect(expected, "--control alias remains compatible");

            Result plain = box.Run("send", "-s", name, "--text", "--json");
            Assert(plain.ExitCode == 0 && !plain.Out.TrimStart().StartsWith("{"), "--text --json remains a plain command");
            Add(expected, "--json");
            peer.Expect(expected, "literal --json payload is preserved");
            Sent(box, name, "--text", "--json");
            Add(expected, "--json");
            peer.Expect(expected, "literal --json payload also works in JSON mode");
            Sent(box, name, "--line", "--log-folder");
            Add(expected, "--log-folder\r");
            peer.Expect(expected, "literal --log-folder payload is preserved");
            Sent(box, name, "--hex", "41 42 00 FF");
            expected.AddRange(new byte[] { 65, 66, 0, 255, 255 });
            peer.Expect(expected, "hex payload uses Telnet IAC escaping");

            Error(box.Run("--json", "send", "-s", name, "--key", "not-a-key"), 2, "usage");
            Error(box.Run("--json", "send", "-s", name, "--hex", "GG"), 2, "usage");
            peer.Expect(expected, "invalid payloads send no bytes");
            Dictionary<string, object> read = Success(box.Run("read", "-s", name, "--clear", "--json"), "read");
            Assert(Text(read, "session") == name, "read identifies its session");
            Assert(Text(read, "text") == banner, "JSON escapes round-trip quotes, backslashes, control characters and Unicode");
            Assert(Text(read, "dataBase64") == Convert.ToBase64String(Encoding.UTF8.GetBytes(banner)), "read includes exact received bytes");
            Assert(Convert.ToBoolean(read["cleared"], CultureInfo.InvariantCulture), "read acknowledges clear");
            Assert(Number(read, "endOffset") - Number(read, "startOffset") == Encoding.UTF8.GetByteCount(banner), "read offsets count bytes");
            Dictionary<string, object> empty = Success(box.Run("--json", "read", "-s", name), "read");
            Assert(Text(empty, "text") == "" && Text(empty, "dataBase64") == "", "read --clear empties the next read");

            using (Invocation tail = box.Begin("--json", "tail", "-s", name))
            {
                WaitUntil(delegate { return TailContains(tail.Out, banner); }, 8000, "tail emits its buffered data");
                string marker = "\r\ntail \"event\" \\ \u65e5\u672c\u8a9e\r\n";
                peer.Write(marker);
                WaitUntil(delegate { return TailContains(tail.Out, marker); }, 8000, "tail emits subsequent data");
                Result plainRead = box.Run("read", "-s", name);
                Assert(plainRead.ExitCode == 0 && plainRead.Out == marker, "plain read prints raw terminal text");
                Dictionary<string, object> stopped = Success(box.Run("stop", "-s", name, "--force", "--json"), "stop");
                Assert(Text(stopped, "session") == name, "stop reports the selected session");
                Result tailResult = tail.Finish(8000);
                Assert(tailResult.ExitCode == 0 && tailResult.Err == "", "tail exits cleanly after stop");
                bool sawData = false;
                bool sawStopped = false;
                foreach (string eventLine in Lines(tailResult.Out))
                {
                    Dictionary<string, object> item = Object(eventLine);
                    Assert(Convert.ToBoolean(item["ok"], CultureInfo.InvariantCulture), "tail event has ok:true");
                    Assert(Text(item, "command") == "tail" && Text(item, "session") == name, "tail event includes command and session");
                    if (Text(item, "event") == "data")
                    {
                        sawData = true;
                        Assert(Text(item, "text") == Encoding.UTF8.GetString(Convert.FromBase64String(Text(item, "dataBase64"))), "tail text and base64 agree");
                    }
                    if (Text(item, "event") == "stopped") { sawStopped = true; }
                }
                Assert(sawData && sawStopped, "tail contains data and terminal stopped events");
            }
            Assert(Directory.Exists(box.SessionDirectory(name)), "stop retains stopped data");
            Assert(List(box, false).Length == 0, "default list hides stopped sessions");
            Assert(Text(List(box, true)[0], "state") == "stopped", "list --all shows stopped sessions");
            Dictionary<string, object> pruned = Success(box.Run("--json", "prune"), "prune");
            Assert(Text(pruned, "session") == name, "prune selects a unique known session");
            Assert(!Directory.Exists(box.SessionDirectory(name)), "prune removes the selected data");
        }
    }

    private static void Selection(Sandbox box)
    {
        string first = box.Name("first");
        string second = box.Name("second");
        string third = box.Name("third");
        using (DummyTelnet a = new DummyTelnet("first\r\n"))
        using (DummyTelnet b = new DummyTelnet("second\r\n"))
        using (DummyTelnet c = new DummyTelnet("third\r\n"))
        {
            Start(box, a, first, false);
            Start(box, b, second, false);
            Error(box.Run("--json", "stop", "--all", "-s", first), 2, "usage");
            Error(box.Run("--json", "prune", "--all"), 2, "usage");
            Error(box.Run("--json", "start", "-s", first, "--host", "127.0.0.1", "--protocol", "telnet",
                "--port", a.Port.ToString(CultureInfo.InvariantCulture)), 6, "conflict");
            Error(box.Run("--json", "stop", "--prune"), 4, "ambiguous");
            Error(box.Run("--json", "stop"), 4, "ambiguous");
            Error(box.Run("--json", "prune"), 4, "ambiguous");
            Assert(List(box, false).Length == 2, "ambiguous operations leave both sessions running");
            Assert(Directory.Exists(box.SessionDirectory(first)) && Directory.Exists(box.SessionDirectory(second)), "ambiguous stop --prune removes nothing");
            Error(box.Run("--json", "prune", "-s", first), 6, "conflict");
            Error(box.Run("--json", "prune", "--all"), 2, "usage");
            Assert(List(box, false).Length == 2, "rejected prune --all leaves active sessions unchanged");
            Success(box.Run("--json", "stop", "-s", first, "--prune"), "stop");
            Assert(!Directory.Exists(box.SessionDirectory(first)), "legacy stop --prune deletes its selected session");
            Assert(List(box, false).Length == 1 && Text(List(box, false)[0], "session") == second, "legacy stop --prune preserves the other session");

            Start(box, c, third, false);
            Success(box.Run("--json", "stop", "-s", third, "--clear-stale"), "stop");
            Error(box.Run("--json", "prune"), 4, "ambiguous");
            Success(box.Run("--json", "prune", "-s", third), "prune");
            Assert(!Directory.Exists(box.SessionDirectory(third)), "single-session prune deletes stopped data");
            Assert(Directory.Exists(box.SessionDirectory(second)) && List(box, false).Length == 1, "single-session prune retains the active connection");
            Error(box.Run("--json", "prune"), 6, "conflict");
            Dictionary<string, object> stop = Success(box.Run("--json", "stop", "--prune"), "stop");
            Assert(Text(stop, "session") == second, "legacy stop --prune selects a unique session");
            Assert(List(box, true).Length == 0, "unique legacy prune removes only its selected data");
        }
    }

    private static void StopAll(Sandbox box)
    {
        string first = box.Name("all-a");
        string second = box.Name("all-b");
        using (DummyTelnet a = new DummyTelnet("a\r\n"))
        using (DummyTelnet b = new DummyTelnet("b\r\n"))
        {
            Start(box, a, first, false);
            Start(box, b, second, false);
            Error(box.Run("--json", "stop", "--all", "--prune"), 2, "usage");
            Assert(List(box, false).Length == 2, "rejected stop --all --prune leaves sessions running");
            Success(box.Run("--json", "stop", "--all"), "stop");
            Assert(List(box, false).Length == 0, "explicit --all stops every isolated session");
            Assert(List(box, true).Length == 2, "stop --all retains both stopped records");
            Success(box.Run("--json", "prune", "-s", first), "prune");
            Success(box.Run("--json", "prune", "-s", second), "prune");
            Assert(List(box, true).Length == 0, "individual cleanup removes both records");
        }
    }

    private static void PasswordStdin(Sandbox box)
    {
        string secret = "test-only-" + Guid.NewGuid().ToString("N") + "-\"\\";
        PasswordInput(box, "stdin-value", secret);
        PasswordInput(box, "stdin-empty", "");
    }

    private static void PasswordInput(Sandbox box, string suffix, string secret)
    {
        string name = box.Name(suffix);
        using (DummyTelnet peer = new DummyTelnet("Password: "))
        {
            string log = Path.Combine(box.Root, "auth logs");
            Result result = box.RunWithInput(secret + "\r\n", "--json", "start", "-s", name,
                "--host", "127.0.0.1", "--protocol", "telnet", "--port", peer.Port.ToString(CultureInfo.InvariantCulture),
                "--password-stdin", "--log-folder", log);
            Success(result, "start");
            peer.Expect(new List<byte>(Encoding.ASCII.GetBytes(secret + "\r\n")), "stdin password is delivered to the dummy server");
            if (secret.Length > 0)
            {
                Assert(!result.Out.Contains(secret) && !result.Err.Contains(secret), "CLI output does not reveal the stdin password");
                Assert(!File.ReadAllText(Path.Combine(log, "termwrap.log"), Encoding.UTF8).Contains(secret), "debug log does not reveal the stdin password");
                Assert(!File.ReadAllText(Path.Combine(box.SessionDirectory(name), "session.info"), Encoding.UTF8).Contains(secret), "metadata does not contain the stdin password");
            }
            Success(box.Run("--json", "stop", "-s", name), "stop");
            Success(box.Run("--json", "prune", "-s", name), "prune");
        }
    }

    private static void ReadinessTimeout(Sandbox box)
    {
        string name = box.Name("timeout");
        using (DummyTelnet peer = new DummyTelnet("connected without a shell prompt\r\n"))
        {
            Error(box.Run("--json", "start", "-s", name, "--host", "127.0.0.1", "--protocol", "telnet",
                "--port", peer.Port.ToString(CultureInfo.InvariantCulture), "--wait-ready",
                "--log-folder", Path.Combine(box.Root, "timeout logs")), 5, "timeout");
        }
    }

    private static void ConnectionRefused(Sandbox box)
    {
        TcpListener unused = new TcpListener(IPAddress.Loopback, 0);
        unused.Start();
        int port = ((IPEndPoint)unused.LocalEndpoint).Port;
        unused.Stop();
        Error(box.Run("--json", "start", "-s", box.Name("refused"), "--host", "127.0.0.1",
            "--protocol", "telnet", "--port", port.ToString(CultureInfo.InvariantCulture),
            "--log-folder", Path.Combine(box.Root, "refused logs")), 1, "failure");
    }

    private static void Start(Sandbox box, DummyTelnet peer, string name, bool waitReady)
    {
        List<string> arguments = new List<string> { "start", "-s", name, "--host", "127.0.0.1",
            "--protocol", "telnet", "--port", peer.Port.ToString(CultureInfo.InvariantCulture),
            "--log-folder", Path.Combine(box.Root, "logs"), "--json" };
        if (waitReady) { arguments.Add("--wait-ready"); }
        Dictionary<string, object> result = Success(box.Run(arguments.ToArray()), "start");
        Assert(Text(result, "session") == name, "start reports the requested session");
    }

    private static void Sent(Sandbox box, string session, string option, string value)
    {
        Dictionary<string, object> result = Success(box.Run("--json", "send", "-s", session, option, value), "send");
        Assert(Text(result, "session") == session, "send reports the selected session");
    }

    private static Dictionary<string, object>[] List(Sandbox box, bool all)
    {
        Dictionary<string, object> result = Success(box.Run(all ? new[] { "--json", "list", "--all" } : new[] { "--json", "list" }), "list");
        object[] items = result["sessions"] as object[];
        Assert(items != null, "list.sessions is an array");
        List<Dictionary<string, object>> sessions = new List<Dictionary<string, object>>();
        foreach (object item in items) { sessions.Add((Dictionary<string, object>)item); }
        return sessions.ToArray();
    }

    private static Dictionary<string, object> Success(Result result, string command)
    {
        Assert(result.ExitCode == 0, command + " succeeds: " + result.Describe());
        Assert(result.Err == "", command + " emits no stderr on success: " + result.Describe());
        Dictionary<string, object> value = Object(result.Out);
        Assert(value.ContainsKey("ok") && value["ok"] is bool && (bool)value["ok"], command + " returns ok:true");
        Assert(Text(value, "command") == command, command + " identifies the command");
        return value;
    }

    private static void Error(Result result, int exitCode, string code)
    {
        Assert(result.ExitCode == exitCode, "expected exit " + exitCode + ": " + result.Describe());
        Assert(result.Out == "", "JSON errors leave stdout empty: " + result.Describe());
        Dictionary<string, object> value = Object(result.Err);
        Assert(value.ContainsKey("ok") && value["ok"] is bool && !(bool)value["ok"], "JSON error returns ok:false");
        Dictionary<string, object> error = (Dictionary<string, object>)value["error"];
        Assert(Text(error, "code") == code, "JSON error code is " + code + ": " + result.Describe());
        Assert(!string.IsNullOrWhiteSpace(Text(error, "message")), "JSON error has a message");
    }

    private static Dictionary<string, object> Object(string value)
    {
        try { return (Dictionary<string, object>)Json.DeserializeObject(value); }
        catch (Exception ex) { throw new Exception("Expected a single JSON object: " + Json.Serialize(value), ex); }
    }

    private static string Text(Dictionary<string, object> value, string field)
    {
        Assert(value.ContainsKey(field) && value[field] is string, "JSON string field " + field + " exists");
        return (string)value[field];
    }

    private static long Number(Dictionary<string, object> value, string field)
    {
        Assert(value.ContainsKey(field) && (value[field] is int || value[field] is long || value[field] is decimal), "JSON numeric field " + field + " exists");
        return Convert.ToInt64(value[field], CultureInfo.InvariantCulture);
    }

    private static bool TailContains(string output, string expected)
    {
        foreach (string line in Lines(output))
        {
            try
            {
                Dictionary<string, object> value = (Dictionary<string, object>)Json.DeserializeObject(line);
                object text;
                if (value.TryGetValue("text", out text) && Convert.ToString(text, CultureInfo.InvariantCulture).Contains(expected)) { return true; }
            }
            catch (ArgumentException) { }
        }
        return false;
    }

    private static string[] Lines(string text)
    {
        return text.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries);
    }

    private static string[] Prepend(string first, string[] rest)
    {
        List<string> values = new List<string> { first };
        values.AddRange(rest);
        return values.ToArray();
    }

    private static void Add(List<byte> expected, string value) { expected.AddRange(Encoding.UTF8.GetBytes(value)); }

    private static void Assert(bool condition, string message)
    {
        _assertions++;
        if (!condition) { throw new Exception(message); }
    }

    private static void WaitUntil(Func<bool> condition, int timeoutMs, string message)
    {
        Stopwatch timer = Stopwatch.StartNew();
        while (!condition())
        {
            if (timer.ElapsedMilliseconds >= timeoutMs) { throw new Exception("Timed out: " + message); }
            Thread.Sleep(30);
        }
        _assertions++;
    }

    private sealed class Sandbox : IDisposable
    {
        public readonly string Root;
        private readonly string _exe;
        private readonly string _id = Guid.NewGuid().ToString("N");
        private int _command;

        public Sandbox(string application, string caseRoot)
        {
            Root = Path.Combine(caseRoot, _id);
            Directory.CreateDirectory(Root);
            _exe = Path.Combine(Root, "termwrap.exe");
            File.Copy(application, _exe, false);
        }

        public string Name(string suffix) { return "cli-test-" + _id + "-" + suffix; }
        public string SessionDirectory(string name) { return Path.Combine(Root, ".termwrap-sessions", name); }
        public Result Run(params string[] args) { return RunWithInput("", args); }

        public Result RunWithInput(string input, params string[] args)
        {
            using (Invocation invocation = new Invocation(_exe, Root, input, args))
            {
                Result result = invocation.Finish(20000);
                _command++;
                // Arguments/input may contain credentials, so record only the outputs.
                File.WriteAllText(Path.Combine(Root, "command-" + _command.ToString("D3", CultureInfo.InvariantCulture) + ".log"), result.Describe(), Encoding.UTF8);
                return result;
            }
        }

        public Invocation Begin(params string[] args) { return new Invocation(_exe, Root, "", args); }

        public void Dispose()
        {
            try { Run("--json", "stop", "--all", "--force"); }
            catch (Exception ex) { Console.Error.WriteLine("Sandbox stop failed: " + ex.Message); }
            // Failure cleanup must never terminate a process merely by its name.
            // Only a process whose executable is this exact private copy qualifies.
            foreach (Process process in Process.GetProcessesByName("termwrap"))
            {
                using (process)
                {
                    try
                    {
                        if (!process.HasExited && string.Equals(Path.GetFullPath(process.MainModule.FileName), _exe, StringComparison.OrdinalIgnoreCase))
                        {
                            process.Kill();
                            process.WaitForExit(5000);
                        }
                    }
                    catch (InvalidOperationException) { }
                    catch (System.ComponentModel.Win32Exception) { }
                }
            }
        }
    }

    private sealed class Result
    {
        public int ExitCode;
        public string Out;
        public string Err;
        public string Describe() { return "exit=" + ExitCode + " stdout=" + Json.Serialize(Out) + " stderr=" + Json.Serialize(Err); }
    }

    private sealed class Invocation : IDisposable
    {
        private readonly Process _process;
        private readonly Thread _stdout;
        private readonly Thread _stderr;
        private readonly StringBuilder _out = new StringBuilder();
        private readonly StringBuilder _err = new StringBuilder();
        public string Out { get { lock (_out) { return _out.ToString(); } } }

        public Invocation(string exe, string directory, string input, string[] args)
        {
            List<string> quoted = new List<string>();
            foreach (string argument in args) { quoted.Add(Quote(argument)); }
            _process = Process.Start(new ProcessStartInfo {
                FileName = exe, WorkingDirectory = directory, Arguments = string.Join(" ", quoted.ToArray()),
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
            });
            _stdout = Drain(_process.StandardOutput, _out);
            _stderr = Drain(_process.StandardError, _err);
            _process.StandardInput.Write(input);
            _process.StandardInput.Close();
        }

        public Result Finish(int timeout)
        {
            if (!_process.WaitForExit(timeout))
            {
                _process.Kill();
                _process.WaitForExit(5000);
                throw new Exception("CLI process timed out. stdout=" + Out);
            }
            if (!_stdout.Join(3000) || !_stderr.Join(3000)) { throw new Exception("CLI output streams did not close"); }
            lock (_err) { return new Result { ExitCode = _process.ExitCode, Out = Out, Err = _err.ToString() }; }
        }

        private static Thread Drain(StreamReader reader, StringBuilder destination)
        {
            Thread thread = new Thread(delegate() {
                char[] buffer = new char[1024];
                int count;
                try
                {
                    while ((count = reader.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        lock (destination) { destination.Append(buffer, 0, count); }
                    }
                }
                catch (IOException) { }
                catch (ObjectDisposedException) { }
            });
            thread.IsBackground = true;
            thread.Start();
            return thread;
        }

        // Windows process argument quoting, including empty arguments and final '\'.
        private static string Quote(string argument)
        {
            StringBuilder quoted = new StringBuilder("\"");
            int backslashes = 0;
            foreach (char c in argument)
            {
                if (c == '\\') { backslashes++; continue; }
                if (c == '"') { quoted.Append('\\', backslashes * 2 + 1); }
                else { quoted.Append('\\', backslashes); }
                quoted.Append(c);
                backslashes = 0;
            }
            quoted.Append('\\', backslashes * 2);
            quoted.Append('"');
            return quoted.ToString();
        }

        public void Dispose()
        {
            if (!_process.HasExited) { _process.Kill(); _process.WaitForExit(5000); }
            _process.Dispose();
        }
    }

    private sealed class DummyTelnet : IDisposable
    {
        private readonly TcpListener _listener;
        private readonly Thread _thread;
        private readonly ManualResetEvent _connected = new ManualResetEvent(false);
        private readonly List<byte> _received = new List<byte>();
        private TcpClient _client;
        private NetworkStream _stream;
        public readonly int Port;

        public DummyTelnet(string banner)
        {
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _thread = new Thread(delegate() {
                try
                {
                    _client = _listener.AcceptTcpClient();
                    _stream = _client.GetStream();
                    _connected.Set();
                    Write(banner);
                    byte[] buffer = new byte[4096];
                    int count;
                    while ((count = _stream.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        lock (_received)
                        {
                            for (int i = 0; i < count; i++) { _received.Add(buffer[i]); }
                        }
                    }
                }
                catch (IOException) { }
                catch (SocketException) { }
                catch (ObjectDisposedException) { }
            });
            _thread.IsBackground = true;
            _thread.Start();
        }

        public void Write(string text)
        {
            if (!_connected.WaitOne(5000)) { throw new Exception("Dummy Telnet client did not connect"); }
            byte[] bytes = Encoding.UTF8.GetBytes(text);
            lock (this) { _stream.Write(bytes, 0, bytes.Length); _stream.Flush(); }
        }

        public void Expect(List<byte> expected, string description)
        {
            WaitUntil(delegate { lock (_received) { return _received.Count >= expected.Count; } }, 5000, description);
            Thread.Sleep(80);
            lock (_received)
            {
                Assert(BitConverter.ToString(_received.ToArray()) == BitConverter.ToString(expected.ToArray()), description);
            }
        }

        public void Dispose()
        {
            _listener.Stop();
            if (_client != null) { _client.Close(); }
            _thread.Join(2000);
            _connected.Dispose();
        }
    }
}
