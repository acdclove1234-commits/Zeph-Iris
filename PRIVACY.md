# Privacy policy

[日本語は下にあります](#プライバシーポリシー)

Zeph-Iris runs on your PC. It has no account, no analytics, no telemetry and no ads. It does not collect or sell personal data.

## What goes over the network

Only the items below. Each one can be turned off, and the ones marked *opt-in* do nothing until you set them up.

| Feature | Sent to | What is sent | When | Turn it off |
|---|---|---|---|---|
| Update check | `api.github.com` (GitHub) | A request for the latest release of this repository, with `Zeph-Iris/<version>` as the user agent | At start and every 6 hours, at most once a day | Settings → "Check for new versions automatically" |
| Device photos | `commons.wikimedia.org` (Wikimedia Commons) and `api.openverse.org` (Openverse; photos are fetched through Openverse, not from the sites that host them) | The model names of your parts (for example "GeForce RTX 5070 Ti") as a search term, with `Zeph-Iris/<version>` and this repository's URL as the user agent | When a part has no photo yet, and again every 30 days while none is found | Devices → "Find device photos online automatically" |
| Downloading an update | `github.com` | A download of the new installer | Only when you choose to update | Do not press Update |
| Phone notifications (*opt-in*) | The ntfy server you enter (`ntfy.sh` by default) | The alert text (for example "CPU 95°C") to the topic you choose | Only after you turn it on | Settings → phone notifications off |
| Phone / web remote (*opt-in*) | Nothing leaves your network; the app listens on your local network | Status and controls for devices that know the access code | Only after you turn it on | Settings → phone access off |
| AI advice (*opt-in*) | The Ollama server you enter (`localhost` by default) | Temperatures, fan speeds and your question | Only when you ask | Do not use the feature |
| OpenRGB lighting (*opt-in*) | The OpenRGB SDK server you enter (`localhost` by default) | LED colors | While lighting is on | Lighting → integration off |

Like any web request, the services above can see your IP address. Their own privacy policies apply:
[GitHub](https://docs.github.com/site-policy/privacy-policies/github-general-privacy-statement),
[Wikimedia](https://foundation.wikimedia.org/wiki/Policy:Privacy_policy),
[Openverse](https://openverse.org/privacy),
[ntfy.sh](https://ntfy.sh/docs/privacy/).

## Music sync AI and remembered songs

The beat AI (Beat This!) runs on your PC only; no audio leaves it. When "Remember songs already heard" is on, the app stores, per song, the beat times and a short beat-strength curve of the song's opening in `data/songs`. Songs are told apart by a SHA-256 code made from "Artist - Song": Spotify's window title, or the song shown in Windows' media info (the name that appears when you press a volume key, for example from YouTube Music or Apple Music). The same song counts as one song in any player app. This is read on your PC only; the title itself and the audio are never saved. "Forget remembered songs" on the Lighting page deletes them.

## Bug reports

"Report a problem" makes a zip file with logs, settings and the list of detected parts **on your PC** and opens the GitHub issue page in your browser. Nothing is uploaded automatically: you read the zip and decide whether to attach it.

## Changes to your PC

- The installer adds PawnIO (a signed driver that lets the app read sensors and set fan speeds) and Start menu shortcuts. The setup wizard in the app adds an auto-start task when you leave "Start with Windows" on. The uninstaller removes the task, shortcuts and firewall rule, and asks whether to delete settings and logs.
- A profile can switch the Windows power plan only if you pick one for it (the default is "do not change"); the original plan is restored when the app exits.
- While the app runs it sets fan speeds. When it exits, fans go back to BIOS control.

---

# プライバシーポリシー

Zeph-Iris は、あなたの PC の中で動きます。アカウント・利用状況の収集（テレメトリー）・広告はありません。個人の情報を集めたり売ったりしません。

## ネットワークに出るもの

下のものだけです。どれも止められます。「自分で設定したときだけ」のものは、設定するまで何もしません。

| 機能 | 送り先 | 送るもの | いつ | 止め方 |
|---|---|---|---|---|
| 新しい版の確認 | `api.github.com`（GitHub） | このリポジトリの最新リリースの問い合わせ（User-Agent は `Zeph-Iris/<版>`） | 起動時と 6 時間ごと（1 日 1 回まで） | 設定 →「新しい版を自動で確かめる」 |
| デバイスの写真 | `commons.wikimedia.org`（Wikimedia Commons）と `api.openverse.org`（Openverse。写真は Openverse を通して取り、元の置き場所のサイトには問い合わせません） | 部品の型番（例「GeForce RTX 5070 Ti」）を検索語として。User-Agent は `Zeph-Iris/<版>` とこのリポジトリの URL | 写真のない部品があるとき（見つからなければ 30 日ごとにもう一度） | デバイス →「デバイスの写真をネットで自動で探す」 |
| 更新のダウンロード | `github.com` | 新しいインストーラーのダウンロード | 更新を選んだときだけ | 更新を押さない |
| スマホ通知（自分で設定したときだけ） | 入力した ntfy サーバー（既定は `ntfy.sh`） | お知らせの文（例「CPU 95°C」）を、決めたトピックへ | オンにしたあとだけ | 設定 → スマホ通知をオフ |
| スマホ・ブラウザーからの操作（自分で設定したときだけ） | 家のネットワークの外へは出ません（家の中のネットワークで待ち受けます） | 状態と操作（アクセスコードを知っている機器だけ） | オンにしたあとだけ | 設定 → スマホからの操作をオフ |
| AI の助言（自分で設定したときだけ） | 入力した Ollama サーバー（既定は `localhost`） | 温度・ファンの回転数と質問 | 質問したときだけ | 使わない |
| OpenRGB の LED（自分で設定したときだけ） | 入力した OpenRGB の SDK サーバー（既定は `localhost`） | LED の色 | ライティングがオンの間 | ライティング → 連携をオフ |

ふつうの Web の通信と同じく、上のサービスからはあなたの IP アドレスが見えます。それぞれのプライバシーポリシーが当てはまります（上の英語の欄のリンク）。

## 音楽連動の AI と覚えた曲

拍の AI（Beat This!）は PC の中だけで動き、音を外に出しません。「一度聴いた曲を覚える」がオンのときは、曲ごとに拍の時刻と、曲の始まりの立ち上がりの強さの並びを `data/songs` に保存します。曲は「アーティスト - 曲名」から作った SHA-256 の印で見分けます。曲名は、Spotify のウィンドウのタイトルか、Windows のメディアの情報（音量ボタンを押すと出る曲名。YouTube Music・Apple Music など）から PC の中で読み（どの再生アプリで流しても同じ曲として扱います）、曲名そのものや音は保存しません。ライティングの画面の「覚えた曲を消す」で消せます。

## 不具合の報告

「不具合を報告…」は、ログ・設定・見つけた部品の一覧を **あなたの PC の中で** zip にまとめ、ブラウザーで GitHub の報告ページを開きます。自動では何も送りません。zip の中身を見て、添付するかをあなたが決めます。

## PC に加える変更

- インストーラーは、PawnIO（センサーを読み、ファンの速さを変えるための署名つきドライバー）とスタートメニューのショートカットを入れます。自動起動のタスクは、アプリのセットアップの「Windows の起動時に自動で起動する」をオンのまま進めると登録します。アンインストールすると、タスク・ショートカット・ファイアウォールの許可を消し、設定とログを消すかを尋ねます。
- 電源プランは、プロファイルにプランを選んだときだけ切り替えます（既定は「変更しない」）。アプリを終了すると、元のプランに戻します。
- アプリが動いている間はファンの速さを変えます。終了すると、ファンは BIOS の制御に戻ります。
