using System;
using System.Diagnostics;
using System.IO;
using System.Security;
using System.Threading.Tasks;
using Xunit;

namespace CobaltumOrm.SourceGenerator.Tests;

public sealed class ExternalMigrationProjectBuildTests
{
    [Fact]
    public void QueryProjectBuildsTypesFromASeparateMigrationProject()
    {
        var repository = FindRepositoryRoot();
        var directory = Path.Combine(
            Path.GetTempPath(),
            "CobaltumOrm.ExternalMigrationTests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var migrationDirectory = Directory.CreateDirectory(Path.Combine(directory, "Database.Migrations"));
            var migrations = Directory.CreateDirectory(Path.Combine(migrationDirectory.FullName, "Migrations"));
            File.WriteAllText(Path.Combine(migrations.FullName, "CreateUsers.cs"), """
                using CobaltumOrm.Migrations;

                namespace Example.Database.Migrations;

                [Migration(1, "create users")]
                public sealed class CreateUsersMigration : Migration
                {
                    public override void Up()
                    {
                        Create.Table("users")
                            .WithColumn("id").AsInt64().PrimaryKey();
                    }

                    public override void Down()
                    {
                        Delete.Table("users");
                    }
                }
                """);
            File.WriteAllText(
                Path.Combine(migrations.FullName, "V2__add_name.sql"),
                "ALTER TABLE users ADD COLUMN name text NOT NULL;");
            File.WriteAllText(Path.Combine(migrationDirectory.FullName, "Program.cs"), "return 0;\n");

            var targets = Escape(Path.Combine(
                repository,
                "src",
                "CobaltumOrm.SourceGenerator",
                "buildTransitive",
                "CobaltumOrm.SourceGenerator.targets"));
            var compiler = Escape(Path.Combine(
                repository,
                "src",
                "CobaltumOrm.Compiler",
                "bin",
                "Debug",
                "netstandard2.0",
                "CobaltumOrm.Compiler.dll"));
            var migrationsProject = Escape(Path.Combine(
                repository,
                "src",
                "CobaltumOrm.Migrations",
                "CobaltumOrm.Migrations.csproj"));
            File.WriteAllText(Path.Combine(migrationDirectory.FullName, "Database.Migrations.csproj"), $$"""
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <OutputType>Exe</OutputType>
                    <TargetFramework>net10.0</TargetFramework>
                    <RootNamespace>Example.Database.Migrations</RootNamespace>
                    <CobaltumOrmMigrationProject>true</CobaltumOrmMigrationProject>
                    <CobaltumOrmGeneratedNamespace>Example.Database.Migrations.Generated</CobaltumOrmGeneratedNamespace>
                  </PropertyGroup>
                  <ItemGroup>
                    <ProjectReference Include="{{migrationsProject}}" />
                    <AdditionalFiles Include="Migrations/V*__*.sql" />
                  </ItemGroup>
                  <PropertyGroup>
                    <CobaltumOrmCompilerTaskAssembly>{{compiler}}</CobaltumOrmCompilerTaskAssembly>
                  </PropertyGroup>
                  <Import Project="{{targets}}" />
                </Project>
                """);

            var queryDirectory = Directory.CreateDirectory(Path.Combine(directory, "App"));
            File.WriteAllText(Path.Combine(queryDirectory.FullName, "Queries.cs"), """
                using System.Data.Common;
                using CobaltumOrm;
                using CobaltumOrm.Generated;

                public static class Queries
                {
                    public static object Read(DbConnection connection) =>
                        connection.Query("SELECT id, name FROM users");

                    public static UsersRow UseGeneratedRow(UsersRow row) => row;

                    public static string GeneratedTableName => SqlSchema.Tables.Users.Name;

                    // CobaltumOrmMigrationProjectReference also references the migration assembly.
                    public static System.Type MigrationType => typeof(Example.Database.Migrations.CreateUsersMigration);
                }
                """);

            var runtimeProject = Escape(Path.Combine(
                repository,
                "src",
                "CobaltumOrm",
                "CobaltumOrm.csproj"));
            var generatorProject = Escape(Path.Combine(
                repository,
                "src",
                "CobaltumOrm.SourceGenerator",
                "CobaltumOrm.SourceGenerator.csproj"));
            var compilerProject = Escape(Path.Combine(
                repository,
                "src",
                "CobaltumOrm.Compiler",
                "CobaltumOrm.Compiler.csproj"));
            var externalProject = Escape(Path.Combine(
                migrationDirectory.FullName,
                "Database.Migrations.csproj"));
            File.WriteAllText(Path.Combine(queryDirectory.FullName, "App.csproj"), $$"""
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net10.0</TargetFramework>
                    <Nullable>enable</Nullable>
                    <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
                  </PropertyGroup>
                  <ItemGroup>
                    <ProjectReference Include="{{runtimeProject}}" />
                    <ProjectReference Include="{{migrationsProject}}" />
                    <ProjectReference Include="{{generatorProject}}"
                                      OutputItemType="Analyzer"
                                      ReferenceOutputAssembly="false" />
                    <ProjectReference Include="{{compilerProject}}"
                                      ReferenceOutputAssembly="false" />
                    <CobaltumOrmMigrationProjectReference Include="{{externalProject}}" />
                  </ItemGroup>
                  <PropertyGroup>
                    <CobaltumOrmCompilerTaskAssembly>{{compiler}}</CobaltumOrmCompilerTaskAssembly>
                  </PropertyGroup>
                  <Import Project="{{targets}}" />
                </Project>
                """);

            var result = RunDotnet(queryDirectory.FullName, "build", "App.csproj", "--nologo");

            Assert.True(result.ExitCode == 0, result.Output);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static ProcessResult RunDotnet(string workingDirectory, params string[] arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "dotnet",
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        startInfo.Environment["MSBUILDDISABLENODEREUSE"] = "1";
        startInfo.Environment["DOTNET_CLI_USE_MSBUILD_SERVER"] = "0";

        using var process = Process.Start(startInfo)!;
        var standardOutput = process.StandardOutput.ReadToEndAsync();
        var standardError = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(120_000))
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException("dotnet did not finish within two minutes.");
        }
        Task.WaitAll(standardOutput, standardError);
        return new ProcessResult(process.ExitCode, standardOutput.Result + standardError.Result);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "CobaltumOrm.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("Repository root was not found.");
    }

    private static string Escape(string value) => SecurityElement.Escape(value)!;

    private sealed class ProcessResult
    {
        internal ProcessResult(int exitCode, string output)
        {
            ExitCode = exitCode;
            Output = output;
        }

        internal int ExitCode { get; }

        internal string Output { get; }
    }
}
