import assert from "node:assert/strict";
import { before, test } from "node:test";
import type { PGlite } from "@electric-sql/pglite";
import { addUser, asRole, newSigningKey, openDatabase, rpcAs } from "./harness.ts";
import { badAdminCredentials, generateCode, handleActivate, handleExport, handleIssue as issueWith, handleRefresh, importCodesKey, importSigningKey, normalizeCode, sha256Hex } from "../supabase/functions/_shared/core.ts";

let db: PGlite;
let key: CryptoKey;
let publicKey: CryptoKey;
let adminClaims: object;
let codesKey: CryptoKey;
const handleIssue = (input: any, rpc: any) => issueWith(input, rpc, codesKey);
let admin: ReturnType<typeof rpcAs>;
let company: string;
const service = () => rpcAs(db, "service_role");
const DEVICE_A = "A".repeat(64);
const DEVICE_B = "B".repeat(64);

before(async () => {
  db = await openDatabase();
  const keys = await newSigningKey();
  key = await importSigningKey(keys.privateJwk);
  codesKey = await importCodesKey(btoa(String.fromCharCode(...crypto.getRandomValues(new Uint8Array(32)))));
  publicKey = await crypto.subtle.importKey("jwk", keys.publicJwk, { name: "ECDSA", namedCurve: "P-256" }, false, ["verify"]);
  adminClaims = { sub: await addUser(db, true), aal: "aal2" };
  admin = rpcAs(db, "authenticated", adminClaims);
  company = await admin("create_company", { p_name: "شركة الاختبار", p_contact: "" }) as string;
});

async function issue(quantity: number, months: number) {
  const r = await handleIssue({ company_id: company, quantity, months, note: "" }, admin);
  assert.equal(r.status, 200);
  const lines = (r.body.csv as string).split("\r\n").filter((l) => l.startsWith("\"TKM-"));
  return { batch: r.body.batch as any, codes: lines.map((l) => l.split(",")[0].replace(/"/g, "")) };
}

function activate(code: string, device: string, ip = "198.51.100.1") {
  return handleActivate({ code, device, machine: "PC-1" }, ip, service(), key);
}

test("codes have 100 random bits and survive how people type them", () => {
  const seen = new Set(Array.from({ length: 2000 }, generateCode));
  assert.equal(seen.size, 2000);
  const code = [...seen][0];
  assert.match(code, /^TKM(-[0-9A-HJKMNP-TV-Z]{5}){4}$/);
  assert.equal(normalizeCode(code.toLowerCase().replace(/-/g, " ")), normalizeCode(code));
  assert.equal(normalizeCode("TKM-OOOOO-IIIII-LLLLL-00000"), "00000" + "11111" + "11111" + "00000");
  assert.equal(normalizeCode("too short"), null);
});

test("expiry counts from issuing, not from activating", async () => {
  const { batch, codes } = await issue(3, 6);
  const issued = new Date(batch.issued_at);
  const expected = new Date(issued);
  expected.setUTCMonth(expected.getUTCMonth() + 6);
  assert.ok(Math.abs(new Date(batch.expires_at).getTime() - expected.getTime()) < 3 * 86400000);

  // Move the batch two months into its life, then activate: the expiry must not move.
  await db.query("update public.license_batches set issued_at = issued_at - interval '2 months' where id = $1", [batch.id ?? batch.batch_id]);
  const before = (await db.query<{ expires_at: Date }>("select expires_at from public.license_batches where id = $1", [batch.batch_id])).rows[0].expires_at;
  const r = await activate(codes[0], DEVICE_A);
  assert.equal(r.body.ok, true, JSON.stringify(r.body));
  const after = (await db.query<{ expires_at: Date }>("select expires_at from public.license_batches where id = $1", [batch.batch_id])).rows[0].expires_at;
  assert.equal(after.getTime(), before.getTime());
});

test("a batch of 1000 issues and exports", async () => {
  const { batch, codes } = await issue(1000, 6);
  assert.equal(batch.quantity, 1000);
  assert.equal(new Set(codes).size, 1000);
  const stored = await db.query<{ n: number }>("select count(*)::int as n from public.license_codes where batch_id = $1", [batch.batch_id]);
  assert.equal(stored.rows[0].n, 1000);
});

test("the database never holds a usable code", async () => {
  const { batch, codes } = await issue(1, 1);
  const { rows } = await db.query<{ code_hash: string; code_hint: string }>("select code_hash, code_hint from public.license_codes where batch_id = $1", [batch.batch_id]);
  assert.notEqual(rows[0].code_hash, codes[0]);
  assert.equal(rows[0].code_hash.length, 64);
  assert.equal(rows[0].code_hint.length, 4);
  assert.equal(rows[0].code_hash, await sha256Hex(normalizeCode(codes[0])!));
});

test("the token verifies with the public key and carries the batch expiry", async () => {
  const { batch, codes } = await issue(1, 6);
  const r = await activate(codes[0], DEVICE_A);
  assert.equal(r.body.ok, true);
  const [payload, signature] = (r.body.token as string).split(".");
  const decode = (s: string) => Uint8Array.from(atob(s.replace(/-/g, "+").replace(/_/g, "/")), (c) => c.charCodeAt(0));
  assert.ok(await crypto.subtle.verify({ name: "ECDSA", hash: "SHA-256" }, publicKey, decode(signature), decode(payload)));
  const fields = new TextDecoder().decode(decode(payload)).split("|");
  assert.equal(fields[0], "v1");
  assert.equal(fields[2], DEVICE_A);
  assert.equal(Number(fields[3]), Math.floor(new Date(batch.expires_at).getTime() / 1000));
  assert.equal(new TextDecoder().decode(decode(fields[5])), "شركة الاختبار");
});

test("one code stays on one machine until released", async () => {
  const { codes } = await issue(1, 6);
  assert.equal((await activate(codes[0], DEVICE_A)).body.ok, true);
  assert.equal((await activate(codes[0], DEVICE_A)).body.ok, true, "the same machine may activate again");
  const other = await activate(codes[0], DEVICE_B);
  assert.equal(other.body.error, "other_device");

  const { rows } = await db.query<{ id: string }>("select a.id from public.activations a join public.license_codes k on k.id = a.code_id where k.code_hash = $1 and a.released_at is null", [await sha256Hex(normalizeCode(codes[0])!)]);
  await admin("release_activation", { p_activation: rows[0].id });
  assert.equal((await activate(codes[0], DEVICE_B)).body.ok, true);
});

test("two machines racing for one code: exactly one wins", async () => {
  const { codes } = await issue(1, 6);
  const results = await Promise.all([activate(codes[0], DEVICE_A), activate(codes[0], DEVICE_B)]);
  assert.equal(results.filter((r) => r.body.ok).length, 1);
});

test("refresh needs the secret, and picks up an extension", async () => {
  const { batch, codes } = await issue(1, 6);
  const first = await activate(codes[0], DEVICE_A);
  const payload = new TextDecoder().decode(Uint8Array.from(atob((first.body.token as string).split(".")[0].replace(/-/g, "+").replace(/_/g, "/")), (c) => c.charCodeAt(0)));
  const codeId = payload.split("|")[1];

  const wrong = await handleRefresh({ code_id: codeId, device: DEVICE_A, secret: "guess" }, "x", service(), key);
  assert.equal(wrong.body.error, "released");
  const stolen = await handleRefresh({ code_id: codeId, device: DEVICE_B, secret: first.body.secret }, "x", service(), key);
  assert.equal(stolen.body.error, "released");

  const newExpiry = new Date(await admin("extend_batch", { p_batch: batch.batch_id, p_months: 12 }) as string);
  assert.ok(newExpiry > new Date(batch.expires_at));
  const fresh = await handleRefresh({ code_id: codeId, device: DEVICE_A, secret: first.body.secret, machine: "PC-1" }, "x", service(), key);
  assert.equal(fresh.body.ok, true);
  const refreshed = new TextDecoder().decode(Uint8Array.from(atob((fresh.body.token as string).split(".")[0].replace(/-/g, "+").replace(/_/g, "/")), (c) => c.charCodeAt(0)));
  assert.equal(Number(refreshed.split("|")[3]), Math.floor(newExpiry.getTime() / 1000));
});

test("revoked and expired codes are refused, and refresh reports it", async () => {
  const { batch, codes } = await issue(3, 6);
  const live = await activate(codes[0], DEVICE_A);
  const codeId = new TextDecoder().decode(Uint8Array.from(atob((live.body.token as string).split(".")[0].replace(/-/g, "+").replace(/_/g, "/")), (c) => c.charCodeAt(0))).split("|")[1];

  await admin("revoke_code", { p_code: codeId });
  assert.equal((await activate(codes[0], DEVICE_A)).body.error, "revoked");
  assert.equal((await handleRefresh({ code_id: codeId, device: DEVICE_A, secret: live.body.secret }, "x", service(), key)).body.error, "revoked");

  await db.query("update public.license_batches set expires_at = now() - interval '1 second' where id = $1", [batch.batch_id]);
  assert.equal((await activate(codes[1], DEVICE_B)).body.error, "expired");

  await db.query("update public.license_batches set expires_at = now() + interval '1 month' where id = $1", [batch.batch_id]);
  await admin("revoke_batch", { p_batch: batch.batch_id });
  assert.equal((await activate(codes[2], DEVICE_B)).body.error, "revoked");
});

test("late renewal runs from today, early renewal adds to what is left", async () => {
  const { batch } = await issue(1, 6);
  await db.query("update public.license_batches set expires_at = now() - interval '3 months' where id = $1", [batch.batch_id]);
  const late = new Date(await admin("extend_batch", { p_batch: batch.batch_id, p_months: 1 }) as string);
  assert.ok(late > new Date(Date.now() + 27 * 86400000) && late < new Date(Date.now() + 32 * 86400000));
});

test("guessing is limited per address, and successes never count", async () => {
  const { codes } = await issue(25, 6);
  for (let i = 0; i < 22; i++) await activate("TKM-00000-00000-00000-00000", DEVICE_A, "203.0.113.9");
  assert.equal((await activate(codes[0], DEVICE_A, "203.0.113.9")).body.error, "rate_limited");

  const devices = Array.from({ length: 25 }, (_, i) => i.toString(16).toUpperCase().padStart(64, "C"));
  for (let i = 0; i < 25; i++) assert.equal((await activate(codes[i], devices[i], "203.0.113.10")).body.ok, true);
});

test("bad input is refused before the database", async () => {
  assert.equal((await handleActivate({ code: "x", device: "not-hex" }, "x", service(), key)).status, 400);
  assert.equal((await handleIssue({ company_id: company, quantity: 0, months: 6 }, admin)).body.error, "bad_quantity");
  assert.equal((await handleIssue({ company_id: company, quantity: 10001, months: 6 }, admin)).body.error, "bad_quantity");
  assert.equal((await handleIssue({ company_id: company, quantity: 1, months: 0 }, admin)).body.error, "bad_months");
  assert.equal((await handleIssue({ company_id: "'; drop table x", quantity: 1, months: 1 }, admin)).body.error, "bad_company");
});

test("the file cannot run formulas in Excel", async () => {
  await db.query("insert into public.companies (name) values ($1)", ["=HYPERLINK(\"http://evil\")"]);
  const { rows } = await db.query<{ id: string }>("select id from public.companies where name like '=%'");
  const r = await handleIssue({ company_id: rows[0].id, quantity: 1, months: 1 }, admin);
  assert.ok((r.body.csv as string).includes("\"'=HYPERLINK(\"\"http://evil\"\")\""), r.body.csv as string);
});

test("machine names cannot carry control or bidi characters", async () => {
  const { codes } = await issue(1, 1);
  await handleActivate({ code: codes[0], device: "D".repeat(64), machine: "PC‮exe.\n<script>" }, "x", service(), key);
  const { rows } = await db.query<{ machine_name: string }>("select machine_name from public.activations where device_hash = $1", ["D".repeat(64)]);
  assert.equal(rows[0].machine_name, "PCexe.<script>");
});

// ---- who can do what ----

const tables = ["admins", "companies", "license_batches", "license_codes", "activations", "activation_failures", "audit_log", "batch_overview", "code_overview", "company_admins", "company_admin_overview"];

async function rowsVisible(role: string, claims: object | null, table: string): Promise<number | string> {
  return await asRole(db, role, claims, async () => {
    try {
      return (await db.query("select * from public." + table)).rows.length;
    } catch (e) {
      return "denied:" + (e as { code?: string }).code;
    }
  });
}

async function callDenied(rpc: ReturnType<typeof rpcAs>, fn: string, args: Record<string, unknown>) {
  await assert.rejects(rpc(fn, args), (e: { code?: string; message?: string }) => e.code === "42501", fn + " must be refused");
}

test("the public (anon) role can read nothing and call nothing", async () => {
  await issue(1, 1);
  for (const t of tables) assert.equal(await rowsVisible("anon", null, t), "denied:42501", t);
  const anon = rpcAs(db, "anon");
  await callDenied(anon, "activate_code", { p_code_hash: "x", p_device: "x", p_machine: "x", p_ip: "x", p_secret_hash: "x" });
  await callDenied(anon, "create_company", { p_name: "x", p_contact: "" });
});

test("a signed-in user who is not an admin sees and does nothing", async () => {
  const claims = { sub: await addUser(db, false), aal: "aal2" };
  for (const t of ["companies", "license_batches", "audit_log", "batch_overview", "code_overview"])
    assert.equal(await rowsVisible("authenticated", claims, t), 0, t);
  assert.equal(await asRole(db, "authenticated", claims, async () => (await db.query("select id from public.activations")).rows.length), 0);
  const user = rpcAs(db, "authenticated", claims);
  await callDenied(user, "create_company", { p_name: "x", p_contact: "" });
  await callDenied(user, "issue_batch", { p_company: company, p_months: 1, p_note: "", p_hashes: ["a"], p_hints: ["a"], p_encrypted: ["a"] });
  await callDenied(user, "revoke_code", { p_code: company });
});

test("an admin without MFA is treated as nobody", async () => {
  const claims = { ...adminClaims, aal: "aal1" };
  assert.equal(await rowsVisible("authenticated", claims, "companies"), 0);
  const noMfa = rpcAs(db, "authenticated", claims);
  await callDenied(noMfa, "create_company", { p_name: "y", p_contact: "" });
  const r = handleIssue({ company_id: company, quantity: 1, months: 1 }, noMfa);
  await assert.rejects(r);
});

test("admins cannot read hashes or device secrets, nor write tables directly", async () => {
  const { batch } = await issue(1, 1);
  assert.ok((await rowsVisible("authenticated", adminClaims, "batch_overview") as number) > 0);
  await asRole(db, "authenticated", adminClaims, async () => {
    await assert.rejects(db.query("select code_hash from public.license_codes"), (e: any) => e.code === "42501");
    await assert.rejects(db.query("select secret_hash from public.activations"), (e: any) => e.code === "42501");
    await assert.rejects(db.query("update public.license_batches set expires_at = now()"), (e: any) => e.code === "42501");
    await assert.rejects(db.query("insert into public.admins (user_id) values (gen_random_uuid())"), (e: any) => e.code === "42501");
    await assert.rejects(db.query("delete from public.audit_log"), (e: any) => e.code === "42501");
    assert.ok((await db.query("select code_hint from public.license_codes where batch_id = $1", [batch.batch_id])).rows.length === 1);
  });
});

test("the dashboard cannot call the program's functions", async () => {
  await callDenied(admin, "activate_code", { p_code_hash: "x", p_device: "x", p_machine: "x", p_ip: "x", p_secret_hash: "x" });
  await callDenied(admin, "refresh_license", { p_code: company, p_device: "x", p_secret_hash: "x", p_machine: "x", p_ip: "x" });
});

// ---- exporting a batch again ----

function csvRows(csv: string) {
  return csv.split("\r\n").filter((l) => l.includes(",")).map((l) => l.split(",").map((c) => c.replace(/^"|"$/g, "")));
}

const summary = (rows: string[][], name: string) => rows.find((r) => r[0] === name)?.[1];

test("a batch exports again with the same codes, and the file shows what is used", async () => {
  const { batch, codes } = await issue(3, 6);
  assert.equal((await activate(codes[1], "E".repeat(64))).body.ok, true);

  const r = await handleExport({ batch_id: batch.batch_id }, admin, codesKey);
  assert.equal(r.status, 200);
  assert.equal(r.body.missing, 0);
  const rows = csvRows(r.body.csv as string);
  assert.equal(summary(rows, "إجمالي الأكواد"), "3");
  assert.equal(summary(rows, "المستخدمة"), "1");
  assert.equal(summary(rows, "المتبقية"), "2");

  const data = rows.filter((row) => row[0].startsWith("TKM-"));
  assert.deepEqual(data.map((row) => row[0]).sort(), [...codes].sort(), "the very same codes come back");
  const used = data.find((row) => row[0] === codes[1])!;
  assert.equal(used[1], "مستخدم");
  assert.equal(used[2], "PC-1");
  assert.equal(data[0][1], "متبقٍ", "unused codes are listed first");
  assert.equal(data.filter((row) => row[1] === "متبقٍ").length, 2);
});

test("exported codes still work, and revoked or expired ones are labelled", async () => {
  const { batch, codes } = await issue(3, 6);
  const first = await handleExport({ batch_id: batch.batch_id }, admin, codesKey);
  const exported = csvRows(first.body.csv as string).filter((row) => row[0].startsWith("TKM-"))[0][0];
  assert.equal((await activate(exported, "F".repeat(64))).body.ok, true);

  const { rows: ids } = await db.query<{ id: string }>("select id from public.license_codes where code_hash = $1", [await sha256Hex(normalizeCode(codes.find((c) => c !== exported)!)!)]);
  await admin("revoke_code", { p_code: ids[0].id });
  let rows = csvRows((await handleExport({ batch_id: batch.batch_id }, admin, codesKey)).body.csv as string);
  assert.equal(summary(rows, "الملغاة"), "1");
  assert.equal(summary(rows, "المستخدمة"), "1");
  assert.equal(summary(rows, "المتبقية"), "1");

  await db.query("update public.license_batches set expires_at = now() - interval '1 day' where id = $1", [batch.batch_id]);
  rows = csvRows((await handleExport({ batch_id: batch.batch_id }, admin, codesKey)).body.csv as string);
  assert.equal(summary(rows, "المتبقية"), "0");
  assert.equal(summary(rows, "منتهية دون استخدام"), "1");
});

test("the database holds no readable code, and another key cannot read it", async () => {
  const { batch, codes } = await issue(2, 1);
  const { rows } = await db.query<{ code_encrypted: string }>("select code_encrypted from public.license_codes where batch_id = $1", [batch.batch_id]);
  for (const row of rows) {
    assert.ok(row.code_encrypted.length > 20);
    assert.ok(!codes.some((c) => row.code_encrypted.includes(c) || row.code_encrypted.includes(c.slice(4))));
  }
  const otherKey = await importCodesKey(btoa(String.fromCharCode(...crypto.getRandomValues(new Uint8Array(32)))));
  const r = await handleExport({ batch_id: batch.batch_id }, admin, otherKey);
  assert.equal(r.body.missing, 2);
  assert.ok(!codes.some((c) => (r.body.csv as string).includes(c)));
});

test("a stored code moved to another row is detected", async () => {
  const { batch } = await issue(2, 1);
  const { rows } = await db.query<{ id: string; code_encrypted: string }>("select id, code_encrypted from public.license_codes where batch_id = $1", [batch.batch_id]);
  await db.query("update public.license_codes set code_encrypted = $1 where id = $2", [rows[1].code_encrypted, rows[0].id]);
  assert.equal((await handleExport({ batch_id: batch.batch_id }, admin, codesKey)).body.missing, 1);
});

test("a batch from before this feature exports status but says its codes are unavailable", async () => {
  const { batch } = await issue(2, 1);
  await db.query("update public.license_codes set code_encrypted = null where batch_id = $1", [batch.batch_id]);
  const r = await handleExport({ batch_id: batch.batch_id }, admin, codesKey);
  assert.equal(r.status, 200);
  assert.equal(r.body.missing, 2);
  const rows = csvRows(r.body.csv as string);
  assert.equal(summary(rows, "إجمالي الأكواد"), "2");
  assert.ok(summary(rows, "ملاحظة")?.includes("2 كود غير متاح"));
  assert.ok((r.body.csv as string).includes("غير محفوظ"));
});

test("only an admin with MFA can export, the secret column stays hidden, and exports are recorded", async () => {
  const { batch } = await issue(1, 1);
  const args = { batch_id: batch.batch_id };
  await assert.rejects(handleExport(args, rpcAs(db, "anon"), codesKey), (e: any) => e.code === "42501");
  await assert.rejects(handleExport(args, rpcAs(db, "authenticated", { sub: await addUser(db, false), aal: "aal2" }), codesKey), (e: any) => e.code === "42501");
  await assert.rejects(handleExport(args, rpcAs(db, "authenticated", { ...adminClaims, aal: "aal1" }), codesKey), (e: any) => e.code === "42501");
  await asRole(db, "authenticated", adminClaims, async () => {
    await assert.rejects(db.query("select code_encrypted from public.license_codes"), (e: any) => e.code === "42501");
  });
  assert.equal((await handleExport({ batch_id: "not-a-uuid" }, admin, codesKey)).body.error, "bad_batch");

  await handleExport(args, admin, codesKey);
  const { rows } = await db.query<{ actor: string }>("select actor from public.audit_log where action = 'export_batch' and subject = $1", [batch.batch_id]);
  assert.equal(rows.length, 1);
  assert.equal(rows[0].actor, (adminClaims as { sub: string }).sub);
});

test("admin actions are recorded", async () => {
  const { batch } = await issue(1, 1);
  const { rows } = await db.query<{ action: string; actor: string }>("select action, actor from public.audit_log where subject = $1", [batch.batch_id]);
  assert.equal(rows[0].action, "issue_batch");
  assert.equal(rows[0].actor, (adminClaims as { sub: string }).sub);
});

function tokenFields(token: string): string[] {
  const bytes = Uint8Array.from(atob(token.split(".")[0].replace(/-/g, "+").replace(/_/g, "/")), (c) => c.charCodeAt(0));
  return new TextDecoder().decode(bytes).split("|");
}

test("Android control rides in the token only when both the company and the device are allowed", async () => {
  const { codes } = await issue(1, 6);
  const first = await activate(codes[0], DEVICE_A);
  // handleActivate returns the code id only inside the signed token.
  const codeId = tokenFields(first.body.token as string)[1];
  const activation = (await db.query<{ id: string }>(
    "select id from public.activations where code_id = $1 and released_at is null", [codeId])).rows[0].id;

  // Off by default: six fields, no flag.
  assert.equal(tokenFields(first.body.token as string).length, 6);

  const refresh = () => handleRefresh({ code_id: codeId, device: DEVICE_A, secret: first.body.secret, machine: "PC-1" }, "x", service(), key);

  // Company on but device off: still not allowed.
  await admin("set_company_android", { p_company: company, p_on: true });
  assert.equal(tokenFields((await refresh()).body.token as string).length, 6);

  // Device on as well: the token now carries the 'a' flag, and still verifies.
  await admin("set_device_android", { p_activation: activation, p_on: true });
  const token = (await refresh()).body.token as string;
  const fields = tokenFields(token);
  assert.equal(fields.length, 7);
  assert.equal(fields[6], "a");
  const decode = (s: string) => Uint8Array.from(atob(s.replace(/-/g, "+").replace(/_/g, "/")), (c) => c.charCodeAt(0));
  const [payload, signature] = token.split(".");
  assert.ok(await crypto.subtle.verify({ name: "ECDSA", hash: "SHA-256" }, publicKey, decode(signature), decode(payload)));

  // Turning the company off revokes it even while the device stays allowed.
  await admin("set_company_android", { p_company: company, p_on: false });
  assert.equal(tokenFields((await refresh()).body.token as string).length, 6);
});

test("only an admin with MFA may toggle Android control", async () => {
  const anon = rpcAs(db, "anon");
  const user = rpcAs(db, "authenticated", { sub: await addUser(db, false), aal: "aal2" });
  const noMfa = rpcAs(db, "authenticated", { ...adminClaims, aal: "aal1" });
  await callDenied(anon, "set_company_android", { p_company: company, p_on: true });
  await callDenied(user, "set_company_android", { p_company: company, p_on: true });
  await callDenied(noMfa, "set_device_android", { p_activation: company, p_on: true });
});

test("auto file transfer rides in the token only when both the company and the device are allowed, alongside Android", async () => {
  const { codes } = await issue(1, 6);
  const first = await activate(codes[0], DEVICE_A);
  const codeId = tokenFields(first.body.token as string)[1];
  const activation = (await db.query<{ id: string }>(
    "select id from public.activations where code_id = $1 and released_at is null", [codeId])).rows[0].id;
  assert.equal(tokenFields(first.body.token as string).length, 6);

  const refresh = () => handleRefresh({ code_id: codeId, device: DEVICE_A, secret: first.body.secret, machine: "PC-1" }, "x", service(), key);

  // Company on but device off: still not allowed.
  await admin("set_company_autofiles", { p_company: company, p_on: true });
  assert.equal(tokenFields((await refresh()).body.token as string).length, 6);

  // Device on as well: the token carries 'f', and still verifies.
  await admin("set_device_autofiles", { p_activation: activation, p_on: true });
  const token = (await refresh()).body.token as string;
  const fields = tokenFields(token);
  assert.equal(fields.length, 7);
  assert.equal(fields[6], "f");
  const decode = (s: string) => Uint8Array.from(atob(s.replace(/-/g, "+").replace(/_/g, "/")), (c) => c.charCodeAt(0));
  const [payload, signature] = token.split(".");
  assert.ok(await crypto.subtle.verify({ name: "ECDSA", hash: "SHA-256" }, publicKey, decode(signature), decode(payload)));

  // Both features on at once: the flags field holds both letters, in a fixed order.
  await admin("set_company_android", { p_company: company, p_on: true });
  await admin("set_device_android", { p_activation: activation, p_on: true });
  assert.equal(tokenFields((await refresh()).body.token as string)[6], "af");

  // Turning the company off revokes auto file transfer while the device stays allowed.
  await admin("set_company_autofiles", { p_company: company, p_on: false });
  assert.equal(tokenFields((await refresh()).body.token as string)[6], "a");
});

test("only an admin with MFA may toggle auto file transfer", async () => {
  const anon = rpcAs(db, "anon");
  const user = rpcAs(db, "authenticated", { sub: await addUser(db, false), aal: "aal2" });
  const noMfa = rpcAs(db, "authenticated", { ...adminClaims, aal: "aal1" });
  await callDenied(anon, "set_company_autofiles", { p_company: company, p_on: true });
  await callDenied(user, "set_company_autofiles", { p_company: company, p_on: true });
  await callDenied(noMfa, "set_device_autofiles", { p_activation: company, p_on: true });
});

// ---- company administrators ----

// Issues a batch for an arbitrary company (the shared issue() helper always uses the main company).
async function issueFor(companyId: string) {
  const r = await handleIssue({ company_id: companyId, quantity: 1, months: 6, note: "" }, admin);
  const code = (r.body.csv as string).split("\r\n").filter((l) => l.startsWith("\"TKM-"))[0].split(",")[0].replace(/"/g, "");
  return code;
}

async function activationId(result: Awaited<ReturnType<typeof activate>>) {
  const codeId = tokenFields(result.body.token as string)[1];
  return (await db.query<{ id: string }>("select id from public.activations where code_id = $1 and released_at is null", [codeId])).rows[0].id;
}

test("a company admin sees only their own company, grants TV per device, and releases a device", async () => {
  const companyB = await admin("create_company", { p_name: "شركة ثانية", p_contact: "" }) as string;
  const actA = await activationId(await activate(await issueFor(company), "1".repeat(64)));
  const actB = await activationId(await activate(await issueFor(companyB), "2".repeat(64)));

  const caUser = await addUser(db, false);
  await db.query("insert into public.company_admins (user_id, company_id, username) values ($1, $2, $3)", [caUser, company, "ca-one"]);
  const claims = { sub: caUser, aal: "aal2" };
  const ca = rpcAs(db, "authenticated", claims);

  // The dashboard learns the role: not the owner, scoped to the one company.
  const role = await ca("my_role", {}) as { owner: boolean; company_id: string };
  assert.equal(role.owner, false);
  assert.equal(role.company_id, company);

  // Every visible device belongs to that company, never company B's.
  const seen = await asRole(db, "authenticated", claims, async () => (await db.query<{ company_id: string }>("select company_id from public.code_overview")).rows);
  assert.ok(seen.length > 0 && seen.every((r) => r.company_id === company));

  // Grants TV for its own device, but not for company B's, and cannot flip the company-level switch.
  await ca("set_device_android", { p_activation: actA, p_on: true });
  await assert.rejects(ca("set_device_android", { p_activation: actB, p_on: true }), (e: { code?: string }) => e.code === "42501");
  await callDenied(ca, "set_company_android", { p_company: company, p_on: true });

  // Same rules for auto file transfer: its own device yes, company B's no, the company switch no.
  await ca("set_device_autofiles", { p_activation: actA, p_on: true });
  await assert.rejects(ca("set_device_autofiles", { p_activation: actB, p_on: true }), (e: { code?: string }) => e.code === "42501");
  await callDenied(ca, "set_company_autofiles", { p_company: company, p_on: true });

  // Releases its own device, not company B's.
  await assert.rejects(ca("release_activation", { p_activation: actB }), (e: { code?: string }) => e.code === "42501");
  await ca("release_activation", { p_activation: actA });
  assert.ok((await db.query<{ released_at: Date | null }>("select released_at from public.activations where id = $1", [actA])).rows[0].released_at);

  // Without MFA it is nobody, exactly like the owner.
  assert.equal(await rowsVisible("authenticated", { sub: caUser, aal: "aal1" }, "code_overview"), 0);
  await callDenied(rpcAs(db, "authenticated", { sub: caUser, aal: "aal1" }), "set_device_android", { p_activation: actB, p_on: true });
});

test("only the owner may create or delete a company-admin account", async () => {
  const target = await addUser(db, false);
  const caUser = await addUser(db, false);
  await db.query("insert into public.company_admins (user_id, company_id, username) values ($1, $2, $3)", [caUser, company, "ca-two"]);
  const ca = rpcAs(db, "authenticated", { sub: caUser, aal: "aal2" });
  const plain = rpcAs(db, "authenticated", { sub: await addUser(db, false), aal: "aal2" });

  await callDenied(plain, "create_company_admin", { p_user: target, p_company: company, p_username: "x" });
  await callDenied(ca, "create_company_admin", { p_user: target, p_company: company, p_username: "x" });
  await callDenied(ca, "delete_company_admin", { p_user: caUser });

  await admin("create_company_admin", { p_user: target, p_company: company, p_username: "made-by-owner" });
  assert.equal((await db.query("select 1 from public.company_admins where user_id = $1", [target])).rows.length, 1);
  await admin("delete_company_admin", { p_user: target });
  assert.equal((await db.query("select 1 from public.company_admins where user_id = $1", [target])).rows.length, 0);
});

test("delete_company removes the company and everything under it, owner only", async () => {
  const doomed = await admin("create_company", { p_name: "شركة للحذف", p_contact: "" }) as string;
  const code = await issueFor(doomed);
  const act = await activationId(await activate(code, "9".repeat(64)));
  const caUser = await addUser(db, false);
  await db.query("insert into public.company_admins (user_id, company_id, username) values ($1, $2, $3)", [caUser, doomed, "ca-doomed"]);

  // A company admin of that company may not delete it.
  await callDenied(rpcAs(db, "authenticated", { sub: caUser, aal: "aal2" }), "delete_company", { p_company: doomed });

  const result = await admin("delete_company", { p_company: doomed }) as { company: string; admin_user_ids: string[] };
  assert.equal(result.company, "شركة للحذف");
  assert.deepEqual(result.admin_user_ids, [caUser]);

  // Nothing of the company survives: company, batches, codes, the activation, and the admin row.
  assert.equal((await db.query("select 1 from public.companies where id = $1", [doomed])).rows.length, 0);
  assert.equal((await db.query("select 1 from public.license_batches where company_id = $1", [doomed])).rows.length, 0);
  assert.equal((await db.query("select 1 from public.activations where id = $1", [act])).rows.length, 0);
  assert.equal((await db.query("select 1 from public.company_admins where user_id = $1", [caUser])).rows.length, 0);

  await assert.rejects(admin("delete_company", { p_company: doomed }), (e: { message?: string }) => /unknown_company/.test(e.message ?? ""));
});

test("company-admin credentials are validated before an account is created", () => {
  assert.equal(badAdminCredentials("alnoor-it", "longenough"), null);
  assert.equal(badAdminCredentials("a", "longenough"), "bad_username");
  assert.equal(badAdminCredentials("bad name", "longenough"), "bad_username");
  assert.equal(badAdminCredentials("وليد", "longenough"), "bad_username");
  assert.equal(badAdminCredentials("good", "short"), "bad_password");
  assert.equal(badAdminCredentials("good", "x".repeat(73)), "bad_password");
});
