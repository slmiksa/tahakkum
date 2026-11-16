// The logic behind the Edge Functions. It uses only web-standard APIs and receives its
// database and signing key as arguments, so the tests run exactly this code.

export type Rpc = (fn: string, args: Record<string, unknown>) => Promise<any>;
export type Result = { status: number; body: Record<string, unknown> };

const ALPHABET = "0123456789ABCDEFGHJKMNPQRSTVWXYZ"; // Crockford base32: no I, L, O or U.
const CODE_LENGTH = 20; // 100 random bits.
const MAX_BATCH = 10000;
const DEVICE_PATTERN = /^[0-9A-F]{64}$/;
const UUID_PATTERN = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/;

const encoder = new TextEncoder();

export function base64Url(bytes: Uint8Array): string {
  let s = "";
  for (const b of bytes) s += String.fromCharCode(b);
  return btoa(s).replace(/\+/g, "-").replace(/\//g, "_").replace(/=+$/, "");
}

export async function sha256Hex(text: string): Promise<string> {
  const digest = new Uint8Array(await crypto.subtle.digest("SHA-256", encoder.encode(text)));
  return Array.from(digest, (b) => b.toString(16).padStart(2, "0")).join("");
}

// 32 possible values fit exactly in 5 bits, so masking a random byte has no bias.
export function generateCode(): string {
  const random = crypto.getRandomValues(new Uint8Array(CODE_LENGTH));
  const chars = Array.from(random, (b) => ALPHABET[b & 31]).join("");
  return "TKM-" + chars.match(/.{5}/g)!.join("-");
}

// Accepts what people actually type, including the letters people confuse with digits.
export function normalizeCode(input: string): string | null {
  const s = input.toUpperCase().replace(/[\s-]/g, "").replace(/^TKM/, "")
    .replace(/O/g, "0").replace(/[IL]/g, "1");
  if (s.length !== CODE_LENGTH) return null;
  for (const c of s) if (!ALPHABET.includes(c)) return null;
  return s;
}

function cleanText(value: unknown, max: number): string {
  // Strips control and bidi characters: the dashboard shows these values to an administrator.
  return String(value ?? "").replace(/[\p{Cc}\p{Cf}\p{Zl}\p{Zp}]/gu, "").trim().slice(0, max);
}

export async function importSigningKey(privateJwk: JsonWebKey): Promise<CryptoKey> {
  return await crypto.subtle.importKey("jwk", privateJwk, { name: "ECDSA", namedCurve: "P-256" }, false, ["sign"]);
}

// v1|code id|device|expiry|issued|company[|flags]. The program verifies this offline with the
// public key. The flags field was added later, so a token without it still parses; the flags are
// concatenated in one field: 'a' = may control Android TV screens over IP, 'f' = may transfer files
// without the other side approving each one, 'p' = may connect to a phone (the one-tap USB flow).
export async function signToken(key: CryptoKey, grant: { code_id: string; company: string; expires_at: string; server_time: string; android_control?: boolean; auto_file_transfer?: boolean; phone_control?: boolean }, device: string): Promise<string> {
  const unix = (t: string) => Math.floor(new Date(t).getTime() / 1000);
  const fields = ["v1", grant.code_id, device, String(unix(grant.expires_at)), String(unix(grant.server_time)), base64Url(encoder.encode(grant.company))];
  let flags = "";
  if (grant.android_control) flags += "a";
  if (grant.auto_file_transfer) flags += "f";
  if (grant.phone_control) flags += "p";
  if (flags) fields.push(flags);
  const payload = fields.join("|");
  const data = encoder.encode(payload);
  const signature = new Uint8Array(await crypto.subtle.sign({ name: "ECDSA", hash: "SHA-256" }, key, data));
  return base64Url(data) + "." + base64Url(signature);
}

function bad(error: string, status = 400): Result {
  return { status, body: { ok: false, error } };
}

export async function handleActivate(input: any, ip: string, rpc: Rpc, key: CryptoKey): Promise<Result> {
  const code = normalizeCode(String(input?.code ?? ""));
  const device = String(input?.device ?? "");
  if (!DEVICE_PATTERN.test(device)) return bad("bad_request");
  // A malformed code still goes to the database as a miss, so guessing is rate limited either way.
  const hash = await sha256Hex(code ?? "invalid:" + String(input?.code ?? "").slice(0, 100));
  const secret = base64Url(crypto.getRandomValues(new Uint8Array(32)));
  const grant = await rpc("activate_code", {
    p_code_hash: hash,
    p_device: device,
    p_machine: cleanText(input?.machine, 64),
    p_ip: ip,
    p_secret_hash: await sha256Hex(secret),
  });
  if (!grant.ok) return { status: 200, body: { ok: false, error: grant.error, expires_at: grant.expires_at } };
  return { status: 200, body: { ok: true, token: await signToken(key, grant, device), secret } };
}

export async function handleRefresh(input: any, ip: string, rpc: Rpc, key: CryptoKey): Promise<Result> {
  const device = String(input?.device ?? "");
  const codeId = String(input?.code_id ?? "");
  const secret = String(input?.secret ?? "");
  if (!DEVICE_PATTERN.test(device) || !UUID_PATTERN.test(codeId) || secret.length === 0 || secret.length > 100) return bad("bad_request");
  const grant = await rpc("refresh_license", {
    p_code: codeId,
    p_device: device,
    p_secret_hash: await sha256Hex(secret),
    p_machine: cleanText(input?.machine, 64),
    p_ip: ip,
  });
  if (!grant.ok) return { status: 200, body: { ok: false, error: grant.error } };
  return { status: 200, body: { ok: true, token: await signToken(key, grant, device) } };
}

function csvCell(value: string): string {
  // A cell starting with these runs as a formula when the company opens the file in Excel.
  const safe = /^[=+\-@\t\r]/.test(value) ? "'" + value : value;
  return '"' + safe.replace(/"/g, '""') + '"';
}

function fromBase64Url(text: string): Uint8Array {
  return Uint8Array.from(atob(text.replace(/-/g, "+").replace(/_/g, "/")), (c) => c.charCodeAt(0));
}

export async function importCodesKey(encoded: string): Promise<CryptoKey> {
  return await crypto.subtle.importKey("raw", fromBase64Url(encoded), "AES-GCM", false, ["encrypt", "decrypt"]);
}

// The hint is authenticated with the code, so a stored value cannot be moved to another row unnoticed.
export async function encryptCode(key: CryptoKey, code: string): Promise<string> {
  const iv = crypto.getRandomValues(new Uint8Array(12));
  const data = await crypto.subtle.encrypt({ name: "AES-GCM", iv, additionalData: encoder.encode(code.slice(-4)) }, key, encoder.encode(code));
  return base64Url(iv) + "." + base64Url(new Uint8Array(data));
}

async function decryptCode(key: CryptoKey, stored: string, hint: string): Promise<string> {
  const [iv, data] = stored.split(".");
  const plain = await crypto.subtle.decrypt({ name: "AES-GCM", iv: fromBase64Url(iv), additionalData: encoder.encode(hint) }, key, fromBase64Url(data));
  return new TextDecoder().decode(plain);
}

type ExportRow = { code: string; used: boolean; revoked: boolean; machine?: string; lastIp?: string; activatedAt?: string; lastSeenAt?: string };

function stamp(value?: string): string {
  return value ? new Date(value).toISOString().slice(0, 16).replace("T", " ") : "";
}

// The file a company receives: a summary on top, then every code with its status, the unused ones first.
function buildCsv(batch: { company: string; expires_at: string; batch_revoked?: boolean }, rows: ExportRow[], note?: string): string {
  const expired = new Date(batch.expires_at).getTime() <= Date.now();
  const statusOf = (r: ExportRow) => r.revoked || batch.batch_revoked ? "ملغى" : r.used ? "مستخدم" : expired ? "منتهٍ" : "متبقٍ";
  const count = (status: string) => rows.filter((r) => statusOf(r) === status).length;
  const line = (...cells: string[]) => cells.map(csvCell).join(",");
  const lines = [
    line("الشركة", batch.company),
    line("تاريخ الانتهاء", String(batch.expires_at).slice(0, 10)),
    line("إجمالي الأكواد", String(rows.length)),
    line("المستخدمة", String(count("مستخدم"))),
    line("المتبقية", String(count("متبقٍ"))),
    line("الملغاة", String(count("ملغى"))),
  ];
  if (count("منتهٍ") > 0) lines.push(line("منتهية دون استخدام", String(count("منتهٍ"))));
  if (note) lines.push(line("ملاحظة", note));
  lines.push("", line("الكود", "الحالة", "اسم الجهاز", "آخر IP", "تاريخ التفعيل", "آخر اتصال"));
  const order = (r: ExportRow) => (statusOf(r) === "متبقٍ" ? 0 : statusOf(r) === "مستخدم" ? 1 : 2);
  for (const r of [...rows].sort((a, b) => order(a) - order(b)))
    lines.push(line(r.code, statusOf(r), r.machine ?? "", r.lastIp ?? "", stamp(r.activatedAt), stamp(r.lastSeenAt)));
  return "﻿" + lines.join("\r\n") + "\r\n";
}

export async function handleExport(input: any, rpc: Rpc, codesKey: CryptoKey): Promise<Result> {
  const id = String(input?.batch_id ?? "");
  if (!UUID_PATTERN.test(id)) return bad("bad_batch");
  const batch = await rpc("export_batch", { p_batch: id });
  let missing = 0;
  const rows: ExportRow[] = await Promise.all(batch.codes.map(async (c: any) => {
    let code: string;
    if (!c.encrypted) {
      missing++;
      code = "غير محفوظ (…" + c.hint + ")";
    } else {
      try {
        code = await decryptCode(codesKey, c.encrypted, c.hint);
      } catch {
        missing++;
        code = "تعذر فك التشفير (…" + c.hint + ")";
      }
    }
    return { code, used: !!c.machine, revoked: c.revoked, machine: c.machine ?? undefined, lastIp: c.last_ip ?? undefined, activatedAt: c.activated_at ?? undefined, lastSeenAt: c.last_seen_at ?? undefined };
  }));
  const note = missing > 0 ? missing + " كود غير متاح لأن هذه الدفعة صدرت قبل تفعيل حفظ الأكواد. لاستبدالها أصدر دفعة جديدة." : undefined;
  return { status: 200, body: { ok: true, batch, missing, csv: buildCsv(batch, rows, note) } };
}

// Runs with the administrator's own token: the database checks that they are an admin with MFA.
export async function handleIssue(input: any, rpc: Rpc, codesKey: CryptoKey): Promise<Result> {
  const quantity = Number(input?.quantity);
  const months = Number(input?.months);
  const company = String(input?.company_id ?? "");
  if (!Number.isInteger(quantity) || quantity < 1 || quantity > MAX_BATCH) return bad("bad_quantity");
  if (!Number.isInteger(months) || months < 1 || months > 120) return bad("bad_months");
  if (!UUID_PATTERN.test(company)) return bad("bad_company");

  const codes = Array.from({ length: quantity }, generateCode);
  const hashes = await Promise.all(codes.map((c) => sha256Hex(normalizeCode(c)!)));
  const batch = await rpc("issue_batch", {
    p_company: company,
    p_months: months,
    p_note: cleanText(input?.note, 500),
    p_hashes: hashes,
    p_hints: codes.map((c) => c.slice(-4)),
    p_encrypted: await Promise.all(codes.map((c) => encryptCode(codesKey, c))),
  });

  const csv = buildCsv(batch, codes.map((code) => ({ code, used: false, revoked: false })));
  return { status: 200, body: { ok: true, batch, csv } };
}

// Adds more codes to an existing batch. The new codes inherit the batch's expiry; only they are
// returned in the file, so the customer receives just the extra codes.
export async function handleAddCodes(input: any, rpc: Rpc, codesKey: CryptoKey): Promise<Result> {
  const quantity = Number(input?.quantity);
  const batchId = String(input?.batch_id ?? "");
  if (!Number.isInteger(quantity) || quantity < 1 || quantity > MAX_BATCH) return bad("bad_quantity");
  if (!UUID_PATTERN.test(batchId)) return bad("bad_batch");

  const codes = Array.from({ length: quantity }, generateCode);
  const hashes = await Promise.all(codes.map((c) => sha256Hex(normalizeCode(c)!)));
  const batch = await rpc("add_batch_codes", {
    p_batch: batchId,
    p_hashes: hashes,
    p_hints: codes.map((c) => c.slice(-4)),
    p_encrypted: await Promise.all(codes.map((c) => encryptCode(codesKey, c))),
  });

  const csv = buildCsv(batch, codes.map((code) => ({ code, used: false, revoked: false })));
  return { status: 200, body: { ok: true, batch, csv } };
}

export function clientIp(headers: Headers): string {
  const forwarded = headers.get("x-forwarded-for") ?? "";
  return cleanText(forwarded.split(",")[0], 64) || "unknown";
}

// ---- company-admin logins (used by the admin-users function) ----

// A login name is stored as username@tahakkum.local, so it must be a plain, email-safe handle.
export const ADMIN_USERNAME = /^[a-zA-Z0-9][a-zA-Z0-9._-]{1,30}$/;

// Returns an error key, or null when the credentials are acceptable. bcrypt (what the auth server
// uses) only looks at the first 72 bytes, so a longer password would silently ignore the rest.
export function badAdminCredentials(username: string, password: string): string | null {
  if (!ADMIN_USERNAME.test(username)) return "bad_username";
  if (password.length < 8 || password.length > 72) return "bad_password";
  return null;
}
