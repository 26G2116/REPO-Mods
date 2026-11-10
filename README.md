# REPO_Mod

R.E.P.O. 用 BepInEx 5 プラグインのテンプレート。

## 構成
- `RepoMod.sln` … Visual Studio で開くソリューション
- `LoadProgress/` … マルチのロード進捗を 10 段階で表示する MOD(詳細: `Docs/loading_flow.md`)
- `MonsterTweaks/` … モンスターの挙動を変える MOD(27 体の強化 + 全モンスター共通のスタン無効・速度)(詳細: `Docs/monster_tweaks.md`、`MonsterTweaks/Package/README.md`)
- `Translation/` … 他の人の MOD を日本語にする翻訳表(XUnity.AutoTranslator 用、詳細: `Translation/README.md`)
- `Tools/` … 逆コンパイルした MOD から英語の文を抜き出すスクリプト
- `RepoModTemplate/Plugin.cs` … プラグイン本体(起動ログ、設定、Harmony 適用)
- `RepoModTemplate/Patches/PlayerControllerPatch.cs` … Harmony パッチのサンプル

## ビルドと確認
1. 初回だけ: **Release** でビルドする → `dist\shunm-<MOD名>-<版>.zip` ができる。
   Gale で「Repo MODs」プロファイルを選び、「ローカル MOD のインポート」でこの zip を取り込む
   (これで Gale の MOD 一覧に表示され、スイッチで ON / OFF できる)
2. 開発中は **Debug** でビルドする(Visual Studio または `dotnet build`)。
   Gale が取り込んだ MOD フォルダの DLL が自動で上書きされる
   - Gale で OFF にしているときはコピーしない(勝手に ON に戻らない)
   - 取り込んでいないときは、警告が出るだけでコピーしない
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

版を上げたら(`.csproj` の `Version`)、Release でビルドして Gale にもう一度インポートする。
