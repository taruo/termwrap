# termwrap architecture

## Principles

- SSH and Telnet are supported; SSH is the default when `--protocol` is omitted.
- The distributable application is a single `termwrap.exe` executable.
- `Program.cs` is the entry point. `SessionSupport.cs` contains session state,
  logging, tail buffers, and SSH transport. `TelnetTransport.cs` contains the
  built-in Telnet client. `CliOptions.cs` parses arguments and
  `CliOutput.cs` formats JSON and exit codes.
- `build.ps1` compiles the application with the .NET Framework C# compiler.

## Session model

- `start` launches a background daemon for the selected protocol.
- The CLI communicates with the daemon through a named pipe for `read`, `tail`,
  `send`, and `stop` requests.
- The daemon detaches the caller's standard handles and keeps no persistent
  stdout/stderr pipe to the invoking CLI.
- `start --wait-ready` waits for the first shell prompt heuristic after the
  command pipe is ready.
- `read`, `tail`, `send`, and `stop` can omit `--session` only when one running
  session is eligible. `tail --wait` waits for a running session.
- `stop --all` is a stop-only bulk operation and retains stopped session data.
- `stop --prune` is a compatibility path for one selected session and is
  rejected with `--all`.
- `prune` deletes one stopped session at a time. `prune --all` is deliberately
  unsupported to prevent accidental bulk deletion.
- Pruning takes the same session mutex used by the daemon and rechecks liveness
  before deleting the directory.
- Session metadata is stored in
  `.termwrap-sessions\NAME\session.info`; received output is appended to
  `.termwrap-sessions\NAME\output.log`.
- Session paths are normalized and constrained to the session root. Metadata is
  written to a temporary file and atomically replaced.

## CLI and output

- `--json` and `--log-folder PATH` are accepted before or after a command.
- SSH suffix arguments are opaque after `--`; unknown termwrap options are not
  silently forwarded to SSH.
- `--password`, `--ask-password`, and `--password-stdin` are mutually exclusive.
- `send --line` sends UTF-8 text plus one CR in the existing `SEND_TEXT` request
  shape for daemon compatibility.
- `send --key` is the canonical control-input option; `--control` remains an
  alias. `--text` does not append a newline.
- JSON is generated with `JavaScriptSerializer` from `System.Web.Extensions`.
  Success is written to stdout and errors to stderr.
- `tail --json` emits `waiting`, `data`, and `stopped` NDJSON events. Read and
  tail data includes display text, Base64 bytes, and byte offsets.
- Exit codes are 0 success, 1 failure, 2 usage, 3 not found, 4 ambiguous,
  5 timeout, and 6 conflict.
- `help COMMAND` and `COMMAND --help` expose the same command description.

## SSH

- The external Windows `ssh.exe` is wrapped by the daemon.
- `--user` becomes `-l USER`; `-tt` is added unless a TTY option is supplied.
- When not overridden, host-key checking uses `StrictHostKeyChecking=no`, a
  per-session `known_hosts`, and the shared Windows host-key file at
  `C:\ProgramData\ssh\ssh_known_hosts`.
- Password handoff and askpass files use restricted ACLs and are removed as
  soon as the daemon consumes them.
- `--legacy-ssh` enables the grouped `ssh-rsa` and `hmac-sha1` compatibility
  options.

## Telnet

- Telnet uses an internal `TcpClient`; it does not depend on `telnet.exe`.
- IAC negotiation is parsed and removed from normal output. Outbound `0xFF`
  bytes are doubled according to Telnet escaping rules.
- When credentials are supplied, login and password prompts are detected and
  answered using the configured prompt strings.

## Logging and verification

- Application debug logging is enabled only with `--log-folder PATH`.
- CLI arguments and sensitive payloads are not written to the debug log.
- Session `output.log` retains received terminal data and may contain secrets
  printed by a remote host.
- `.termwrap-sessions` is runtime state and is excluded from release assets.
- `test.ps1` builds into a temporary directory and runs
  `tests/CliRegression.cs` against mock Telnet peers.
