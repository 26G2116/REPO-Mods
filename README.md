# REPO_Mod

R.E.P.O. 用 BepInEx 5 プラグインのテンプレート。

## 構成
- `RepoMod.sln` … Visual Studio で開くソリューション
- `RepoModTemplate/Plugin.cs` … プラグイン本体(起動ログ、設定、Harmony 適用)
- `RepoModTemplate/Patches/PlayerControllerPatch.cs` … Harmony パッチのサンプル

## ビルドと確認
1. `RepoMod.sln` を Visual Studio で開いてビルドする(または `dotnet build`)
2. ビルドすると DLL が Gale の「Repo MODs」プロファイルの
   `BepInEx\plugins\RepoModTemplate\` に自動でコピーされる
3. Gale で「Repo MODs」プロファイルを選んで起動する
4. ログで確認する:
   `%APPDATA%\com.kesomannen.gale\repo\profiles\Repo MODs\BepInEx\LogOutput.log`
   - `RepoModTemplate 0.1.0 を読み込みました` … 読み込み成功
   - `PlayerController.Start が呼ばれました` … パッチ成功

※ ゲームを起動したままだと DLL のコピーに失敗する。ビルド前にゲームを閉じる。

## パスの変更
`RepoModTemplate.csproj` の `GameDir`(ゲームのフォルダ)と `ProfileDir`(Gale プロファイル)を書き換える。

## 新しい MOD を作るとき
フォルダをコピーして、名前を変える:
- `.csproj` のファイル名と `AssemblyName` / `RootNamespace`
- `Plugin.cs` の `PluginGuid` / `PluginName` / namespace
