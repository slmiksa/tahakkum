// A local stand-in for Supabase: the real migration on PGlite and the real function logic,
// over HTTP. tests\LicenseTest.cs drives the desktop program's code against it. The /test
// routes exist only here.
import { createServer } from "node:http";
import { addUser, newSigningKey, openDatabase, rpcAs } from "./harness.ts";
import { handleActivate, handleIssue, handleRefresh, importCodesKey, importSigningKey } from "../supabase/functions/_shared/core.ts";

const db = await openDatabase();
const keys = await newSigningKey();
const key = await importSigningKey(keys.privateJwk);
const codesKey = await importCodesKey(btoa(String.fromCharCode(...crypto.getRandomValues(new Uint8Array(32)))));
const admin = rpcAs(db, "authenticated", { sub: await addUser(db, true), aal: "aal2" });
const service = rpcAs(db, "service_role");
const company = await admin("create_company", { p_name: "شركة الاختبار", p_contact: "" }) as string;

// PGlite handles one statement at a time and these switch roles, so requests take turns.
let queue: Promise<unknown> = Promise.resolve();
function inTurn<T>(work: () => Promise<T>): Promise<T> {
  const run = queue.then(work, work);
  queue = run.catch(() => undefined);
  return run;
}

async function route(path: string, body: any): Promise<unknown> {
  switch (path) {
    case "/activate": return (await handleActivate(body, "127.0.0.1", service, key)).body;
    case "/refresh": return (await handleRefresh(body, "127.0.0.1", service, key)).body;
    case "/test/issue": {
      const r = await handleIssue({ company_id: company, quantity: body.quantity, months: body.months }, admin, codesKey);
      const lines = (r.body.csv as string).split("\r\n").filter((l) => l.startsWith("\"TKM-"));
      return { batch: (r.body.batch as any).batch_id, codes: lines.map((l) => l.split(",")[0].replace(/"/g, "")) };
    }
    case "/test/expire":
      await db.query("update public.license_batches set expires_at = now() - interval '1 minute' where id = $1", [body.batch]);
      return {};
    case "/test/extend":
      return { expires_at: await admin("extend_batch", { p_batch: body.batch, p_months: body.months }) };
    case "/test/revoke":
      await admin("revoke_batch", { p_batch: body.batch });
      return {};
    case "/test/release": {
      const { rows } = await db.query<{ id: string }>(
        "select a.id from public.activations a join public.license_codes k on k.id = a.code_id where k.batch_id = $1 and a.released_at is null", [body.batch]);
      for (const row of rows) await admin("release_activation", { p_activation: row.id });
      return {};
    }
  }
  return null;
}

const server = createServer((req, res) => {
  let raw = "";
  req.on("data", (c) => (raw += c));
  req.on("end", async () => {
    try {
      const out = await inTurn(() => route(req.url ?? "", JSON.parse(raw || "{}")));
      res.writeHead(out === null ? 404 : 200, { "content-type": "application/json" });
      res.end(JSON.stringify(out ?? { ok: false, error: "not_found" }));
    } catch (error) {
      console.error(error);
      res.writeHead(500, { "content-type": "application/json" });
      res.end(JSON.stringify({ ok: false, error: "server_error" }));
    }
  });
});

server.listen(0, "127.0.0.1", () => {
  const { port } = server.address() as { port: number };
  console.log(JSON.stringify({ url: "http://127.0.0.1:" + port, x: keys.publicJwk.x, y: keys.publicJwk.y }));
});
