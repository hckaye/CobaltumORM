# CobaltumMigrations

This executable project contains the CobaltumORM migrations for the application.

Store local credentials with .NET user secrets:

```console
dotnet user-secrets set "ConnectionStrings:Cobaltum" \
  "Host=localhost;Database=app;Username=app;Password=secret"
```

For other environments, use `ConnectionStrings__Cobaltum` or add an
`appsettings.{Environment}.json` file without credentials. Select the environment with
`--environment` or `DOTNET_ENVIRONMENT`.

Create and inspect migrations from this directory:

```console
cobaltum migrations add "create users"
cobaltum migrations schema
cobaltum migrations status --environment Development
cobaltum migrations up --dry-run --environment Development
cobaltum migrations up --write-schema --environment Development
cobaltum migrations up --environment Development
```

Publish a trimmed executable or a Native AOT executable for a specific runtime:

```console
dotnet publish -c Release -r <RID> -p:PublishTrimmed=true
dotnet publish -c Release -r <RID> -p:PublishAot=true
```

To use these migrations for build-time Query checks and generated schema types, add this
item to the Query project's `.csproj`. The item also references this project's assembly, so
runtime types such as `CobaltumMigrationCatalog` are available to the Query project:

```xml
<ItemGroup>
  <CobaltumOrmMigrationProjectReference
      Include="../CobaltumMigrations/CobaltumMigrations.csproj" />
</ItemGroup>
```
