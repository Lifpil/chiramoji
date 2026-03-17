# インストーラー作成手順（初心者向け）

## 1. 事前準備
- .NET SDK 7 以上
- Inno Setup 6
  - https://jrsoftware.org/isdl.php からインストール

## 2. setup.exe を作る
プロジェクトのルートで次を実行します。

```powershell
.\build-installer.ps1
```

バージョン番号を変える場合:

```powershell
.\build-installer.ps1 -Version 1.0.0
```

## 3. 完成ファイル
- 出力先: `dist\chiramoji-Setup.exe`
- これをユーザーに配布すればインストールできます。

## 4. ユーザー向け案内文（そのまま使えます）
1. `chiramoji-Setup.exe` をダブルクリック
2. 「次へ」を押して進む
3. 「インストール」を押す
4. 完了画面で「今すぐ起動する」にチェックしたまま「完了」
