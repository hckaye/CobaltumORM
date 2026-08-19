using CobaltumOrm.Tool;
using Xunit;

namespace CobaltumOrm.Tool.Tests;

public sealed class AddCommandTests
{
    [Fact]
    public async Task AddsCobaltumOrmConfigurationToAnExistingProject()
    {
        using var fixture = new AddFixture();
        fixture.WriteApplication("Example.App");
        var migration = fixture.WriteMigration("Example.Database", "Sqlite");
        using var output = new StringWriter();
        using var error = new StringWriter();

        var exitCode = await fixture.Application(output, error).RunAsync(
            new[]
            {
                "add", "--project", Path.GetRelativePath(fixture.Root, fixture.ApplicationProject),
                "--migration-project", Path.GetRelativePath(fixture.Root, migration),
            },
            CancellationToken.None);

        Assert.Equal(0, exitCode);
        var project = File.ReadAllText(fixture.ApplicationProject);
        Assert.Contains("<TargetFramework>net10.0</TargetFramework>", project, StringComparison.Ordinal);
        Assert.Contains("<PackageReference Include=\"Other.Package\" Version=\"1.2.3\" />", project, StringComparison.Ordinal);
        Assert.Contains("<PackageReference Include=\"CobaltumOrm\" Version=\"1.0.1\" />", project, StringComparison.Ordinal);
        Assert.Contains("<PackageReference Include=\"CobaltumOrm.Migrations\" Version=\"1.0.1\" />", project, StringComparison.Ordinal);
        Assert.Contains("<PackageReference Include=\"CobaltumOrm.SourceGenerator\" Version=\"1.0.1\" PrivateAssets=\"all\" />", project, StringComparison.Ordinal);
        Assert.Contains("<PackageReference Include=\"Microsoft.Data.Sqlite\" Version=\"10.0.7\" />", project, StringComparison.Ordinal);
        Assert.Contains("<PackageReference Include=\"SQLitePCLRaw.bundle_e_sqlite3\" Version=\"2.1.12\" />", project, StringComparison.Ordinal);
        Assert.Contains("<PackageReference Include=\"SQLitePCLRaw.core\" Version=\"2.1.12\" />", project, StringComparison.Ordinal);
        Assert.Contains("<CobaltumOrmDatabaseProvider>Sqlite</CobaltumOrmDatabaseProvider>", project, StringComparison.Ordinal);
        Assert.Contains("<CobaltumOrmGeneratedNamespace>Example.Database</CobaltumOrmGeneratedNamespace>", project, StringComparison.Ordinal);
        Assert.Contains("<CompilerVisibleProperty Include=\"CobaltumOrmGeneratedNamespace\" />", project, StringComparison.Ordinal);
        Assert.Contains("<CompilerVisibleProperty Include=\"CobaltumOrmDatabaseProvider\" />", project, StringComparison.Ordinal);
        Assert.Contains(
            "<CobaltumOrmMigrationProjectReference Include=\"../Database/Example.Database.csproj\" />",
            project,
            StringComparison.Ordinal);
        Assert.Contains("<Compile Include=\"Existing.cs\" />", project, StringComparison.Ordinal);
        Assert.Contains("Updated ", output.ToString(), StringComparison.Ordinal);
        Assert.Contains("added CobaltumOrmMigrationProjectReference ../Database/Example.Database.csproj", output.ToString(), StringComparison.Ordinal);
        Assert.Equal(string.Empty, error.ToString());
        Assert.True(File.Exists(fixture.SourceFile));
    }

    [Fact]
    public async Task IsIdempotentAndDoesNotRewriteAnAlreadyConfiguredProject()
    {
        using var fixture = new AddFixture();
        fixture.WriteApplication("Example.App");
        var migration = fixture.WriteMigration("Example.Database", "PostgreSql");
        var args = new[]
        {
            "add", "--project", fixture.ApplicationProject,
            "--migration-project", migration,
        };

        using var firstOutput = new StringWriter();
        using var firstError = new StringWriter();
        Assert.Equal(0, await fixture.Application(firstOutput, firstError).RunAsync(args, CancellationToken.None));
        var firstBytes = File.ReadAllBytes(fixture.ApplicationProject);

        using var secondOutput = new StringWriter();
        using var secondError = new StringWriter();
        Assert.Equal(0, await fixture.Application(secondOutput, secondError).RunAsync(args, CancellationToken.None));

        Assert.Equal(firstBytes, File.ReadAllBytes(fixture.ApplicationProject));
        Assert.Contains("No changes needed", secondOutput.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("Updated", secondOutput.ToString(), StringComparison.Ordinal);
        Assert.Equal(string.Empty, secondError.ToString());
    }

    [Fact]
    public async Task ReportsConflictingConfigurationWithoutWritingAnything()
    {
        using var fixture = new AddFixture();
        fixture.WriteApplication("Example.App", generatedNamespace: "Example.Existing.Generated");
        var migrationPath = fixture.MigrationPath("Example.Database");
        var before = File.ReadAllBytes(fixture.ApplicationProject);
        using var output = new StringWriter();
        using var error = new StringWriter();

        var exitCode = await fixture.Application(output, error).RunAsync(
            new[]
            {
                "add", "--project", fixture.ApplicationProject,
                "--migration-project", migrationPath,
                "--create-migration-project",
            },
            CancellationToken.None);

        Assert.Equal(2, exitCode);
        Assert.Contains("CobaltumOrmGeneratedNamespace", error.ToString(), StringComparison.Ordinal);
        Assert.Equal(before, File.ReadAllBytes(fixture.ApplicationProject));
        Assert.False(File.Exists(migrationPath));
        Assert.False(Directory.Exists(Path.GetDirectoryName(migrationPath)!));
    }

    [Fact]
    public async Task CreatesAMigrationProjectOnlyWhenOptedIn()
    {
        using var fixture = new AddFixture();
        fixture.WriteApplication("Example.App", packageVersion: "1.2.3");
        var migrationPath = fixture.MigrationPath("Example.Database");
        using var output = new StringWriter();
        using var error = new StringWriter();

        var exitCode = await fixture.Application(output, error).RunAsync(
            new[]
            {
                "add", "--project", fixture.ApplicationProject,
                "--migration-project", migrationPath,
                "--create-migration-project", "--provider", "SqlServer", "--framework", "net10.0",
            },
            CancellationToken.None);

        Assert.Equal(0, exitCode);
        Assert.True(File.Exists(migrationPath));
        Assert.True(File.Exists(Path.Combine(Path.GetDirectoryName(migrationPath)!, "Program.cs")));
        Assert.True(File.Exists(Path.Combine(Path.GetDirectoryName(migrationPath)!, "Migrations", "README.md")));
        Assert.Contains("<CobaltumOrmDatabaseProvider>SqlServer</CobaltumOrmDatabaseProvider>", File.ReadAllText(fixture.ApplicationProject), StringComparison.Ordinal);
        Assert.Contains("<PackageReference Include=\"CobaltumOrm.Migrations\" Version=\"1.2.3\" />", File.ReadAllText(fixture.ApplicationProject), StringComparison.Ordinal);
        Assert.Contains("<PackageReference Include=\"Microsoft.Data.SqlClient\" Version=\"7.0.2\" />", File.ReadAllText(fixture.ApplicationProject), StringComparison.Ordinal);
        Assert.Contains("<PackageReference Include=\"CobaltumOrm\" Version=\"1.2.3\" />", File.ReadAllText(migrationPath), StringComparison.Ordinal);
        Assert.Contains("Created migration project", output.ToString(), StringComparison.Ordinal);
        Assert.Equal(string.Empty, error.ToString());
    }

    [Fact]
    public async Task DoesNotReplaceFilesInARequestedCreationDirectory()
    {
        using var fixture = new AddFixture();
        fixture.WriteApplication("Example.App");
        var migrationPath = fixture.MigrationPath("Example.Database");
        var migrationDirectory = Directory.CreateDirectory(Path.GetDirectoryName(migrationPath)!);
        var sourceFile = Path.Combine(migrationDirectory.FullName, "Program.cs");
        File.WriteAllText(sourceFile, "// user source");
        var before = File.ReadAllBytes(fixture.ApplicationProject);
        using var output = new StringWriter();
        using var error = new StringWriter();

        var exitCode = await fixture.Application(output, error).RunAsync(
            new[]
            {
                "add", "--project", fixture.ApplicationProject,
                "--migration-project", migrationPath,
                "--create-migration-project",
            },
            CancellationToken.None);

        Assert.Equal(2, exitCode);
        Assert.Contains("is not empty", error.ToString(), StringComparison.Ordinal);
        Assert.Equal("// user source", File.ReadAllText(sourceFile));
        Assert.Equal(before, File.ReadAllBytes(fixture.ApplicationProject));
        Assert.False(File.Exists(migrationPath));
    }

    [Fact]
    public async Task RequiresOptInBeforeCreatingAMissingMigrationProject()
    {
        using var fixture = new AddFixture();
        fixture.WriteApplication("Example.App");
        var migrationPath = fixture.MigrationPath("Example.Database");
        var before = File.ReadAllBytes(fixture.ApplicationProject);
        using var output = new StringWriter();
        using var error = new StringWriter();

        var exitCode = await fixture.Application(output, error).RunAsync(
            new[]
            {
                "add", "--project", fixture.ApplicationProject,
                "--migration-project", migrationPath,
            },
            CancellationToken.None);

        Assert.Equal(2, exitCode);
        Assert.Contains("--create-migration-project", error.ToString(), StringComparison.Ordinal);
        Assert.Equal(before, File.ReadAllBytes(fixture.ApplicationProject));
        Assert.False(File.Exists(migrationPath));
    }

    [Fact]
    public async Task RejectsAConflictingPackageVersionBeforeChangingTheProject()
    {
        using var fixture = new AddFixture();
        fixture.WriteApplication("Example.App", packageVersion: "9.9.9");
        var migration = fixture.WriteMigration("Example.Database", "PostgreSql", "1.0.1");
        var before = File.ReadAllBytes(fixture.ApplicationProject);
        using var output = new StringWriter();
        using var error = new StringWriter();

        var exitCode = await fixture.Application(output, error).RunAsync(
            new[]
            {
                "add", "--project", fixture.ApplicationProject,
                "--migration-project", migration,
            },
            CancellationToken.None);

        Assert.Equal(2, exitCode);
        Assert.Contains("CobaltumORM package versions conflict", error.ToString(), StringComparison.Ordinal);
        Assert.Equal(before, File.ReadAllBytes(fixture.ApplicationProject));
    }

    [Fact]
    public async Task RejectsAConflictingMigrationReferenceBeforeChangingTheProject()
    {
        using var fixture = new AddFixture();
        fixture.WriteApplication("Example.App");
        var migration = fixture.WriteMigration("Example.Database", "PostgreSql");
        var project = File.ReadAllText(fixture.ApplicationProject)
            .Replace("</Project>", "  <ItemGroup>\n    <CobaltumOrmMigrationProjectReference Include=\"../Other/Other.csproj\" />\n  </ItemGroup>\n</Project>", StringComparison.Ordinal);
        File.WriteAllText(fixture.ApplicationProject, project);
        var before = File.ReadAllBytes(fixture.ApplicationProject);
        using var output = new StringWriter();
        using var error = new StringWriter();

        var exitCode = await fixture.Application(output, error).RunAsync(
            new[]
            {
                "add", "--project", fixture.ApplicationProject,
                "--migration-project", migration,
            },
            CancellationToken.None);

        Assert.Equal(2, exitCode);
        Assert.Contains("different CobaltumOrm migration project", error.ToString(), StringComparison.Ordinal);
        Assert.Equal(before, File.ReadAllBytes(fixture.ApplicationProject));
    }

    [Fact]
    public async Task PreservesProjectTextOutsideTheAddedXmlAndItsLineEndings()
    {
        using var fixture = new AddFixture();
        fixture.WriteApplication("Example.App");
        var original = File.ReadAllText(fixture.ApplicationProject).Replace("\n", "\r\n", StringComparison.Ordinal);
        File.WriteAllText(fixture.ApplicationProject, original);
        var migration = fixture.WriteMigration("Example.Database", "PostgreSql");
        using var output = new StringWriter();
        using var error = new StringWriter();

        var exitCode = await fixture.Application(output, error).RunAsync(
            new[]
            {
                "add", "--project", fixture.ApplicationProject,
                "--migration-project", migration,
            },
            CancellationToken.None);

        Assert.Equal(0, exitCode);
        var updated = File.ReadAllText(fixture.ApplicationProject);
        Assert.Contains("\r\n", updated, StringComparison.Ordinal);
        Assert.Contains("<Compile Include=\"Existing.cs\" />", updated, StringComparison.Ordinal);
        Assert.Contains("<PackageReference Include=\"Other.Package\" Version=\"1.2.3\" />", updated, StringComparison.Ordinal);
        Assert.Contains("<RootNamespace>Example.App</RootNamespace>", updated, StringComparison.Ordinal);
        Assert.Equal(string.Empty, error.ToString());
    }

    [Fact]
    public async Task AddsPackageVersionsToNearestCentralPackageFile()
    {
        using var fixture = new AddFixture();
        fixture.WriteApplication("Example.App");
        fixture.WriteCentralPackages();
        var migration = fixture.WriteMigration("Example.Database", "PostgreSql");
        using var output = new StringWriter();
        using var error = new StringWriter();

        var exitCode = await fixture.Application(output, error).RunAsync(
            new[]
            {
                "add", "--project", fixture.ApplicationProject,
                "--migration-project", migration,
            },
            CancellationToken.None);

        Assert.Equal(0, exitCode);
        var project = File.ReadAllText(fixture.ApplicationProject);
        var packages = File.ReadAllText(fixture.PackagesPath);
        Assert.Contains("<PackageReference Include=\"CobaltumOrm\" />", project, StringComparison.Ordinal);
        Assert.Contains("<PackageReference Include=\"CobaltumOrm.Migrations\" />", project, StringComparison.Ordinal);
        Assert.Contains("<PackageReference Include=\"CobaltumOrm.SourceGenerator\" PrivateAssets=\"all\" />", project, StringComparison.Ordinal);
        Assert.DoesNotContain("PackageReference Include=\"CobaltumOrm\" Version=", project, StringComparison.Ordinal);
        Assert.Contains("<PackageVersion Include=\"CobaltumOrm\" Version=\"1.0.1\" />", packages, StringComparison.Ordinal);
        Assert.Contains("<PackageVersion Include=\"CobaltumOrm.Migrations\" Version=\"1.0.1\" />", packages, StringComparison.Ordinal);
        Assert.Contains("<PackageVersion Include=\"CobaltumOrm.SourceGenerator\" Version=\"1.0.1\" />", packages, StringComparison.Ordinal);
        Assert.Contains("<PackageVersion Include=\"Npgsql\" Version=\"10.0.3\" />", packages, StringComparison.Ordinal);
        Assert.Contains("<UnrelatedPackageSetting>keep</UnrelatedPackageSetting>", packages, StringComparison.Ordinal);
        Assert.Contains($"Updated {fixture.PackagesPath}", output.ToString(), StringComparison.Ordinal);
        Assert.Equal(string.Empty, error.ToString());
    }

    [Fact]
    public async Task CentralPackageManagementIsIdempotentForBothFiles()
    {
        using var fixture = new AddFixture();
        fixture.WriteApplication("Example.App");
        fixture.WriteCentralPackages();
        var migration = fixture.WriteMigration("Example.Database", "PostgreSql");
        var args = new[]
        {
            "add", "--project", fixture.ApplicationProject,
            "--migration-project", migration,
        };

        using var firstOutput = new StringWriter();
        using var firstError = new StringWriter();
        Assert.Equal(0, await fixture.Application(firstOutput, firstError).RunAsync(args, CancellationToken.None));
        var firstProjectBytes = File.ReadAllBytes(fixture.ApplicationProject);
        var firstPackagesBytes = File.ReadAllBytes(fixture.PackagesPath);

        using var secondOutput = new StringWriter();
        using var secondError = new StringWriter();
        Assert.Equal(0, await fixture.Application(secondOutput, secondError).RunAsync(args, CancellationToken.None));

        Assert.Equal(firstProjectBytes, File.ReadAllBytes(fixture.ApplicationProject));
        Assert.Equal(firstPackagesBytes, File.ReadAllBytes(fixture.PackagesPath));
        Assert.Contains("No changes needed", secondOutput.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("Updated", secondOutput.ToString(), StringComparison.Ordinal);
        Assert.Equal(string.Empty, secondError.ToString());
    }

    [Fact]
    public async Task AcceptsAnExistingCompatibleCentralPackageVersion()
    {
        using var fixture = new AddFixture();
        fixture.WriteApplication("Example.App");
        fixture.WriteCentralPackages(("CobaltumOrm", "1.0.1"));
        var migration = fixture.WriteMigration("Example.Database", "PostgreSql");
        using var output = new StringWriter();
        using var error = new StringWriter();

        var exitCode = await fixture.Application(output, error).RunAsync(
            new[]
            {
                "add", "--project", fixture.ApplicationProject,
                "--migration-project", migration,
            },
            CancellationToken.None);

        Assert.Equal(0, exitCode);
        var packages = File.ReadAllText(fixture.PackagesPath);
        Assert.Equal(1, CountOccurrences(packages, "<PackageVersion Include=\"CobaltumOrm\""));
        Assert.DoesNotContain("conflict", error.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task UsesTheMigrationProjectsCentralDriverVersion()
    {
        using var fixture = new AddFixture();
        fixture.WriteApplication("Example.App");
        fixture.WriteCentralPackages(("Npgsql", "8.0.0"));
        var migration = fixture.WriteMigration("Example.Database", "PostgreSql");
        var migrationProject = File.ReadAllText(migration)
            .Replace(
                "</Project>",
                "  <ItemGroup>\n    <PackageReference Include=\"Npgsql\" />\n  </ItemGroup>\n</Project>",
                StringComparison.Ordinal);
        File.WriteAllText(migration, migrationProject);
        using var output = new StringWriter();
        using var error = new StringWriter();

        var exitCode = await fixture.Application(output, error).RunAsync(
            new[]
            {
                "add", "--project", fixture.ApplicationProject,
                "--migration-project", migration,
            },
            CancellationToken.None);

        Assert.Equal(0, exitCode);
        Assert.Contains("<PackageReference Include=\"Npgsql\" />", File.ReadAllText(fixture.ApplicationProject), StringComparison.Ordinal);
        Assert.DoesNotContain("PackageReference Include=\"Npgsql\" Version=", File.ReadAllText(fixture.ApplicationProject), StringComparison.Ordinal);
        Assert.Contains("<PackageVersion Include=\"Npgsql\" Version=\"8.0.0\" />", File.ReadAllText(fixture.PackagesPath), StringComparison.Ordinal);
        Assert.Equal(string.Empty, error.ToString());
    }

    [Fact]
    public async Task CreatesMigrationProjectUnderTheSameCentralPackageFile()
    {
        using var fixture = new AddFixture();
        fixture.WriteApplication("Example.App");
        fixture.WriteCentralPackages();
        var migration = fixture.MigrationPath("Example.Database");
        using var output = new StringWriter();
        using var error = new StringWriter();

        var exitCode = await fixture.Application(output, error).RunAsync(
            new[]
            {
                "add", "--project", fixture.ApplicationProject,
                "--migration-project", migration,
                "--create-migration-project", "--provider", "Sqlite",
            },
            CancellationToken.None);

        Assert.Equal(0, exitCode);
        var project = File.ReadAllText(migration);
        var packages = File.ReadAllText(fixture.PackagesPath);
        Assert.DoesNotContain("Version=", project, StringComparison.Ordinal);
        Assert.DoesNotContain("VersionOverride", project, StringComparison.Ordinal);
        Assert.Contains("<PackageVersion Include=\"CobaltumOrm\" Version=\"1.0.1\" />", packages, StringComparison.Ordinal);
        Assert.Contains("<PackageVersion Include=\"CobaltumOrm.Migrations\" Version=\"1.0.1\" />", packages, StringComparison.Ordinal);
        Assert.Contains("<PackageVersion Include=\"CobaltumOrm.SourceGenerator\" Version=\"1.0.1\" />", packages, StringComparison.Ordinal);
        Assert.Contains("<PackageVersion Include=\"CobaltumOrm.Migrations.Sqlite\" Version=\"1.0.1\" />", packages, StringComparison.Ordinal);
        Assert.Contains("<PackageVersion Include=\"Microsoft.Data.Sqlite\" Version=\"10.0.7\" />", packages, StringComparison.Ordinal);
        Assert.Contains("<PackageVersion Include=\"SQLitePCLRaw.bundle_e_sqlite3\" Version=\"2.1.12\" />", packages, StringComparison.Ordinal);
        Assert.Contains("<PackageVersion Include=\"SQLitePCLRaw.core\" Version=\"2.1.12\" />", packages, StringComparison.Ordinal);
        Assert.Equal(1, CountOccurrences(packages, "<PackageVersion Include=\"CobaltumOrm\""));
        Assert.Equal(1, CountOccurrences(packages, "<PackageVersion Include=\"CobaltumOrm.Migrations.Sqlite\""));
        Assert.Contains("<UnrelatedPackageSetting>keep</UnrelatedPackageSetting>", packages, StringComparison.Ordinal);
        Assert.Equal(string.Empty, error.ToString());
    }

    [Fact]
    public async Task CreatesMigrationProjectWithASeparateCentralPackageFile()
    {
        using var fixture = new AddFixture();
        fixture.WriteApplication("Example.App");
        fixture.WriteCentralPackages();
        var migrationPackagesPath = Path.Combine(fixture.Root, "Database", "Directory.Packages.props");
        fixture.WriteCentralPackagesAt(migrationPackagesPath);
        var migration = Path.Combine(fixture.Root, "Database", "Separate", "Example.Database.csproj");
        using var output = new StringWriter();
        using var error = new StringWriter();

        var exitCode = await fixture.Application(output, error).RunAsync(
            new[]
            {
                "add", "--project", fixture.ApplicationProject,
                "--migration-project", migration,
                "--create-migration-project", "--provider", "SqlServer",
            },
            CancellationToken.None);

        Assert.Equal(0, exitCode);
        var targetPackages = File.ReadAllText(fixture.PackagesPath);
        var migrationPackages = File.ReadAllText(migrationPackagesPath);
        var migrationProject = File.ReadAllText(migration);
        Assert.Contains("<PackageVersion Include=\"CobaltumOrm\" Version=\"1.0.1\" />", targetPackages, StringComparison.Ordinal);
        Assert.Contains("<PackageVersion Include=\"Microsoft.Data.SqlClient\" Version=\"7.0.2\" />", targetPackages, StringComparison.Ordinal);
        Assert.Contains("<PackageVersion Include=\"CobaltumOrm.Migrations.SqlServer\" Version=\"1.0.1\" />", migrationPackages, StringComparison.Ordinal);
        Assert.Contains("<PackageVersion Include=\"Microsoft.Data.SqlClient\" Version=\"7.0.2\" />", migrationPackages, StringComparison.Ordinal);
        Assert.DoesNotContain("Version=", migrationProject, StringComparison.Ordinal);
        Assert.Contains("<UnrelatedPackageSetting>keep</UnrelatedPackageSetting>", targetPackages, StringComparison.Ordinal);
        Assert.Contains("<UnrelatedPackageSetting>keep</UnrelatedPackageSetting>", migrationPackages, StringComparison.Ordinal);
        Assert.Contains($"Updated {fixture.PackagesPath}", output.ToString(), StringComparison.Ordinal);
        Assert.Contains($"Updated {migrationPackagesPath}", output.ToString(), StringComparison.Ordinal);
        Assert.Equal(string.Empty, error.ToString());
    }

    [Fact]
    public async Task RejectsMigrationCentralConflictBeforeCreatingOrChangingFiles()
    {
        using var fixture = new AddFixture();
        fixture.WriteApplication("Example.App");
        fixture.WriteCentralPackages();
        var migrationPackagesPath = Path.Combine(fixture.Root, "Database", "Directory.Packages.props");
        fixture.WriteCentralPackagesAt(
            migrationPackagesPath,
            ("CobaltumOrm.Migrations.SqlServer", "9.9.9"));
        var migration = Path.Combine(fixture.Root, "Database", "New", "Example.Database.csproj");
        var projectBefore = File.ReadAllBytes(fixture.ApplicationProject);
        var targetPackagesBefore = File.ReadAllBytes(fixture.PackagesPath);
        var migrationPackagesBefore = File.ReadAllBytes(migrationPackagesPath);
        using var output = new StringWriter();
        using var error = new StringWriter();

        var exitCode = await fixture.Application(output, error).RunAsync(
            new[]
            {
                "add", "--project", fixture.ApplicationProject,
                "--migration-project", migration,
                "--create-migration-project", "--provider", "SqlServer",
            },
            CancellationToken.None);

        Assert.Equal(2, exitCode);
        Assert.Contains("central PackageVersion", error.ToString(), StringComparison.Ordinal);
        Assert.Equal(projectBefore, File.ReadAllBytes(fixture.ApplicationProject));
        Assert.Equal(targetPackagesBefore, File.ReadAllBytes(fixture.PackagesPath));
        Assert.Equal(migrationPackagesBefore, File.ReadAllBytes(migrationPackagesPath));
        Assert.False(Directory.Exists(Path.GetDirectoryName(migration)!));
    }

    [Fact]
    public async Task CreationOutsideCentralManagementUsesTheTargetCentralDriverVersionExplicitly()
    {
        using var fixture = new AddFixture();
        fixture.WriteApplication("Example.App");
        fixture.WriteCentralPackages(("Npgsql", "8.0.0"));
        var migration = fixture.ExternalMigrationPath("Example.Database");
        using var output = new StringWriter();
        using var error = new StringWriter();

        var exitCode = await fixture.Application(output, error).RunAsync(
            new[]
            {
                "add", "--project", fixture.ApplicationProject,
                "--migration-project", migration,
                "--create-migration-project",
            },
            CancellationToken.None);

        Assert.Equal(0, exitCode);
        var project = File.ReadAllText(migration);
        Assert.Contains("<PackageReference Include=\"CobaltumOrm\" Version=\"1.0.1\" />", project, StringComparison.Ordinal);
        Assert.Contains("<PackageReference Include=\"CobaltumOrm.Migrations.PostgreSql\" Version=\"1.0.1\" />", project, StringComparison.Ordinal);
        Assert.Contains("<PackageReference Include=\"Npgsql\" Version=\"8.0.0\" />", project, StringComparison.Ordinal);
        Assert.DoesNotContain("VersionOverride", project, StringComparison.Ordinal);
        Assert.Equal(string.Empty, error.ToString());
    }

    [Fact]
    public void NormalizesInformationalVersionsWithoutRemovingPrereleaseLabels()
    {
        Assert.Equal(
            "1.2.3-rc.1",
            AddCommand.NormalizeInformationalVersion("1.2.3-rc.1+build.42"));
        Assert.Equal(
            "1.2.3-rc.1",
            AddCommand.NormalizeInformationalVersion("1.2.3-rc.1"));
    }

    [Fact]
    public async Task RejectsConflictingCentralPackageVersionsBeforeWritingEitherFile()
    {
        using var fixture = new AddFixture();
        fixture.WriteApplication("Example.App");
        fixture.WriteCentralPackages(("CobaltumOrm", "1.0.0"), ("CobaltumOrm", "2.0.0"));
        var migration = fixture.WriteMigration("Example.Database", "PostgreSql");
        var projectBefore = File.ReadAllBytes(fixture.ApplicationProject);
        var packagesBefore = File.ReadAllBytes(fixture.PackagesPath);
        using var output = new StringWriter();
        using var error = new StringWriter();

        var exitCode = await fixture.Application(output, error).RunAsync(
            new[]
            {
                "add", "--project", fixture.ApplicationProject,
                "--migration-project", migration,
            },
            CancellationToken.None);

        Assert.Equal(2, exitCode);
        Assert.Contains("conflicting PackageVersion", error.ToString(), StringComparison.Ordinal);
        Assert.Equal(projectBefore, File.ReadAllBytes(fixture.ApplicationProject));
        Assert.Equal(packagesBefore, File.ReadAllBytes(fixture.PackagesPath));
    }

    [Fact]
    public async Task PreservesBothFilesWhenTargetValidationFailsWithCentralManagement()
    {
        using var fixture = new AddFixture();
        fixture.WriteApplication("Example.App", generatedNamespace: "Example.Existing.Generated");
        fixture.WriteCentralPackages();
        var migration = fixture.WriteMigration("Example.Database", "PostgreSql");
        var projectBefore = File.ReadAllBytes(fixture.ApplicationProject);
        var packagesBefore = File.ReadAllBytes(fixture.PackagesPath);
        using var output = new StringWriter();
        using var error = new StringWriter();

        var exitCode = await fixture.Application(output, error).RunAsync(
            new[]
            {
                "add", "--project", fixture.ApplicationProject,
                "--migration-project", migration,
            },
            CancellationToken.None);

        Assert.Equal(2, exitCode);
        Assert.Contains("CobaltumOrmGeneratedNamespace", error.ToString(), StringComparison.Ordinal);
        Assert.Equal(projectBefore, File.ReadAllBytes(fixture.ApplicationProject));
        Assert.Equal(packagesBefore, File.ReadAllBytes(fixture.PackagesPath));
    }

    [Fact]
    public async Task UsesAnOlderConsistentCobaltumOrmVersionAlreadyInBothProjects()
    {
        using var fixture = new AddFixture();
        fixture.WriteApplication("Example.App", packageVersion: "1.2.3");
        var migration = fixture.WriteMigration("Example.Database", "PostgreSql", "1.2.3");
        using var output = new StringWriter();
        using var error = new StringWriter();

        var exitCode = await fixture.Application(output, error).RunAsync(
            new[]
            {
                "add", "--project", fixture.ApplicationProject,
                "--migration-project", migration,
            },
            CancellationToken.None);

        Assert.Equal(0, exitCode);
        var project = File.ReadAllText(fixture.ApplicationProject);
        Assert.Contains("<PackageReference Include=\"CobaltumOrm\" Version=\"1.2.3\" />", project, StringComparison.Ordinal);
        Assert.Contains("<PackageReference Include=\"CobaltumOrm.Migrations\" Version=\"1.2.3\" />", project, StringComparison.Ordinal);
        Assert.Contains("<PackageReference Include=\"CobaltumOrm.SourceGenerator\" Version=\"1.2.3\" PrivateAssets=\"all\" />", project, StringComparison.Ordinal);
        Assert.DoesNotContain("Version=\"1.0.1\"", project, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RejectsInconsistentCobaltumOrmVersionsBeforeWriting()
    {
        using var fixture = new AddFixture();
        fixture.WriteApplication("Example.App", packageVersion: "1.2.3");
        var migration = fixture.WriteMigration("Example.Database", "PostgreSql", "2.0.0");
        var projectBefore = File.ReadAllBytes(fixture.ApplicationProject);
        var migrationBefore = File.ReadAllBytes(migration);
        using var output = new StringWriter();
        using var error = new StringWriter();

        var exitCode = await fixture.Application(output, error).RunAsync(
            new[]
            {
                "add", "--project", fixture.ApplicationProject,
                "--migration-project", migration,
            },
            CancellationToken.None);

        Assert.Equal(2, exitCode);
        Assert.Contains("CobaltumORM package versions conflict", error.ToString(), StringComparison.Ordinal);
        Assert.Equal(projectBefore, File.ReadAllBytes(fixture.ApplicationProject));
        Assert.Equal(migrationBefore, File.ReadAllBytes(migration));
    }

    [Fact]
    public async Task AddHelpDescribesTheExistingProjectWorkflow()
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        var application = new ToolApplication(output, error, new RecordingProcessRunner());

        var exitCode = await application.RunAsync(new[] { "add", "--help" }, CancellationToken.None);

        Assert.Equal(0, exitCode);
        Assert.Contains("cobaltum add --project <path> --migration-project <path>", output.ToString(), StringComparison.Ordinal);
        Assert.Contains("--create-migration-project", output.ToString(), StringComparison.Ordinal);
        Assert.Equal(string.Empty, error.ToString());
    }

    private sealed class AddFixture : IDisposable
    {
        public AddFixture()
        {
            Root = Path.Combine(Path.GetTempPath(), "CobaltumOrm.Tool.Tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Root);
            ApplicationDirectory = Directory.CreateDirectory(Path.Combine(Root, "App")).FullName;
            ApplicationProject = Path.Combine(ApplicationDirectory, "Example.App.csproj");
            SourceFile = Path.Combine(ApplicationDirectory, "Existing.cs");
        }

        public string Root { get; }

        public string ApplicationDirectory { get; }

        public string ApplicationProject { get; }

        public string SourceFile { get; }

        public string PackagesPath => Path.Combine(Root, "Directory.Packages.props");

        private readonly List<string> _externalDirectories = new();

        public void WriteCentralPackages(params (string Id, string Version)[] packageVersions)
            => WriteCentralPackagesAt(PackagesPath, packageVersions);

        public void WriteCentralPackagesAt(string path, params (string Id, string Version)[] packageVersions)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var packageLines = string.Join(
                "\n",
                packageVersions.Select(package =>
                    $"    <PackageVersion Include=\"{package.Id}\" Version=\"{package.Version}\" />"));
            File.WriteAllText(
                path,
                $"""
                <Project>
                  <PropertyGroup>
                    <ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally>
                    <UnrelatedPackageSetting>keep</UnrelatedPackageSetting>
                  </PropertyGroup>
                  <ItemGroup>
                {packageLines}
                  </ItemGroup>
                </Project>
                """);
        }

        public string ExternalMigrationPath(string projectName)
        {
            var directory = Path.Combine(
                Path.GetDirectoryName(Root)!,
                Path.GetFileName(Root) + "-outside");
            _externalDirectories.Add(directory);
            return Path.Combine(directory, projectName + ".csproj");
        }

        public void WriteApplication(string rootNamespace, string? generatedNamespace = null, string? packageVersion = null)
        {
            var namespaceElement = generatedNamespace is null
                ? string.Empty
                : $"    <CobaltumOrmGeneratedNamespace>{generatedNamespace}</CobaltumOrmGeneratedNamespace>\n";
            var packageElement = packageVersion is null
                ? string.Empty
                : $"    <PackageReference Include=\"CobaltumOrm\" Version=\"{packageVersion}\" />\n";
            File.WriteAllText(
                ApplicationProject,
                $"""
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net10.0</TargetFramework>
                    <RootNamespace>{rootNamespace}</RootNamespace>
                {namespaceElement}  </PropertyGroup>
                  <ItemGroup>
                    <Compile Include="Existing.cs" />
                    <PackageReference Include="Other.Package" Version="1.2.3" />
                {packageElement}  </ItemGroup>
                </Project>
                """);
            File.WriteAllText(SourceFile, "// user source");
        }

        public string WriteMigration(string rootNamespace, string provider, string? cobaltumPackageVersion = null)
        {
            var path = MigrationPath(Path.GetFileName(rootNamespace));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var packageGroup = cobaltumPackageVersion is null
                ? string.Empty
                : $"""
                  <ItemGroup>
                    <PackageReference Include="CobaltumOrm" Version="{cobaltumPackageVersion}" />
                    <PackageReference Include="CobaltumOrm.Migrations" Version="{cobaltumPackageVersion}" />
                    <PackageReference Include="CobaltumOrm.SourceGenerator" Version="{cobaltumPackageVersion}" PrivateAssets="all" />
                  </ItemGroup>
                """;
            File.WriteAllText(
                path,
                $"""
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <OutputType>Exe</OutputType>
                    <TargetFramework>net10.0</TargetFramework>
                    <RootNamespace>{rootNamespace}</RootNamespace>
                    <CobaltumOrmMigrationProject>true</CobaltumOrmMigrationProject>
                    <CobaltumOrmDatabaseProvider>{provider}</CobaltumOrmDatabaseProvider>
                  </PropertyGroup>
                {packageGroup}
                </Project>
                """);
            return path;
        }

        public string MigrationPath(string projectName) =>
            Path.Combine(Root, "Database", projectName + ".csproj");

        public ToolApplication Application(StringWriter output, StringWriter error) =>
            new(output, error, new RecordingProcessRunner(), Root);

        public void Dispose()
        {
            foreach (var directory in _externalDirectories.Distinct(StringComparer.Ordinal))
            {
                if (Directory.Exists(directory))
                {
                    Directory.Delete(directory, recursive: true);
                }
            }

            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }
    }

    private static int CountOccurrences(string value, string needle)
    {
        var count = 0;
        var index = 0;
        while ((index = value.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += needle.Length;
        }

        return count;
    }

    private sealed class RecordingProcessRunner : IProcessRunner
    {
        public Task<int> RunAsync(System.Diagnostics.ProcessStartInfo startInfo, CancellationToken cancellationToken) =>
            Task.FromResult(0);
    }
}
