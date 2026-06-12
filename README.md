# KDCapture
Remote kernel dump capture tool for WinPE over KDNET.

## Overview
KDCapture is a lightweight utility that automates remote kernel dump capture from WinPE targets over KDNET.

It is designed for incident investigation when a DR workflow becomes unresponsive and interactive debugging is difficult in field environments.
Instead of manually operating kd command sequences, KDCapture performs the connection and dump workflow in a repeatable way, with logging for traceability.

## Prerequisites

### Host machine (where KDCapture runs)
- Windows x64
- .NET Framework 4.8 or later
  - Windows Server 2022: usually preinstalled
  - Windows Server 2019 or earlier: verify and install if needed
- Administrator privilege (recommended for KD-related operations)
- Inbound UDP port for KDNET (for example, UDP 50000) allowed on Windows Firewall

Check .NET Framework release value:

```cmd
reg query "HKLM\SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full" /v Release
```

If `Release` is `528040` or higher, .NET Framework 4.8 or later is installed.

### Required debugger binaries
This repository does not include debugger binaries such as `kd.exe`, `dbgeng.dll`, `dbghelp.dll`, `dbgcore.dll`, and `EngHost.exe`.
Before building KDCapture, place these binaries in the project folder (`KDCapture.csproj` directory) or under `Resources\` in the same project.
They are embedded at build time and required to produce a working executable.

### Target machine (WinPE)
- WinPE booted target with KDNET enabled (`bcdedit /dbgsettings net ...` and debug enabled)
- Network reachability from target WinPE to host KDNET port

## Usage

### 目的
- ユーザーにWinDbg本体を操作してもらわず、ダンプ取得だけを1コマンド化する
- WinPE 側のアプリを一定時間動かしてから kernel dump を取得し、取得後も WinPE 操作を継続できるようにする

### 方針
- kd.exeをラップした単一実行モジュールを用意する
- 例: KDCapture.exe --key <key> --port 50000 --targetip <WinPE_IP> --output C:\dumps\target.dmp

### 実装イメージ
- Debugging Tools for Windowsから必要な実行ファイル/DLLを同梱
- 必要例: kd.exe, dbgeng.dll, dbghelp.dll, dbgcore.dll, EngHost.exe
- ラッパーが以下を自動実行
  - 指定秒数待機 (遅延)
  - KDNET接続
  - `.dump /k` の実行
  - `g` でターゲットを再開
  - ダンプ完了確認と終了
  - `ValidateSequenceNumber` の大量ログは要約表示

### 利点
- ユーザーはWinDbgの多機能UIを触らずに済む
- 現場では1つのモジュールと1コマンドだけで運用しやすい
- ダンプ作成中にファイルサイズが増えている間は timeout を自動延長するため、途中打ち切りが起きにくい

## KDNET の key について

- key は IP アドレスではなく、KDNET 接続時の共有キー(認証用トークン)
- WinPE 側とデバッガー(kd.exe/KDCapture)側で同じ値を設定する必要がある
- 値が一致しない場合は接続できない

実運用では、WinPE 側で bcdedit /dbgsettings net を実行したときに表示されるキーを、そのまま使うのが基本。

## KDCapture を使ったリモートカーネルダンプ取得手順

### 現在のKDCaptureの挙動
- dump 種別は `kernel-only dump` (`.dump /k`)
- `--targetip` を指定すると、KD 接続文字列に `target=<WinPE_IP>` を付ける
- `--delay-before-dump-sec` の既定値は `480` 秒 (= 8分)
- `--delay-before-dump-sec > 0` の場合、指定秒数待機してから kd.exe を起動し、dump を開始する
- dump コマンドは `.dump /k "<path>";g` を使い、ダンプ後に WinPE 側を再開する
- ダンプ完了を検知したら、ホスト側の kd.exe を終了して KDCapture を正常終了する
- `--capture-timeout-sec` は接続待ち/ダンプ完了待ちの timeout。dump ファイルが成長している間は自動延長される
- `ValidateSequenceNumber: ...` は生ログを連打せず、要約行だけ表示する

### 前提
- ターゲット: WinPE (DVD起動可)
- ホスト: KDCapture.exe を実行するPC
- KDCapture.exe は kd.exe と必要DLLを内包している前提

### 1. WinPE側でKDNETを有効化

```cmd
bcdedit /dbgsettings net hostip:<ホストPCのIP> port:50000
bcdedit /debug on
```

- 実行時に表示される key を控える
- 実運用では、この表示された key をそのまま使う

### 2. ホスト側で単発ダンプを採取

```cmd
KDCapture.exe --key <WinPEで表示されたkey> --port 50000 --targetip <WinPE_IP> --output C:\dumps\target.dmp
```

- 既定では起動後 8分待機してから kd 接続と dump を開始する
- 即時に dump を開始したい場合は `--delay-before-dump-sec 0` を付ける
- ダンプはホスト側(C:\dumps)に保存される
- 正常終了後、WinPE 側は操作可能な状態を維持する
- `Connected to target ...` の後、`You can get the target MAC address by running .kdtargetmac command.` が出たあとに `Connected to Windows...` まで数分かかる場合がある

### 3. フリーズ直前対策として定期採取(推奨)

```cmd
KDCapture.exe --key <WinPEで表示されたkey> --port 50000 --targetip <WinPE_IP> --output C:\dumps\target_latest.dmp --loop --interval-sec 30 --capture-timeout-sec 300 --no-debuggee-timeout-sec 30 --delay-before-dump-sec 480
```

- 上記例では、KDCapture 起動から 8分後に kd 接続と dump を開始する
- 1回の採取が終わった後、30秒待って次の試行へ進む
- 成功ごとに3世代ローテーションで保持する
  - 最新: target_latest.dmp
  - 1世代前: target_latest.dmp.1
  - 2世代前: target_latest.dmp.2
- KDNET接続不能/タイムアウトになった時点で停止
- 停止時に残っている3世代が、フリーズ直前までの最後の成功ダンプ群になる

### 4. ログ出力を有効化する場合

```cmd
KDCapture.exe --key <WinPEで表示されたkey> --port 50000 --targetip <WinPE_IP> --output C:\dumps\target_latest.dmp --loop --interval-sec 30 --capture-timeout-sec 300 --no-debuggee-timeout-sec 30 --delay-before-dump-sec 480 --log C:\dumps\kdcapture.log
```

- --log 未指定時は <output>.log が自動生成される
  - 例: output が C:\dumps\target.dmp の場合、ログは C:\dumps\target.dmp.log

### 5. 主なオプション
- `--targetip <WinPE_IP>`: KD 接続文字列に `target=<WinPE_IP>` を付加する。実運用ではほぼ必須。
- `--capture-timeout-sec <sec>`: dump が進行しない状態で打ち切るまでの秒数。dump ファイルが成長している間は自動延長される。
- `--no-debuggee-timeout-sec <sec>`: 接続後に `no_debuggee` が続いた場合に早期失敗とみなす秒数。
- `--delay-before-dump-sec <sec>`: kd.exe 起動前に待機する秒数。既定値は `480`。

### 6. 取得後の確認ポイント
- ダンプファイルサイズが 0 byte でないこと
- target_latest.dmp.log (または --log で指定したログ) に timeout / exited with code などの異常記録がないこと
- ログに `Dump successfully written` と `Auto-exiting on first successful capture.` が出ていること
- KDCapture 終了後に WinPE 側が操作可能であること
- `ValidateSequenceNumber` の大量生ログではなく、要約行だけが出ていること
- 必要に応じて最後の複数世代を別名で退避して比較すること

## 実行確認用コマンドライン

以下はホスト側でそのまま実行できるサンプル。

### 1. 単発採取

```cmd
KDCapture.exe --key 1.2.3.4 --port 50000 --targetip 10.0.0.10 --delay-before-dump-sec 480 --output C:\dumps\target.dmp
```

### 2. 3世代保持の定期採取

```cmd
KDCapture.exe --key 1.2.3.4 --port 50000 --targetip 10.0.0.10 --output C:\dumps\target_latest.dmp --loop --interval-sec 30 --capture-timeout-sec 300 --no-debuggee-timeout-sec 30 --delay-before-dump-sec 480
```

### 3. 3世代保持 + ログ明示

```cmd
KDCapture.exe --key 1.2.3.4 --port 50000 --targetip 10.0.0.10 --output C:\dumps\target_latest.dmp --loop --interval-sec 30 --capture-timeout-sec 300 --no-debuggee-timeout-sec 30 --delay-before-dump-sec 480 --log C:\dumps\kdcapture.log
```

### 4. 即時dump開始に戻す場合

```cmd
KDCapture.exe --key 1.2.3.4 --port 50000 --targetip 10.0.0.10 --output C:\dumps\target.dmp --delay-before-dump-sec 0
```

### 5. 生成ファイル確認

```cmd
dir C:\dumps\target*.dmp*
type C:\dumps\kdcapture.log
```

実運用では、key に固定値 1.2.3.4 を使わず、WinPE 側で bcdedit /dbgsettings net 実行時に表示されたキーを使うこと。

## DVD起動向け: ISO作成時にKDNET設定を事前埋め込みする手順

目的: WinPE起動後に `bcdedit /debug on` を実行しなくても、KDNET有効状態で起動できるようにする。

### 前提
- 作業用フォルダ: `E:\work\media`
- ここに WinPE ISO の中身を展開済みであること
  - copype amd64 E:\work のあと、DR ISOの中身を上書きコピー
- ホスト(ダンプ採取PC) IP、port、key を事前に確定しておくこと

### 1. 編集対象BCDを確認
通常、以下2つを両方設定する(UEFI/BIOS両対応のため)。

- `E:\work\media\Boot\BCD`
- `E:\work\media\EFI\Microsoft\Boot\BCD`

存在確認:

```cmd
dir E:\work\media\Boot\BCD
dir E:\work\media\EFI\Microsoft\Boot\BCD
```

### 2. BIOS側BCDへ設定

```cmd
bcdedit /store E:\work\media\Boot\BCD /dbgsettings net hostip:192.168.1.10 port:50000
bcdedit /store E:\work\media\Boot\BCD /set {default} debug yes
```

### 3. UEFI側BCDへ設定

```cmd
bcdedit /store E:\work\media\EFI\Microsoft\Boot\BCD /dbgsettings net hostip:192.168.1.10 port:50000
bcdedit /store E:\work\media\EFI\Microsoft\Boot\BCD /set {default} debug yes
```

**ここで /dbgsettings コマンドで返ったkey値を、KDCapture --keyの引数に与えること**

### 4. 設定確認

```cmd
bcdedit /store E:\work\media\Boot\BCD /enum
bcdedit /store E:\work\media\Boot\BCD /dbgsettings
bcdedit /store E:\work\media\EFI\Microsoft\Boot\BCD /enum
bcdedit /store E:\work\media\EFI\Microsoft\Boot\BCD /dbgsettings
```

確認ポイント:
- `debug` が `Yes`
- `dbgsettings` が `NET`
- `hostip` / `port` / `key` が想定値

### 5. ISOを再作成してDVD化
既存の WinPE ISO 作成手順(例: `MakeWinPEMedia /ISO ...`)で再作成する。

### 運用注意
- 起動方式が不明な現場では、必ず2つのBCDを両方設定する。
- `hostip` と `key` を変更する場合は ISO を作り直す。

### KDCapture実行例(採取PC側)

```cmd
KDCapture.exe --key 1.2.3.4 --port 50000 --targetip <WinPE_IP> --output C:\dumps\target_latest.dmp --loop --interval-sec 30 --capture-timeout-sec 300 --no-debuggee-timeout-sec 30 --delay-before-dump-sec 480 --log C:\dumps\kdcapture.log
```

