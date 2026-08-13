# CobaltumORM の ORM ベンチマーク

[English](README.md) | 日本語

PostgreSQL に接続し、CobaltumORM、Dapper、EF Core、LINQ to DB、RepoDB の読み取り性能を BenchmarkDotNet で比較します。ADO.NET の手書き実装を基準値として併記します。

## 必要なもの

- .NET 10 SDK
- Docker API と互換性のあるコンテナ実行環境。Docker Desktop などを利用できます。

既定では Testcontainers が `postgres:18-alpine` を取得して起動します。ホスト側の PostgreSQL は不要です。

## 実行

リポジトリのルートで次のコマンドを実行します。

```console
dotnet run --project benchmarks/CobaltumOrm.Benchmarks -c Release
```

初回は NuGet パッケージと PostgreSQL イメージを取得します。Testcontainers が PostgreSQL を起動し、10,000 件のデータを作成してから測定を始めます。測定終了時にコンテナを停止します。

1 件取得だけを実行する場合は、フィルターを指定します。

```console
dotnet run --project benchmarks/CobaltumOrm.Benchmarks -c Release -- --filter '*SingleRowBenchmarks*'
```

短時間の確認には BenchmarkDotNet の Short ジョブを使えます。正式な比較結果には既定のジョブを使ってください。

```console
dotnet run --project benchmarks/CobaltumOrm.Benchmarks -c Release -- --job short --filter '*SingleRowBenchmarks*'
```

利用できるベンチマークの一覧は、PostgreSQL を起動せずに表示できます。

```console
dotnet run --project benchmarks/CobaltumOrm.Benchmarks -c Release -- --list flat
```

結果は、コマンドを実行したディレクトリの `BenchmarkDotNet.Artifacts/results` に出力されます。

## 測定内容

| クラス | 処理 | パラメーター |
| --- | --- | --- |
| `SingleRowBenchmarks` | 主キーを指定して 1 件を取得し、6 列を `BenchmarkPost` に変換する | `id = 5000` |
| `MultipleRowsBenchmarks` | 主キー順に複数件を取得し、6 列を `BenchmarkPost` に変換する | 10 件、1,000 件 |

比較条件は次のとおりです。

- すべて非同期 API を使い、結果をメモリ上へすべて読み込みます。
- PostgreSQL の起動、テーブル作成、10,000 件のデータ投入、接続開始は測定時間に含めません。
- CobaltumORM は `[Query<T>]` から生成した名前付きクエリを使います。
- Dapper と RepoDB はパラメーター付き SQL を使います。
- EF Core と LINQ to DB は、同じ列、絞り込み、並び順になる LINQ クエリを使います。
- EF Core の変更追跡は無効にします。
- ADO.NET は `NpgsqlCommand` と `NpgsqlDataReader` で同じ処理を手書きした基準値です。
- BenchmarkDotNet は実行時間とメモリ割り当てを記録します。

PostgreSQL の往復時間も測定値に含まれます。特に 1 件取得では、ORM 内部の処理時間より PostgreSQL との通信時間が大きくなる場合があります。異なるマシンや負荷状況で得た数値は直接比較しないでください。

## 既存の PostgreSQL を使う

`COBALTUM_BENCHMARK_CONNECTION_STRING` を設定すると、Docker コンテナを起動せず、指定した PostgreSQL に接続します。

```console
export COBALTUM_BENCHMARK_CONNECTION_STRING='Host=localhost;Port=5432;Database=cobaltum_benchmarks;Username=postgres;Password=postgres'
dotnet run --project benchmarks/CobaltumOrm.Benchmarks -c Release
```

指定したデータベースでは、測定を始める前に `cobaltum_benchmark_posts` テーブルを削除して作り直します。ベンチマーク専用のデータベースを指定してください。

Docker イメージだけを変更する場合は `COBALTUM_BENCHMARK_POSTGRES_IMAGE` を設定します。

```console
export COBALTUM_BENCHMARK_POSTGRES_IMAGE='postgres:18-alpine'
```

## ビルドだけ行う

次のコマンドはコンテナを起動せず、ベンチマークプロジェクトをコンパイルします。

```console
dotnet build benchmarks/CobaltumOrm.Benchmarks/CobaltumOrm.Benchmarks.csproj -c Release
```
