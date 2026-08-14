# CobaltumORM coding agent tools

English | [日本語](agent-tools.ja.md)

Use `assistant init` to add CobaltumORM instructions to an existing project. Use `inspect` and
`doctor` before a build, or expose the same analysis through the MCP server. The tool evaluates
the selected build configuration; it does not connect to a database or execute migrations.

## Existing project setup

Install the global tool, or update an existing installation:

```console
dotnet tool update --global CobaltumOrm.Tool --version tool-version
dotnet tool install --global CobaltumOrm.Tool --version tool-version
```

Run the second command only when the tool is not installed. If the project already has an
application project and a migration project, connect them first:

```console
cobaltum add --project <app.csproj> --migration-project <migration.csproj>
```

`--create-migration-project` is an opt-in alternative when the migration project does not yet
exist. It creates the missing migration project without replacing existing files:

```console
cobaltum add --project <app.csproj> --migration-project <migration.csproj> \
  --create-migration-project
```

Initialize instructions, inspect the evaluated project, check its configuration, and build it:

```console
cobaltum assistant init --project <app.csproj>
cobaltum inspect --project <app.csproj> --format json
cobaltum doctor --project <app.csproj> --format json
dotnet build <app.csproj>
```

`assistant init` uses `auto` by default. It creates `.cobaltum/assistant.md`, updates every
recognized adapter already present in the project, and creates `AGENTS.md` when no adapter is
present. Select a target explicitly when only one instruction format should be managed:

| Target | Use when |
| --- | --- |
| `agents` | The project uses `AGENTS.md`. |
| `claude` | The project uses `CLAUDE.md` for Claude Code. |
| `cursor` | The project uses Cursor rules. |
| `copilot` | The project uses GitHub Copilot instructions. |
| `all` | The project needs all supported instruction formats. |

Pass a target with `--target`, for example:

```console
cobaltum assistant init --project <app.csproj> --target claude
```

## Files managed by `assistant init`

Every run manages `.cobaltum/assistant.md`. It contains the CobaltumORM instructions. The selected
adapter contains a short instruction that directs the coding agent to that file.

| Target selection | Adapter files created or updated |
| --- | --- |
| `auto` | Every recognized existing adapter, or `AGENTS.md` if none exists |
| `agents` | `AGENTS.md` |
| `claude` | `CLAUDE.md` |
| `cursor` | `.cursor/rules/cobaltum.mdc` |
| `copilot` | `.github/copilot-instructions.md` |
| `all` | `AGENTS.md`, `CLAUDE.md`, `.cursor/rules/cobaltum.mdc`, and `.github/copilot-instructions.md` |

The command owns only the CobaltumORM-managed block. It preserves user content outside that block.
For an existing `AGENTS.md`, `CLAUDE.md`, or `.github/copilot-instructions.md` without a managed
block, it appends one. It refuses to overwrite an unrecognized dedicated file at
`.cobaltum/assistant.md` or `.cursor/rules/cobaltum.mdc`.

Before creating directories or writing a file, the command plans and validates every selected
target. Re-running the same command leaves the managed files unchanged and reports them as
unchanged. The command does not provide a broader transactional guarantee.

## Project reports

`inspect` evaluates the application or query project and runs CobaltumORM analysis without
publishing generated source files. Its JSON output includes the selected project path, target
framework, configuration, namespaces, provider, evaluated input paths and references, generated
artifact metadata, analyzed and processed sources, and diagnostics. It exits with code 0 when
generation succeeds and code 1 when generation reports an error.

`doctor` evaluates the same project and returns a project status, checks, and generation
diagnostics. The checks cover the target framework, CobaltumORM wiring, database provider,
generated namespace, migration inputs, and generation diagnostics. It exits with code 1 only when
the overall status is `error`; `ok` and `warning` exit with code 0. Invalid command options use the
normal CLI usage error path and exit with code 2.

Neither command accesses a database, executes migrations, or publishes generated files. By
default, project evaluation restores packages. That restore can update normal files under `obj`.
Pass `--no-restore` when the project is already restored and no restore should run.

## MCP server setup

Start the stdio server with an absolute application project path:

```console
cobaltum mcp --project <absolute-app.csproj>
```

Use an absolute path because an MCP client may start the server from a different working directory.
The project is selected when the server starts.

### Codex

The following syntax was verified with `codex mcp add --help`:

```console
codex mcp add cobaltum -- cobaltum mcp --project /absolute/path/App.csproj
```

### Claude Code

```console
claude mcp add cobaltum -- cobaltum mcp --project /absolute/path/App.csproj
```

See the [Claude Code MCP documentation](https://docs.anthropic.com/en/docs/claude-code/mcp).

### Visual Studio Code and GitHub Copilot

Create `.vscode/mcp.json` with the server definition:

```json
{
  "servers": {
    "cobaltum": {
      "command": "cobaltum",
      "args": ["mcp", "--project", "/absolute/path/App.csproj"]
    }
  }
}
```

Follow the [GitHub Copilot MCP setup instructions](https://docs.github.com/en/copilot/how-tos/provide-context/use-mcp-in-your-ide/extend-copilot-chat-with-mcp).

### Other stdio clients

Configure the client with this command and argument pair:

```text
command: cobaltum
args: mcp --project /absolute/path/App.csproj
```

## MCP tools and resources

The server exposes these five tools:

| Tool | Result |
| --- | --- |
| `inspect_project` | Project and generated-artifact metadata plus diagnostics. |
| `doctor_project` | Project checks and generation diagnostics. |
| `list_generated_artifacts` | Metadata for generated artifacts held in memory. |
| `read_generated_artifact` | Source and metadata for one artifact returned by `list_generated_artifacts`. |
| `explain_diagnostic` | The English or Japanese documentation section for a supported diagnostic. |

It also exposes these seven resources:

- `cobaltum://docs/quick-reference/en`
- `cobaltum://docs/quick-reference/ja`
- `cobaltum://docs/recipes/en`
- `cobaltum://docs/recipes/ja`
- `cobaltum://docs/diagnostics/en`
- `cobaltum://docs/diagnostics/ja`
- `cobaltum://docs/llms.txt`

All five tools are annotated read-only and closed-world. They do not connect to a database,
execute migrations, or publish generated files. The server accepts the project path only at
startup, so tool calls cannot supply an arbitrary project path. `read_generated_artifact` accepts
only an exact artifact name returned by `list_generated_artifacts`, not a path.

The resources are embedded in the installed tool and provide deterministic local retrieval of the
reference material. An external vector database or RAG service is not required for the initial
integration.

MCP project evaluation has the same restore behavior as `inspect` and `doctor`: MSBuild restore
can update normal files under `obj` unless the server is started with `--no-restore`.

## Agent workflow

1. Call `inspect_project` and address each returned diagnostic.
2. Call `explain_diagnostic` for a documented diagnostic code and the required language.
3. Call `list_generated_artifacts`, then `read_generated_artifact` for an exact returned name when generated source needs review.
4. Call `doctor_project` and resolve error checks.
5. Run `dotnet build <app.csproj>`.
