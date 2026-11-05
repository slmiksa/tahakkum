import { createClient } from "npm:@supabase/supabase-js@2.80.0";
import { handleIssue, importCodesKey } from "../_shared/core.ts";

const cors = {
  "Access-Control-Allow-Origin": Deno.env.get("DASHBOARD_ORIGIN") ?? "*",
  "Access-Control-Allow-Headers": "authorization, apikey, content-type, x-client-info",
  "Access-Control-Allow-Methods": "POST, OPTIONS",
  "Vary": "Origin",
};
const codesKey = await importCodesKey(Deno.env.get("CODES_KEY")!);

Deno.serve(async (req) => {
  if (req.method === "OPTIONS") return new Response(null, { headers: cors });
  if (req.method !== "POST") return new Response("method not allowed", { status: 405, headers: cors });

  // The caller's own token, not the service role: the database decides whether they may issue.
  const db = createClient(Deno.env.get("SUPABASE_URL")!, (Deno.env.get("SUPABASE_PUBLISHABLE_KEY") ?? Deno.env.get("SUPABASE_ANON_KEY"))!, {
    global: { headers: { Authorization: req.headers.get("Authorization") ?? "" } },
    auth: { persistSession: false },
  });
  try {
    const result = await handleIssue(await req.json(), async (fn, args) => {
      const { data, error } = await db.rpc(fn, args);
      if (error) throw error;
      return data;
    }, codesKey);
    return Response.json(result.body, { status: result.status, headers: cors });
  } catch (error) {
    console.error(error);
    const denied = (error as { code?: string }).code === "42501";
    return Response.json({ ok: false, error: denied ? "not_admin" : "server_error" }, { status: denied ? 403 : 500, headers: cors });
  }
});
