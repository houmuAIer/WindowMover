# WindowMover

複数モニタ環境で、複数のウィンドウをまとめて別のモニタへ移動するための常駐ツール（Windows 10 / 11、x64）。

- `Ctrl + Alt + W` で選択パネルを呼び出す
- ウィンドウ名・プロセス名・ライブサムネイル付きの一覧から、エクスプローラーと同じ操作感（クリック／Ctrl+クリック／Shift+クリック／Ctrl+A）で複数選択
- 移動先モニタをボタンまたは数字キーで指定すると、選択したウィンドウがまとめて移動する

---

## 1. 必要なもの

| 用途 | ソフト | 入手方法 |
|---|---|---|
| ビルド | .NET 8 SDK | `winget install Microsoft.DotNet.SDK.8` |
| インストーラー作成 | Inno Setup 6 | `winget install JRSoftware.InnoSetup` |

`winget` が使えない場合は、それぞれ公式サイトからインストーラーを入手してください。

---

## 2. フォルダ構成

```
WindowMover\
  src\
    WindowMover.csproj
    app.manifest
    App.xaml / App.xaml.cs
    MainWindow.xaml / MainWindow.xaml.cs
    Native.cs
    Models.cs
    Enumerators.cs
    WindowMoverService.cs
  installer\
    WindowMover.iss
  README.md
```

---

## 3. ビルドと動作確認

PowerShell またはコマンドプロンプトで `src` フォルダに移動し、まず開発用に実行して動作を確認します。

```powershell
cd C:\path\to\WindowMover\src
dotnet restore
dotnet run -c Debug
```

起動するとタスクトレイに常駐します。`Ctrl + Alt + W` でパネルが開けば成功です。終了はトレイアイコンの右クリックメニューから。

---

## 4. 単一 exe の publish

配布用に、.NET ランタイム同梱の単一 exe を作ります。

```powershell
cd C:\path\to\WindowMover\src
dotnet publish -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true `
  -p:IncludeNativeLibrariesForSelfExtract=true `
  -p:EnableCompressionInSingleFile=true
```

出力先は次のとおりです。

```
src\bin\Release\net8.0-windows\win-x64\publish\WindowMover.exe
```

---

## 5. インストーラー exe の作成

Inno Setup のコンパイラを使って `installer\WindowMover.iss` をコンパイルします。

```powershell
cd C:\path\to\WindowMover\installer
& "C:\Program Files (x86)\Inno Setup 6\ISCC.exe" WindowMover.iss
```

GUI で行う場合は `WindowMover.iss` をダブルクリックして Inno Setup Compiler で開き、`Build > Compile` を実行します。

生成物：

```
installer\Output\WindowMoverSetup-1.0.1.exe
```

この exe を実行すると Program Files にインストールされ、スタートメニューと（既定で）スタートアップにショートカットが作成されます。アンインストールは「アプリと機能」から行えます。

---

## 6. 使い方

1. `Ctrl + Alt + W` を押すとパネルが開く（もう一度押すと閉じる）
2. 移動したいウィンドウのカードをクリックして選択する
   - `Ctrl + クリック`：選択の追加・解除
   - `Shift + クリック`：範囲選択
   - `Ctrl + A`：全選択・全解除
   - 上部の検索ボックスでウィンドウ名・プロセス名による絞り込み
3. 下部の移動先モニタボタンをクリック、または数字キー `1`〜`9` を押す
4. 選択したウィンドウがまとめてそのモニタへ移動する

その他のキー：`Esc` 閉じる、`F5` 一覧の再取得、`Enter` カーソルのあったモニタへ移動。

---

## 7. 仕様上の注意

- **最大化ウィンドウ**：いったん通常状態に戻してから移動し、移動後に再度最大化します。
- **最小化ウィンドウ**：DWM のライブサムネイルが取得できないため「最小化中」と表示されます。移動すると通常状態に復元されます。
- **拡大率の異なるモニタ間**：移動元の作業領域に対する相対位置・相対サイズを保って配置します。アプリ側が `WM_DPICHANGED` で自身をリサイズするため、移動後に一度だけ位置を補正しています。
- **管理者権限のアプリ**：タスクマネージャーやレジストリエディタなど昇格プロセスのウィンドウは、UIPI により通常権限からは移動できません。移動対象に含めたい場合は `src\app.manifest` の `asInvoker` を `requireAdministrator` に変更して再ビルドしてください。ただし起動のたびに UAC プロンプトが出るため、スタートアップからの自動起動は使えなくなります。その場合はタスクスケジューラに「最上位の特権で実行する」でログオン時タスクとして登録してください。
- **UWP アプリ**：`DWMWA_CLOAKED` により、閉じているように見える不可視ウィンドウは一覧から除外しています。
- **ショートカットが登録できない場合**：他アプリが `Ctrl + Alt + W` を使用しています。`src\App.xaml.cs` の `Native.VK_W` と修飾キーの組み合わせを変更して再ビルドしてください。
- **AllowsTransparency**：`MainWindow.xaml` で `False` にしています。`True`（レイヤードウィンドウ）にすると DWM サムネイルが一切描画されなくなるため、変更しないでください。

---

## ライセンス

[MIT License](LICENSE)
