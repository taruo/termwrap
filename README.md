# termwrap

`termwrap` は、AI から呼び出して CLI 操作を継続実行できるようにするための、Windows 用 SSH/Telnet セッションラッパーです。

配布用の `termwrap.exe` は GitHub Releases から取得できます。

## できること
- `ssh` と `telnet` の両方を扱えます
- 接続は daemon プロセスとして維持されます
- 出力確認は `read` または `tail` で行えます
- 入力送信は `send` で 1 操作ずつ行えます
- `stop --prune` で停止と掃除をまとめて行えます
- 実行中セッションが 1 つだけなら `--session` を省略できます

## ファイル構成
- `termwrap.exe`
  実行ファイルです。公開時は GitHub Releases の asset 配布を前提とします
- `Program.cs`
  CLI 入口、引数解析、help、start/stop/list/read/tail/send を持ちます
- `SessionSupport.cs`
  セッション情報、named pipe、tail バッファ、ログ、SSH 補助を持ちます
- `TelnetTransport.cs`
  Telnet 通信の実装です
- `PipeSecurityFactory.cs`
  named pipe ACL の生成です
- `ARCHITECTURE.md`
  実装方針と内部設計です

## 保存先
セッション情報は `termwrap.exe` と同じ階層の `.termwrap-sessions` に保存します。

例:
- `.termwrap-sessions\ssh-001\session.info`
- `.termwrap-sessions\ssh-001\output.log`
- `.termwrap-sessions\ssh-001\known_hosts`

`output.log` には受信内容を追記保存します。

## ログ方針
アプリ全体ログはデフォルトでは作りません。

`--log-folder PATH` を指定したときだけ、次の場所に `termwrap.log` を出力します。
- `<PATH>\termwrap.log`

作らないもの:
- `.termwrap-data`
- `termwrap.log` の自動生成

## ビルド
```powershell
.\build.ps1
```

生成物:
- `termwrap.exe`

## クイックスタート
### SSH で接続
```powershell
.\termwrap.exe start --host HOST --user USER --password PASSWORD --wait-ready
```

### Telnet で接続
```powershell
.\termwrap.exe start --host HOST --protocol telnet --port 23 --user USER --password PASSWORD --wait-ready
```

### 出力を読む
```powershell
.\termwrap.exe read --clear
```

### 1 コマンドずつ送る
```powershell
.\termwrap.exe send --text "uname -a"
.\termwrap.exe send --control enter
.\termwrap.exe read --clear
```

### 停止して掃除する
```powershell
.\termwrap.exe stop --prune
```

## セッション名
### 明示する場合
```powershell
.\termwrap.exe start --session ssh-main --host HOST --user USER --password PASSWORD
```

### 省略する場合
`start` で `--session` を省略すると、次の形式で自動採番します。
- SSH: `ssh-001`, `ssh-002`, ...
- Telnet: `telnet-001`, `telnet-002`, ...

採用されたセッション名は `start` 成功時に表示されます。

## コマンド一覧
### 共通形式
```powershell
.\termwrap.exe [--log-folder PATH] <command> ...
```

- `--log-folder PATH`
  指定した場合だけ `<PATH>\termwrap.log` を出力します

### start
```powershell
.\termwrap.exe [--log-folder PATH] start [--session SESSION] --host HOST [--protocol ssh|telnet] [--port PORT] [--user USER] [--password PASSWORD] [--login-prompt TEXT] [--password-prompt TEXT] [--legacy-ssh] [--wait-ready] [ssh-args...]
```

- `--host`
  必須です
- `--protocol`
  省略時は `ssh` です
- `--port`
  省略時は `ssh=22`, `telnet=23` です
- `--session`
  省略時は自動採番です
- `--legacy-ssh`
  古い SSH 機器向けの `ssh-rsa` / `hmac-sha1` 互換オプションをまとめて有効にします
- `--wait-ready`
  command pipe ready に加えて、初回プロンプトが見えるまで待ってから戻ります

`start` は command pipe の `PING` に応答できる状態になってから成功を返します。

### list
```powershell
.\termwrap.exe list [--all] [--verbose]
```

- `--all`
  停止済みも表示します
- `--verbose`
  `pid`, `remotePid`, `auth`, `target` まで表示します

### read
```powershell
.\termwrap.exe read [--session SESSION] [--clear]
```

- `--clear`
  読み取った内容をバッファから消します

### tail
```powershell
.\termwrap.exe tail [--session SESSION] [--wait]
```

- `--wait`
  対象セッションが実行中になるまで待ってから追尾します

### send
```powershell
.\termwrap.exe send [--session SESSION] --text TEXT
.\termwrap.exe send [--session SESSION] --hex HEX
.\termwrap.exe send [--session SESSION] --control CONTROL
```

`--text` `--hex` `--control` は 1 つだけ指定できます。

`--control` の例:
- `ctrl-c`
- `ctrl-d`
- `ctrl-z`
- `esc`
- `tab`
- `enter`
- `up`
- `down`
- `left`
- `right`
- `backspace`

### stop
```powershell
.\termwrap.exe stop [--session SESSION] [--clear-stale] [--prune]
```

- `--clear-stale`
  応答しない古いセッション情報を強制的に掃除します
- `--prune`
  停止後にセッションディレクトリも削除します

`stop --prune` は `STOP` 送信後、daemon が落ちるまで短時間待ってから削除します。`--session` なしなら既知セッションを全件掃除します。

## セッション選択ルール
- `start` は `--host` 必須です
- `start` の `--session` は任意です
- `read` `tail` `send` `stop` は、実行中セッションが 1 つだけなら `--session` を省略できます
- 実行中セッションが複数ある場合は `--session` が必要です

## SSH の扱い
- Windows の `ssh.exe` を使って標準入出力をラップします
- `--user` 指定時は `-l` を追加します
- TTY 指定が無い場合は `-tt` を追加します
- `StrictHostKeyChecking` を明示しない場合は `no` を追加します
- `UserKnownHostsFile` はセッション配下の `known_hosts` を使います
- `--password` 指定時は一時的な `askpass.cmd` を作り、`SSH_ASKPASS` で渡します
- `--legacy-ssh` は古い機器向けの互換オプションです

## Telnet の扱い
- `telnet.exe` には依存せず、内部実装で接続します
- 既定ポートは `23` です
- Telnet IAC シーケンスを処理して通常データだけを表示します
- `--user` と `--password` を指定した場合は `login:` / `password:` を見て自動ログインします

## トラブルシュート
### `read` が失敗する
まず一覧を確認します。

```powershell
.\termwrap.exe list --all --verbose
```

見る場所:
- `.termwrap-sessions\<session>\session.info`
- `.termwrap-sessions\<session>\output.log`
- `--log-folder` を使っている場合は `<log-folder>\termwrap.log`

### 古い SSH 機器で鍵方式エラーになる
```powershell
.\termwrap.exe start --host HOST --user USER --password PASSWORD --legacy-ssh
```

### `--session` を省略できるか分からない
```powershell
.\termwrap.exe list --all --verbose
```

実行中が 1 つだけなら省略できます。

## リリース時の扱い
- ルート直下には現行ソース、ドキュメント、ビルドスクリプト、実行ファイルだけを置きます
- `old\` はローカル退避用であり、Git 管理や公開対象には含めません
- `termwrap.exe` はローカルに残しますが、公開リポジトリには含めず GitHub Releases asset として配布します
- `.termwrap-sessions` は実行時作業領域なので、配布物には含めません

## 関連ファイル
- `LICENSE`
- [ARCHITECTURE.md](./ARCHITECTURE.md)
- `Program.cs`
- `SessionSupport.cs`
- `TelnetTransport.cs`
- `PipeSecurityFactory.cs`

## ライセンス
MIT License です。詳細は `LICENSE` を参照してください。
