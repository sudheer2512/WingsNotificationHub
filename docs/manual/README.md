# Wings Notification Hub user manual

One source, two outputs:

| File | What it is |
|---|---|
| `user-manual.pdf` | A4 PDF with cover, contents with page numbers, bookmarks and running heads |
| `user-manual.html` | One self-contained file: fonts and images are embedded, no network needed |
| `src/parts/*.html` | The content, in reading order |
| `src/manual.css` | The look, for screen and print |
| `src/rail.js` | Side rail, search and phone menu of the HTML file |
| `src/fonts/`, `src/brand.svg` | IBM Plex Sans (SIL Open Font License) and the Wings mark |
| `images/` | Screenshots, cropped to the relevant region |
| `build.mjs` | Builds both outputs |

## Rebuild

You need Node 20 or later and Google Chrome.

```bash
cd docs/manual
npm install
npm run build
```

`build.mjs` drives your installed Chrome through `playwright-core`. To use another Chromium build, set `CHROME_PATH`
to its executable. `npm install` creates `node_modules/` here; it is ignored by the repository's `.gitignore`.

The PDF is rendered at least twice: the first pass finds the page of every heading, the next pass writes those numbers
into the contents, part openers, cross-references and index. The build stops if the numbers do not settle.

## Editing

Write plain HTML in `src/parts/`. The build expands a few shorthands:

| Write | You get |
|---|---|
| `<h2 id="ch-x" data-ix="Term one\|Term two">Title</h2>` | A numbered chapter (`h3` for a section). `data-ix` adds index entries. |
| `<h2 id="app-x" appendix="1">` | An appendix, lettered in order |
| `<h2 ... class="flow">` | A chapter that does not start a new PDF page |
| `<fig src="x.jpg" cap="Caption" w="60" pins="1@12,30 2@55,8"/>` | A numbered figure, `w` % wide, with numbered callouts at x,y % |
| `<st>Delivered</st>` | A status chip with the console's own icon and tone. Unknown words need `tone="good\|warn\|bad\|info\|maint\|off"`. |
| `<x to="sec-retry"/>` | A link such as “section 6.4 on page 21”. The page part appears only in the PDF. |
| `<aside class="tip">`, `note`, `warn` | A callout panel |
| `<y/>`, `<n/>` | Yes and No marks for the role matrix |

Screens in the images come from a demo instance with seeded data. If you retake them, use a light theme, a viewport of
about 1360×860 at device scale 1.5, and JPEG quality about 78. Never capture a password or secret.
