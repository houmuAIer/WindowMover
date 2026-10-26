; WindowMover インストーラー定義（Inno Setup 6 用）
; ビルド手順は README.md を参照。
; 事前に src フォルダで dotnet publish を実行し、単一ファイルの WindowMover.exe を作っておくこと。

#define MyAppName "WindowMover"
#define MyAppVersion "1.0.1"
#define MyAppPublisher "WindowMover"
#define MyAppExeName "WindowMover.exe"
#define MyPublishDir "..\src\bin\Release\net8.0-windows\win-x64\publish"

[Setup]
AppId={{9C5F1B42-3A7D-4E58-9A61-2C0E6F8B7D31}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
DefaultDirName={autopf}\{#MyAppName}
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
UninstallDisplayName={#MyAppName}
UninstallDisplayIcon={app}\{#MyAppExeName}
OutputDir=Output
OutputBaseFilename=WindowMoverSetup-{#MyAppVersion}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
PrivilegesRequired=admin
; 更新インストール時に起動中の WindowMover を閉じる
CloseApplications=yes
RestartApplications=no

[Languages]
Name: "japanese"; MessagesFile: "compiler:Languages\Japanese.isl"

[Tasks]
Name: "desktopicon"; Description: "デスクトップにショートカットを作成する"; GroupDescription: "追加のタスク:"; Flags: unchecked
Name: "startupicon"; Description: "Windows へのサインイン時に自動起動する（推奨）"; GroupDescription: "追加のタスク:"

[Files]
Source: "{#MyPublishDir}\{#MyAppExeName}"; DestDir: "{app}"; Flags: ignoreversion
; 自己完結（self-contained）で publish しているため .NET ランタイムの同梱は不要

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{group}\{#MyAppName} のアンインストール"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon
Name: "{userstartup}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: startupicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "{#MyAppName} を起動する（Ctrl+Alt+W で呼び出せます）"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
Type: filesandordirs; Name: "{app}"
