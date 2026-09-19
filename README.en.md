# termwrap

A Windows wrapper for persistent, scriptable SSH and Telnet sessions.
The distributable `termwrap.exe` is published as a GitHub Release asset.
The Japanese documentation is in [README.md](README.md).

## Quick start

```powershell
# Start a connection (read the password without echoing it)
.\termwrap.exe start --host HOST --user USER -s main --ask-password

# Send a line and read the response
.\termwrap.exe send -s main --line "uname -a"
.\termwrap.exe read -s main --clear

# Stop the connection, then remove its local session data
.\termwrap.exe stop -s main
.\termwrap.exe prune -s main
```

SSH is the default protocol. Use `--protocol telnet` for Telnet. Default ports
are SSH 22 and Telnet 23. If omitted, a session name is generated as
`ssh-001`, `telnet-001`, and so on.

## Safety rules

- `stop --all` stops every known session but keeps its local session data.
- Deletion is always limited to one session. `prune --all` is intentionally
  unsupported, and `stop --all --prune` is rejected as well.
- `prune -s NAME` deletes only the selected stopped session. Omitting the
  session is allowed only when exactly one known session exists.
- A running session is never deleted by `prune`; an explicit running target
  returns conflict (exit code 6).

## Common syntax

```text
termwrap [--json] [--log-folder PATH] <command> [options]
termwrap <command> [options] [--json] [--log-folder PATH]
```

`-s NAME` is an alias for `--session NAME`. `--json` writes successful results
to stdout and errors to stderr. `--log-folder PATH` enables the application
debug log; without it, `termwrap.log` is not created automatically.

## Commands

### start

```text
termwrap start --host HOST [-s NAME] [--protocol ssh|telnet] [--port PORT]
    [--user USER] [--ask-password | --password-stdin | --password VALUE]
    [--login-prompt TEXT] [--password-prompt TEXT] [--legacy-ssh]
    [--wait-ready] [-- SSH-ARGS...]
```

- `--ask-password` reads a password from an interactive console without echo.
- `--password-stdin` reads one UTF-8 line from standard input without trimming
  surrounding whitespace.
- `--password VALUE` is retained for compatibility, but the value may be
  visible in process arguments or shell history. Prefer `--password-stdin`
  for automation.
- Password input modes are mutually exclusive.
- SSH arguments must follow `--`, for example:
  `start --host HOST -- -o ConnectTimeout=10`.
- `--wait-ready` additionally waits for the first shell-prompt heuristic,
  with an 8-second deadline.

### send

```text
termwrap send [-s NAME] (--text TEXT | --line TEXT | --hex HEX | --key KEY)
```

- `--text` sends exact UTF-8 text without appending Enter; empty text is valid.
- `--line` sends UTF-8 text followed by one CR. Embedded CR/LF is rejected.
- `--hex` sends raw bytes; spaces and hyphens between hex digits are ignored.
- `--key` supports `ctrl-c`, `ctrl-d`, `ctrl-z`, `esc`, `tab`, `enter`,
  `up`, `down`, `left`, `right`, and `backspace`.
- `--control` is a compatibility alias for `--key`.

Successful `send` means the input was delivered to the session; it does not
mean that a remote command completed successfully.

### read and tail

```text
termwrap read [-s NAME] [--clear]
termwrap tail [-s NAME] [--wait]
```

`read --clear` consumes the unread buffer. It does not clear the `tail` history.
`tail --wait` waits for the selected session to appear or for one running
session to become unambiguous. The in-memory buffer is limited to the most
recent 1 MiB; stopped-session output remains in `output.log`.

### list

```text
termwrap list [--all|-a] [--verbose|-v]
```

The default view contains running sessions. `--all` includes stopped sessions;
`--verbose` adds process and authentication details.

### stop and prune

```text
termwrap stop [-s NAME | --all] [--force] [--prune]
termwrap prune [-s NAME]
```

- `stop` stops the selected connection. An explicitly stopped session is
  treated as success.
- `stop --force` attempts verified stale-process cleanup if graceful stop
  fails. `--clear-stale` is a compatibility alias.
- `stop --prune` is a compatibility option that stops and removes one
  selected session. It cannot be combined with `--all`.
- `prune` removes the selected stopped session data and logs without stopping
  a connection.

## JSON and exit codes

With `--json`, normal commands emit one JSON object. `tail` emits newline-
delimited events (`waiting`, `data`, and `stopped`).

| Exit code | Error code | Meaning |
| ---: | --- | --- |
| 0 | — | Success |
| 1 | `failure` | Communication or operating-system error |
| 2 | `usage` | Invalid argument or option |
| 3 | `not_found` | Target does not exist or is not running |
| 4 | `ambiguous` | An omitted target is not unique |
| 5 | `timeout` | Start, stop, or pipe wait timed out |
| 6 | `conflict` | Requested state transition conflicts with the session state |

## Migration from older versions

The compatibility options `--session`, `send --control`,
`stop --clear-stale`, `stop --prune`, and `read --clear` remain available.

- Bulk deletion was removed: `prune --all` and `stop --all --prune` are no
  longer accepted. Use `stop --all`, review `list --all`, and delete selected
  sessions individually with `prune -s NAME`.
- SSH suffix arguments now follow `--`, for example:
  `start ... -- -o OPTION=VALUE`.

## Storage, logs, and credentials

Session data is stored below `.termwrap-sessions\NAME` next to the executable.
`session.info` contains management metadata and `output.log` contains received
terminal output. The application debug log is created only when
`--log-folder PATH` is supplied. Passwords are passed through ACL-protected
temporary files and removed after handoff; avoid putting secrets in terminal
output because they may be retained in `output.log`.

## Build and test

```powershell
.\build.ps1
.\test.ps1
```

The scripts use the .NET Framework `csc.exe` compiler and
`System.Web.Extensions`. The regression suite builds an isolated copy of the
application and uses mock Telnet peers; it never replaces the installed
`termwrap.exe`.
