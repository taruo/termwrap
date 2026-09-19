# termwrap

English documentation: [README.en.md](README.en.md)

AIやスクリプトから SSH / Telnet の対話セッションを継続操作する Windows 用ラッパーです。
配布用 `termwrap.exe` は GitHub Releases の asset で提供します。

## クイックスタート

```powershell
# 接続（パスワードは画面に表示せず入力）
.\termwrap.exe start --host HOST --user USER -s main --ask-password

# 一行をEnter付きで送る
.\termwrap.exe send -s main --line "uname -a"
.\termwrap.exe read -s main --clear

# 接続を終了し、保存データを削除
.\termwrap.exe stop -s main
.\termwrap.exe prune -s main
```

SSHが既定です。Telnetは `--protocol telnet` を指定します。
ポートの既定は SSH=22、Telnet=23 です。接続名を省略すると
`ssh-001` / `telnet-001` のように自動採番します。

## 共通ルール

```text
termwrap [--json] [--log-folder PATH] <command> [options]
termwrap <command> [options] [--json] [--log-folder PATH]
```

- `-s NAME` は `--session NAME` と同じです。
- `read` / `tail` / `send` / `stop` の対象省略は、実行中が1件の場合だけです。
- `prune` の対象省略は、停止済みを含む既知セッションが1件の場合だけです。
- `stop --all` は全セッションを停止しますが、保存データは削除しません。削除は常に1セッションずつです。
- `--json` と `--log-folder` はコマンドの前後に置けます。
- オプション値と `--` 以降の引数は共通オプションとして解釈しません。
  例えば `send --text "--json"` は文字列 `--json` を送信します。
- 不明なオプション、重複した値オプション、排他的オプションの併用はエラーです。
- `termwrap help`、`termwrap help start`、`termwrap start --help` で説明を表示します。
- セッション名は文字・数字・`-`・`_`・`.` を使用でき、`.` / `..` は禁止です。

## コマンド

### start

```text
termwrap start --host HOST [-s NAME] [--protocol ssh|telnet] [--port PORT]
    [--user USER] [--ask-password | --password-stdin | --password VALUE]
    [--login-prompt TEXT] [--password-prompt TEXT] [--legacy-ssh]
    [--wait-ready] [-- SSH-ARGS...]
```

- `--ask-password`: 対話コンソールで非表示入力します。入力リダイレクト時は使えません。
- `--password-stdin`: 標準入力からUTF-8の1行を読みます。改行だけを除去し、前後の空白を保持します。
  EOFと空の1行は区別します。UTF-8を出力する秘密管理ツール等から渡してください。
- `--password VALUE`: 互換用です。呼び出し元のプロセス引数やシェル履歴に値が残り得るため、
  新規自動処理では `--password-stdin` を推奨します。
- 認証入力方式は3種類のうち1つだけ指定できます。省略時はSSH鍵等の既存設定を使います。
- `--wait-ready`: daemonの応答に加え、従来のプロンプト判定で準備完了を待ちます。
  起動待機の期限は8秒です。任意の機器のプロンプトを保証するものではありません。
- `--login-prompt` / `--password-prompt`: Telnet自動ログイン時の検出文字列です。
- `--legacy-ssh`: 古いSSH機器向けに `ssh-rsa` / `hmac-sha1` 互換設定を追加します。
- SSHへ直接渡す引数は必ず `--` の後ろに指定します。

```powershell
.\termwrap.exe start --host HOST --user USER -s main -- -o ConnectTimeout=10
.\termwrap.exe start --host HOST --protocol telnet --port 23 -s console
```

SSHはWindowsの `ssh.exe` を使用します。TTY指定がなければ `-tt` を追加します。
従来どおり `StrictHostKeyChecking` 未指定時は `no`、
`UserKnownHostsFile` はセッション配下、`GlobalKnownHostsFile` は
`C:\ProgramData\ssh\ssh_known_hosts` を使用します。

### send

```text
termwrap send [-s NAME] (--text TEXT | --line TEXT | --hex HEX | --key KEY)
```

- `--text`: UTF-8の文字列をそのまま送り、Enterは付けません。空文字も指定できます。
- `--line`: UTF-8の文字列と末尾のCR（Enter）を1回の要求で送ります。
  空文字はEnterだけです。文字列内のCR/LFは拒否します。
- `--hex`: 生バイトを送ります。16進数字の間の空白・ハイフンは無視します。
- `--key`: `ctrl-c`、`ctrl-d`、`ctrl-z`、`esc`、`tab`、`enter`、
  `up`、`down`、`left`、`right`、`backspace` が使えます。
- 互換用の `--control` は `--key` と同じです。
- 成功は「入力を送信した」意味です。リモートコマンドの完了や成功は `read` 等で確認します。

### read / tail

```text
termwrap read [-s NAME] [--clear]
termwrap tail [-s NAME] [--wait]
```

`read --clear` は未読バッファを読み出して消去します。`tail` の履歴は消しません。
`tail --wait` は指定セッションの起動、または実行中セッションが1件になるまで待ちます。
複数候補があればエラーです。追尾間隔は2秒、Ctrl+Cで終了します。
バッファはメモリ上の直近1MiBです。停止後の出力はセッションの `output.log` を参照します。

### list

```text
termwrap list [--all|-a] [--verbose|-v]
```

既定では実行中のみ表示します。`--all` は停止済みも表示、
`--verbose` は人間向け表示を詳細化します。

### stop / prune

```text
termwrap stop [-s NAME | --all] [--force] [--prune]
termwrap prune [-s NAME]
```

- `stop`: 選択した接続を停止します。停止済みの明示対象は成功として扱います。
- `stop --force`: 通常停止に失敗した場合も、記録されたプロセス情報に基づいて終了を試みます。
  `--clear-stale` は互換用の別名です。
- `prune`: 停止済みセッションのデータとログを削除します。接続は停止しません。
- `prune --all` は危険な一括削除になるため、サポートしていません。対象を省略する場合も、既知セッションが1件のときだけです。
- `prune -s NAME` で稼働中を指定すると終了コード6です。
- `stop --prune` は互換用です。選択対象の停止後にデータを削除します。
  `--all` との併用はできず、削除は選択した1セッションだけです。
- 全件操作は順に処理します。途中でエラーが起きても、完了済みの処理は取り消しません。

## JSONと終了コード

`--json` 時、結果は標準出力、エラーは標準エラーにJSONで返します。
通常コマンドは1オブジェクト、`tail` は1行1イベントのNDJSONです。

```json
{"ok":true,"command":"list","sessions":[]}
{"ok":true,"command":"send","session":"main","mode":"line","sent":true}
{"ok":false,"error":{"code":"not_found","message":"session is not running: main"}}
```

`list` の各セッションは `session`、`state`、`protocol`、`host`、`port`、
`daemonPid`、`remotePid`、`startedAtUtc`、`authMode` を持ちます。
`state` は `running` / `stopped` です。

`read` と `tail` のデータイベントは `text`、`dataBase64`、`startOffset`、
`endOffset`、`cleared` を持ちます。`text` はUTF-8として表示した文字列、
`dataBase64` は元のバイト列です。分割されたUTF-8や非UTF-8出力を正確に扱う場合は
`dataBase64` を連結してから復号してください。
`tail` のイベント種別は `waiting`、`data`、`stopped` です。

| 終了コード | error.code | 意味 |
|---|---|---|
| 0 | — | 成功 |
| 1 | failure | 通信・OSエラーなど |
| 2 | usage | 引数やオプションが不正 |
| 3 | not_found | 対象がない、または実行していない |
| 4 | ambiguous | 省略した対象が一意に決まらない |
| 5 | timeout | 起動・停止・pipe接続の待機期限超過 |
| 6 | conflict | 稼働中の削除など状態が競合 |

## 旧版からの移行

従来の `--session`、`send --control`、`stop --clear-stale`、
`stop --prune`、`read --clear` は引き続き使えます。
次の2点は意図的な変更です。

- 一括削除: `prune --all` と `stop --all --prune` は廃止しました。`stop --all` 後に、必要なセッションを `prune -s NAME` で個別に削除してください。
- SSH追加引数: `start ... -o OPTION=VALUE` → `start ... -- -o OPTION=VALUE`

既存daemonにも `send --line` は使えます（従来の文字送信要求でCRを一緒に送ります）。

## 保存・ログ・認証情報

セッションは実行ファイル直下の `.termwrap-sessions\NAME` に保存します。
`session.info` は管理情報、`output.log` は受信した端末出力です。
デバッグログは `--log-folder PATH` 指定時だけ `PATH\termwrap.log` に作成します。
CLI引数全体、パスワード、送受信ペイロードはデバッグログに記録しません。
接続先が画面に表示した秘密情報は `output.log` に含まれ得ます。

パスワードは制限ACL付きの一時ファイルでdaemonに渡し、受領直後に削除します。
SSHの `askpass.cmd` / `askpass.secret` も制限ACLで保護し、通常終了時に削除します。
pipeの許可対象は現在ユーザー・SYSTEM・Administratorsです。

## ビルドと検証

```powershell
.\build.ps1
.\test.ps1
```

.NET Frameworkの `csc` と `System.Web.Extensions` を使用します。
`build.ps1` は実行場所に依存せず `termwrap.exe` を生成し、失敗時はエラーを返します。
`test.ps1` は一時フォルダへビルドし、ループバックの模擬Telnetサーバーと
隔離セッションで検証します。実際のSSH接続や既存セッションは使いません。

- `Program.cs`: コマンド実行とhelp
- `CliOptions.cs`: 共通・コマンド別の引数解析、認証入力
- `CliOutput.cs`: JSON出力、エラーコード
- `SessionSupport.cs`: セッション、pipe、SSH、ログとバッファ
- `TelnetTransport.cs`: Telnet通信
- `PipeSecurityFactory.cs`: pipe ACL
- `tests/CliRegression.cs`: CLI回帰試験
- `ARCHITECTURE.md`: 日本語の設計資料

Git公開にはソースを含め、`termwrap.exe` はRelease assetとして配布します。
`.termwrap-sessions`、ログ、`old`、テスト成果物は公開対象外です。
ライセンスはMITです（`LICENSE` 参照）。
