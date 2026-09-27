// Re-wraps every docs page in the shared chrome (head, header, sidebar, pager, footer) and
// refreshes the marked header/footer regions of index.html. Article content is read from the
// current file each run, so this is idempotent. Run: node docs/scripts/site-chrome.cjs [page.html]
const fs = require("fs");
const path = require("path");
const DOCS = path.join(__dirname, "..");
const SITE = "https://spillerstech.us/PartnerCenterBridge/";
const REPO = "https://github.com/Spillers-Technology/PartnerCenterBridge";
const RELEASES = REPO + "/releases/latest";

const PAGES = [
  { file: "getting-started.html", nav: "Get started", group: "Get started", title: "Get started",
    desc: "Download the signed Windows build of Partner Center Bridge, verify it, run it, create the administrator, and connect Microsoft 365 through GDAP and the Secure Application Model.",
    og: "pcbridge-dashboard.jpg" },
  { file: "local-workbench.html", nav: "Local Workbench", group: "Get started", title: "Local Workbench", og: "pcbridge-dashboard.jpg" },
  { file: "operations.html", nav: "Operations", group: "Guides", title: "Operations", og: "pcbridge-access-parity.jpg" },
  { file: "workflows.html", nav: "Workflows", group: "Guides", title: "Workflows", og: "pcbridge-workflows.jpg" },
  { file: "authentication.html", nav: "Authentication", group: "Guides", title: "Authentication", og: "pcbridge-login.jpg" },
  { file: "sam-bootstrap.html", nav: "SAM bootstrap", group: "Guides", title: "SAM bootstrap", og: "pcbridge-tenants.jpg" },
  { file: "config-snapshots.html", nav: "Config snapshots", group: "Guides", title: "Config snapshots", og: "pcbridge-config-snapshots.jpg" },
  { file: "mcp-server.html", nav: "MCP server", group: "Guides", title: "MCP server", og: "pcbridge-approvals.jpg" },
  { file: "architecture.html", nav: "Architecture", group: "Reference", title: "Architecture", og: "pcbridge-dashboard.jpg" },
  { file: "deployment.html", nav: "Run as a server", group: "Reference", title: "Run as a server",
    desc: "Run Partner Center Bridge as one shared instance for a team: Docker Compose with the published ghcr.io images, and Kubernetes with Kustomize, Flux and SOPS.",
    og: "pcbridge-tenants.jpg" },
];
const GROUPS = ["Get started", "Guides", "Reference"];

const ICON = {
  download: '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true"><path d="M12 3v12m0 0 5-5m-5 5-5-5M4 17v2a2 2 0 0 0 2 2h12a2 2 0 0 0 2-2v-2"/></svg>',
  github: '<svg viewBox="0 0 24 24" fill="currentColor" aria-hidden="true"><path d="M12 .5a11.5 11.5 0 0 0-3.64 22.41c.58.1.79-.25.79-.56v-2c-3.2.7-3.87-1.37-3.87-1.37-.53-1.33-1.28-1.69-1.28-1.69-1.05-.72.08-.7.08-.7 1.16.08 1.77 1.19 1.77 1.19 1.03 1.77 2.7 1.26 3.36.96.1-.75.4-1.26.73-1.55-2.56-.29-5.25-1.28-5.25-5.69 0-1.26.45-2.29 1.19-3.1-.12-.29-.52-1.46.11-3.05 0 0 .97-.31 3.17 1.18a11 11 0 0 1 5.77 0c2.2-1.49 3.17-1.18 3.17-1.18.63 1.59.23 2.76.11 3.05.74.81 1.19 1.84 1.19 3.1 0 4.42-2.7 5.39-5.27 5.68.41.36.78 1.06.78 2.14v3.17c0 .31.21.67.8.56A11.5 11.5 0 0 0 12 .5Z"/></svg>',
};
const BRAND_SVG = '<svg viewBox="0 0 24 24" aria-hidden="true"><rect class="brand-mark-bg" width="24" height="24" rx="6" fill="#4f46e5"/><path class="brand-mark-fg" d="M5 15.5 12 6l3.2 4.4L18 7.5" fill="none" stroke="#fff" stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round"/><path class="brand-mark-fg" d="M6 17.5h12" stroke="#fff" stroke-width="1.8" stroke-linecap="round"/></svg>';
const FAVICON = "data:image/svg+xml,%3Csvg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 24 24'%3E%3Crect width='24' height='24' rx='6' fill='%234f46e5'/%3E%3Cpath d='M5 15.5 12 6l3.2 4.4L18 7.5' fill='none' stroke='white' stroke-width='1.8' stroke-linecap='round' stroke-linejoin='round'/%3E%3Cpath d='M6 17.5h12' stroke='white' stroke-width='1.8' stroke-linecap='round'/%3E%3C/svg%3E";

function header(isIndex) {
  const h = (a) => (isIndex ? a : "index.html" + a);
  return `<!-- @header: shared across every page; keep in sync -->
<header class="site-header">
  <div class="wrap topbar">
    <a class="brand" href="index.html" aria-label="Partner Center Bridge home">
      ${BRAND_SVG}
      <span class="brand-name">Partner Center Bridge</span>${isIndex ? "" : '<span class="brand-tag">Docs</span>'}
    </a>
    <button class="nav-toggle" type="button" aria-expanded="false" aria-controls="site-nav" hidden><span class="bars" aria-hidden="true"></span>Menu</button>
    <nav class="site-nav" id="site-nav" aria-label="Primary">
      <a href="${h("#features")}">Features</a>
      <a href="${h("#how-it-works")}">How it works</a>
      <a href="${h("#security")}">Security</a>
      <a href="getting-started.html"${isIndex ? "" : ' aria-current="page"'}>Docs</a>
      <a href="${h("#faq")}">FAQ</a>
      <span class="nav-sep" aria-hidden="true"></span>
      <a class="nav-icon" href="${REPO}" rel="noopener">${ICON.github}<span>GitHub</span></a>
      <a class="nav-download" href="${RELEASES}">${ICON.download}<span>Download</span></a>
    </nav>
  </div>
</header>
<!-- /@header -->`;
}

function footer(isIndex) {
  const h = (a) => (isIndex ? a : "index.html" + a);
  const docs = PAGES.map((p) => `          <li><a href="${p.file}">${p.nav}</a></li>`).join("\n");
  return `<!-- @footer: shared across every page; keep in sync -->
<footer class="site-footer">
  <div class="wrap footer-grid">
    <div class="footer-about">
      <a class="brand" href="index.html">
        ${BRAND_SVG}
        <span class="brand-name">Partner Center Bridge</span>
      </a>
      <p>A free, open-source Microsoft 365 operations workbench for MSP technicians, from Spillers Technology.</p>
      <p class="footer-release"><a class="btn btn-secondary btn-sm" href="${RELEASES}">${ICON.download}Download for Windows</a></p>
    </div>
    <nav class="footer-col" aria-label="Product">
      <h2>Product</h2>
      <ul>
        <li><a href="${h("#features")}">Features</a></li>
        <li><a href="${h("#how-it-works")}">How it works</a></li>
        <li><a href="${h("#security")}">Security</a></li>
        <li><a href="${h("#status")}">Maturity</a></li>
        <li><a href="${h("#screens")}">Screens</a></li>
        <li><a href="${h("#faq")}">FAQ</a></li>
      </ul>
    </nav>
    <nav class="footer-col" aria-label="Documentation">
      <h2>Docs</h2>
      <ul>
${docs}
      </ul>
    </nav>
    <nav class="footer-col" aria-label="Project">
      <h2>Project</h2>
      <ul>
        <li><a href="${RELEASES}">Releases</a></li>
        <li><a href="${REPO}" rel="noopener">GitHub repository</a></li>
        <li><a href="${REPO}/blob/main/LICENSE" rel="noopener">MIT license</a></li>
        <li><a href="${REPO}/blob/main/CONTRIBUTING.md" rel="noopener">Contributing</a></li>
        <li><a href="${REPO}/blob/main/SECURITY.md" rel="noopener">Security policy</a></li>
        <li><a href="https://spillerstech.us/">Spillers Technology</a></li>
      </ul>
    </nav>
  </div>
  <div class="wrap footer-legal">
    <p>MIT licensed. Built by <a href="https://spillerstech.us/">Spillers Technology</a>.</p>
    <p>Partner Center Bridge is an independent open-source project. It is not affiliated with, sponsored by, or endorsed by Microsoft. Microsoft, Microsoft 365, Microsoft Entra, Intune, Exchange Online and Partner Center are trademarks of the Microsoft group of companies.</p>
  </div>
</footer>
<!-- /@footer -->`;
}

function sidebar(current) {
  const groups = GROUPS.map((g) => {
    const items = PAGES.filter((p) => p.group === g)
      .map((p) => `          <li><a href="${p.file}"${p.file === current.file ? ' aria-current="page"' : ""}>${p.nav}</a></li>`)
      .join("\n");
    return `      <div class="docs-nav-group">
        <p class="docs-nav-title">${g}</p>
        <ul>
${items}
        </ul>
      </div>`;
  }).join("\n");
  return `<aside class="docs-sidebar" aria-label="Documentation">
    <button class="sidebar-toggle" type="button" aria-expanded="false" aria-controls="docs-nav" hidden><span><span class="label">Docs</span>${current.nav}</span><span class="chev" aria-hidden="true"></span></button>
    <nav class="docs-nav" id="docs-nav" aria-label="Documentation pages">
${groups}
      <div class="docs-nav-download">
        <p><strong>Partner Center Bridge for Windows</strong>Signed, self-contained, no server needed.</p>
        <a class="btn btn-primary btn-sm" href="${RELEASES}">${ICON.download}Download</a>
      </div>
    </nav>
  </aside>`;
}

function pager(current) {
  const i = PAGES.indexOf(current);
  const prev = PAGES[i - 1], next = PAGES[i + 1];
  let s = `<nav class="doc-pager" aria-label="Previous and next page">\n`;
  if (prev) s += `  <a class="prev" href="${prev.file}"><span>&larr; Previous</span>${prev.nav}</a>\n`;
  if (next) s += `  <a class="next" href="${next.file}"><span>Next &rarr;</span>${next.nav}</a>\n`;
  return s + `</nav>`;
}

function head(p, desc, hasLightbox) {
  const url = SITE + p.file;
  const title = `${p.title} - Partner Center Bridge docs`;
  return `<!DOCTYPE html>
<html lang="en">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title>${title}</title>
<meta name="description" content="${desc}">
<meta name="color-scheme" content="light dark">
<meta name="theme-color" content="#ffffff" media="(prefers-color-scheme: light)">
<meta name="theme-color" content="#0b1120" media="(prefers-color-scheme: dark)">
<link rel="canonical" href="${url}">
<meta property="og:type" content="article">
<meta property="og:site_name" content="Partner Center Bridge">
<meta property="og:title" content="${title}">
<meta property="og:description" content="${desc}">
<meta property="og:url" content="${url}">
<meta property="og:image" content="${SITE}assets/screenshots/${p.og}">
<meta property="og:image:width" content="1440">
<meta property="og:image:height" content="960">
<meta name="twitter:card" content="summary_large_image">
<link rel="icon" href="${FAVICON}">
<link rel="preconnect" href="https://fonts.googleapis.com">
<link rel="preconnect" href="https://fonts.gstatic.com" crossorigin>
<link rel="stylesheet" href="https://fonts.googleapis.com/css2?family=Inter:wght@400;500;600;700;800&amp;display=swap">
<link rel="stylesheet" href="styles.css">${hasLightbox ? '\n<link rel="stylesheet" href="assets/lightbox.css">' : ""}
</head>`;
}

function buildDoc(p) {
  const file = path.join(DOCS, p.file);
  const src = fs.readFileSync(file, "utf8");
  const a = src.indexOf('<article class="doc">');
  const b = src.indexOf("</article>", a);
  if (a < 0 || b < 0) throw new Error("no article in " + p.file);
  let body = src.slice(a + '<article class="doc">'.length, b);
  body = body.replace(/\s*<p class="doc-eyebrow">[\s\S]*?<\/p>\s*/, "\n");
  body = body.replace(/\s*<nav class="doc-pager"[\s\S]*?<\/nav>\s*/, "\n");
  body = body.trim();
  let desc = p.desc;
  if (!desc) {
    const m = src.match(/<meta name="description" content="([^"]*)">/);
    desc = m[1];
  }
  const hasLightbox = /data-lightbox/.test(body);
  const out = `${head(p, desc, hasLightbox)}
<body>
<a class="skip-link" href="#content">Skip to content</a>
${header(false)}

<div class="docs-shell">
  <!-- @sidebar: generated from the shared page list; keep in sync -->
  ${sidebar(p)}
  <!-- /@sidebar -->

  <main id="content" class="docs-main">
<article class="doc">
<p class="doc-eyebrow"><a href="getting-started.html">Docs</a> <span aria-hidden="true">/</span> <span>${p.group}</span></p>
${body}

${pager(p)}
</article>
  </main>

  <aside class="docs-toc" aria-label="On this page">
    <p class="docs-toc-title">On this page</p>
  </aside>
</div>

${footer(false)}
<script src="assets/site.js"></script>${hasLightbox ? '\n<script src="assets/lightbox.js"></script>' : ""}
</body>
</html>
`;
  fs.writeFileSync(file, out);
}

function refreshIndex() {
  const file = path.join(DOCS, "index.html");
  let s = fs.readFileSync(file, "utf8");
  const swap = (tag, html) => {
    const re = new RegExp(`<!-- @${tag}[\\s\\S]*?<!-- /@${tag} -->`);
    if (!re.test(s)) throw new Error("index.html missing @" + tag);
    s = s.replace(re, () => html);
  };
  swap("header", header(true));
  swap("footer", footer(true));
  fs.writeFileSync(file, s);
}

const only = process.argv[2];
for (const p of PAGES) if (!only || only === p.file) buildDoc(p);
if (!only || only === "index.html") {
  if (fs.readFileSync(path.join(DOCS, "index.html"), "utf8").includes("<!-- @header")) refreshIndex();
  else console.log("index.html has no markers yet; skipped");
}
console.log("done");
