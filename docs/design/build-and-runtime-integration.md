# Build and runtime integration

## SQL dialect boundaries

The query analyzer implements `IQueryAnalyzer`; PostgreSQL uses
`PostgreSqlQueryAnalyzer`. The migration runner accepts an
`IMigrationDatabaseAdapter`; PostgreSQL uses `PostgreSqlMigrationAdapter`. Supporting
another database requires both a query analyzer and a migration adapter for that
dialect.

Query execution depends on `DbConnection`, `DbCommand`, and `DbDataReader`. Generated
runtime code contains regular C# and ADO.NET calls and does not use runtime code
generation.

## Package build integration

`CobaltumOrm.SourceGenerator` includes an MSBuild target through `buildTransitive`. The
target runs a source transformation before `CoreCompile`, then runs the source generator.
The compiler task does not require a machine-wide installation.

A compile-time-known `Query("...")` call is transformed into a typed query before C#
compilation. The transformation supplies the SQL analysis result to generated code and
keeps source locations so C# and SQL diagnostics point to the consumer source file.

The compiler task and source generator target `netstandard2.0`. CoreCLR projects can use
the integration through a compatible .NET SDK and SDK-style MSBuild project. Mono can use
the generated runtime code when the project is built with a compatible SDK and MSBuild;
classic `mcs` and `xbuild` do not run the compile-time integration. Unity does not run
NuGet `buildTransitive` targets or custom MSBuild tasks automatically, so the compile-time
`Query` integration is not currently available in a normal Unity or IL2CPP project.

## ProjectReference configuration

The repository sample uses ProjectReference entries for the source generator and compiler
task. ProjectReference does not import a NuGet package's `buildTransitive` assets, so the
task assembly and target must be specified explicitly.

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <CobaltumOrmGeneratedNamespace>CobaltumOrm.Sample.Generated</CobaltumOrmGeneratedNamespace>
  </PropertyGroup>

  <ItemGroup>
    <ProjectReference Include="../../src/CobaltumOrm/CobaltumOrm.csproj" />
    <ProjectReference Include="../../src/CobaltumOrm.Migrations/CobaltumOrm.Migrations.csproj" />
    <ProjectReference Include="../../src/CobaltumOrm.Migrations.PostgreSql/CobaltumOrm.Migrations.PostgreSql.csproj" />
    <ProjectReference Include="../../src/CobaltumOrm.SourceGenerator/CobaltumOrm.SourceGenerator.csproj"
                      OutputItemType="Analyzer"
                      ReferenceOutputAssembly="false" />
    <ProjectReference Include="../../src/CobaltumOrm.Compiler/CobaltumOrm.Compiler.csproj"
                      ReferenceOutputAssembly="false" />
    <AdditionalFiles Include="Migrations/V*__*.sql" />
    <CompilerVisibleProperty Include="CobaltumOrmGeneratedNamespace" />
  </ItemGroup>

  <PropertyGroup>
    <CobaltumOrmCompilerTaskAssembly>$(MSBuildThisFileDirectory)../../src/CobaltumOrm.Compiler/bin/$(Configuration)/netstandard2.0/CobaltumOrm.Compiler.dll</CobaltumOrmCompilerTaskAssembly>
  </PropertyGroup>

  <Import Project="../../src/CobaltumOrm.SourceGenerator/buildTransitive/CobaltumOrm.SourceGenerator.targets" />
</Project>
```

The sample combines a version 10 C# migration, `V20__add_display_name.sql`, and a version
30 C# migration in one assembly. The following command runs the source transformation,
source generator, and C# build without starting a database:

```sh
dotnet build CobaltumOrm.sln -c Release
```

## PostgreSQL end-to-end tests

`tests/CobaltumOrm.PostgreSql.E2E.Tests` uses Testcontainers to start one
`postgres:17-alpine` container per test collection. It does not use a fixed host port or
an existing PostgreSQL instance. The fixture applies CobaltumORM migrations and executes
generated queries through Npgsql.

```shell
dotnet test tests/CobaltumOrm.PostgreSql.E2E.Tests/CobaltumOrm.PostgreSql.E2E.Tests.csproj
```

The test fails rather than skipping when Docker is unavailable. Use the following filter
to run the solution tests without Docker:

```shell
dotnet test CobaltumOrm.sln --filter "Category!=E2E"
```
