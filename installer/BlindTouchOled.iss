#ifndef MyAppName
  #define MyAppName "繝悶Λ繧､繝ｳ繝峨ち繝・メ OLED"
#endif
#ifndef MyAppVersion
  #define MyAppVersion "1.0.2"
#endif
#ifndef MyAppPublisher
  #define MyAppPublisher "ChiraMoji"
#endif
#ifndef MyAppExeName
  #define MyAppExeName "BlindTouchOled.exe"
#endif
#ifndef MyAppId
  #define MyAppId "{{0A99B5F7-4413-4126-A6B5-7C9369AFB5D3}"
#endif

[Setup]
AppId={#MyAppId}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
DefaultDirName={autopf}\{#MyAppName}
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
OutputDir=..\dist
OutputBaseFilename=BlindTouchOled-Setup
SetupIconFile=..\BlindTouchOled\Assets\ChiraMoji.ico
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
Name: "desktopicon"; Description: "繝・せ繧ｯ繝医ャ繝励↓繧ｷ繝ｧ繝ｼ繝医き繝・ヨ繧剃ｽ懈・縺吶ｋ"; GroupDescription: "霑ｽ蜉繧ｪ繝励す繝ｧ繝ｳ:"; Flags: unchecked

[Files]
Source: "..\publish\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "Launch {#MyAppName}"; Flags: nowait postinstall skipifsilent

[Messages]
japanese.WelcomeLabel2=縺薙・繧ｻ繝・ヨ繧｢繝・・縺ｧ縺ｯ縲ーname] 繧偵♀菴ｿ縺・・PC縺ｫ繧､繝ｳ繧ｹ繝医・繝ｫ縺励∪縺吶・n%n縲梧ｬ｡縺ｸ縲阪ｒ繧ｯ繝ｪ繝・け縺吶ｋ縺ｨ騾ｲ縺ｿ縺ｾ縺吶・
japanese.SelectDirDesc=繧､繝ｳ繧ｹ繝医・繝ｫ蜈医ヵ繧ｩ繝ｫ繝繧堤｢ｺ隱阪＠縺ｦ縺上□縺輔＞縲・
japanese.SelectTasksDesc=蠢・ｦ√↑繧ｪ繝励す繝ｧ繝ｳ繧帝∈謚槭＠縺ｦ縺上□縺輔＞縲・
japanese.FinishedHeadingLabel=繧､繝ｳ繧ｹ繝医・繝ｫ縺悟ｮ御ｺ・＠縺ｾ縺励◆
japanese.FinishedLabel= [name] 縺ｮ繧､繝ｳ繧ｹ繝医・繝ｫ縺悟ｮ御ｺ・＠縺ｾ縺励◆縲・n%n縲悟ｮ御ｺ・阪ｒ謚ｼ縺吶→繧ｻ繝・ヨ繧｢繝・・繧堤ｵゆｺ・＠縺ｾ縺吶・


