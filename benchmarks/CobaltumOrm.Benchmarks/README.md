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

## Reference results from August 13, 2026

The measurement environment was:

- Apple M5 with 10 physical and 10 logical cores
- macOS Tahoe 26.5.2
- .NET SDK 10.0.203 and .NET 10.0.7
- BenchmarkDotNet 0.15.8
- PostgreSQL 18 from `postgres:18-alpine`

The time values below are the arithmetic mean and half of the 99.9% confidence interval reported by BenchmarkDotNet.

| Method | 1 row | 10 rows | 1,000 rows |
| --- | ---: | ---: | ---: |
| CobaltumORM | 180.0 ± 3.55 μs | 156.2 ± 3.05 μs | 979.5 ± 49.03 μs |
| Dapper | 180.4 ± 3.60 μs | 155.2 ± 3.04 μs | 971.9 ± 42.85 μs |
| EF Core | 188.8 ± 3.57 μs | 176.5 ± 3.46 μs | 1,227.6 ± 89.18 μs |
| LINQ to DB | 185.3 ± 3.66 μs | 233.3 ± 11.00 μs | 1,086.7 ± 60.44 μs |
| RepoDB | 280.7 ± 30.68 μs | 218.5 ± 8.91 μs | 995.3 ± 51.28 μs |
| ADO.NET | 287.6 ± 21.43 μs | 191.0 ± 4.86 μs | 980.5 ± 52.49 μs |

Managed memory allocated per operation was:

| Method | 1 row | 10 rows | 1,000 rows |
| --- | ---: | ---: | ---: |
| CobaltumORM | 2.72 KB | 7.68 KB | 578.98 KB |
| Dapper | 3.00 KB | 8.71 KB | 673.21 KB |
| EF Core | 10.23 KB | 17.50 KB | 774.42 KB |
| LINQ to DB | 8.27 KB | 14.20 KB | 595.96 KB |
| RepoDB | 3.67 KB | 8.54 KB | 580.02 KB |
| ADO.NET | 3.09 KB | 7.65 KB | 571.63 KB |

CobaltumORM and Dapper differed by less than 1% in mean execution time for all three workloads. CobaltumORM allocated 9% to 14% less managed memory than Dapper in this run.

BenchmarkDotNet reported multimodal distributions for some RepoDB, EF Core, LINQ to DB, and ADO.NET measurements, and removed outliers from several measurements. Treat small differences as measurement variation and repeat the benchmark on the target machine before making performance decisions. These results measure SQL execution and materialization. They do not measure build time, incremental source generator execution, or compile-time query analysis.

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
