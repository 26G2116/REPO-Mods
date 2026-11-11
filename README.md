# REPO-Mods

[R.E.P.O.](https://store.steampowered.com/app/3241660/REPO/) 用の BepInEx 5 プラグイン(MOD)集です。

> English: A collection of BepInEx 5 plugins for R.E.P.O. The main one is **MonsterTweaks**, which heavily buffs 27 monsters
> (works when only the host installs it). Documentation is in Japanese. MIT licensed.

## MOD の一覧
| MOD | 内容 | 説明書 |
|---|---|---|
| **MonsterTweaks** | モンスター 27 体の個別の強化と、全モンスター共通の「スタン無効」「移動速度倍率」。**ホストだけ導入すれば全員に効く** | [MonsterTweaks/Package/README.md](MonsterTweaks/Package/README.md) |
| **DroneTweaks** | 削除されたドローン・オーブを店に戻す(アイテムごとに ON/OFF)。ヒーラードローンで倒れた人を復活できる。**ホストだけ導入すれば全員に効く** | [DroneTweaks/Package/README.md](DroneTweaks/Package/README.md) |
| **LoadProgress** | マルチプレイのロード中に、全員のロードの進み具合を 10 段階で表示する | [LoadProgress/Package/README.md](LoadProgress/Package/README.md) |
| Translation | ほかの作者の MOD の画面の英語を日本語にする翻訳表(XUnity.AutoTranslator 用) | [Translation/README.md](Translation/README.md) |

### MonsterTweaks の主な強化
- エルサ・APEX プレデター・ローブ・クリーンアップクルー・リーパーなど 27 体の行動を強化(敵ごとに ON / OFF・数値を設定できる)
- できるだけゲーム本来の効果(卵の爆発、ロールの杖、無重力の杖、本物の敵の分身など)を使うので、MOD なしの参加者の画面でも自然に見える
- 設定は REPOConfig を使えばゲーム中に変更でき、すぐ反映される

**注意**: 強化はかなり強めです。ホストが導入すると、参加者は MOD を入れていなくても強化された敵と戦うことになります。
知らない人と遊ぶ部屋で使うときは、事前に伝えてください。

## 導入方法(遊ぶ人向け)
1. MOD マネージャー([Gale](https://github.com/Kesomannen/gale) または r2modman)で R.E.P.O. のプロファイルを作り、BepInEx を入れる
2. MOD の zip を用意する(下の「ビルド」で作るか、Releases に置いてあればそれを使う)
3. MOD マネージャーの「ローカル MOD のインポート」で zip を取り込む
4. そのプロファイルでゲームを起動する

- MonsterTweaks は基本的に**ホストだけ**が入れれば効きます(一部の演出は、MOD を入れた人の画面にだけ出ます)
- 設定ファイルは `BepInEx\config\shunm.MonsterTweaks.cfg`(MOD マネージャーの設定画面からも編集できる)

## ビルド(開発する人向け)
必要なもの: .NET SDK(`dotnet build` が使えること)、R.E.P.O. 本体、BepInEx を入れた Gale のプロファイル。

1. 各 `.csproj` の `GameDir`(ゲームのフォルダ)と `ProfileDir`(Gale のプロファイル)を自分の環境に合わせる
   (`dotnet build -p:GameDir=...` のように指定してもよい)
2. **Release** でビルドする → `dist\shunm-<MOD名>-<版>.zip` ができる。Gale の「ローカル MOD のインポート」で取り込む
3. 開発中は **Debug** でビルドする。Gale が取り込んだ MOD フォルダの DLL が自動で上書きされる
   - Gale で OFF にしているときや、まだ取り込んでいないときはコピーしない
   - ゲームを起動したままだと DLL のコピーに失敗するので、ビルド前にゲームを閉じる
4. ログ: `%APPDATA%\com.kesomannen.gale\repo\profiles\<プロファイル名>\BepInEx\LogOutput.log`

ゲームの DLL(Assembly-CSharp など)はゲームのフォルダから参照するだけで、このリポジトリには含めていません。
非公開のメンバーには BepInEx.AssemblyPublicizer でアクセスしています。

### フォルダ構成
- `MonsterTweaks/` … モンスターを強化する MOD(`Enemies/` に敵ごとの処理。開発の記録: `Docs/monster_tweaks.md`)
- `DroneTweaks/` … ドローン・オーブを変える MOD(削除アイテムの復活、ヒーラードローンの復活機能)
- `LoadProgress/` … ロードの進み具合を表示する MOD(仕組み: `Docs/loading_flow.md`)
- `RepoModTemplate/` … 新しい MOD を作るときのひな形(起動ログ・設定・Harmony パッチのサンプル)
- `Translation/` … 日本語化の翻訳表
- `Tools/` … MOD の設定項目や英語の文を抜き出す補助スクリプト

### 新しい MOD を作るとき
`RepoModTemplate` をコピーして、次の名前を変える。
- `.csproj` のファイル名と `AssemblyName` / `RootNamespace`
- `Plugin.cs` の `PluginGuid` / `PluginName` / namespace

## ライセンス
[MIT License](LICENSE) です。自由に使用・改変・再配布できます(著作権表示とライセンス文は残してください)。

- R.E.P.O. は semiwork の作品です。この MOD 集は非公式のファンメイドで、semiwork とは関係ありません。
  ゲームのファイル(DLL・画像・音・モデル)は含んでいません。
- `Translation/` の翻訳表の原文(英語)は、それぞれの MOD の作者のものです。
