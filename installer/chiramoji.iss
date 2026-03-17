#ifndef MyAppVersion
  #define MyAppVersion "1.0.0"
#endif
#ifndef MyAppPublisher
  #define MyAppPublisher "ChiraMoji"
#endif
#ifndef MyAppExeName
  #define MyAppExeName "chiramoji.exe"
#endif
#ifndef MyAppId
  #define MyAppId "{{0A99B5F7-4413-4126-A6B5-7C9369AFB5D3}"
#endif

[Setup]
AppId={#MyAppId}
AppName=ちらもじ
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
DefaultDirName={autopf}\ちらもじ
DefaultGroupName=ちらもじ
DisableProgramGroupPage=yes
OutputDir=..\dist
OutputBaseFilename=chiramoji-Setup
SetupIconFile=..\chiramoji\Assets\ChiraMoji.ico
UninstallDisplayIcon={app}\{#MyAppExeName}
Compression=lzma
SolidCompression=yes
WizardStyle=modern
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible

[Languages]
Name: "japanese"; MessagesFile: "compiler:Languages\Japanese.isl"

[Tasks]
Name: "desktopicon"; Description: "デスクトップにショートカットを作成する"; GroupDescription: "追加オプション:"; Flags: unchecked

[Files]
Source: "..\publish\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\ちらもじ"; Filename: "{app}\{#MyAppExeName}"
Name: "{autodesktop}\ちらもじ"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "ちらもじ を起動する"; Flags: nowait postinstall skipifsilent

[Messages]
japanese.WelcomeLabel2=このセットアップでは [name] をお使いのPCにインストールします。%n%n「次へ」をクリックすると続行します。
japanese.SelectDirDesc=インストール先のフォルダーを指定してください。
japanese.SelectTasksDesc=必要なオプションを選択してください。
japanese.FinishedHeadingLabel=インストールが完了しました
japanese.FinishedLabel=[name] のインストールが完了しました。%n%n「完了」を押すとセットアップを終了します。
