# CobaltumORM クイックリファレンス

[English](quick-reference.md) | 日本語

下の表からクエリ API を選んでください。生成される型の名前、プロバイダー設定、必要な確認コマンドが
続きます。詳細な説明は [README.ja.md](../../README.ja.md)、作業単位の実例は [recipes.ja.md](recipes.ja.md)、
ビルドエラーは [diagnostics.ja.md](diagnostics.ja.md) にあります。

## CobaltumORM の役割

SQL は手で書きます。ビルドがそれを解析し、マイグレーションから組み立てたスキーマと照合して、実行
用の C# を生成します。ビルド中にデータベースへは接続しません。変更追跡と `SaveChanges` に相当する
仕組みはなく、クエリと更新処理はすべて明示的に実行します。

## クエリ API の選択

| 場面 | API | 結果型 |
| --- | --- | --- |
| SQL がコンパイル時定数、または値だけを埋め込んだ補間文字列の場合 | `connection.Query(sql)` | 呼び出しごとに生成される `record` |
| 既存の型へ行をマッピングする場合 | `connection.Query<T>(sql)` | `T` |
| 同じ SQL を複数箇所で使う場合 | `partial class` への `[Query("Name", sql)]` | 生成される `NameResult` |
| 名前付きクエリを既存の型へマッピングする場合 | `[Query<T>("Name", sql)]` | `T` |
| SQL の文字列が実行時にしか決まらない、または解析対象外の構文を使う場合 | `connection.NoCheckQuery(sql)` | `CobaltumRawRow` |
| 検査しない SQL を既存の型へマッピングする場合 | `connection.NoCheckQuery<T>(sql)` | `T` |
| 1 つのテーブルを任意の条件で絞り込む場合 | `Tables.<Table>.Query().Where(...)` | 生成されるテーブルの `record` |

`QueryDynamic(sql)` は `NoCheckQuery(sql)` の旧名です。新しいコードでは `NoCheckQuery` を使います。

## ビルドが強制する規則

- `Query(sql)` はコンパイル時定数の文字列か補間文字列を受け取ります。定数でない文字列は
  [COB007](diagnostics.ja.md#cob007) または [COB100](diagnostics.ja.md#cob100) で拒否されます。
- 補間の穴は値だけを表します。`$"SELECT {columns} FROM t"` は拒否されます。各穴は
  `@__cobaltum_value_0` から始まる名前の `DbParameter` になります。
- 補間文字列の `INSERT`、`UPDATE`、`DELETE` は `Query` では受け付けません。固定 SQL と
  `WithParameter` を使います。
- 行を返す検査対象の `Query` に書ける文はちょうど 1 つです。
- `Query` の中の DDL は拒否されます。スキーマ変更はマイグレーションに書きます。
- `WithParameter` に渡した名前と型は、解析した SQL と照合されます。
- `Query<T>` は返る列名、CLR 型、null 許容性を `T` のコンストラクターまたは書き込み可能なメンバーと
  照合します。名前は大文字小文字と区切り文字の違いを無視して照合するため、`display_name` は
  `DisplayName` に一致します。`[ResultColumn("name")]` で列名を明示できます。
- `NoCheckQuery<T>` は `T` の構造だけを検査し、SQL とは照合できません。不一致はデータリーダーが行を
  読む時点で例外になります。

## マイグレーション

- C# マイグレーションは `Migration` を継承し、`[Migration(version)]` または
  `[Migration(version, description)]` を付けます。バージョンは正の 64 ビット整数です。
- `Up` がクエリ検査に使うスキーマを定義します。`Down` は実行時のロールバックに使います。
- スキーマに影響する引数はすべてコンパイル時定数である必要があります。名前、長さ、精度、位取り、
  既定値のリテラル、`Execute.Sql` に渡す SQL はビルド時に読み取られます。
- `Up` の中の `if`、ループ、ヘルパーメソッド呼び出しは [COB001](diagnostics.ja.md#cob001) で拒否
  されます。
- `Execute.Script`、`Execute.EmbeddedScript`、`Execute.WithConnection`、`IfDatabase` の述語を取る
  オーバーロード、`IfDatabase.Delegate` はビルド時に評価できません。スクリプトでスキーマを変える
  必要がある場合は、Flyway 互換の SQL ファイルを `AdditionalFiles` に追加します。
- Flyway 互換 SQL のファイル名は `V<version>__<description>.sql` です。ロールバック用の SQL を持た
  ないため、これを含むロールバックは開始前に拒否されます。
- C# マイグレーションと SQL ファイルで同じバージョンは使えません。どちらもバージョンの昇順で適用
  されます。

## 生成される API

生成された型は `CobaltumOrmGeneratedNamespace` に置かれます。既定値は `CobaltumOrm.Generated` です。

| 生成される名前 | 内容 |
| --- | --- |
| `SqlSchema.Schemas.<Schema>` | 引用符付きのスキーマ名の `const string` |
| `SqlSchema.Tables.<Table>.Name` | スキーマ修飾したテーブル名 |
| `SqlSchema.Tables.<Table>.Columns.<Column>` | 引用符付きの列名 |
| `<Table>Row` | テーブル 1 行を表す `public sealed record` |
| `Tables.<Table>` | `Query()`、`All()`、`Where(...)` を持つテーブルオブジェクト |
| `Tables.<Table>.<Column>` | `Equal(value)` を持つ型付きの列 |
| `<Container>.<Name>Result` | 名前付きクエリの結果 `record` |
| `<Container>.<Name>Parameters` | 名前付きクエリのパラメーター `record` |
| `<Container>.<Name>` | `CobaltumQueryDefinition<TParameters, TResult>` |
| `<Container>.<Name>Async` | 接続と各パラメーターを受け取る非同期メソッド |
| `CobaltumMigrationCatalog.All` | 順序どおりのマイグレーション一覧。実行時のアセンブリ走査は不要 |

`SqlSchema` には現在のスキーマに存在する名前だけが入ります。マイグレーションで列名を変えると古い
メンバーは生成されなくなるため、その名前を使う SQL はコンパイルエラーになります。

## プロバイダー

| データベース | `CobaltumOrmDatabaseProvider` | ドライバーパッケージ | マイグレーションアダプター |
| --- | --- | --- | --- |
| PostgreSQL | `PostgreSql` | `Npgsql` | `PostgreSqlMigrationAdapter` |
| MySQL | `MySql` | `MySqlConnector` | `MySqlMigrationAdapter` |
| SQLite | `Sqlite` | `Microsoft.Data.Sqlite` | `SqliteMigrationAdapter` |
| SQL Server | `SqlServer` | `Microsoft.Data.SqlClient` | `SqlServerMigrationAdapter` |
| Oracle | `Oracle` | `Oracle.ManagedDataAccess.Core` | `OracleMigrationAdapter` |

既定値は `PostgreSql` です。ビルドから読み取れるように `CompilerVisibleProperty` に列挙する必要が
あります。未知の値は [COB008](diagnostics.ja.md#cob008) になります。PostgreSQL が主な対象で、解析の
対応範囲が最も広いです。Oracle の PL/SQL ブロックは解析しません。

## ビルドによる確認は必須

SQL の検査はコンパイル中に実行されるため、ビルドしていないコードは未検証です。クエリ、マイグレー
ション、結果型、プロバイダー設定のいずれかを変えたら、ビルドして出力を確認します。

```console
dotnet build
dotnet test
```

`COB` で始まるコードのビルドエラーは CobaltumORM が報告したものです。
[diagnostics.ja.md](diagnostics.ja.md) で内容を確認してください。Roslyn は同じ URL を診断のヘルプ
リンクとして報告します。

## 検査の範囲外

解析器が対象とするのは PostgreSQL の CRUD で使う範囲です。権限、制約、トリガー、実データに依存する
成否は検査しません。`MERGE`、`GROUPING SETS`、`CUBE`、`ROLLUP`、配列のスライス、多次元配列、`unnest`
と `generate_subscripts` 以外のテーブル関数、ユーザー定義関数の結果型は対応範囲外です。これらには
`NoCheckQuery` を使います。
