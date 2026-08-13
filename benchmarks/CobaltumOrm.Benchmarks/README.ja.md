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

## 2026 年 8 月 13 日の参考結果

測定環境は次のとおりです。

- 物理 10 コア、論理 10 コアの Apple M5
- macOS Tahoe 26.5.2
- .NET SDK 10.0.203、.NET 10.0.7
- BenchmarkDotNet 0.15.8
- `postgres:18-alpine` の PostgreSQL 18

実行時間は、BenchmarkDotNet が算出した算術平均と 99.9% 信頼区間の半幅です。

| 実装 | 1 件 | 10 件 | 1,000 件 |
| --- | ---: | ---: | ---: |
| CobaltumORM | 180.0 ± 3.55 μs | 156.2 ± 3.05 μs | 979.5 ± 49.03 μs |
| Dapper | 180.4 ± 3.60 μs | 155.2 ± 3.04 μs | 971.9 ± 42.85 μs |
| EF Core | 188.8 ± 3.57 μs | 176.5 ± 3.46 μs | 1,227.6 ± 89.18 μs |
| LINQ to DB | 185.3 ± 3.66 μs | 233.3 ± 11.00 μs | 1,086.7 ± 60.44 μs |
| RepoDB | 280.7 ± 30.68 μs | 218.5 ± 8.91 μs | 995.3 ± 51.28 μs |
| ADO.NET | 287.6 ± 21.43 μs | 191.0 ± 4.86 μs | 980.5 ± 52.49 μs |

1 回の処理で確保したマネージドメモリは次のとおりです。

| 実装 | 1 件 | 10 件 | 1,000 件 |
| --- | ---: | ---: | ---: |
| CobaltumORM | 2.72 KB | 7.68 KB | 578.98 KB |
| Dapper | 3.00 KB | 8.71 KB | 673.21 KB |
| EF Core | 10.23 KB | 17.50 KB | 774.42 KB |
| LINQ to DB | 8.27 KB | 14.20 KB | 595.96 KB |
| RepoDB | 3.67 KB | 8.54 KB | 580.02 KB |
| ADO.NET | 3.09 KB | 7.65 KB | 571.63 KB |

3 つの測定で、CobaltumORM と Dapper の平均実行時間の差は 1% 未満でした。CobaltumORM のマネージドメモリ確保量は Dapper より 9% から 14% 少ない結果でした。

RepoDB、EF Core、LINQ to DB、ADO.NET の一部では、測定値が複数の範囲に分かれる警告が出ています。複数の測定では外れ値も除外されています。小さな差は測定時の変動として扱い、性能を判断する場合は対象環境でも測定してください。この結果は SQL の実行と取得結果の変換を測定したものです。ビルド時間、インクリメンタルソースジェネレーターの実行時間、コンパイル時の Query 解析時間は含みません。

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
