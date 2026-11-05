// Runs the real migration on PGlite (Postgres compiled to WebAssembly), with the small part
// of Supabase it relies on recreated: the auth schema and the three standard roles.
import { PGlite } from "@electric-sql/pglite";
import { readdirSync, readFileSync } from "node:fs";
import type { Rpc } from "../supabase/functions/_shared/core.ts";

const MIGRATIONS = new URL("../supabase/migrations/", import.meta.url);

const SUPABASE_STUB = `
  create role anon nologin;
  create role authenticated nologin;
  create role service_role nologin bypassrls;
  grant usage on schema public to anon, authenticated, service_role;
  create schema auth;
  grant usage on schema auth to anon, authenticated, service_role;
  create table auth.users (id uuid primary key default gen_random_uuid());
  create function auth.jwt() returns jsonb language sql stable as $$
    select coalesce(nullif(current_setting('request.jwt.claims', true), ''), '{}')::jsonb $$;
  create function auth.uid() returns uuid language sql stable as $$
    select nullif(auth.jwt() ->> 'sub', '')::uuid $$;
  -- Supabase grants these by default on every new object; the migration has to undo them.
  alter default privileges in schema public grant all on tables to anon, authenticated, service_role;
  alter default privileges in schema public grant execute on functions to anon, authenticated, service_role;
`;

export async function openDatabase(): Promise<PGlite> {
  const db = new PGlite();
  await db.exec(SUPABASE_STUB);
  for (const name of readdirSync(MIGRATIONS).sort()) await db.exec(readFileSync(new URL(name, MIGRATIONS), "utf8"));
  return db;
}

export async function addUser(db: PGlite, admin: boolean): Promise<string> {
  const { rows } = await db.query<{ id: string }>("insert into auth.users default values returning id");
  if (admin) await db.query("insert into public.admins (user_id) values ($1)", [rows[0].id]);
  return rows[0].id;
}

function sqlLiteral(value: string): string {
  return "'" + value.replace(/'/g, "''") + "'";
}

// Runs one call the way PostgREST does: as a role, with the caller's token claims.
export async function asRole<T>(db: PGlite, role: string, claims: object | null, body: () => Promise<T>): Promise<T> {
  await db.exec("reset role; select set_config('request.jwt.claims', " + sqlLiteral(JSON.stringify(claims ?? {})) + ", false); set role " + role);
  try {
    return await body();
  } finally {
    await db.exec("reset role; select set_config('request.jwt.claims', '', false)");
  }
}

export function rpcAs(db: PGlite, role: string, claims: object | null = null): Rpc {
  return (fn, args) => asRole(db, role, claims, async () => {
    const keys = Object.keys(args);
    const params = keys.map((k, i) => k + " => $" + (i + 1)).join(", ");
    const { rows } = await db.query<Record<string, unknown>>("select public." + fn + "(" + params + ") as result", keys.map((k) => args[k]));
    return rows[0].result;
  });
}

export async function newSigningKey(): Promise<{ privateJwk: JsonWebKey; publicJwk: JsonWebKey }> {
  const pair = await crypto.subtle.generateKey({ name: "ECDSA", namedCurve: "P-256" }, true, ["sign", "verify"]);
  return {
    privateJwk: await crypto.subtle.exportKey("jwk", pair.privateKey),
    publicJwk: await crypto.subtle.exportKey("jwk", pair.publicKey),
  };
}
