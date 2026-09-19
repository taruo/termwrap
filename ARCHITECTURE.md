# termwrap アーキテクチャ

英語版: [ARCHITECTURE.en.md](ARCHITECTURE.en.md)

## 方針
- 接続プロトコルは `ssh` と `telnet` をサポートする
- `--protocol` 未指定時は `ssh` を既定にする

## 構成
- 実行ファイルは `termwrap.exe` の 1 本に集約する
- メインエントリポイントは `Program.cs`
- セッション管理、ログ、tail バッファ、SSH トランスポートは `SessionSupport.cs`
- Telnet 自前実装は `TelnetTransport.cs`
- named pipe の ACL 生成は `PipeSecurityFactory.cs`
- ビルドは `build.ps1` から `csc` で行う
- 旧版ソースや一時検証 artefact はローカルでは `old\` 配下へ退避できる

## セッションモデル
- `start` 実行時にバックグラウンド daemon プロセスを起動する
- daemon は protocol に応じて `ssh.exe` または内蔵 Telnet クライアントを開始する
- CLI 本体は named pipe で daemon に `read` `tail` `send` `stop` を送る
- `CliOptions.cs` が引数解析・入力方式の排他・対象指定の検証、`CliOutput.cs` がJSON・終了コードを担当する
- `start` は `--host` を必須とし、`--session` 省略時は `ssh-001` / `telnet-001` 形式で短いセッション名を自動採番する
- `start` は command pipe の `PING` 応答が返るまで待ってから成功を返す
- daemon は起動元の標準入出力ハンドルを切り離し、永続化したCLIのstdout/stderr pipeを保持しない
- `start --wait-ready` は初回のシェルプロンプトが tail バッファに現れるまで追加で待機する
- `--log-folder` 指定時だけアプリ全体ログを出力し、未指定時は `termwrap.log` を自動作成しない
- `read` `tail` `send` `stop` は `--session` 省略時、実行中セッションが 1 つだけなら暗黙選択する
- `tail --wait` は対象セッションが実行中になるまで待機してから追尾を開始する
- `-s` は `--session` の別名。`stop --all` は全件停止専用で、保存データは削除しない
- `stop --prune` は互換用として選択対象だけを停止・削除する。`--all` との併用は拒否し、省略時も実行中1件だけを選択する
- `prune` は停止済みのデータのみ、常に1セッションずつ削除する。`prune --all` はサポートしない
- `prune` はdaemonと同じセッションmutexを取得してから生存状態を再確認し、起動・停止中の削除を防ぐ
- `stop --prune` は `STOP` 送信後、daemon 終了を短時間待ってからセッションディレクトリを削除する
- `list --verbose` で daemon pid / remote pid / auth mode まで確認できる
- 各セッションのメタ情報はバイナリ直下の `.termwrap-sessions\<session>\session.info` に保存する
- 各セッションの受信ログはバイナリ直下の `.termwrap-sessions\<session>\output.log` に追記保存する
- daemon の生存判定は PID だけでなくプロセス名と起動時刻を照合し、PID 再利用による停止済みセッションの誤検出を防ぐ
- セッションパスは正規化後に `.termwrap-sessions` 直下であることを検証し、`.` / `..` を拒否する
- `session.info` は一時ファイルへ完全に書いてから原子的に置換する

## CLI 入出力
- 共通オプション `--json` / `--log-folder PATH` はコマンド前後で使用できる。オプション値を読み飛ばす際は値を再解釈しない
- SSHの追加引数は `--` で区切り、その後はtermwrapのオプション解析をしない。不明なオプションはSSHへ暗黙転送しない
- `--password` / `--ask-password` / `--password-stdin` は排他。stdinはUTF-8の1行、対話入力は非表示とする
- `send --line` はUTF-8文字列と末尾CRを既存の `SEND_TEXT` 1要求で送るため、旧daemonとの互換性を保つ
- `send --key` は `--control` の別名。`--text` は改行なしを維持し、入力種別は必ず1種類のみ
- JSONは .NET Framework の `System.Web.Extensions` 内の `JavaScriptSerializer` で生成し、標準出力に成功結果、標準エラーにエラーを出す
- `tail --json` は waiting / data / stopped のNDJSON。read/tailは表示文字列と元のバイト列のBase64・バイトオフセットを返す
- 終了コードは 0=成功、1=一般失敗、2=引数、3=対象なし、4=対象曖昧、5=タイムアウト、6=状態競合
- `help COMMAND` と `COMMAND --help` は同じ説明を出す。機械向けhelpはJSON内のtextとして返す
- 回帰テストは `test.ps1` が一時フォルダへビルドし、`tests/CliRegression.cs` が模擬TelnetとCLIを実行する

## SSH 処理
- 外部の `ssh.exe` を起動して標準入出力をラップする
- `--user` 指定時は `-l` を追加する
- TTY 指定が無い場合は `-tt` を付与する
- `StrictHostKeyChecking` 未指定時は `no` を追加する
- `GlobalKnownHostsFile` は Windows 共通の `C:\ProgramData\ssh\ssh_known_hosts` を参照する
- `UserKnownHostsFile` はセッション配下の `known_hosts` を使い、必要に応じて共通ファイル内容を初期コピーする
- `--password` は保護ACL付き一時ファイルでdaemonへ受け渡し、daemon起動直後に削除する
- SSH askpass のスクリプトと秘密ファイルはユーザー・SYSTEM・Administratorsだけに許可し、パスワードをバッチ本文へ埋め込まない

## Telnet 処理
- `telnet.exe` には依存せず `TcpClient` で直接接続する
- 既定ポートは `23`
- 受信時は IAC シーケンスを解釈し、通常データだけを表示バッファへ流す
- `WILL/WONT/DO/DONT` は受信側で解釈して除去し、互換性重視で原則無応答とする
- 送信時は `0xFF` を二重化して Telnet のエスケープ規則に合わせる
- `--user` と `--password` が与えられた場合は `login:` / `password:` プロンプトを検知して自動投入する
- `--legacy-ssh` で古い SSH 機器向けの `ssh-rsa` / `hmac-sha1` 互換オプションをまとめて有効化する
- command channel は named pipe を使い、daemon 側で待受ログを出して切り分けしやすくした
- command pipe は現在ユーザー・SYSTEM・Administratorsだけに FullControl を許可する
- pipe client/server のログに pipe 名と実行 ID を追加し、接続拒否の切り分けをしやすくした
- `termwrap.log` は `FileShare.ReadWrite` 前提で追記し、複数プロセスからのログ出力で command channel を巻き込んで壊さないようにした

## ログ方針
- アプリ全体のデバッグログは `--log-folder` 指定時のみ `<log-folder>\termwrap.log` に保存する
- CLI引数全体をデバッグログへ記録しない。pipeログの `SEND_TEXT` / `SEND_HEX`、`READ` / `TAIL` ペイロードをマスクする
- セッションごとの受信内容は `output.log` に保存する
- 障害解析時はまず `termwrap.log` と対象セッションの `session.info` / `output.log` を見る
- `.termwrap-sessions` は実行時作業領域なので、配布物には含めない
- `termwrap.exe` は GitHub Releases の asset 配布を前提とし、Git 管理対象には含めない

## 主要コマンド
- `[--json] [--log-folder PATH] start [-s SESSION] --host HOST [--protocol ssh|telnet] [--port PORT] [--user USER] [--password PASSWORD|--ask-password|--password-stdin] [--login-prompt TEXT] [--password-prompt TEXT] [--legacy-ssh] [--wait-ready] [-- ssh-args...]`
- `stop [-s SESSION|--all] [--force] [--prune]` (`--clear-stale` は `--force` の別名)
- `prune [-s SESSION]`
- `list [--all] [--verbose]`
- `tail [--session SESSION] [--wait]`
- `read [--session SESSION] [--clear]`
- `send [--session SESSION] --text TEXT`
- `send [--session SESSION] --line TEXT`
- `send [--session SESSION] --hex HEX`
- `send [--session SESSION] --key KEY` (`--control` は `--key` の別名)
- `help [COMMAND]`





