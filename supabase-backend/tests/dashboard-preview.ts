// Serves the real dashboard files, with the sample-data mock in place of supabase-js.
import { readFile } from "node:fs/promises";
import { createServer } from "node:http";
import { join } from "node:path";

const here = import.meta.dirname;
const dashboard = join(here, "..", "..", "dashboard");
const types: Record<string, string> = { html: "text/html; charset=utf-8", js: "text/javascript; charset=utf-8", css: "text/css; charset=utf-8", svg: "image/svg+xml", png: "image/png" };

createServer(async (req, res) => {
  const path = (req.url ?? "/").split("?")[0];
  try {
    let body: string | Buffer;
    if (path === "/vendor/supabase.js") body = await readFile(join(here, "dashboard-mock.js"), "utf8");
    else if (path === "/config.js") body = 'window.TAHAKKUM_CONFIG = { url: "http://mock", anonKey: "mock" };';
    else if (path === "/" || /^\/[a-z]+\.(js|css|html|svg|png)$/.test(path)) body = await readFile(join(dashboard, path === "/" ? "index.html" : path));
    else throw new Error("not found");
    res.writeHead(200, { "content-type": types[path === "/" ? "html" : path.split(".").pop()!] });
    res.end(body);
  } catch {
    res.writeHead(404);
    res.end();
  }
}).listen(47890, "127.0.0.1");
