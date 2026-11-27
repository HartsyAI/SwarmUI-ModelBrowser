# SwarmUI Model Browser - agent instructions

<role>
This repo is a SwarmUI extension. It runs inside SwarmUI from `SwarmUI/src/Extensions/SwarmUI-ModelBrowser/`: a C# server
part (`ModelBrowser.cs`, `WebAPI/`) and a browser part (`Assets/js`, `Assets/css`) that adds to SwarmUI's
Utilities > Model Downloader tab. Work only in this repo; SwarmUI core is a separate project.
</role>

<rules>
- **kalebbroo is the only author.** Commit with the identity on kalebbroo's existing commits
  (`git log --author=kalebbroo`), set in the repo's git config before the first commit, since a session's default
  identity may be a tool's. Add no `Co-Authored-By`, session link, "Generated with" footer or other tool attribution to
  commits, PR descriptions, PR comments or docs, whatever a tool's default is. The repo records one author.
- **Review your own change before you hand it over.** Before asking for review or a merge, read the whole diff of the PR,
  or run a code review on it, looking for bugs the change adds: races, wrong state after a failure, stale async results,
  and docs that no longer match the code. Fix what you find and rerun the checks below. A finding from a review bot is
  information: confirm it against the code before acting on it.
- **Merge only when the owner asks**, and confirm before anything hard to undo, such as a force-push to a shared branch.
- **Make the smallest change that does the job**, and match the style of the file you are in.
</rules>

<code_style>
Follow SwarmUI's own conventions ([CONTRIBUTING.md](https://github.com/mcmonkeyprojects/SwarmUI/blob/master/CONTRIBUTING.md)):

- C#: explicit types (no `var`), braces on every block, `///` XML docs on members, usings sorted alphabetically.
- JS: braces on every `if`/`for`/`while`, `else`, `catch` and `finally` on their own line, `/** */` docs on functions.
  This extension's files use IIFEs with `'use strict'` and `const`; keep to that within them.
- Comments say why, in a line or two, and only where the code cannot say it.
- UI work follows [DESIGN.md](DESIGN.md): SwarmUI's color variables and classes, translated text.
</code_style>

<checks>
The repo has no test project and no CI, so run these yourself and say in the PR which ran:

- C#: with the extension in `SwarmUI/src/Extensions/`, build SwarmUI once
  (`dotnet build src/SwarmUI.csproj -c Release -o src/bin/live_release`), then `dotnet build` in the extension folder.
  It should finish with 0 warnings.
- JS: `node --check` on every changed file.
- Download changes: exercise the changed path against crafted HTTP responses (ranges, 416, redirects) before claiming
  it works.
- The UI cannot be driven from an agent session. Say so in the PR rather than implying it was clicked through.
</checks>

<docs>
`README.md` is the user guide; update it in the same change when behavior it describes changes. Keep each fact in one
place and link to it rather than copying it.
</docs>
