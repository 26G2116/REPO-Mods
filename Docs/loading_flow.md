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
