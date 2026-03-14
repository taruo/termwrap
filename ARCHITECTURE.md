# termwrap アーキテクチャ

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
- `start` は `--host` を必須とし、`--session` 省略時は `ssh-001` / `telnet-001` 形式で短いセッション名を自動採番する
- `start` は command pipe の `PING` 応答が返るまで待ってから成功を返す
- `start --wait-ready` は初回のシェルプロンプトが tail バッファに現れるまで追加で待機する
- `--log-folder` 指定時だけアプリ全体ログを出力し、未指定時は `termwrap.log` を自動作成しない
- `read` `tail` `send` `stop` は `--session` 省略時、実行中セッションが 1 つだけなら暗黙選択する
- `tail --wait` は対象セッションが実行中になるまで待機してから追尾を開始する
- `stop --prune` は `--session` 指定時に単一セッションを掃除し、`--session` 省略時は既知セッションを全件掃除する
- `stop --prune` は `STOP` 送信後、daemon 終了を短時間待ってからセッションディレクトリを削除する
- `list --verbose` で daemon pid / remote pid / auth mode まで確認できる
- 各セッションのメタ情報はバイナリ直下の `.termwrap-sessions\<session>\session.info` に保存する
- 各セッションの受信ログはバイナリ直下の `.termwrap-sessions\<session>\output.log` に追記保存する

## SSH 処理
- 外部の `ssh.exe` を起動して標準入出力をラップする
- `--user` 指定時は `-l` を追加する
- TTY 指定が無い場合は `-tt` を付与する
- `StrictHostKeyChecking` 未指定時は `no` を追加する
- `GlobalKnownHostsFile` は Windows 共通の `C:\ProgramData\ssh\ssh_known_hosts` を参照する
- `UserKnownHostsFile` はセッション配下の `known_hosts` を使い、必要に応じて共通ファイル内容を初期コピーする
- `--password` 指定時は一時的な `askpass.cmd` を作って `SSH_ASKPASS` で渡す

## Telnet 処理
- `telnet.exe` には依存せず `TcpClient` で直接接続する
- 既定ポートは `23`
- 受信時は IAC シーケンスを解釈し、通常データだけを表示バッファへ流す
- `WILL/WONT/DO/DONT` は受信側で解釈して除去し、互換性重視で原則無応答とする
- 送信時は `0xFF` を二重化して Telnet のエスケープ規則に合わせる
- `--user` と `--password` が与えられた場合は `login:` / `password:` プロンプトを検知して自動投入する
- `--legacy-ssh` で古い SSH 機器向けの `ssh-rsa` / `hmac-sha1` 互換オプションをまとめて有効化する
- command channel は named pipe を使い、daemon 側で待受ログを出して切り分けしやすくした
- command pipe 作成時に明示 ACL を適用し、別実行コンテキストからの `send` `read` `stop` 接続失敗を減らす
- pipe client/server のログに pipe 名と実行 ID を追加し、接続拒否の切り分けをしやすくした
- `termwrap.log` は `FileShare.ReadWrite` 前提で追記し、複数プロセスからのログ出力で command channel を巻き込んで壊さないようにした

## ログ方針
- アプリ全体のデバッグログは `--log-folder` 指定時のみ `<log-folder>\termwrap.log` に保存する
- セッションごとの受信内容は `output.log` に保存する
- 障害解析時はまず `termwrap.log` と対象セッションの `session.info` / `output.log` を見る
- `.termwrap-sessions` は実行時作業領域なので、配布物には含めない
- `termwrap.exe` は GitHub Releases の asset 配布を前提とし、Git 管理対象には含めない

## 主要コマンド
- `[--log-folder PATH] start [--session SESSION] --host HOST [--protocol ssh|telnet] [--port PORT] [--user USER] [--password PASSWORD] [--login-prompt TEXT] [--password-prompt TEXT] [--legacy-ssh] [--wait-ready] [ssh-args...]`
- `stop [--session SESSION] [--clear-stale] [--prune]`
- `list [--all] [--verbose]`
- `tail [--session SESSION] [--wait]`
- `read [--session SESSION] [--clear]`
- `send [--session SESSION] --text TEXT`
- `send [--session SESSION] --hex HEX`
- `send [--session SESSION] --control CONTROL`





