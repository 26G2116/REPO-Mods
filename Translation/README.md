# MOD の日本語化(XUnity.AutoTranslator 用)

「Repo MODs」プロファイルの MOD の英語を、XUnity.AutoTranslator で日本語に置き換えるための翻訳表。
ゲーム本体は公式の日本語(ゲームの設定で言語を日本語にする)、SharePermissions は MOD 自身の日本語を使う。

## 入れ方
1. Gale で `RTLC-XUnity_AutoTranslator` を入れる
2. 次の 2 つをプロファイルにコピーする
   (プロファイルの場所: Gale の「プロファイル」→ フォルダを開く。既定は
   `%APPDATA%\com.kesomannen.gale\repo\profiles\<プロファイル名>`)

   | このフォルダのファイル | コピー先 |
   |---|---|
   | `AutoTranslatorConfig.ini` | `BepInEx\config\`(すでにある場合は下の「設定」の項目だけ書き換える) |
   | `ja\Text\REPO_Mods_ja.txt` | `BepInEx\Translation\ja\Text\` |
3. ゲームの言語を日本語にして起動する

## 設定(AutoTranslatorConfig.ini)
- `Endpoint=` を空にして、機械翻訳を使わない(プレイヤー名やチャットを外部に送らない)
- `Language=ja` / `FromLanguage=en`
- `EnableIMGUI=True`(BanEnforcer の画面や LateJoinNow の部屋情報表示に必要)

## ゲーム中の操作(XUnity の機能)
- `Alt + T`: 翻訳の ON / OFF(元の英語を確認したいとき)
- `Alt + R`: 翻訳表の読み込み直し(ゲームを起動したまま翻訳表を直したとき)

## 翻訳表の書き方
- `英語=日本語`(左は画面に出る文と完全に一致させる。`=` を含む場合は `\=`)
- 数字や名前が入る文は正規表現: `r:"^Banned (.+)\.$"=$1 を BAN しました。`
- 設定画面(REPOConfig)の項目名は、設定のキーの大文字の区切りにスペースを入れた形で出る
  (例: `SpawnUpgradeItems` → `Spawn Upgrade Items`)
- 訳を作るときは `Tools\extract_binds.py`(設定項目)と `Tools\extract_literals.py`(画面の文の候補)で
  逆コンパイルした MOD から英語を抜き出した

## 訳していないもの
- MOD 名(REPOConfig の MOD 一覧)… 元の名前のほうが探しやすいため
- キーの名前(F6 など)、ping の表示(`[H] 45ms`)
- アイテム名など、MOD のアセット(バンドル)の中にある文 … 遊んでみて英語が残っていたら追加する
