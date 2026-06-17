# roslyn-extractor

Roslyn-based extractor for **VB.NET** and **C#** used by graphify. Outputs
`{ "nodes": [...], "edges": [...], "raw_calls": [...] }` JSON to stdout, which
graphify ingests like any other per-file extraction fragment.

It exists because there is no tree-sitter grammar for VB.NET on PyPI, and Roslyn
gives real semantic resolution (base types, return/parameter/field types,
cross-file calls) instead of regex guesses.

## What it extracts

- **Structure** (`contains`, `imports`): classes, modules, interfaces, structs,
  enums + members, methods (Sub/Function), properties, events, fields,
  namespaces, imports.
- **Semantics** (`EXTRACTED`): `extends`, `implements`, `type_of`, `returns`,
  `uses_type` — resolved via the Roslyn `SemanticModel`.
- **Rationale** (the "why" layer): the leading `'` / `'''`-XML-doc comment that
  documents a declaration becomes a `rationale` node linked to the code by a
  `rationale_for` edge — the developers' own business-language description,
  harvested locally with no LLM. VB and C#.
- **Call graph** (`calls`): method invocations.
  - **Intra-file** → resolved precisely (semantic, then name match).
  - **Cross-file** → resolved semantically in `--project` mode; otherwise emitted
    as `raw_calls` and linked later by graphify's
    `resolve_cross_file_raw_calls` (conservative: unique label only).

## Modes

```
roslyn-extractor <file.vb|file.cs>                  # single file
roslyn-extractor --batch <f1> <f2> ...              # NDJSON, one line per file (JIT paid once)
roslyn-extractor --project <dir>                    # one Compilation over a dir — cross-file semantics
roslyn-extractor --project-filelist <list> [ctx]    # explicit file set + optional binding-only context
```

`--project-filelist` reads file paths from a text file (one per line), so it
bypasses the OS command-line length limit that `--batch` hits on large repos.
The optional second list is **context**: those files are compiled together for
binding (e.g. platform base classes) but do **not** produce nodes — only the
extract set does, and calls to context-only methods are dropped.

## Build

```
dotnet publish roslyn-extractor.csproj -c Release -r win-x64 --self-contained -o ../tools/roslyn
```

graphify looks for the binary at `<repo>/tools/roslyn/roslyn-extractor(.exe)`
(see `graphify/extract.py::_roslyn_exe`). The folder is gitignored — build it
locally; re-run after pulling extractor changes.

## Notes / limits

- Cross-file call resolution is only as good as the references available to the
  Compilation. Without the project's referenced assemblies (3rd-party DLLs,
  sibling project outputs in `bin/`), calls into those types stay unresolved —
  same-source-tree calls still resolve.
- `--batch` callers should run `resolve_cross_file_raw_calls` over the collected
  per-file results to turn `raw_calls` into cross-file `calls` edges.
- Confidence is always one of `EXTRACTED` / `INFERRED` / `AMBIGUOUS` (graphify's
  validator rejects others).
