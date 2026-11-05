import { createClient } from "npm:@supabase/supabase-js@2.80.0";
import { clientIp, handleActivate, importSigningKey } from "../_shared/core.ts";

const db = createClient(Deno.env.get("SUPABASE_URL")!, (Deno.env.get("SUPABASE_SECRET_KEY") ?? Deno.env.get("SUPABASE_SERVICE_ROLE_KEY"))!);
const key = await importSigningKey(JSON.parse(Deno.env.get("LICENSE_SIGNING_KEY")!));

Deno.serve(async (req) => {
  if (req.method !== "POST") return new Response("method not allowed", { status: 405 });
  try {
    const result = await handleActivate(await req.json(), clientIp(req.headers), async (fn, args) => {
      const { data, error } = await db.rpc(fn, args);
      if (error) throw error;
      return data;
    }, key);
    return Response.json(result.body, { status: result.status });
  } catch (error) {
    console.error(error);
    return Response.json({ ok: false, error: "server_error" }, { status: 500 });
  }
});
