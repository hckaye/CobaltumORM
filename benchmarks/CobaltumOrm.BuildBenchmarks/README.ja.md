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

測定環境は Apple M5 10 コア、メモリ 32 GB の MacBook Air、macOS 26.5.2、.NET SDK 10.0.203 です。単位はミリ秒です。

### 最適化前

MSBuild の変更なしスキップと解析キャッシュを追加する前の基準値です。各条件を 3 回記録しました。

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

### 最適化後

MSBuild の変更なしスキップと解析キャッシュを追加した後に、各条件を 5 回記録しました。

| プロファイル | 構成 | ビルド | 中央値 | 最小値 | 最大値 |
| --- | --- | --- | ---: | ---: | ---: |
| `small` | `plain` | `clean` | 958 | 881 | 1,221 |
| `small` | `plain` | `no-change` | 830 | 757 | 862 |
| `small` | `plain` | `one-file-change` | 814 | 812 | 933 |
| `small` | `cobaltum` | `clean` | 1,511 | 1,354 | 1,590 |
| `small` | `cobaltum` | `no-change` | 899 | 785 | 963 |
| `small` | `cobaltum` | `one-file-change` | 1,746 | 1,684 | 1,941 |
| `medium` | `plain` | `clean` | 1,121 | 928 | 1,268 |
| `medium` | `plain` | `no-change` | 1,019 | 894 | 1,268 |
| `medium` | `plain` | `one-file-change` | 1,323 | 1,054 | 2,363 |
| `medium` | `cobaltum` | `clean` | 5,540 | 3,486 | 17,553 |
| `medium` | `cobaltum` | `no-change` | 762 | 708 | 826 |
| `medium` | `cobaltum` | `one-file-change` | 3,305 | 3,109 | 3,450 |
| `large` | `plain` | `clean` | 1,360 | 1,198 | 1,771 |
| `large` | `plain` | `no-change` | 3,424 | 1,095 | 3,855 |
| `large` | `plain` | `one-file-change` | 1,260 | 1,185 | 1,774 |
| `large` | `cobaltum` | `clean` | 11,920 | 7,699 | 18,469 |
| `large` | `cobaltum` | `no-change` | 1,093 | 995 | 1,179 |
| `large` | `cobaltum` | `one-file-change` | 8,475 | 7,086 | 11,129 |

最適化前後の `cobaltum` の中央値を比較すると、次のように短縮しました。

| プロファイル | ビルド | 最適化前 | 最適化後 | 短縮 |
| --- | --- | ---: | ---: | ---: |
| `small` | `clean` | 2,086 | 1,511 | 28% |
| `small` | `no-change` | 1,483 | 899 | 39% |
| `small` | `one-file-change` | 1,942 | 1,746 | 10% |
| `medium` | `clean` | 9,250 | 5,540 | 40% |
| `medium` | `no-change` | 3,576 | 762 | 79% |
| `medium` | `one-file-change` | 5,743 | 3,305 | 42% |
| `large` | `clean` | 14,479 | 11,920 | 18% |
| `large` | `no-change` | 12,775 | 1,093 | 91% |
| `large` | `one-file-change` | 13,212 | 8,475 | 36% |

`plain` の `no-change` には他の条件より大きな振れがあったため、最適化前後の比較には `cobaltum` の絶対時間を使っています。

正常にビルドした後の変更なしビルドでは、ソース、マイグレーション、SQL、参照、タスクアセンブリ、動作を変える設定を順序付きの読みやすいマニフェストと比較します。入力が変わらず、生成したファイルがすべて残っていれば、MSBuild は重い変換処理を省略し、記録済みの `Compile` 項目を復元します。入力の追加、削除、編集、プロバイダーや生成名前空間の変更、参照やタスクアセンブリの変更、出力ファイルの削除があると、変換を再実行します。クエリ以外の C# ファイルの編集も、定数、結果型、マイグレーション、名前解決がプロジェクト全体のクエリに影響するため、変換を無効にします。

最終スキーマと正常に完了した SQL 解析は `obj` にキャッシュします。入力から求めたキャッシュキーが同じ場合は、マイグレーションの再適用、SQL のパース、名前と型の解決を省略します。生成された C# はキャッシュしません。C# のコンパイル、シンボルの収集、結果型の割り当て、コード生成は必要に応じて実行します。

1,000 件ずつの `no-change` は 1.1 秒で、通常の反復ビルドが規模に比例して十数秒まで伸びる問題は解消しました。一方、`clean` は 11.9 秒、クエリを含まない C# ファイルの変更でも 8.5 秒かかります。大規模なアプリケーションでは、`[Query]`、`Query(...)`、マイグレーション参照を専用の Query ライブラリにまとめ、アプリケーションや UI の変更でそのプロジェクトを再ビルドしない構成が適しています。スキーマの変更頻度が低い場合や Source Generator を使えない場合は、ルート README の `cobaltum generate` を使った明示的な生成も選べます。既定は引き続き Source Generator です。
