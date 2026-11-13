# Code signing policy

[日本語は下にあります](#コード署名の方針)

Free code signing provided by [SignPath.io](https://about.signpath.io/), certificate by [SignPath Foundation](https://signpath.org/).

## What is signed

- `Zeph-Iris.exe` (the app) and `Zeph-Iris-<version>-setup.exe` (the installer), both built from this repository's source by the [Release build](.github/workflows/release.yml) workflow on GitHub Actions.
- Nothing else. Third-party files keep their own maintainers' signatures and are included unchanged, with their SHA-256 pinned in [make-dist.ps1](Distribution/make-dist.ps1): the [PawnIO](https://pawnio.eu/) installer (signed by its authors) and [PresentMon](https://github.com/GameTechDev/PresentMon) (signed by Intel; the app runs it only when its Intel signature is valid). See [CREDITS.txt](CREDITS.txt) for all third-party components and their licenses.
- Updates: once the app is signed, the in-app updater only accepts an installer that carries a valid signature from the same publisher, besides checking its size and SHA-256 against the GitHub release.

Every signing request is approved by hand. Builds made on a personal PC are never signed.

## Team roles

| Role | Members |
|---|---|
| Committers and reviewers | [acdclove1234-commits](https://github.com/acdclove1234-commits) |
| Approvers | [acdclove1234-commits](https://github.com/acdclove1234-commits) |

Changes from anyone else come in as pull requests and are reviewed before they are merged. Build scripts and workflows get the same review as the app's code. Everyone with access uses multi-factor authentication on GitHub and SignPath.

## Privacy

See the [privacy policy](PRIVACY.md). In short: no account, no telemetry. The app only contacts GitHub (update check) and Wikimedia Commons (device photos) on its own, and both can be turned off. Other network features (phone notifications, phone remote, AI advice, OpenRGB) do nothing until you set them up. The installer shows this before installing.

## Reporting a problem with a signed file

If you think a signed Zeph-Iris file is malicious or was not built from this repository, open an [issue](https://github.com/acdclove1234-commits/Zeph-Iris/issues) or contact [SignPath Foundation](https://signpath.org/).

---

# コード署名の方針

無料のコード署名は [SignPath.io](https://about.signpath.io/) が提供し、証明書は [SignPath Foundation](https://signpath.org/) が発行しています。

## 署名するもの

- `Zeph-Iris.exe`（アプリ）と `Zeph-Iris-<版>-setup.exe`（インストーラー）。どちらも、このリポジトリのソースから GitHub Actions の [Release build](.github/workflows/release.yml) で作ったものだけです。
- ほかのものには署名しません。ほかの人が作ったファイルは、作った人の署名のまま手を加えずに入れています（SHA-256 は [make-dist.ps1](Distribution/make-dist.ps1) で決めています）: [PawnIO](https://pawnio.eu/) のインストーラー（作者の署名）と [PresentMon](https://github.com/GameTechDev/PresentMon)（Intel の署名。アプリは Intel の署名が正しいときだけ動かします）。使っている部品とライセンスは [CREDITS.txt](CREDITS.txt) にあります。
- 更新: アプリに署名したあとは、アプリの中からの更新で、大きさと SHA-256 に加えて、同じ発行元の正しい署名があるインストーラーだけを使います。

署名は、毎回手で承認してから行います。個人の PC で作ったものには署名しません。

## 役割

| 役割 | メンバー |
|---|---|
| コミットとレビュー | [acdclove1234-commits](https://github.com/acdclove1234-commits) |
| 署名の承認 | [acdclove1234-commits](https://github.com/acdclove1234-commits) |

ほかの人からの変更はプルリクエストで受け取り、レビューしてから取り込みます。ビルドのスクリプトとワークフローも、アプリのコードと同じようにレビューします。関わる人は全員、GitHub と SignPath で多要素認証を使います。

## プライバシー

[プライバシーポリシー](PRIVACY.md) をご覧ください。アカウントもテレメトリーもありません。アプリが自分から通信するのは GitHub（新しい版の確認）と Wikimedia Commons（デバイスの写真）だけで、どちらも止められます。インストーラーは、入れる前にこのことを表示します。

## 署名されたファイルに問題があると思ったら

署名された Zeph-Iris のファイルが悪意のあるものだと思ったり、このリポジトリから作られていないと思ったりしたら、[Issue](https://github.com/acdclove1234-commits/Zeph-Iris/issues) か [SignPath Foundation](https://signpath.org/) に連絡してください。
