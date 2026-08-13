# CobaltumORM ORM benchmarks

English | [日本語](README.ja.md)

This BenchmarkDotNet project connects to PostgreSQL and compares read performance across CobaltumORM, Dapper, EF Core, LINQ to DB, and RepoDB. A handwritten ADO.NET implementation provides the baseline.

## Requirements

- .NET 10 SDK
- A Docker API-compatible container runtime, such as Docker Desktop

By default, Testcontainers pulls and starts `postgres:18-alpine`. A PostgreSQL installation on the host is not required.

## Run the benchmarks

Run this command from the repository root:

```console
dotnet run --project benchmarks/CobaltumOrm.Benchmarks -c Release
```

The first run restores NuGet packages and pulls the PostgreSQL image. Testcontainers starts PostgreSQL and inserts 10,000 rows before measurement begins. It stops the container after the run.

Use a filter to run only the single-row benchmarks:

```console
dotnet run --project benchmarks/CobaltumOrm.Benchmarks -c Release -- --filter '*SingleRowBenchmarks*'
```

BenchmarkDotNet's Short job is useful for checking the setup. Use the default job for results intended for comparison.

```console
dotnet run --project benchmarks/CobaltumOrm.Benchmarks -c Release -- --job short --filter '*SingleRowBenchmarks*'
```

List the available benchmarks without starting PostgreSQL:

```console
dotnet run --project benchmarks/CobaltumOrm.Benchmarks -c Release -- --list flat
```

Results are written to `BenchmarkDotNet.Artifacts/results`, relative to the directory where the command runs.

## Workloads

| Class | Operation | Parameters |
| --- | --- | --- |
| `SingleRowBenchmarks` | Fetch one row by primary key and materialize six columns into `BenchmarkPost` | `id = 5000` |
| `MultipleRowsBenchmarks` | Fetch rows in primary-key order and materialize six columns into `BenchmarkPost` | 10 and 1,000 rows |

The comparisons use these conditions:

- Every implementation uses an asynchronous API and buffers the complete result in memory.
- PostgreSQL startup, table creation, insertion of 10,000 seed rows, and connection opening are outside the measured operation.
- CobaltumORM uses a named query generated from `[Query<T>]`.
- Dapper and RepoDB use parameterized SQL.
- EF Core and LINQ to DB use LINQ queries with equivalent columns, filters, and ordering.
- EF Core change tracking is disabled.
- The ADO.NET baseline implements the same operation with `NpgsqlCommand` and `NpgsqlDataReader`.
- BenchmarkDotNet records execution time and managed memory allocations.

The measurements include the PostgreSQL round trip. For the single-row workload, database communication can be larger than the ORM's own processing time. Do not directly compare results collected on different machines or under different system load.

## Use an existing PostgreSQL server

Set `COBALTUM_BENCHMARK_CONNECTION_STRING` to skip the Docker container and connect to an existing PostgreSQL server:

```console
export COBALTUM_BENCHMARK_CONNECTION_STRING='Host=localhost;Port=5432;Database=cobaltum_benchmarks;Username=postgres;Password=postgres'
dotnet run --project benchmarks/CobaltumOrm.Benchmarks -c Release
```

Before measurement starts, setup drops and recreates the `cobaltum_benchmark_posts` table in the selected database. Use a database reserved for benchmarks.

Set `COBALTUM_BENCHMARK_POSTGRES_IMAGE` to select another Docker image:

```console
export COBALTUM_BENCHMARK_POSTGRES_IMAGE='postgres:18-alpine'
```

## Build without running

This command compiles the benchmark project without starting a container:

```console
dotnet build benchmarks/CobaltumOrm.Benchmarks/CobaltumOrm.Benchmarks.csproj -c Release
```
