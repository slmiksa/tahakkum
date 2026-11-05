// Serves the license dashboard. It needs only Node.js 18 or newer, nothing to install.
//
//   node server.mjs
//
// PORT and HOST change where it listens. By default it answers on this machine only.
// TLS_CERT and TLS_KEY (PEM files) make it serve HTTPS. It refuses to listen beyond this
// machine without them, because the administrator's password would cross the network unprotected.
import { readFileSync } from "node:fs";
import { readFile } from "node:fs/promises";
import { createServer as createHttpServer } from "node:http";
import { createServer as createHttpsServer } from "node:https";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";

const here = dirname(fileURLToPath(import.meta.url));
const port = Number(process.env.PORT ?? 8080);
const host = process.env.HOST ?? "127.0.0.1";
const tls = process.env.TLS_CERT && process.env.TLS_KEY
  ? { cert: readFileSync(process.env.TLS_CERT), key: readFileSync(process.env.TLS_KEY) }
  : null;

if (!tls && host !== "127.0.0.1" && host !== "localhost") {
  console.error("Refusing to listen on " + host + " without HTTPS. Set TLS_CERT and TLS_KEY.");
  process.exit(1);
}

// Only these are served, so this script and anything else in the folder never can be.
const FILES = {
  "/": ["index.html", "text/html; charset=utf-8"],
  "/index.html": ["index.html", "text/html; charset=utf-8"],
  "/app.js": ["app.js", "text/javascript; charset=utf-8"],
  "/config.js": ["config.js", "text/javascript; charset=utf-8"],
  "/styles.css": ["styles.css", "text/css; charset=utf-8"],
  "/vendor/supabase.js": ["vendor/supabase.js", "text/javascript; charset=utf-8"],
  "/logo.svg": ["logo.svg", "image/svg+xml"],
  "/icon.svg": ["icon.svg", "image/svg+xml"],
  "/wordmark.png": ["wordmark.png", "image/png"],
};

const HEADERS = {
  "Content-Security-Policy": "default-src 'none'; script-src 'self'; style-src 'self'; img-src 'self' data:; connect-src 'self' https://*.supabase.co; base-uri 'none'; form-action 'none'; frame-ancestors 'none'",
  "X-Content-Type-Options": "nosniff",
  "X-Frame-Options": "DENY",
  "Referrer-Policy": "no-referrer",
  "X-Robots-Tag": "noindex",
  "Cache-Control": "no-cache",
};

const handler = async (req, res) => {
  const entry = Object.hasOwn(FILES, (req.url ?? "/").split("?")[0]) ? FILES[(req.url ?? "/").split("?")[0]] : null;
  if (!entry || (req.method !== "GET" && req.method !== "HEAD")) {
    res.writeHead(404, HEADERS);
    res.end();
    return;
  }
  try {
    const body = await readFile(join(here, entry[0]));
    res.writeHead(200, { ...HEADERS, "Content-Type": entry[1] });
    res.end(req.method === "HEAD" ? undefined : body);
  } catch {
    res.writeHead(500, HEADERS);
    res.end();
  }
};

(tls ? createHttpsServer(tls, handler) : createHttpServer(handler)).listen(port, host, () => {
  console.log("Dashboard running at " + (tls ? "https" : "http") + "://" + host + ":" + port);
});
