# CobaltumORM タスク別レシピ

[English](recipes.md) | 日本語

サンプルプロジェクトが CI でコード例をすべてビルドします。API の選択は
[quick-reference.ja.md](quick-reference.ja.md) で、ビルドエラーは
[diagnostics.ja.md](diagnostics.ja.md) で確認してください。

サンプルのスキーマは `app.users(id int identity primary key, email varchar(240) not null,
display_name varchar(120) null, created_at timestamptz not null)` です。

## テーブルを作る

マイグレーションプロジェクトの `Migrations` ディレクトリに C# マイグレーションを追加します。既存の
どのマイグレーションよりも大きいバージョンを付け、`Down` に逆の操作を書きます。

<!-- snippet: migration-csharp -->
```csharp
[Migration(10, "create users")]
public sealed class CreateUsersMigration : Migration
{
    public override void Up()
    {
        Create.Table("users")
            .InSchema("app")
            .WithColumn("id").AsInt32().Identity().PrimaryKey()
            .WithColumn("email").AsString(240).NotNullable();
    }

    public override void Down()
    {
        Delete.Table("users").InSchema("app");
    }
}
```

## SQL でテーブルを変更する

`V<version>__<description>.sql` という名前の Flyway 互換ファイルを追加し、
`<AdditionalFiles Include="Migrations/V*__*.sql" />` で取り込みます。ビルドはバージョン順にスキーマ
へ適用し、実行時は前方向にのみ適用します。

<!-- snippet: migration-flyway -->
```sql
ALTER TABLE app.users ADD COLUMN display_name varchar(120) NULL;
```

## 実行時にマイグレーションを適用する

`CobaltumMigrationCatalog.All` はマイグレーションからバージョン順に生成されます。プロバイダーの
アダプターとともに `MigrationRunner` へ渡します。アセンブリ走査を行わないため、trimmed publish と
Native AOT でも動作します。

<!-- snippet: migration-runner -->
```csharp
public static Task MigrateUpAsync(
    DbConnection connection,
    CancellationToken cancellationToken = default) =>
    new MigrationRunner(new PostgreSqlMigrationAdapter())
        .MigrateUpAsync(connection, CobaltumMigrationCatalog.All, cancellationToken);
```

このコードを書かずに `cobaltum` CLI で同じマイグレーションを実行することもできます。
`cobaltum migrations up --project src/MyApp.Database` を使います。

## 生成された結果型で行を読む

固定 SQL を `Query` に渡します。ビルドが SQL を解析し、すべての名前をスキーマと照合して、選択した
列をプロパティに持つ `record` を生成します。値は `WithParameter` で渡します。

<!-- snippet: checked-select -->
```csharp
public static async Task<string?> ReadDisplayNameAsync(
    DbConnection connection,
    int id,
    CancellationToken cancellationToken = default)
{
    var rows = await connection
        .Query("SELECT id, display_name FROM app.users WHERE id = @id")
        .WithParameter("@id", id, DbType.Int32)
        .ReadAsync(cancellationToken);

    return rows[0].DisplayName;
}
```

## 既存の型へ行を読み込む

`Query<T>` は返る列を `T` へマッピングします。ビルドは列名、CLR 型、null 許容性を `T` のコンストラ
クターと照合し、一致しない場合は [COB009](diagnostics.ja.md#cob009) を報告します。既定の照合で足り
ない場合は `[ResultColumn]` で列名を明示します。

<!-- snippet: result-type -->
```csharp
public sealed record UserSummary(
    [ResultColumn("id")] int Id,
    [ResultColumn("display_name")] string? DisplayName);
```

<!-- snippet: result-type-query -->
```csharp
public static Task<IReadOnlyList<UserSummary>> ReadSummariesAsync(
    DbConnection connection,
    CancellationToken cancellationToken = default) =>
    connection
        .Query<UserSummary>("SELECT id, display_name FROM app.users ORDER BY id")
        .ReadAsync(cancellationToken);
```

## 同じ SQL に名前を付けて再利用する

名前空間の直下にあるジェネリックではない `partial class` に `[Query(name, sql)]` を付けます。属性は
複数指定できます。1 つにつき結果 `record`、パラメーター `record`、クエリ定義、非同期メソッドが生成
されます。

<!-- snippet: named-query -->
```csharp
[Query(
    "ByEmail",
    "SELECT id, display_name FROM app.users WHERE email = @email")]
public static partial class UserDirectory
{
}
```

<!-- snippet: named-query-call -->
```csharp
public static Task<IReadOnlyList<UserDirectory.ByEmailResult>> ReadByEmailAsync(
    DbConnection connection,
    string email,
    CancellationToken cancellationToken = default) =>
    UserDirectory.ByEmailAsync(connection, email, cancellationToken: cancellationToken);
```

結果型を生成せず既存の型へマッピングする場合は `[Query<T>(name, sql)]` を使います。

## 補間文字列の中で値を渡す

補間の穴は `DbParameter` に置き換えられ、SQL のテキストにはなりません。穴を書けるのは値の位置だけで、
`$"SELECT {columns} FROM app.users"` は [COB103](diagnostics.ja.md#cob103) で拒否されます。

<!-- snippet: interpolated-query -->
```csharp
public static async Task<string?> ReadDisplayNameInterpolatedAsync(
    DbConnection connection,
    int id,
    CancellationToken cancellationToken = default)
{
    var rows = await connection
        .Query($"SELECT id, display_name FROM app.users WHERE id = {id}")
        .ReadAsync(cancellationToken);

    return rows[0].DisplayName;
}
```

## INSERT、UPDATE、DELETE を実行する

文を固定 SQL で書き、値を `WithParameter` で渡して `ExecuteAsync` を呼びます。戻り値は影響を受けた
行数です。`RETURNING` を含む文は `ExecuteAsync` ではなく `ReadAsync` を使い、結果 `record` が生成され
ます。補間文字列による更新処理は `Query` では受け付けません。

<!-- snippet: constant-dml -->
```csharp
public static Task<int> RenameAsync(
    DbConnection connection,
    int id,
    string displayName,
    CancellationToken cancellationToken = default) =>
    connection
        .Query("UPDATE app.users SET display_name = @name WHERE id = @id")
        .WithParameter("@name", displayName, DbType.String)
        .WithParameter("@id", id, DbType.Int32)
        .ExecuteAsync(cancellationToken);
```

## SQL を組み立てずに 1 つのテーブルを絞り込む

`Tables.<Table>` はテーブルを型付きクエリとして公開します。`Where` と `WhereIf` はパラメーター化した
条件を `AND` で連結し、結果の `record` は変わりません。`WhereIf` は条件が `false` のとき渡した関数を
呼びません。

<!-- snippet: table-query -->
```csharp
public static Task<IReadOnlyList<AppUsersRow>> ReadFilteredAsync(
    DbConnection connection,
    int id,
    bool filterByEmail,
    string email,
    CancellationToken cancellationToken = default)
{
    var query = Tables.Users
        .Query()
        .Where(Tables.Users.Id.Equal(id))
        .WhereIf(filterByEmail, () => Tables.Users.Email.Equal(email));

    return connection.Query(query, transaction: null, cancellationToken);
}
```

## ビルドで検査できない SQL を実行する

`NoCheckQuery` は、実行時にしか文字列が決まらない SQL や、解析対象外の構文を受け付けます。SQL は
ビルド時にいっさい検査されません。`ReadAsync` は列の序数と名前を保持した `CobaltumRawRow` を返します。

<!-- snippet: no-check-query -->
```csharp
public static Task<IReadOnlyList<CobaltumRawRow>> ReadDynamicAsync(
    DbConnection connection,
    string sql,
    int id,
    CancellationToken cancellationToken = default) =>
    connection
        .NoCheckQuery(sql)
        .WithParameter("@id", id, DbType.Int32)
        .ReadAsync(cancellationToken);
```

`NoCheckQuery<T>` は同じマッピング規則を既存の型に適用します。ビルドは `T` の構造を検査しますが SQL
とは照合できないため、列の欠落、重複、型の不一致はデータリーダーが行を読む時点で例外になります。
余分な列は無視されます。

<!-- snippet: no-check-query-result -->
```csharp
public static Task<IReadOnlyList<UserSummary>> ReadDynamicSummariesAsync(
    DbConnection connection,
    string sql,
    CancellationToken cancellationToken = default) =>
    connection
        .NoCheckQuery<UserSummary>(sql)
        .ReadAsync(cancellationToken);
```

## 変更を確認する

SQL の検査はコンパイル中に実行されます。クエリ、マイグレーション、結果型、プロバイダー設定を変えた
ら、ビルドしてからテストを実行します。

```console
dotnet build
dotnet test
```
