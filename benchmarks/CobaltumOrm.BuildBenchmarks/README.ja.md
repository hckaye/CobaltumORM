# CobaltumORM のビルド時間ベンチマーク

[English](README.md) | 日本語

`[Query]`、`Query(...)` 呼び出し、SQL マイグレーションを大量に含むプロジェクトを生成し、ビルド時間を測ります。同じソースから、CobaltumORM の解析とコード生成を無効にした比較用プロジェクトも生成します。

## 実行

.NET 10 SDK が必要です。データベースと Docker は使いません。

リポジトリのルートで次のコマンドを実行します。

```console
dotnet run --project benchmarks/CobaltumOrm.BuildBenchmarks -c Release -- --profile all --runs 3
```

`--profile` には `small`、`medium`、`large`、`all` を指定できます。複数の `--profile` も指定できます。`--runs` は、ウォームアップ後に記録する回数です。

生成したプロジェクトは一時ディレクトリに置かれ、正常終了時に削除されます。内容を確認する場合は `--keep` を指定します。生成先の親ディレクトリは `--work-directory <path>` で変更できます。

## 負荷

| プロファイル | `[Query]` | `Query(...)` メソッド | テーブル | マイグレーションの SQL 文 | マイグレーションの行数 |
| --- | ---: | ---: | ---: | ---: | ---: |
| `small` | 100 | 100 | 25 | 125 | 375 |
| `medium` | 500 | 500 | 100 | 500 | 1,500 |
| `large` | 1,000 | 1,000 | 200 | 1,000 | 3,000 |

名前付きクエリは 1 クラスあたり 25 件、`Query(...)` 呼び出しは 1 ファイルあたり 100 メソッドに分けています。各クエリは 2 列を取得し、`id` をパラメーターとして使います。

マイグレーションは 1 個の Flyway 形式 SQL ファイルです。各テーブルを 7 列で作成した後、4 回の `ALTER TABLE` で列を追加します。このファイルから、すべてのクエリを検査するためのスキーマと C# のテーブル型が生成されます。

比較用の `plain` プロジェクトにも同じ C# ファイルと SQL ファイルを含めます。CobaltumORM のライブラリは参照しますが、コンパイル時の解析、コード生成、ソース変換は実行しません。`cobaltum` プロジェクトは通常の Source Generator と `CobaltumOrmTransformTask` を実行します。

## 測定方法

各プロジェクトで次の 3 種類を測ります。

| 表示名 | 測る処理 |
| --- | --- |
| `clean` | 計測前にプロジェクトを `dotnet clean` し、すべての出力を作り直す |
| `no-change` | 完了済みのビルドに対し、ファイルを変更せずにもう一度ビルドする |
| `one-file-change` | クエリを含まない C# ファイルを 1 個変更してビルドする |

依存プロジェクトのビルド、NuGet restore、`dotnet clean` は計測時間に含めません。計測対象の `dotnet build` には `--no-restore --no-dependencies` を指定します。MSBuild によるプロジェクト参照の評価は計測時間に含まれます。MSBuild と C# コンパイラーのサーバー設定は SDK の既定値です。

各条件を 1 回実行してウォームアップしてから、指定回数を測り、中央値、最小値、最大値を出力します。

## 2026-08-13 の測定結果

測定環境は Apple M5 10 コア、メモリ 32 GB の MacBook Air、macOS 26.5.2、.NET SDK 10.0.203 です。各条件を 3 回記録しました。単位はミリ秒です。

| プロファイル | 構成 | ビルド | 中央値 | 最小値 | 最大値 |
| --- | --- | --- | ---: | ---: | ---: |
| `small` | `plain` | `clean` | 1,155 | 977 | 3,177 |
| `small` | `plain` | `no-change` | 1,060 | 1,048 | 1,063 |
| `small` | `plain` | `one-file-change` | 1,231 | 1,067 | 1,240 |
| `small` | `cobaltum` | `clean` | 2,086 | 2,072 | 2,787 |
| `small` | `cobaltum` | `no-change` | 1,483 | 1,478 | 1,619 |
| `small` | `cobaltum` | `one-file-change` | 1,942 | 1,899 | 2,016 |
| `medium` | `plain` | `clean` | 953 | 949 | 955 |
| `medium` | `plain` | `no-change` | 885 | 855 | 925 |
| `medium` | `plain` | `one-file-change` | 980 | 957 | 1,059 |
| `medium` | `cobaltum` | `clean` | 9,250 | 8,804 | 9,570 |
| `medium` | `cobaltum` | `no-change` | 3,576 | 2,783 | 3,942 |
| `medium` | `cobaltum` | `one-file-change` | 5,743 | 4,957 | 6,881 |
| `large` | `plain` | `clean` | 1,253 | 1,136 | 1,407 |
| `large` | `plain` | `no-change` | 1,149 | 1,086 | 2,580 |
| `large` | `plain` | `one-file-change` | 1,716 | 1,692 | 1,909 |
| `large` | `cobaltum` | `clean` | 14,479 | 12,400 | 18,985 |
| `large` | `cobaltum` | `no-change` | 12,775 | 9,364 | 13,054 |
| `large` | `cobaltum` | `one-file-change` | 13,212 | 12,705 | 13,889 |

比較用プロジェクトとの差は次のとおりです。

| プロファイル | `clean` | `no-change` | `one-file-change` |
| --- | ---: | ---: | ---: |
| `small` | +931 ms | +423 ms | +711 ms |
| `medium` | +8,296 ms | +2,691 ms | +4,764 ms |
| `large` | +13,225 ms | +11,627 ms | +11,496 ms |

100 件ずつの規模では追加時間は 1 秒未満でした。500 件ずつでは 2.7 秒から 8.3 秒、1,000 件ずつでは 11.5 秒から 13.2 秒増えています。

`large` の変更なしビルドは 12.8 秒で、clean build の 14.5 秒と近い値でした。現在の `CobaltumOrmTransformSources` には、入力が変わっていないときに処理全体を省略する MSBuild の入出力指定がありません。そのため、変更なしでもマイグレーションとソースを再解析します。また、C# ファイルを 1 個変更すると Source Generator の入力であるコンパイル全体が変わるため、すべてのスキーマとクエリが再解析されます。
