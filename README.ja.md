# CobaltumORM

[English](README.md) | 日本語

[![NuGet](https://img.shields.io/nuget/v/CobaltumOrm.svg)](https://www.nuget.org/packages/CobaltumOrm)
[![NuGet](https://img.shields.io/nuget/v/CobaltumOrm.Migrations.svg)](https://www.nuget.org/packages/CobaltumOrm.Migrations)
[![NuGet](https://img.shields.io/nuget/v/CobaltumOrm.Migrations.PostgreSql.svg)](https://www.nuget.org/packages/CobaltumOrm.Migrations.PostgreSql)
[![NuGet](https://img.shields.io/nuget/v/CobaltumOrm.SourceGenerator.svg)](https://www.nuget.org/packages/CobaltumOrm.SourceGenerator)
[![NuGet](https://img.shields.io/nuget/v/CobaltumOrm.Tool.svg)](https://www.nuget.org/packages/CobaltumOrm.Tool)
[![NuGet](https://img.shields.io/nuget/v/CobaltumOrm.Templates.svg)](https://www.nuget.org/packages/CobaltumOrm.Templates)

CobaltumORM は PostgreSQL を主な対象とする .NET / C# 向け ORM です。マイグレーションとコンパイル時の SQL 解析は、PostgreSQL、MySQL、SQLite、SQL Server、Oracle に対応しています。

## 目次

- [特徴](#特徴)
- [データベースプロバイダー](#データベースプロバイダー)
- [Getting Started](#getting-started)
- [プロジェクト設定](#プロジェクト設定)
- [マイグレーションの入力](#マイグレーションの入力)
- [CLI によるマイグレーション管理](#cli-によるマイグレーション管理)
- [生成されるテーブル型](#生成されるテーブル型)
- [名前付きクエリ](#名前付きクエリ)
- [内容がコンパイル時に決まる `Query` の結果型](#内容がコンパイル時に決まる-query-の結果型)
- [補間文字列を使う `Query`](#補間文字列を使う-query)
- [固定 SQL の `INSERT`、`UPDATE`、`DELETE`](#固定-sql-の-insertupdatedelete)
- [コンパイル時の SQL 検査を行わない `NoCheckQuery`](#コンパイル時の-sql-検査を行わない-nocheckquery)
- [接続と実行時の動作](#接続と実行時の動作)
- [trimmed publish と Native AOT](#trimmed-publish-と-native-aot)
- [対応環境](#対応環境)

## 特徴

CobaltumORM は、SQL を明示的に書きながら、型安全なデータ変換とマイグレーションを利用できる ORM です。
検索結果を受け取る型を自動生成し、スキーマ名、テーブル名、列名、SQL の誤りをコンパイル時に検出します。

- C# マイグレーションと Flyway 互換 SQL から、データベースへ接続せずにビルド時点のスキーマを組み立てます。
- `Query("...")` と `[Query(...)]` の SQL をビルド時に解析します。対応している SQL では、文法とスキーマ、テーブル、列の存在を検査します。
- 結果行を返す文の列名、C# 型、null 許容の有無から、クエリごとに結果型を生成します。`SELECT` に加え、`RETURNING` を持つ PostgreSQL の `INSERT`、`UPDATE`、`DELETE` が対象です。
- マイグレーションで名前を変更または削除すると、古い `SqlSchema` 参照と古い名前を含む SQL はコンパイルエラーになります。現在のスキーマでは実行できない SQL をビルド時に検出します。
  - 現在の検査対象は、PostgreSQL の CRUD 操作に関わる一部の構文です。権限、制約、トリガー、実データに依存する成否はビルド時には検査できません。
- EF Core の変更追跡や `SaveChanges` に相当する API は提供しません。クエリと更新処理は明示的に実行します。

### 主要な他ORMとの比較

| ORM | 主な使い方 | クエリと結果型 |
| --- | --- | --- |
| CobaltumORM | SQL を `Query` または `[Query]` で定義する | マイグレーションから得たスキーマに対して SQL をビルド時に検査し、結果行を返す文ごとの `record` 型を生成する |
| [EF Core](https://learn.microsoft.com/en-us/ef/core/) | `DbContext`、エンティティ、LINQ、変更追跡を使う。SQL を直接実行する API もある | LINQ の射影や、モデルに含まれるエンティティ、キーレスエンティティ、単一の値を表す型を使う |
| [Dapper](https://github.com/DapperLib/Dapper) | `DbConnection` の `Query<T>` や `Execute` に SQL とパラメーターを渡す | `Query<T>` で指定した型へ変換するか、`Query` で実行時に列が決まる行を返す |

## データベースプロバイダー

主な対象は PostgreSQL です。マイグレーションプロジェクトの作成では、次の 5 つのプロバイダー名を使えます。

| データベース | `--provider` のプロバイダー名 | マイグレーションパッケージ | ADO.NET ドライバーパッケージ | 接続型 | マイグレーションアダプター |
| --- | --- | --- | --- | --- | --- |
| PostgreSQL | `PostgreSql` | `CobaltumOrm.Migrations.PostgreSql` | `Npgsql` | `NpgsqlConnection` | `PostgreSqlMigrationAdapter` |
| MySQL | `MySql` | `CobaltumOrm.Migrations.MySql` | `MySqlConnector` | `MySqlConnection` | `MySqlMigrationAdapter` |
| SQLite | `Sqlite` | `CobaltumOrm.Migrations.Sqlite` | `Microsoft.Data.Sqlite` | `SqliteConnection` | `SqliteMigrationAdapter` |
| SQL Server | `SqlServer` | `CobaltumOrm.Migrations.SqlServer` | `Microsoft.Data.SqlClient` | `SqlConnection` | `SqlServerMigrationAdapter` |
| Oracle | `Oracle` | `CobaltumOrm.Migrations.Oracle` | `Oracle.ManagedDataAccess.Core` | `OracleConnection` | `OracleMigrationAdapter` |

`cobaltum migrations init` と `dotnet new cobaltum-migrations` の既定値は `PostgreSql` です。生成プロジェクトには、選択したデータベースのマイグレーションパッケージ、ドライバーパッケージ、接続用 using、アダプターだけが入ります。`CobaltumOrmDatabaseProvider` を設定し、そのプロパティをコンパイラーから参照できるようにします。

別のデータベースを使うときは、表にある名前を指定します。

```console
cobaltum migrations init MyApp.Database --provider PostgreSql
cobaltum migrations init MyApp.Database --provider MySql
cobaltum migrations init MyApp.Database --provider Sqlite
cobaltum migrations init MyApp.Database --provider SqlServer
cobaltum migrations init MyApp.Database --provider Oracle
```

手動でマイグレーションプロジェクトを定義する場合は、表から 1 行を選び、対応するパッケージ参照とエントリポイントを使います。次の PostgreSQL の例にあるプロジェクト設定を使い、データベースごとのパッケージと接続部分を、選択したデータベースの行に置き換えてください。

```xml
<PropertyGroup>
  <CobaltumOrmDatabaseProvider>PostgreSql</CobaltumOrmDatabaseProvider>
</PropertyGroup>

<ItemGroup>
  <PackageReference Include="CobaltumOrm.Migrations.PostgreSql" Version="postgresql-adapter-version" />
  <PackageReference Include="Npgsql" Version="npgsql-version" />
  <CompilerVisibleProperty Include="CobaltumOrmDatabaseProvider" />
</ItemGroup>
```

生成されるエントリポイントも、表にある接続型とアダプターを使います。PostgreSQL の例は次のとおりです。

```csharp
using CobaltumOrm.Migrations.PostgreSql;
using Npgsql;

public override DbConnection CreateConnection(MigrationProjectContext context) =>
    new NpgsqlConnection(context.ConnectionString);

public override IMigrationDatabaseAdapter CreateAdapter() =>
    new PostgreSqlMigrationAdapter();
```

MySQL、SQLite、SQL Server、Oracle の開発リソースは限られています。これらは主な対象ではありません。問題を見つけた場合は Issue を作成し、修正、テスト、ドキュメントのいずれかを提供してください。

現在確認できている範囲は次のとおりです。

- 既存の PostgreSQL E2E テスト
- 実際のインメモリ SQLite テスト
- サーバーを使わない MySQL ドライバー経由のテスト
- SQL Server と Oracle の SQL / unit テスト。この変更では実サーバー E2E はありません

データベースごとの制限は次のとおりです。

- SQLite の `AlterColumn` にはテーブルの再構築が必要で、データベース共通の操作ではサポートしていません。
- MySQL のコンパイル時と実行時の `AlterColumn` には、型と null 許容の指定がすべて必要です。
- SQL Server の null 許容だけを変更する `AlterColumn` には、変更後の型が必要です。
- Oracle の PL/SQL ブロックはコンパイル時に解析しません。
- Oracle の `INTERVAL` 型は、共通の CLR 型解析ではサポートしていません。

## Getting Started

この例では .NET 8 SDK と、`myapp` という名前の PostgreSQL データベースを使います。`tool-version`、`runtime-version`、`migrations-version`、`generator-version`、`npgsql-version` は、使用するパッケージのバージョンに置き換えてください。

### 1. CLI をインストールする

```console
dotnet tool install --global CobaltumOrm.Tool --version tool-version
```

### 2. マイグレーションプロジェクトとアプリケーションを作る

```console
mkdir MyApp
cd MyApp

cobaltum migrations init MyApp.Database \
  --output src/MyApp.Database \
  --framework net8.0 \
  --provider PostgreSql

dotnet new console \
  --name MyApp \
  --output src/MyApp \
  --framework net8.0
```

### 3. 最初のマイグレーションを追加する

CLI から、ロールバック可能な C# マイグレーションを作成します。この例ではファイル名を固定するために `--version 1` を指定します。

```console
cobaltum migrations add "create users" \
  --version 1 \
  --project src/MyApp.Database
```

生成された `src/MyApp.Database/Migrations/1_CreateUsersMigration.cs` を次の内容に置き換えます。

```csharp
using CobaltumOrm.Migrations;

namespace MyApp.Database.Migrations;

[Migration(1, "create users")]
public sealed class CreateUsersMigration : Migration
{
    public override void Up()
    {
        Create.Table("users")
            .WithColumn("id").AsInt64().NotNullable().PrimaryKey()
            .WithColumn("display_name").AsString().Nullable();

        Execute.Sql("INSERT INTO users (id, display_name) VALUES (1, 'first user');");
    }

    public override void Down()
    {
        Delete.Table("users");
    }
}
```

CLI がクラス、属性、バージョン、空の `Up` と `Down` を生成します。ここでは `Up` に `users` テーブルの作成と初期データの追加、`Down` にテーブルの削除を記述しています。

### 4. Query プロジェクトからマイグレーションを参照する

`src/MyApp/MyApp.csproj` を次の内容に置き換えます。`CobaltumOrmMigrationProjectReference` は、SQL の検査とコード生成のために、別の実行可能プロジェクトからマイグレーションを読み取る参照です。

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net8.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <CobaltumOrmGeneratedNamespace>MyApp.Database</CobaltumOrmGeneratedNamespace>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="CobaltumOrm" Version="runtime-version" />
    <PackageReference Include="CobaltumOrm.Migrations" Version="migrations-version" />
    <PackageReference Include="CobaltumOrm.SourceGenerator"
                      Version="generator-version"
                      PrivateAssets="all" />
    <PackageReference Include="Npgsql" Version="npgsql-version" />
    <CobaltumOrmMigrationProjectReference
        Include="../MyApp.Database/MyApp.Database.csproj" />
    <CompilerVisibleProperty Include="CobaltumOrmGeneratedNamespace" />
  </ItemGroup>
</Project>
```

### 5. コンパイル時に検査する Query を書く

`src/MyApp/Program.cs` を次の内容に置き換えます。

```csharp
using CobaltumOrm;
using Npgsql;

await using var connection = new NpgsqlConnection(
    "Host=localhost;Database=myapp;Username=postgres;Password=postgres");
var rows = await UserQueries.ReadAllAsync(connection);

foreach (var row in rows)
{
    Console.WriteLine($"{row.Id}: {row.DisplayName}");
}

[Query("ReadAll", "SELECT id, display_name FROM users ORDER BY id")]
public static partial class UserQueries
{
}
```

マイグレーションプロジェクトを restore してから、Query プロジェクトをビルドします。

```console
dotnet restore src/MyApp.Database/MyApp.Database.csproj
dotnet build src/MyApp/MyApp.csproj
```

`[Query]` 属性を使うと、SQL に再利用できる名前を付けられます。ビルド時に `1_CreateUsersMigration.cs` を読み取り、`SELECT` を検査して、`UserQueries.ReadAllAsync` とその結果型を生成します。この処理では PostgreSQL へ接続しません。この短い例では、実行時の接続方法が分かるように接続文字列をコードへ直接書いています。

### 6. マイグレーションを確認して適用する

生成された `src/MyApp.Database/appsettings.json` を次のサンプル設定に置き換えます。

```json
{
  "ConnectionStrings": {
    "Cobaltum": "Host=localhost;Database=myapp;Username=postgres;Password=postgres"
  }
}
```

続いて、マイグレーションの内容を確認してから適用します。

```console
cobaltum migrations up \
  --dry-run \
  --project src/MyApp.Database

cobaltum migrations up \
  --project src/MyApp.Database
```

dry run では、データベースを変更せず、対象ファイル、SQL、適用後のスキーマを表示します。次の `up` コマンドで `users` テーブルと最初の行を作成し、マイグレーションバージョン `1` を履歴へ記録します。

### 7. Query を実行する

```console
dotnet run --project src/MyApp
```

実行結果は次のとおりです。

```text
1: first user
```

## プロジェクト設定

CobaltumORM のコンパイル時検査とコード生成は、.NET SDK 形式のプロジェクトで利用できます。必要なパッケージを参照し、Flyway 互換 SQL を `AdditionalFiles` に追加します。

### PackageReference を使う場合

各 `Version` は、実際に参照するパッケージのバージョンへ置き換えてください。パッケージごとにバージョンが異なる場合は個別の値を指定します。

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="CobaltumOrm" Version="runtime-version" />
    <PackageReference Include="CobaltumOrm.Migrations" Version="migrations-version" />
    <PackageReference Include="CobaltumOrm.Migrations.PostgreSql" Version="postgresql-adapter-version" />
    <PackageReference Include="CobaltumOrm.SourceGenerator"
                      Version="generator-version"
                      PrivateAssets="all" />
    <AdditionalFiles Include="Migrations/V*__*.sql" />
    <CompilerVisibleProperty Include="CobaltumOrmGeneratedNamespace" />
  </ItemGroup>

  <PropertyGroup>
    <CobaltumOrmGeneratedNamespace>MyApp.Database</CobaltumOrmGeneratedNamespace>
  </PropertyGroup>
</Project>
```

`CobaltumOrmGeneratedNamespace` の既定値は `CobaltumOrm.Generated` です。CobaltumORM を使うプロジェクトでは C# 9 以降を指定してください。対象の .NET に `DateOnly` / `TimeOnly` がない場合は、PostgreSQL の `date` / `time` に対応する型として `DateTime` / `TimeSpan` を生成します。

## マイグレーションの入力

### C# マイグレーション

C# マイグレーションは `Migration` を継承し、`[Migration(positiveLongVersion)]` を付けます。FluentMigrator に似たメソッドチェーンで操作を記述します。`Up` に書いた操作からビルド時のスキーマを組み立て、`Down` は実行時のロールバックに使います。

```csharp
using CobaltumOrm.Migrations;

namespace MyApp.Database;

[Migration(10, "create users")]
public sealed class CreateUsersMigration : Migration
{
    public override void Up()
    {
        Create.Table("users")
            .WithColumn("id").AsInt64().Identity().PrimaryKey()
            .WithColumn("display_name").AsString(120).Nullable()
            .InSchema("accounts");

        Alter.Table("users")
            .AddColumn("created_at").AsDateTimeOffset().NotNullable()
            .InSchema("accounts");
    }

    public override void Down()
    {
        Delete.Table("users").InSchema("accounts");
    }
}
```

現在、ビルド時に解析できる操作は次のとおりです。

- `Create.Table`、`InSchema`、`WithColumn`、`As...`、`Nullable`、`NotNullable`、`PrimaryKey`、`Identity`
- `Alter.Table`、`AddColumn`、`AlterColumn`
- `Delete.Table`、`Delete.Column(...).FromTable(...)`
- `Rename.Table(...).To(...)`、`Rename.Column(...).OnTable(...).To(...)`
- `Execute.Sql(constantSql)`

名前、長さ、精度、小数部の桁数、`Execute.Sql` の SQL にはコンパイル時定数を指定してください。`Up` の実行内容を `if`、ループ、補助メソッドで変える構成は解析できません。解析できないメソッドチェーンや制御フローはコンパイルエラーになります。

### Flyway 互換 SQL

CobaltumORM は、Flyway のバージョン管理マイグレーションと互換性のある `V<version>__<description>.sql` 形式を読み込めます。互換範囲は、ファイル名の規則とバージョン順の適用です。この形式のファイルを、CobaltumORM では Flyway 互換 SQL と呼びます。

Flyway 互換 SQL は `AdditionalFiles` に含めます。例えば `Migrations/V20__add_display_name.sql` です。バージョンには正の 64 ビット整数を使います。C# マイグレーションと Flyway 互換 SQL で同じバージョンは使えません。説明部分に含まれる `_` は、マイグレーション履歴では空白になります。

C# マイグレーションと Flyway 互換 SQL は、バージョンの昇順で適用されます。SQL は書き換えずに実行されます。Flyway のバージョン管理マイグレーションと同様にロールバック用の SQL を持たないため、Flyway 互換 SQL を含むロールバックは開始前に拒否されます。

Flyway 互換 SQL と `Execute.Sql` では、対応している `CREATE TABLE`、`DROP TABLE`、`ALTER TABLE`、テーブル名や列名の変更がスキーマに反映されます。一般的な `UNIQUE`、`FOREIGN KEY`、`CHECK`、排他制約も受け付けますが、生成するスキーマ情報からこれらの制約を参照することはできません。生成列は型の付いた列として扱いますが、生成式は公開しません。列の既定値の変更はスキーマ情報へ反映します。`INSERT`、`UPDATE`、`DELETE`、`SELECT`、インデックス操作、`COMMENT` はテーブルの列構成を変えないため、スキーマを組み立てる際には読み飛ばしますが、実行用 SQL からは削除しません。検索結果の列構成を変える可能性がある未対応の操作はコンパイルエラーになります。

SQL は選択した provider の字句規則に従って文ごとに分割されます。主な対象は PostgreSQL です。PostgreSQL の入力では、単一引用符で囲んだ文字列、エスケープ文字列、二重引用符で囲んだ識別子、ドル引用符、行コメント、入れ子になったブロックコメント内のセミコロンは、文の区切りとして扱いません。列の既定値、null 許容の有無、主キー、スキーマ名付きのテーブル名も保持されます。

マイグレーションを実行するときは、選択した provider のマイグレーションアダプターを指定します。PostgreSQL の例は次のとおりです。

```csharp
using CobaltumOrm.Migrations;
using CobaltumOrm.Migrations.PostgreSql;
using MyApp.Database;

var runner = new MigrationRunner(new PostgreSqlMigrationAdapter());
await runner.MigrateUpAsync(
    connection,
    CobaltumMigrationCatalog.All,
    cancellationToken);
```

## CLI によるマイグレーション管理

.NET tool をグローバルにインストールします。ローカルの tool manifest に同じパッケージを追加しても使えます。

```console
dotnet tool install --global CobaltumOrm.Tool --version tool-version
```

CLI からマイグレーションプロジェクトを生成できます。

```console
cobaltum migrations init MyApp.Database \
  --output src/MyApp.Database \
  --framework net8.0 \
  --provider PostgreSql
```

同じプロジェクトを .NET のプロジェクトテンプレートから生成することもできます。NuGet パッケージをインストールしてから `dotnet new` を実行します。

```console
dotnet new install CobaltumOrm.Templates@templates-version
dotnet new cobaltum-migrations \
  --name MyApp.Database \
  --output src/MyApp.Database \
  --framework net8.0 \
  --provider PostgreSql
```

どちらの方法でも、選択した provider 用の `Program.cs`、`appsettings.json`、プロジェクトごとに異なる `UserSecretsId`、`Migrations` ディレクトリを持つ実行可能プロジェクトができます。`CobaltumOrmMigrationProjectReference` から利用するための source generator 設定も含まれます。CLI で選べる対象は `net8.0`、`net9.0`、`net10.0` です。`--provider` を省略すると PostgreSQL を使います。出力先にファイルがある場合、CLI は上書きせずエラーにします。

CLI の provider 名は大文字と小文字を区別しません。無効な値を指定すると、使用できる値を表示してエラーになります。

生成したプロジェクトには接続パスワードを書き込みません。`status`、`up`、`down` を実行する前に、user secrets または環境変数で `ConnectionStrings:Cobaltum` を設定してください。

手動で定義する場合は、次の決まった形式を使います。`OutputType`、`RootNamespace`、`CobaltumOrmMigrationProject` は、条件なしのプロパティとして定義してください。C# マイグレーションと Flyway 互換 SQL は `Migrations` に置きます。

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net8.0</TargetFramework>
    <RootNamespace>MyApp.Database</RootNamespace>
    <CobaltumOrmMigrationProject>true</CobaltumOrmMigrationProject>
    <CobaltumOrmGeneratedNamespace>MyApp.Database.Generated</CobaltumOrmGeneratedNamespace>
    <CobaltumOrmDatabaseProvider>PostgreSql</CobaltumOrmDatabaseProvider>
    <UserSecretsId>myapp-database-migrations</UserSecretsId>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="CobaltumOrm" Version="runtime-version" />
    <PackageReference Include="CobaltumOrm.Migrations" Version="migrations-version" />
    <PackageReference Include="CobaltumOrm.Migrations.PostgreSql" Version="postgresql-adapter-version" />
    <PackageReference Include="CobaltumOrm.SourceGenerator"
                      Version="generator-version"
                      PrivateAssets="all" />
    <PackageReference Include="Npgsql" Version="npgsql-version" />
    <AdditionalFiles Include="Migrations/V*__*.sql" />
    <CompilerVisibleProperty Include="CobaltumOrmGeneratedNamespace" />
    <CompilerVisibleProperty Include="CobaltumOrmDatabaseProvider" />
  </ItemGroup>

  <ItemGroup>
    <None Update="appsettings*.json"
          CopyToOutputDirectory="PreserveNewest"
          CopyToPublishDirectory="PreserveNewest"
          TargetPath="CobaltumOrm.Migrations/$(AssemblyName)/%(Filename)%(Extension)" />
  </ItemGroup>
</Project>
```

source generator は `CobaltumOrmGeneratedNamespace` に `CobaltumMigrationCatalog.All` を生成します。エントリポイントはこの一覧をホストへ渡し、CobaltumORM が読み込んだ設定を使ってデータベース接続を作ります。

```csharp
using System.Data.Common;
using CobaltumOrm.Migrations;
using CobaltumOrm.Migrations.PostgreSql;
using Npgsql;

return await MigrationProjectHost.RunAsync<DatabaseMigrationProject>(
    args,
    global::MyApp.Database.Generated.CobaltumMigrationCatalog.All);

public sealed class DatabaseMigrationProject : MigrationProject
{
    public override DbConnection CreateConnection(MigrationProjectContext context) =>
        new NpgsqlConnection(context.ConnectionString);

    public override IMigrationDatabaseAdapter CreateAdapter() =>
        new PostgreSqlMigrationAdapter();
}
```

CobaltumORM は .NET の設定規約に合わせ、`appsettings.json`、`appsettings.{Environment}.json`、`Development` 環境の .NET user secrets、環境変数の順に読み込みます。後から読み込んだ値が優先されます。接続文字列のキーは `ConnectionStrings:Cobaltum` です。環境変数では、どの OS でも使える `ConnectionStrings__Cobaltum` を指定します。環境名は `--environment`、`DOTNET_ENVIRONMENT`、`Production` の順で決まります。

JSON ファイルには環境ごとのホスト名やデータベース名を保存できます。パスワードなどの認証情報はコミットせず、ローカル開発では user secrets、それ以外では環境変数またはデプロイ先の secret 管理機能を使ってください。

```console
dotnet user-secrets set --project src/MyApp.Database \
  "ConnectionStrings:Cobaltum" "Host=localhost;Database=myapp;Username=myapp;Password=secret"

DOTNET_ENVIRONMENT=Staging \
ConnectionStrings__Cobaltum="Host=db;Database=myapp;Username=myapp;Password=secret" \
cobaltum migrations status
```

`--settings` を指定すると、2 つの既定 appsettings ファイルの代わりに、指定した JSON ファイルを読み込みます。user secrets と環境変数による上書きは引き続き有効です。

```console
cobaltum migrations status --environment Staging
cobaltum migrations up --environment Production --settings config/migrations.production.json
```

`--project` を省略すると、カレントディレクトリ以下から `CobaltumOrmMigrationProject` が `true` のプロジェクトを再帰的に探します。複数見つかった場合は、`--project` に `.csproj` または対象ディレクトリを指定してください。

```console
cobaltum migrations init MyApp.Database --output src/MyApp.Database --provider PostgreSql
cobaltum migrations add "create users" --project src/MyApp.Database
cobaltum migrations list --project src/MyApp.Database
cobaltum migrations status --project src/MyApp.Database
cobaltum migrations up --project src/MyApp.Database
cobaltum migrations up --dry-run --project src/MyApp.Database
cobaltum migrations down 20260810090000 --project src/MyApp.Database
cobaltum migrations down 20260810090000 --dry-run --project src/MyApp.Database
cobaltum migrations down 0 --project src/MyApp.Database
```

`init` は決まった形式のマイグレーションプロジェクトを作り、プロジェクト名を root namespace に使います。`add` は、ロールバック可能な C# マイグレーションをプロジェクトの `Migrations` ディレクトリに作成します。バージョンを省略すると UTC の日時が使われます。`--version` を使う場合は、既存の C# マイグレーションと Flyway 互換 SQL より大きい正の値を指定してください。`list` はプロジェクトをビルドし、データベースへ接続せずにマイグレーション定義を表示します。`status`、`up`、`down` はマイグレーションプロジェクトの対象 .NET とビルド設定で動きます。既定以外のビルドが必要な場合は、`--configuration`、`--framework`、`--no-build` を使えます。`down 0` は、ロールバック可能なマイグレーションをすべて戻します。ロールバックできないマイグレーションが対象に含まれる場合は、処理を始める前にエラーになります。

`up` または `down` に `--dry-run` を付けると、対象となる `Migrations` 以下のファイル、実行予定の SQL、実行後に想定されるテーブルと列を表示します。選択した接続先からマイグレーション履歴を読み取りますが、履歴テーブルの作成、マイグレーション SQL の実行、履歴の更新は行いません。最終スキーマはマイグレーション定義から組み立てます。対応範囲は選択した provider のビルド時スキーマ生成にある操作と同じです。テーブル構造を変える可能性がある未対応の SQL が含まれる場合は、不完全なスキーマを表示せずエラーにします。

### Query プロジェクトからマイグレーション定義を使う

Query を定義するプロジェクトや生成されたスキーマ型を使うプロジェクトに、マイグレーションプロジェクトを CobaltumORM の入力として追加します。

```xml
<ItemGroup>
  <CobaltumOrmMigrationProjectReference
      Include="../MyApp.Database/MyApp.Database.csproj" />
</ItemGroup>
```

Query プロジェクトのビルド時に、指定したプロジェクトの `Migrations/**/*.cs` と `Migrations/V*__*.sql` を読み込みます。CLI が実行するものと同じ順序のマイグレーションから、`SqlSchema`、`Tables`、row の `record`、名前付き Query の結果型を生成し、直接書いた `Query(...)` も検査します。スキーマ生成だけが目的なら、マイグレーションの実行可能プロジェクトを通常の `ProjectReference` に追加する必要はありません。生成型をアプリケーション固有の namespace に置く場合は、Query プロジェクトで `CobaltumOrmGeneratedNamespace` を指定します。

Query を実行するアプリケーションから、マイグレーションプロジェクトで定義した接続も使う場合は、2 種類の参照を追加します。

```xml
<ItemGroup>
  <ProjectReference Include="../MyApp.Database/MyApp.Database.csproj" />
  <CobaltumOrmMigrationProjectReference
      Include="../MyApp.Database/MyApp.Database.csproj" />
</ItemGroup>
```

これで、接続文字列の環境変数や設定キーをアプリケーション側に書かずに接続を作成できます。

```csharp
using CobaltumOrm.Migrations;
using MyApp.Database;

using var database =
    MigrationProjectConnection.Create<DatabaseMigrationProject>();
var rows = await UserQueries.ReadAllAsync(database.Connection);
```

`MigrationProjectConnection` は、CLI と同じ優先順位で設定を読み、同じ `DatabaseMigrationProject.CreateConnection` を呼びます。このオブジェクトを破棄すると、接続と読み込んだ設定の両方が破棄されます。環境の選択には .NET 標準の `DOTNET_ENVIRONMENT` を使います。生成されたマイグレーションプロジェクトでは `appsettings*.json` をプロジェクトごとの専用ディレクトリへコピーするため、アプリケーション自身の設定ファイルを置き換えることはありません。

## 生成されるテーブル型

各テーブルから `public sealed record` 型と、列を型安全に参照するためのテーブル情報が生成されます。スキーマ名とテーブル名は `[CobaltumTable]`、各プロパティの SQL 上の名前、データ型、null 許容の有無、主キー、既定値の式は `[CobaltumColumn]` に記録されます。C# のプロパティ名が重複する場合は、末尾に `_2`、`_3` を付けます。異なるスキーマに同名のテーブルがある場合は、スキーマ名を `record` 型の名前に含めます。

`Tables.Users` には `Query()`、`All()`、`Where(...)` があります。`Query()` と `All()` が返す `CobaltumQueryDefinition<TRecord>` に `Where(...)` と `WhereIf(...)` を続けて書けます。値は SQL 文字列へ連結せず、`DbParameter` として渡されます。絞り込み条件を追加しても結果の `record` 型は変わりません。

```csharp
using System.Collections.Generic;
using System.Data.Common;
using System.Threading;
using System.Threading.Tasks;
using CobaltumOrm;
using CobaltumOrm.Sample.Generated;

public static class UsersReader
{
    public static async Task<IReadOnlyList<AppUsersRow>> ReadAllAsync(
        DbConnection connection,
        DbTransaction? transaction = null,
        CancellationToken cancellationToken = default)
    {
        return await connection.Query(
            Tables.Users.All(),
            transaction,
            cancellationToken);
    }

    public static Task<IReadOnlyList<AppUsersRow>> ReadFilteredAsync(
        DbConnection connection,
        int id,
        bool includeDisplayName,
        string? displayName,
        DbTransaction? transaction = null,
        CancellationToken cancellationToken = default)
    {
        var query = Tables.Users
            .Query()
            .Where(Tables.Users.Id.Equal(id))
            .WhereIf(
                includeDisplayName,
                () => Tables.Users.DisplayName.Equal(displayName));

        return connection.Query(
            query,
            transaction,
            cancellationToken);
    }
}
```

この例の `AppUsersRow`、`Tables.Users`、`Id`、`DisplayName` は、サンプルにある `app.users` スキーマから生成される名前です。`id` は `int` です。`WhereIf` の条件が `false` の場合は渡した関数を呼ばず、`true` の場合だけ絞り込み条件を追加します。

## 名前付きクエリ

繰り返し使う SQL は、名前空間の直下にあるジェネリックではない `partial class` に `[Query]` 属性を付けて定義できます。同じクラスに複数指定できます。

```csharp
using CobaltumOrm;
using MyApp.Database;

[Query(
    "ById",
    $"SELECT {SqlSchema.Tables.AccountsUsers.Columns.Id}, {SqlSchema.Tables.AccountsUsers.Columns.DisplayName} " +
    $"FROM {SqlSchema.Tables.AccountsUsers.Name} " +
    $"WHERE {SqlSchema.Tables.AccountsUsers.Columns.Id} = @id")]
[Query(
    "ByName",
    $"SELECT {SqlSchema.Tables.AccountsUsers.Columns.Id}, {SqlSchema.Tables.AccountsUsers.Columns.DisplayName} " +
    $"FROM {SqlSchema.Tables.AccountsUsers.Name} " +
    $"WHERE {SqlSchema.Tables.AccountsUsers.Columns.DisplayName} = @name")]
public static partial class UserQueries
{
}
```

`SqlSchema` はマイグレーションから生成され、スキーマ名、テーブル名、列名を PostgreSQL の識別子として引用符で囲んだ `const string` として公開します。例えばスキーマ名は `SqlSchema.Schemas.Accounts`、テーブル名は `SqlSchema.Tables.AccountsUsers.Name`、列名は `SqlSchema.Tables.AccountsUsers.Columns.DisplayName` です。C# 10 以降では上のように属性内の補間文字列で使えます。C# 9 では同じ定数を `"SELECT " + SqlSchema...` のように連結してください。

これらのメンバーは最新のスキーマに存在する名前だけを生成します。名前を変更するマイグレーションで `display_name` を `name` に変えた場合、`DisplayName` は生成されなくなるため、古いクエリは C# のコンパイルエラーになります。新しい `Name` へ明示的に修正してください。値は `SqlSchema` から補間せず、従来どおり `@id` などのパラメーターを使います。

`SqlSchema` を使わず、`"SELECT id FROM accounts.users"` のように名前を直接書いた SQL もコンパイル時の検査対象です。対応範囲内の SQL はパーサーで文法を確認し、マイグレーション適用後のスキーマと照合して、指定したスキーマ、テーブル、列が存在するかを確認します。存在しない名前や文法エラーは、`COB004` と SQL のエラーコードを伴うコンパイルエラーになります。

各クエリから、結果型の `ByIdResult`、パラメーター型の `ByIdParameters`、型付きクエリ定義の `ById`、呼び出し用メソッドの `ByIdAsync` が生成されます。

```csharp
var rows = await UserQueries.ByIdAsync(
    connection,
    id: 42L,
    transaction: transaction,
    cancellationToken: cancellationToken);

_ = rows[0].Id;

await connection.Query(
    UserQueries.ById,
    new UserQueries.ByIdParameters(42L),
    transaction: transaction,
    cancellationToken: cancellationToken);
```

名前付きクエリの SQL、列、パラメーターの型も、PostgreSQL のスキーマに対してコンパイル時に検査されます。同じ SQL を複数箇所から使う場合は、名前付きクエリを型付き API として利用できます。

## 内容がコンパイル時に決まる `Query` の結果型

`Query("...")` にコンパイル時定数の SQL を渡すと、結果列に対応する `record` 型が生成されます。`SELECT` と、`RETURNING` を持つ PostgreSQL のデータ変更文で利用できます。`ReadAsync` の結果から各列を C# のプロパティとして参照できます。

```csharp
using System.Data;
using System.Data.Common;
using System.Threading;
using CobaltumOrm;

public static class UserReader
{
    public static async System.Threading.Tasks.Task<(long Id, string? DisplayName)> ReadAsync(
        DbConnection connection,
        long id,
        CancellationToken cancellationToken = default)
    {
        var rows = await connection
            .Query("SELECT id, display_name FROM accounts.users WHERE id = @id")
            .WithParameter("@id", id, DbType.Int64)
            .ReadAsync(cancellationToken);

        return (rows[0].Id, rows[0].DisplayName);
    }
}
```

この `rows` の `record` 型には `Id` と `DisplayName` があります。`rows[0].Email` のように結果にないプロパティを参照すると、通常の C# コンパイルエラーになります。SQL の文法違反、スキーマにない列、型を判断できないパラメーターもコンパイルエラーです。`WithParameter` にコンパイル時定数で指定した名前と C# 型も、SQL の解析結果と一致するか検査されます。これらの検査は、内容がコンパイル時に決まる対応済みの文が対象です。

対応する `SELECT` の範囲には、CTE、再帰 CTE、`VALUES`、派生テーブル、相関サブクエリ、`DISTINCT ON`、集合演算、結合、絞り込み、グループ化、並び順、`LIMIT` / `OFFSET` / `FETCH`、行ロック、`CASE`、型変換、日付と interval のリテラル、インラインまたは名前付きのウィンドウ、集約関数の `FILTER`、主要なスカラー関数と集約関数が含まれます。PostgreSQL 固有の演算子では、`ILIKE`、正規表現、`IS DISTINCT FROM`、JSON の値取得、包含、重複、剰余、べき乗を解析します。詳しくは [PostgreSQL SELECT の対応範囲](docs/design/poc-sql-type-inference.md#supported-select-syntax) を参照してください。

`WithParameter` は SQL へ値を文字列連結せず、`DbParameter` として渡します。検査対象の文ではパラメーターの `DbType` を推論し、`json` / `jsonb` では PostgreSQL の型名もデータベースドライバーへ渡します。値を指定しなかったパラメーターは `ReadAsync` の前に検出されます。

## 補間文字列を使う `Query`

補間文字列の `{...}` は、SQL の値を書ける位置でだけ使えます。補間した値はスキーマ名、テーブル名、列名などには展開されず、`DbParameter` のプレースホルダーに置き換えられます。

```csharp
using System.Data.Common;
using System.Threading;
using CobaltumOrm;

public static async System.Threading.Tasks.Task<string?> ReadByIdAsync(
    DbConnection connection,
    long id,
    CancellationToken cancellationToken = default)
{
    var rows = await connection
        .Query($"SELECT id, display_name FROM accounts.users WHERE id = {id}")
        .ReadAsync(cancellationToken);

    return rows[0].DisplayName;
}
```

この SQL は実行時には `id = @__cobaltum_value_0` のようになり、`id` の値はパラメーターとして渡されます。`$"SELECT {fields} FROM accounts.users"` のように、列名、テーブル名、SQL のキーワードなどを `{...}` に入れる書き方は拒否されます。書式指定や桁揃えの指定も使えません。

補間文字列を使った `INSERT`、`UPDATE`、`DELETE` は、検査対象の `Query` では実行できません。コンパイル時定数の SQL と `WithParameter` を使ってください。

## 固定 SQL の `INSERT`、`UPDATE`、`DELETE`

コンパイル時定数で指定した `INSERT`、`UPDATE`、`DELETE` のうち、`RETURNING` を持たない文は `ExecuteAsync` で実行します。`RETURNING` を持つ文は `ReadAsync` で実行し、結果を受け取る `record` 型を生成します。

```csharp
using System.Data;
using System.Data.Common;
using System.Threading;
using System.Threading.Tasks;
using CobaltumOrm;

public static class UserWriter
{
    public static Task<int> UpdateNameAsync(
        DbConnection connection,
        long id,
        string newName,
        CancellationToken cancellationToken = default) =>
        connection
            .Query("UPDATE accounts.users SET display_name = @name WHERE id = @id")
            .WithParameter("@name", newName, DbType.String)
            .WithParameter("@id", id, DbType.Int64)
            .ExecuteAsync(cancellationToken);
}
```

`ExecuteAsync` の戻り値は、データベースドライバーが返す更新件数です。パラメーターの値は `DbParameter` になり、`null` は `DBNull.Value` になります。固定 SQL もコンパイル時にパーサーで解析し、文法、対象のスキーマ、テーブル、列、式の型、パラメーターの型を、マイグレーション適用後のスキーマと照合します。`INSERT ... VALUES`、`DEFAULT VALUES`、`INSERT ... SELECT`、`ON CONFLICT`、`UPDATE ... FROM`、`DELETE ... USING`、`TRUNCATE`、`RETURNING`、データ変更文を囲む CTE が対応範囲です。権限、制約、トリガー、実データに依存する結果は、データベースへ接続しないため検査できません。スキーマを変更する SQL は `Query` ではなくマイグレーションに書いてください。

コンパイル時の解析は PostgreSQL の全構文には対応していません。`MERGE`、`FROM` 内のテーブル関数、`GROUPING SETS`、`CUBE`、`ROLLUP`、配列コンストラクター、ユーザー定義関数の戻り値の型は未対応です。ウィンドウのフレーム句は受け付けますが、その内容までは検査しません。これらが必要な場合は `NoCheckQuery` を使います。

SQL を直接書いて PostgreSQL の `json` / `jsonb` に文字列を渡す場合は、`WithConfiguredParameter("@document", json, DbType.String, static parameter => ((NpgsqlParameter)parameter).DataTypeName = "jsonb")` のように Npgsql のパラメーターを直接設定します。生成されたクエリでは、この設定も自動で行います。

## コンパイル時の SQL 検査を行わない `NoCheckQuery`

SQL をコンパイル時に検査できない場合や、対応範囲外の SQL をそのまま実行する場合は `NoCheckQuery(sql)` を使います。戻り値は型なしの `CobaltumRawQuery` です。`ReadAsync` は `CobaltumRawRow` の一覧を返します。SQL の文法、スキーマ名、テーブル名、列名、結果の列構成はコンパイル時に検査されません。

```csharp
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Threading;
using System.Threading.Tasks;
using CobaltumOrm;

public static class DynamicReader
{
    public static Task<IReadOnlyList<CobaltumRawRow>> ReadAsync(
        DbConnection connection,
        string sql,
        long id,
        CancellationToken cancellationToken = default) =>
        connection
            .NoCheckQuery(sql)
            .WithParameter("@id", id, DbType.Int64)
            .ReadAsync(cancellationToken);
}
```

`QueryDynamic(sql)` は互換性のために残している同等の API です。新しいコードでは、検査を行わないことが名前から分かる `NoCheckQuery(sql)` を使います。

`CobaltumRawRow` は列番号と列名を保持し、`row[0]`、`row["id"]`、`GetValues("name")` で値を取得できます。重複した列名を文字列のインデクサーで取得すると、対象を一つに決められないことを示す例外が発生します。実行時に有無が変わる絞り込み条件のために SQL 文字列を組み立てる必要がなければ、`Tables.Users.Query().Where(...).WhereIf(condition, () => ...)` を使ってください。生成された `record` 型を保ったまま条件だけを追加できます。

## 接続と実行時の動作

生成されたクエリと `Query`、`NoCheckQuery`、`QueryDynamic` は、`DbConnection`、`DbCommand`、`DbDataReader` を使います。Npgsql などの ADO.NET データベースドライバーが提供する `DbConnection` を渡せます。

閉じた接続はクエリの実行時に `OpenAsync(cancellationToken)` で開き、処理後に閉じます。すでに開いていた接続は開いたままです。`DbTransaction` を渡す場合は、接続が開いており、その接続に対して作成されたトランザクションである必要があります。`DbTransaction` と `CancellationToken` は、生成されたクエリからコマンドとデータ読み取り処理へ渡されます。

## trimmed publish と Native AOT

実行時ライブラリとマイグレーション用パッケージには、trim と AOT の解析を有効にした `net8.0` と `net10.0` のアセンブリが含まれます。以前の対象向けに、`netstandard2.0` と `netstandard2.1` も引き続き提供します。

trimmed publish を使う場合は、実行可能プロジェクトに次の設定を追加します。

```xml
<PropertyGroup>
  <PublishTrimmed>true</PublishTrimmed>
  <TrimMode>full</TrimMode>
</PropertyGroup>
```

Native AOT では `PublishAot` を使います。Native AOT では trim も行われます。

```xml
<PropertyGroup>
  <PublishAot>true</PublishAot>
</PropertyGroup>
```

対象の Runtime Identifier を指定して publish します。

```console
dotnet publish -c Release -r linux-x64
```

source generator が作る `CobaltumMigrationCatalog.All` は、実行時にアセンブリを走査しません。上の例のように、この一覧を `MigrationRunner` と `MigrationProjectHost` に渡してください。手書きの一覧は `MigrationInfo.Create<TMigration>(version, description)` で作れます。

CobaltumORM は、PostgreSQL の `json` と `jsonb` に必要な Npgsql の設定を、生成コードから直接行います。それ以外の生成コードでは `DbType` を使います。PostgreSQL のプロジェクトには、インストールとマイグレーションプロジェクトの例にある Npgsql の参照が必要です。

利用する ADO.NET ドライバーも、対象の publish 方式に対応している必要があります。ドライバーから trim または AOT の警告が出た場合は、publish 前に解消してください。

## 対応環境

- プロジェクトは .NET SDK 形式で作成してください。
- CoreCLR と、.NET SDK / MSBuild でビルドした Mono アプリケーションで利用できます。
- .NET 8 以降では、生成されたマイグレーション一覧を使う trimmed アプリケーションと Native AOT アプリケーションに対応します。
- 従来の `mcs` / `xbuild` と、通常の Unity / IL2CPP プロジェクトでは、`Query` のコンパイル時検査と結果型の生成を利用できません。
