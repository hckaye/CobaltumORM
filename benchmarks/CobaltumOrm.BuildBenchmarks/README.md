# CobaltumORM build-time benchmarks

English | [日本語](README.ja.md)

This project generates build workloads containing many `[Query]` attributes, `Query(...)` calls, and SQL migration statements. It also generates a matching project with CobaltumORM analysis and code generation disabled.

## Run the benchmarks

The .NET 10 SDK is required. No database or Docker installation is used.

Run this command from the repository root:

```console
dotnet run --project benchmarks/CobaltumOrm.BuildBenchmarks -c Release -- --profile all --runs 3
```

`--profile` accepts `small`, `medium`, `large`, or `all`. The option can be repeated. `--runs` sets the number of recorded builds after warmup.

Generated projects are stored under a temporary directory and deleted after a successful run. Pass `--keep` to retain them for inspection. `--work-directory <path>` selects their parent directory.

## Workloads

| Profile | `[Query]` | `Query(...)` methods | Tables | Migration SQL statements | Migration lines |
| --- | ---: | ---: | ---: | ---: | ---: |
| `small` | 100 | 100 | 25 | 125 | 375 |
| `medium` | 500 | 500 | 100 | 500 | 1,500 |
| `large` | 1,000 | 1,000 | 200 | 1,000 | 3,000 |

Named queries are divided into classes containing 25 queries each. Source `Query(...)` calls are divided into files containing 100 methods each. Every query selects two columns and uses `id` as a parameter.

The migration is one Flyway-compatible SQL file. Each table is created with seven columns, followed by four `ALTER TABLE` statements that add columns. The generated schema and C# table types cover every query in the workload.

The `plain` comparison project contains the same C# and SQL files. It references the CobaltumORM runtime libraries but does not run compile-time analysis, code generation, or source transformation. The `cobaltum` project runs the regular Source Generator and `CobaltumOrmTransformTask`.

## Measurement method

Three build scenarios are measured for each project:

| Name | Measured operation |
| --- | --- |
| `clean` | Run `dotnet clean` before measurement, then recreate every project output |
| `no-change` | Build an already completed project again without changing a file |
| `one-file-change` | Change one C# file that contains no query, then build |

Dependency builds, NuGet restore, and `dotnet clean` are outside the measured interval. The measured command uses `dotnet build --no-restore --no-dependencies`. MSBuild evaluation of project references remains inside the measured interval. MSBuild and C# compiler server settings use the SDK defaults.

Each scenario runs once for warmup. The benchmark then reports the median, minimum, and maximum of the requested sample count.

## Results from 2026-08-13

These results were collected on a MacBook Air with a 10-core Apple M5 and 32 GB of memory, running macOS 26.5.2 and .NET SDK 10.0.203. Each scenario has three recorded samples. Times are milliseconds.

| Profile | Variant | Build | Median | Minimum | Maximum |
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

The added time over the matching plain project was:

| Profile | `clean` | `no-change` | `one-file-change` |
| --- | ---: | ---: | ---: |
| `small` | +931 ms | +423 ms | +711 ms |
| `medium` | +8,296 ms | +2,691 ms | +4,764 ms |
| `large` | +13,225 ms | +11,627 ms | +11,496 ms |

At 100 queries of each form, the added time remained below one second. At 500 of each form, it ranged from 2.7 to 8.3 seconds. At 1,000 of each form, it ranged from 11.5 to 13.2 seconds.

The `large` no-change build took 12.8 seconds, close to its 14.5-second clean build. `CobaltumOrmTransformSources` currently has no MSBuild input and output declaration that skips the task when its inputs are unchanged, so it analyzes the migrations and sources during no-change builds. Changing one C# file also changes the complete compilation supplied to the Source Generator, causing the schema and all queries to be analyzed again.
