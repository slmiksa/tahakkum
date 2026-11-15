import { createClient } from "npm:@supabase/supabase-js@2.80.0";
import { badAdminCredentials } from "../_shared/core.ts";

// Creates, deletes, and resets the password of a company-admin login. Creating or deleting an auth
// user needs the service role, so this function does it — but only after the caller proves, with
// their own token, that they are the owner (an admin with MFA). The company-admin row itself is
// written through the owner-token RPCs, which record the audit entry and re-check ownership.

const cors = {
  "Access-Control-Allow-Origin": Deno.env.get("DASHBOARD_ORIGIN") ?? "*",
  "Access-Control-Allow-Headers": "authorization, apikey, content-type, x-client-info",
  "Access-Control-Allow-Methods": "POST, OPTIONS",
  "Vary": "Origin",
};

const USERNAME_DOMAIN = "tahakkum.local";
const UUID = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/;

function json(body: Record<string, unknown>, status = 200) {
  return Response.json(body, { status, headers: cors });
}

Deno.serve(async (req) => {
  if (req.method === "OPTIONS") return new Response(null, { headers: cors });
  if (req.method !== "POST") return new Response("method not allowed", { status: 405, headers: cors });

  // The caller's own token: the database says whether they are the owner.
  const caller = createClient(Deno.env.get("SUPABASE_URL")!, (Deno.env.get("SUPABASE_PUBLISHABLE_KEY") ?? Deno.env.get("SUPABASE_ANON_KEY"))!, {
    global: { headers: { Authorization: req.headers.get("Authorization") ?? "" } },
    auth: { persistSession: false },
  });
  // The service role: only reached after the owner check below passes.
  const admin = createClient(Deno.env.get("SUPABASE_URL")!, Deno.env.get("SUPABASE_SERVICE_ROLE_KEY")!, {
    auth: { persistSession: false },
  });

  try {
    const { data: isOwner } = await caller.rpc("is_admin");
    if (!isOwner) return json({ ok: false, error: "not_admin" }, 403);

    const input = await req.json().catch(() => ({}));
    const action = String(input?.action ?? "");

    if (action === "create") {
      const username = String(input?.username ?? "").trim();
      const password = String(input?.password ?? "");
      const company = String(input?.company_id ?? "");
      const bad = badAdminCredentials(username, password);
      if (bad) return json({ ok: false, error: bad }, 400);
      if (!UUID.test(company)) return json({ ok: false, error: "bad_company" }, 400);

      const email = username.toLowerCase() + "@" + USERNAME_DOMAIN;
      const created = await admin.auth.admin.createUser({ email, password, email_confirm: true });
      if (created.error || !created.data.user) return json({ ok: false, error: "username_taken" }, 400);

      // Record the row under the owner's identity. If this is refused, undo the auth user we just made.
      const { error: rowError } = await caller.rpc("create_company_admin", { p_user: created.data.user.id, p_company: company, p_username: username });
      if (rowError) {
        await admin.auth.admin.deleteUser(created.data.user.id);
        const denied = (rowError as { code?: string }).code === "42501";
        return json({ ok: false, error: denied ? "not_admin" : "server_error" }, denied ? 403 : 500);
      }
      return json({ ok: true, user_id: created.data.user.id });
    }

    if (action === "delete" || action === "reset") {
      const userId = String(input?.user_id ?? "");
      if (!UUID.test(userId)) return json({ ok: false, error: "bad_request" }, 400);
      // Only ever touch an actual company-admin login, never the owner or an unrelated auth user.
      const { data: target } = await caller.from("company_admins").select("user_id").eq("user_id", userId).maybeSingle();
      if (!target) return json({ ok: false, error: "not_a_company_admin" }, 404);

      if (action === "reset") {
        const password = String(input?.password ?? "");
        if (password.length < 8 || password.length > 72) return json({ ok: false, error: "bad_password" }, 400);
        const r = await admin.auth.admin.updateUserById(userId, { password });
        if (r.error) return json({ ok: false, error: "server_error" }, 500);
        return json({ ok: true });
      }

      // delete: drop the row (owner-checked, audited) first, then the auth user.
      const { error: rowError } = await caller.rpc("delete_company_admin", { p_user: userId });
      if (rowError) {
        const denied = (rowError as { code?: string }).code === "42501";
        return json({ ok: false, error: denied ? "not_admin" : "server_error" }, denied ? 403 : 500);
      }
      await admin.auth.admin.deleteUser(userId);
      return json({ ok: true });
    }

    if (action === "delete-company") {
      const companyId = String(input?.company_id ?? "");
      if (!UUID.test(companyId)) return json({ ok: false, error: "bad_request" }, 400);
      // Remove the company and everything under it under the owner's identity (audited, re-checked).
      const { data, error: rowError } = await caller.rpc("delete_company", { p_company: companyId });
      if (rowError) {
        const denied = (rowError as { code?: string }).code === "42501";
        const unknown = ((rowError as { message?: string }).message ?? "").includes("unknown_company");
        return json({ ok: false, error: denied ? "not_admin" : unknown ? "unknown_company" : "server_error" },
          denied ? 403 : unknown ? 404 : 500);
      }
      // Then delete the company admins' GoTrue logins; their rows are already gone.
      const ids = ((data as { admin_user_ids?: string[] })?.admin_user_ids ?? []).filter((id) => UUID.test(id));
      for (const id of ids) {
        try { await admin.auth.admin.deleteUser(id); } catch (_e) { /* a stray login never blocks the delete */ }
      }
      return json({ ok: true, admins: ids.length });
    }

    return json({ ok: false, error: "bad_request" }, 400);
  } catch (error) {
    console.error(error);
    return json({ ok: false, error: "server_error" }, 500);
  }
});
