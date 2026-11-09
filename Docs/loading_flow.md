# R.E.P.O. マルチプレイのロード処理(2026-11-09 調査)

調査元: `Assembly-CSharp.dll`(2026-11-05 更新版)を dnSpy で逆コンパイル
(出力: `D:\MOD_Tools\decompiled\REPO`、git には入れない)

## 前提
- ネットワークは Photon PUN。ホスト = `PhotonNetwork.IsMasterClient`
- マップ生成はホストだけが行い、モジュールや敵は `PhotonNetwork.Instantiate` でクライアントに複製される
- 「全員そろうまで待つ」場所が何か所かあり、ホストは「報告が届いたプレイヤーのリスト」を持っている

## ロードの流れ(各プレイヤーの到達チェックポイント)

| # | 段階 | クライアント側の処理 | ホストに届く通知 | ホストが持つ記録 |
|---|---|---|---|---|
| 1 | Reload シーン到着 | `ReloadScene.Start` | RPC `ReloadScene.PlayerReady` | `ReloadScene.PlayersReadyList` (private) |
| 2 | Main シーン読み込み・アバター生成 | `NetworkManager.Start` で PlayerAvatar を Instantiate | RPC `NetworkManager.PlayerSpawnedRPC` | `NetworkManager.instantiatedPlayerAvatarsList` (private) |
| - | 全員そろう | ホストが `AllPlayerSpawnedRPC` → `LevelGenerator.AllPlayersReady = true` | | |
| 3 | マップ生成(ホストのみ) | Tiles → StartRoom → ModuleGeneration → ConnectObjects → BlockObjects | - | `LevelGenerator.State` |
| 4 | モジュール受信 | `Module.Start` で `ModulesSpawned++`、目標数 `ModulesSpawnedTarget` に達したら送信 | RPC `LevelGenerator.ModulesReadyRPC` | `LevelGenerator.ModulesReadyPlayerList` (private) |
| 5 | アイテム・バルブ配置(ホスト) → NavMesh 構築(各自) | `LocalSetupReady` = NavMesh 完了 + Map 生成完了 + アイテムアイコン準備完了 | - | - |
| 6 | スポーン地点へ移動 | `PlayerAvatar` で spawn 後、`LocalSetupReady` なら送信 | RPC `LevelGenerator.PlayerSpawnedRPC` | `LevelGenerator.SpawnedReadyPlayerList` (private) |
| 7 | 敵の受信 | `EnemiesSpawned >= EnemiesSpawnTarget` で送信 | RPC `LevelGenerator.EnemyReadyRPC` | `LevelGenerator.EnemyReadyPlayerList` (internal) |
| 8 | 生成完了 | ホストが `GenerateDone` を全員へ → `GameDirector.SetStart()`、`Generated = true` | | |
| 9 | トラックのロード演出終了 | `PlayerAvatar.LoadingLevelAnimationCompleted` | RPC `LoadingLevelAnimationCompletedRPC` (全員へ) | 各 `PlayerAvatar.levelAnimationCompleted` |

`LevelGenerator.Generate()` の待ちループで `State` が切り替わる:
`Load`(#2待ち) → `Tiles`〜`BlockObjects`(#3) → `ModuleSpawnLocal` → `ModuleSpawnRemote`(#4待ち)
→ `LevelPoint` → `Item` → `Valuable` → `PlayerSetup` → `PlayerSpawn`(#6待ち) → `Enemy`(#7待ち) → `Done`

## MOD 設計への示唆
- **ホストだけに MOD を入れれば**、#1・#2・#4・#6・#7・#9 の「誰がどこまで来たか」は、
  上のリストを読むだけで分かる(private のものは Harmony の `AccessTools` / `Traverse` で読む)。
  → 段階表示(例: 「Aさん: モジュール受信済み / Bさん: NavMesh 構築中」)はバニラのクライアント相手でも可能。
- **段階の中の細かい進み具合**(例: モジュール 37/80、NavMesh 構築中)はホストに送られていない。
  → これを出したいなら、クライアントにも MOD を入れて独自 RPC / Photon イベントで送る必要がある。
- 既存機能: `NetworkManager.StuckInLoading()` でロードが止まったら Back キーで抜けられる。
- ロード画面は `LoadingUI`。表示を重ねるならここ、または独自の Canvas / OnGUI。

## LoadProgress MOD の実装方針(v0.1.0)
- 5 を 5a(貴重品受信 = NavMesh 構築の合図を受けた)と 5b(`LocalSetupReady`)に分けて 10 段階
- Harmony パッチは使わず、毎 0.1 秒ゲームの状態を読む(更新で壊れにくい)。private は publicizer で参照
- 開始: `Reload` シーン読み込み(保険として新しい LevelGenerator 検出)。
  終了: 自分が `GameDirector.gameState.Main` 到達 + 全員完了(確認できない人がいても 10 秒で閉じる)
- 共有: Photon イベント 173 `{ver, int[] actor, int[] mask}`。参加者は自分の分、ホストは全員分を送る。
  受信側は「本人の申告」か「ホストからの情報」だけ OR で取り込む。表示していない間は捨てる
- 段階は順番に進むので、上位の段階が分かれば下位も完了とみなす(MOD なしの人の 5a/5b などを埋める)
- 演出がないレベル(ロビー等)は個人の演出終了フラグが立たないので、Main 到達で全員の 9 を完了扱い

## 今後追加したい MOD(ユーザーの予定)
- 権限 MOD(内容は未確認)
