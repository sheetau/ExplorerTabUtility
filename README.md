# ExplorerTabUtility

Windows 11のエクスプローラーをタブにまとめる常駐アプリです。「ファイルの場所を開く」で指定されたファイルの選択・フォーカス・表示位置も引き継ぎます。

[w4po/ExplorerTabUtility](https://github.com/w4po/ExplorerTabUtility)を基に、選択状態の保持と操作速度を改善した別バージョンです。

## 導入

1. [最新リリース](https://github.com/sheetau/ExplorerTabUtility/releases/latest)から端末に合うZIPを取得します。通常は **x64**、ARM搭載PCは **arm64** です。
2. 書き込み可能なフォルダーへZIP全体を展開し、`ExplorerTabUtility.exe`を起動します。付属ファイルは同じフォルダーに置いてください。

Windows 11の標準エクスプローラーに対応しています。必要なランタイムは同梱しています。別のExplorerTabUtilityが動いている場合は、先に終了してください。

## 使い方

- **Window hook**：新しいエクスプローラーをタブにまとめます。Ctrl＋Shiftを押しながら開くと通常のウィンドウで開けます。
- **Reuse tabs**：同じフォルダーが開いていれば、そのタブを再利用します。
- **Shortcuts**：キーボード・マウスの操作を設定します。項目を展開して編集し、下の **Add shortcut** で追加します。変更は自動保存されます。
- **Preferences**：自動起動、更新通知、閉じたタブの記憶などを切り替えます。

設定はトレイアイコンから開けます。実行ファイルをもう一度起動しても開きます。設定画面を閉じても常駐し、トレイメニューの **Exit** で終了します。

タスクバーの「File Explorer」と、ピン留めされたフォルダーからも直接タブを開けます。ファイルや識別できない項目はWindows標準の動作になります。

## 更新と設定

**Check for updates** を有効にすると、起動時と常駐中にGitHub Releasesの新版を確認します。通知から設定を開き、**Update and restart** を押すと、設定を引き継いで更新・再起動します。トレイの **Check for updates** でも確認できます。

手動で更新する場合はアプリを終了し、新しいZIPを同じフォルダーへ展開してください。設定は `%APPDATA%/ExplorerTabUtility/settings.json` に保存されます。

この版の配布・更新元は[このリポジトリ](https://github.com/sheetau/ExplorerTabUtility/releases)です。w4po版のWinGet／Chocolateyパッケージとは異なります。

原作者はw4poおよび貢献者の皆さんです。[MITライセンス](LICENSE)で公開しています。
