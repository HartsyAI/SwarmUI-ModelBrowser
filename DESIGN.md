# Model Browser UI design

The Model Browser lives inside SwarmUI's Utilities > Model Downloader tab, so it should look and behave like part of
SwarmUI in every theme a user picks. When in doubt, copy how SwarmUI core does it.

## Colors

Use SwarmUI's theme variables, never hard-coded colors, so every theme (`modern_dark`, `modern_light` and the rest)
restyles the extension too. The main ones:

| Variable | Use |
|---|---|
| `--background`, `--background-soft`, `--background-gray` | Page, panel and inset backgrounds |
| `--text`, `--text-soft` | Body text and secondary text |
| `--light-border` | Borders and dividers |
| `--emphasis` | Accents, selection, links |
| `--button-background`, `--button-text` | Anything drawn as a button outside `basic-button` |
| `--red`, `--green`, `--background-danger` | Error and success states |

If no variable fits, use the closest existing one rather than a new name: themes only define SwarmUI's own variables,
so an invented one always falls back to whatever is hard-coded beside it.

## Reuse SwarmUI's classes

Build controls from SwarmUI's own classes and add extension styling only on top:

| Need | SwarmUI class |
|---|---|
| Buttons | `basic-button` |
| Dropdowns and text inputs | `auto-dropdown`, `auto-text`, `auto-text-wide` |
| Panels | Bootstrap `card`, `card-header`, `card-body`, `card-img-top` (as core uses them) |
| Model cards and their menu | `model-block`, `model-descblock`, `model-block-menu-button` |
| Popover menus | `sui-popover`, `sui_popover_model_button`, opened with `doPopover()` |
| Result grids | `browser-content-container` |

Extension-only classes start with `model-browser-`. Style SwarmUI classes only beneath a `model-browser-` container, so
the rest of SwarmUI is never affected. Select by class, not by element ID, as SwarmUI's contributor guidelines ask.

## Reuse SwarmUI's JS helpers

Use core's helpers instead of writing new ones: `createDiv`, `escapeHtml`, `stripHtmlToText`, `fileSizeStringify`,
`copyText`, `showError`, `doPopover`, and `genericRequest` (through `ModelBrowser.Utils.genericRequestAsync`). Escape
anything from a provider before it goes into HTML.

## Text

Pass every user-visible string through `translate()`, or give static markup the `translate` class and call
`applyTranslations()` after inserting it. Provider values (model names, base models, tags, file formats) are identifiers
and stay untranslated.

## Layout

- Use `rem` and the spacing SwarmUI uses nearby. Keep controls on SwarmUI's rows and sizes rather than custom ones.
- It must work in current Chrome, Firefox and Safari, and on phone-width screens (Android Chrome, iOS Safari).
- Check a visual change in at least one dark and one light theme.
- Add no fonts, icon sets or CSS/JS frameworks; use what SwarmUI already loads.

## Known exceptions

These predate this guide. Do not copy them; fix them when you work nearby:

- The download progress bar colors are hard-coded (`#0000aa`, `#aaaa00`, `#aa0000`, `#00aa00`).
- The `warn` and `danger` card badges (early access, NSFW ratings, taken down) use `--warning-bg`, `--warning-border`,
  `--danger-bg` and `--danger-border`, which no SwarmUI theme defines, so their fallback colors always show.
- The layout overrides are scoped with `#Utilities-ModelDownloader-Tab`, core's tab ID, because the tab has no class to
  scope by.
