# SourceGit - Opensource Git GUI client.

## About This Fork

This fork started as an effort to support WSL repositories from the Windows application: SourceGit runs on the Windows host, while Git operations can target repositories inside WSL.

To support that, this fork uses AI to refactor the Git execution path into a frontend-backend separated pipeline:

```text
GUI interaction -> Git operation -> Git command -> Git backend
```

Each layer has a narrow role:

* GUI interaction: shows repository state returned by Git and accepts user actions.
* Git operation: represents user interactions as abstract Git operations.
* Git command: builds the concrete Git CLI invocation.
* Git backend: runs Git CLI in the target environment, such as Windows or WSL. Linux and macOS should remain compatible, but are not tested.

This keeps backend details out of the GUI layer, so the same UI can work with native Windows Git or WSL Git.

Compared with upstream SourceGit, this fork mainly changes:

* An explicit GUI-operation-command-backend pipeline.
* A backend/controller layer for Git execution.
* WSL path handling and temporary-file bridging to generate Git CLI invocations that can run in WSL.
* Automatic refresh after GUI actions that mutate repository state, such as commit, push, pull, checkout, branch, tag, stash, submodule, and worktree operations.
* Lightweight 60s periodic refresh for the active repository, used to catch external Git changes or missed filesystem notifications.
* A manual refresh button.

Trade-offs:

* Repository updates no longer rely on the C# filesystem watcher as the source of truth. This is important for WSL support, but changes made outside the GUI may not appear immediately.
* Get used to using manual refresh.

This fork plans to track upstream changes from time to time, using AI assistance to merge upstream code changes into this refactor.

## 关于这个 Fork (Chinese translation)

这个 fork 最初是为了支持在 Windows 应用中操作 WSL 仓库：SourceGit 运行在 Windows host 上，但 Git 操作可以作用于 WSL 里的 repository。

为此，这个 fork 利用 AI 把 Git 执行路径重构为前后端分离的 pipeline：

```text
GUI interaction -> Git operation -> Git command -> Git backend
```

各层职责如下：

* GUI interaction：显示 Git 返回的仓库状态，并接收用户交互。
* Git operation：抽象的 Git 操作，来表示用户交互。
* Git command：具体的 Git CLI 调用。
* Git backend：Git CLI 实际执行的环境，例如 Windows 或 WSL。Linux 和 macOS 理论上应保持兼容，但没测试。

这样可以避免 backend 细节泄漏到 GUI 层，让同一套 UI 同时支持 native Windows Git 和 WSL Git。

相较于上游 SourceGit，这个 fork 的主要改动包括：

* 明确 GUI-operation-command-backend pipeline。
* 引入 Git 执行的 backend/controller 层。
* 增加 WSL path 处理和临时文件桥接，用于生成 WSL 可执行的 Git CLI。
* 对会修改仓库状态的 GUI 操作自动 refresh，例如 commit、push、pull、checkout、branch、tag、stash、submodule、worktree 等。
* 对当前 active repository 增加轻量级60s周期 refresh，用来捕获 GUI 外部的 Git 操作，或 filesystem notification 漏掉的变化。
* 增加了手动refresh 按钮。

取舍：

* 仓库更新不再把 C# filesystem watcher 作为事实来源。这对 WSL 支持很重要，但 GUI 外部的 Git 操作可能不会第一时间反映到界面。
* 需要养成手动 refresh 的习惯。

该 fork 计划不定期跟踪主线更新，并用 AI 将主线代码变更合并进当前重构。

## Build

Requirements:

* Git
* .NET SDK 10.0.x
* Initialized submodules： `git submodule update --init --recursive`

Build:

```sh
dotnet build -c Release
```

---

[![stars](https://img.shields.io/github/stars/sourcegit-scm/sourcegit.svg)](https://github.com/sourcegit-scm/sourcegit/stargazers)
[![forks](https://img.shields.io/github/forks/sourcegit-scm/sourcegit.svg)](https://github.com/sourcegit-scm/sourcegit/forks)
[![license](https://img.shields.io/github/license/sourcegit-scm/sourcegit.svg)](LICENSE)
[![latest](https://img.shields.io/github/v/release/sourcegit-scm/sourcegit.svg)](https://github.com/sourcegit-scm/sourcegit/releases/latest)
[![downloads](https://img.shields.io/github/downloads/sourcegit-scm/sourcegit/total)](https://github.com/sourcegit-scm/sourcegit/releases)

## Screenshots

* Dark Theme

  ![Theme Dark](./screenshots/theme_dark.png)

* Light Theme

  ![Theme Light](./screenshots/theme_light.png)

* Custom

  You can find custom themes from [sourcegit-theme](https://github.com/sourcegit-scm/sourcegit-theme.git). And welcome to share your own themes.

## Highlights

* Supports Windows/macOS/Linux
* Opensource/Free
* Fast
* Deutsch/English/Español/Bahasa Indonesia/Français/Italiano/Português/Русский/Українська/简体中文/繁體中文/日本語/தமிழ் (Tamil)/한국어
* Built-in light/dark themes
* Customize theme
* Visual commit graph
* Supports SSH access with each remote
* GIT commands with GUI
  * Clone/Fetch/Pull/Push...
  * Merge/Rebase/Reset/Revert/Cherry-pick...
  * Amend/Reword/Squash
  * Interactive rebase
  * Branches
  * Remotes
  * Tags
  * Stashes
  * Submodules
  * Worktrees
  * Archive
  * Diff
  * Save as patch/apply
  * File histories
  * Blame
  * Revision Diffs
  * Branch Diff
  * Image Diff - Side-By-Side/Swipe/Blend
* Git command logs
* Search commits
* GitFlow
* Git LFS
* Bisect
* Issue Link
* Workspace
* Custom Action
* Create PR on GitHub/Gitlab/Gitea/Gitee/Bitbucket...
* Using AI to generate commit message
* Built-in conventional commit message helper.

> [!WARNING]
> **Linux** only tested on **Debian 12** on both **X11** & **Wayland**.

## How to Use

**To use this tool, you need to install Git(>=2.25.1) first.**

You can download the latest stable from [Releases](https://github.com/sourcegit-scm/sourcegit/releases/latest) or download workflow artifacts from [GitHub Actions](https://github.com/sourcegit-scm/sourcegit/actions) to try this app based on latest commits.

This software creates a folder, which is platform-dependent, to store user settings, downloaded avatars and crash logs.

| OS      | PATH                                      |
|---------|-------------------------------------------|
| Windows | `%APPDATA%\SourceGit`                     |
| Linux   | `~/.sourcegit`                            |
| macOS   | `~/Library/Application Support/SourceGit` |

> [!TIP]
> * You can open this data storage directory from the main menu `Open Data Storage Directory`.
> * You can create a `data` folder next to the `SourceGit` executable to force this app to store data (user settings, downloaded avatars and crash logs) into it (Portable-Mode). Only works with Windows packages and Linux AppImages.

For **Windows** users:

* **MSYS Git is NOT supported**. Please use official [Git for Windows](https://git-scm.com/download/win) instead.
* You can install the latest stable by `scoop` with follow commands:
  ```shell
  scoop bucket add extras
  scoop install sourcegit
  ```
* Pre-built binaries can be found in [Releases](https://github.com/sourcegit-scm/sourcegit/releases/latest)

> [!NOTE]
> `git-flow` is no longer shipped with **Git for Windows** since `2.51.1`. You can use it by following these steps:
>  * Download [git-flow-next](https://github.com/gittower/git-flow-next/releases)
>  * Unzip & Rename the `git-flow-next` to `git-flow`
>  * Copy to `$GIT_INSTALL_DIR/cmd` or just add its path to you `PATH` directly

For **macOS** users:

* Thanks [@ybeapps](https://github.com/ybeapps) for making `SourceGit` available on `Homebrew`:
  ```shell
  brew install --cask sourcegit
  ```
* If you want to install `SourceGit.app` from GitHub Release manually, you need run following command to make sure it works:
  ```shell
  sudo xattr -cr /Applications/SourceGit.app
  ```
> [!NOTE]
> macOS packages in the `Release` page of this project are all unsigned. If you are worried about potential security issues with the above command, you can download the signed package from the [distribution repository](https://github.com/ybeapps/homebrew-sourcegit/releases) provided by [@ybeapps](https://github.com/ybeapps) (there is no need to execute the above command while installing `SourceGit`).

* Make sure [git-credential-manager](https://github.com/git-ecosystem/git-credential-manager/releases) is installed on your mac.
* You can run `echo $PATH > ~/Library/Application\ Support/SourceGit/PATH` to generate a custom PATH env file to introduce `PATH` env to SourceGit.

For **Linux** users:

* Thanks [@aikawayataro](https://github.com/aikawayataro) for providing `rpm` and `deb` repositories, hosted on [Codeberg](https://codeberg.org/yataro/-/packages).

  `deb` how to:
  ```shell
  sudo mkdir -p /etc/apt/keyrings
  curl https://codeberg.org/api/packages/yataro/debian/repository.key | sudo tee /etc/apt/keyrings/sourcegit.asc
  echo "deb [signed-by=/etc/apt/keyrings/sourcegit.asc, arch=amd64,arm64] https://codeberg.org/api/packages/yataro/debian generic main" | sudo tee /etc/apt/sources.list.d/sourcegit.list
  sudo apt update
  sudo apt install sourcegit
  ```

  `rpm` how to:
  ```shell
  curl https://codeberg.org/api/packages/yataro/rpm.repo | sed -e 's/gpgcheck=1/gpgcheck=0/' > sourcegit.repo

  # Fedora 41 and newer
  sudo dnf config-manager addrepo --from-repofile=./sourcegit.repo
  # Fedora 40 and earlier
  sudo dnf config-manager --add-repo ./sourcegit.repo

  sudo dnf install sourcegit
  ```

  If your distribution isn't using `dnf`, please refer to the documentation of your distribution on how to add an `rpm` repository.

* Thanks [@gadfly3173](https://github.com/gadfly3173) for providing `deb` repository, hosted on https://deb-repo.gadfly.vip

  ```shell
  # Import GPG key
  curl -fsSL https://deb-repo.gadfly.vip/public.key | sudo gpg --dearmor -o /usr/share/keyrings/deb-repo.gpg

  # Add repository (DEB822 format, recommended for Bookworm+ / 22.04+)
  echo "Types: deb
  URIs: https://deb-repo.gadfly.vip
  Suites: stable
  Components: main
  Architectures: $(dpkg --print-architecture)
  Signed-By: /usr/share/keyrings/deb-repo.gpg" | sudo tee /etc/apt/sources.list.d/deb-repo.sources
  
  # Or use one‑line format for older releases:
  # echo "deb [arch=$(dpkg --print-architecture) signed-by=/usr/share/keyrings/deb-repo.gpg] https://deb-repo.gadfly.vip stable main" | sudo tee /  etc/apt/sources.list.d/deb-repo.list
  
  # Update and install
  sudo apt update
  sudo apt install sourcegit
  ```

* `AppImage` files can be found on [AppImage hub](https://appimage.github.io/SourceGit/), `xdg-open` (`xdg-utils`) must be installed to support open native file manager.
* Make sure [git-credential-manager](https://github.com/git-ecosystem/git-credential-manager/releases) or [git-credential-libsecret](https://pkgs.org/search/?q=git-credential-libsecret) is installed on your Linux.
* Maybe you need to set environment variable `AVALONIA_SCREEN_SCALE_FACTORS`. See https://github.com/AvaloniaUI/Avalonia/wiki/Configuring-X11-per-monitor-DPI.
* If you can NOT type accented characters, such as `ê`, `ó`, try to set the environment variable `AVALONIA_IM_MODULE` to `none`.

## Commandline arguments

Users can also launcher `SourceGit` from commandline. Usage:

```
<SOURCEGIT_EXEC> <DIR>                       // Open repository in existing `SourceGit` instance or a new one
<SOURCEGIT_EXEC> --history <FILE_OR_DIR>     // Launch `SourceGit` to see the history of a file or dir
<SOURCEGIT_EXEC> --blame <FILE_PATH>         // Launch `SourceGit` to blame a file (HEAD version only) 
```

## OpenAI

This software supports using OpenAI or other AI service that has an OpenAI compatible HTTP API to generate commit message. You need configurate the service in `Preference` window.

For `OpenAI`:

* `Server` must be `https://api.openai.com/v1`

For other AI service:

* The `Server` should fill in a URL equivalent to OpenAI's `https://api.openai.com/v1`. For example, when using `Ollama`, it should be `http://localhost:11434/v1` instead of `http://localhost:11434/api/generate`
* The `API Key` is optional that depends on the service

## External Tools

This app supports open repository in external tools listed in the table below.

| Tool                          | Windows | macOS | Linux |
|-------------------------------|---------|-------|-------|
| Visual Studio Code            | YES     | YES   | YES   |
| Visual Studio Code - Insiders | YES     | YES   | YES   |
| VSCodium                      | YES     | YES   | YES   |
| Cursor                        | YES     | YES   | YES   |
| Sublime Text                  | YES     | YES   | YES   |
| Zed                           | YES     | YES   | YES   |
| Visual Studio                 | YES     | NO    | NO    |

> [!NOTE]
> This app will try to find those tools based on some pre-defined or expected locations automatically. If you are using one portable version of these tools, it will not be detected by this app.  
> To solve this problem you can add a file named `external_editors.json` in app data storage directory and provide the path directly.  
> User can also exclude some editors by using `external_editors.json`.

The format of `external_editors.json`:
```json
{
    "tools": {
        "Visual Studio Code": "D:\\VSCode\\Code.exe"
    },
    "excludes": [
        "Visual Studio Community 2019"
    ]
}
```

> [!NOTE]
> This app also supports a lot of `JetBrains` IDEs, installing `JetBrains Toolbox` will help this app to find them.

## Conventional Commit Helper

You can define your own conventional commit types (per-repository) by following steps:

1. Create a json file with your own conventional commit type definitions. For example:
```json
[
  {
    "Name": "New Feature",
    "Type": "Feature",
    "Description": "Adding a new feature",
    "PrefillShortDesc": "this is a test"
  },
  {
    "Name": "Bug Fixes",
    "Type": "Fix",
    "Description": "Fixing a bug"
  }
]
```
2. Configure the `Conventional Commit Types` in repository configuration window.  

## Contributing

Everyone is welcome to submit a PR. Please make sure your PR is based on the latest `develop` branch and the target branch of PR is `develop`.

This project has a submodule in `depends/AvaloniaEdit` which is a custom fork of [Official AvaloniaEdit](https://github.com/AvaloniaUI/AvaloniaEdit). 
Please make sure it is initialized - enable `--recurse-submodules` option while cloning or run `git submodule update --init` after cloned.

In short, here are the commands to get started once [.NET tools are installed](https://dotnet.microsoft.com/en-us/download):

```sh
dotnet nuget add source https://api.nuget.org/v3/index.json -n nuget.org
dotnet restore
dotnet build
dotnet run --project src/SourceGit.csproj
```

Thanks to all the people who contribute.

[![Contributors](https://contrib.rocks/image?repo=sourcegit-scm/sourcegit&columns=20)](https://github.com/sourcegit-scm/sourcegit/graphs/contributors)

## Translation Status

You can find the current translation status in [TRANSLATION.md](https://github.com/sourcegit-scm/sourcegit/blob/develop/TRANSLATION.md)

### Translate Utility Script

A script that assists with translations by reading the target language, comparing it with the base language, and going through missing keys one by one, so the translator can provide the translations interactively without needing to check each key manually.

#### Usage

Check for a given language (e.g., `pt_BR`) and optionally check for missing translations:

```bash
python translate_helper.py pt_BR [--check]
```

- `pt_BR` is the target language code (change as needed), it should correspond to a file named `pt_BR.axaml` in the `src/Resources/Locales/` directory, so you can replace it with any other language code you want to translate, e.g., `de_DE`, `es_ES`, etc.
- `--check` is an optional flag used to only check for missing keys without prompting for translations, useful for getting a list of missing translations.

The script will read the base language file (`en_US.axaml`) and the target language file (e.g., `pt_BR.axaml`), identify missing keys, and prompt you to provide translations for those keys. If the `--check` flag is used, it will only list the missing keys without prompting for translations.

## Third-Party Components

For detailed license information, see [THIRD-PARTY-LICENSES.md](THIRD-PARTY-LICENSES.md).
