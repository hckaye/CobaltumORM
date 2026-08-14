# CobaltumORM コーディングエージェント用ツール

[English](agent-tools.md) | 日本語

既存プロジェクトへ CobaltumORM の指示を追加するには `assistant init` を使います。ビルド前の確認には
`inspect` と `doctor` を使い、同じ解析を MCP サーバーからも利用できます。ツールは選択したビルド構成を
評価します。データベースへの接続やマイグレーションの実行は行いません。

## 既存プロジェクトのセットアップ

グローバルツールを更新します。未インストールの場合はインストールします。

```console
dotnet tool update --global CobaltumOrm.Tool --version tool-version
dotnet tool install --global CobaltumOrm.Tool --version tool-version
```

2 番目のコマンドは、ツールが未インストールの場合だけ実行します。アプリケーションプロジェクトと
マイグレーションプロジェクトがすでにある場合は、先に関連付けます。

```console
cobaltum add --project <app.csproj> --migration-project <migration.csproj>
```

`--create-migration-project` は、マイグレーションプロジェクトがまだない場合の明示的な選択肢です。
既存ファイルを置き換えずに、存在しないマイグレーションプロジェクトを作成します。

```console
cobaltum add --project <app.csproj> --migration-project <migration.csproj> \
  --create-migration-project
```

指示を初期化し、評価済みのプロジェクトと設定を確認してからビルドします。

```console
cobaltum assistant init --project <app.csproj>
cobaltum inspect --project <app.csproj> --format json
cobaltum doctor --project <app.csproj> --format json
dotnet build <app.csproj>
```

`assistant init` の既定値は `auto` です。`.cobaltum/assistant.md` を作成し、プロジェクト内にある認識済みの
アダプターファイルをすべて更新します。認識済みのアダプターファイルがない場合は `AGENTS.md` を作成します。
指示の形式を 1 つだけ管理したいときは、対象を明示的に選びます。

| 対象 | 選ぶ場面 |
| --- | --- |
| `agents` | プロジェクトで `AGENTS.md` を使う場合。 |
| `claude` | Claude Code 用に `CLAUDE.md` を使う場合。 |
| `cursor` | Cursor のルールを使う場合。 |
| `copilot` | GitHub Copilot の指示ファイルを使う場合。 |
| `all` | 対応しているすべての指示形式が必要な場合。 |

対象は `--target` で指定します。

```console
cobaltum assistant init --project <app.csproj> --target claude
```

## `assistant init` が管理するファイル

すべての実行で `.cobaltum/assistant.md` を管理します。このファイルには CobaltumORM の指示を書きます。
選択したアダプターファイルには、コーディングエージェントにこのファイルを読むよう指示する短い内容を
書きます。

| 対象の選択 | 作成または更新するアダプターファイル |
| --- | --- |
| `auto` | 認識済みの既存アダプターすべて。存在しない場合は `AGENTS.md` |
| `agents` | `AGENTS.md` |
| `claude` | `CLAUDE.md` |
| `cursor` | `.cursor/rules/cobaltum.mdc` |
| `copilot` | `.github/copilot-instructions.md` |
| `all` | `AGENTS.md`、`CLAUDE.md`、`.cursor/rules/cobaltum.mdc`、`.github/copilot-instructions.md` |

コマンドが管理するのは CobaltumORM の管理ブロックだけです。管理ブロックの外にあるユーザーの内容は
保持します。管理ブロックがない既存の `AGENTS.md`、`CLAUDE.md`、`.github/copilot-instructions.md` には、
ブロックを追加します。`.cobaltum/assistant.md` または `.cursor/rules/cobaltum.mdc` に認識できない既存
ファイルがある場合は、上書きを拒否します。

ディレクトリの作成やファイルの書き込みの前に、選択したすべての対象を計画して検証します。同じコマンドを
再実行した場合、管理対象のファイルは変わらず、変更なしとして報告します。このコマンドがそれ以上の
トランザクション保証を提供するわけではありません。

## プロジェクトレポート

`inspect` はアプリケーションまたはクエリプロジェクトを評価し、生成したソースファイルを公開せずに
CobaltumORM の解析を実行します。JSON 出力には、選択したプロジェクトパス、対象フレームワーク、構成、
名前空間、プロバイダー、評価した入力パスと参照、生成物のメタデータ、解析済みと処理済みのソース、
診断情報が含まれます。生成が成功した場合は終了コード 0、生成がエラーを報告した場合は終了コード 1 です。

`doctor` は同じプロジェクトを評価し、プロジェクトの状態、チェック、生成時の診断情報を返します。チェック
対象は、対象フレームワーク、CobaltumORM の設定、データベースプロバイダー、生成先の名前空間、
マイグレーション入力、生成時の診断情報です。全体の状態が `error` の場合だけ終了コード 1 になります。
`ok` と `warning` の終了コードは 0 です。無効なコマンドオプションは通常の CLI 使用方法エラーとして扱われ、
終了コード 2 になります。

どちらのコマンドも、データベースへのアクセス、マイグレーションの実行、生成ファイルの公開を行いません。
既定では、プロジェクトの評価前にパッケージを復元します。この復元によって通常の `obj` 以下のファイルが
更新されることがあります。プロジェクトがすでに復元済みで、復元を行わない場合は `--no-restore` を指定します。

## MCP サーバーのセットアップ

絶対パスのアプリケーションプロジェクトを指定して stdio サーバーを起動します。

```console
cobaltum mcp --project <absolute-app.csproj>
```

MCP クライアントは別の作業ディレクトリからサーバーを起動することがあるため、絶対パスを使います。
プロジェクトはサーバーの起動時に選択されます。

### Codex

次の構文は `codex mcp add --help` で確認しました。

```console
codex mcp add cobaltum -- cobaltum mcp --project /absolute/path/App.csproj
```

### Claude Code

```console
claude mcp add cobaltum -- cobaltum mcp --project /absolute/path/App.csproj
```

[Claude Code の MCP ドキュメント](https://docs.anthropic.com/en/docs/claude-code/mcp)を参照してください。

### Visual Studio Code と GitHub Copilot

`.vscode/mcp.json` にサーバー定義を作成します。

```json
{
  "servers": {
    "cobaltum": {
      "command": "cobaltum",
      "args": ["mcp", "--project", "/absolute/path/App.csproj"]
    }
  }
}
```

[GitHub Copilot の MCP 設定手順](https://docs.github.com/en/copilot/how-tos/provide-context/use-mcp-in-your-ide/extend-copilot-chat-with-mcp)に従ってください。

### その他の stdio クライアント

クライアントには次のコマンドと引数の組を設定します。

```text
command: cobaltum
args: mcp --project /absolute/path/App.csproj
```

## MCP のツールとリソース

サーバーは次の 5 つのツールを公開します。

| ツール | 結果 |
| --- | --- |
| `inspect_project` | プロジェクトと生成物のメタデータ、診断情報。 |
| `doctor_project` | プロジェクトのチェックと生成時の診断情報。 |
| `list_generated_artifacts` | メモリ上にある生成物のメタデータ。 |
| `read_generated_artifact` | `list_generated_artifacts` が返した 1 つの生成物のソースとメタデータ。 |
| `explain_diagnostic` | 対応している診断コードの英語または日本語のドキュメント節。 |

公開するリソースは次の 7 つです。

- `cobaltum://docs/quick-reference/en`
- `cobaltum://docs/quick-reference/ja`
- `cobaltum://docs/recipes/en`
- `cobaltum://docs/recipes/ja`
- `cobaltum://docs/diagnostics/en`
- `cobaltum://docs/diagnostics/ja`
- `cobaltum://docs/llms.txt`

5 つのツールにはすべて読み取り専用と closed-world の注釈があります。データベースへの接続、
マイグレーションの実行、生成ファイルの公開は行いません。プロジェクトパスはサーバー起動時にだけ受け取る
ため、ツール呼び出しで任意のプロジェクトパスを渡すことはできません。`read_generated_artifact` はパスではなく、
`list_generated_artifacts` が返した完全一致の生成物名だけを受け取ります。

リソースはインストール済みのツールに埋め込まれており、参照資料を決まった内容でローカル取得できます。
初期導入では、外部のベクトルデータベースや RAG サービスは必要ありません。

MCP のプロジェクト評価でも、`inspect` と `doctor` と同じ復元処理を行います。サーバーを
`--no-restore` なしで起動すると、MSBuild の復元によって通常の `obj` 以下のファイルが更新されることが
あります。

## エージェントの作業手順

1. `inspect_project` を呼び出し、返された診断情報に対応します。
2. 対応している診断コードには、必要な言語を指定して `explain_diagnostic` を呼び出します。
3. `list_generated_artifacts` を呼び出し、生成ソースを確認する場合は返された完全一致の名前で `read_generated_artifact` を呼び出します。
4. `doctor_project` を呼び出し、`error` のチェックを解消します。
5. `dotnet build <app.csproj>` を実行します。
