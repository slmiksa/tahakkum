// A stand-in for supabase-js with sample data, for looking at the dashboard without a project.
// Only tests/dashboard-preview.ts serves it; it never ships.
"use strict";
(function () {
  const now = Date.now();
  const iso = (days) => new Date(now + days * 86400000).toISOString();
  const state = {
    session: null,
    level: "aal1",
    factors: [],
    companies: [
      { id: "c1", name: "شركة النور للتقنية", contact: "it@alnoor.example", created_at: iso(-90), android_control: true },
      { id: "c2", name: "مجموعة الرياض", contact: null, created_at: iso(-30), android_control: false },
    ],
    batch_overview: [
      { id: "b1", company_id: "c1", company: "شركة النور للتقنية", quantity: 1000, months: 6, note: "الفرع الرئيسي", issued_at: iso(-60), expires_at: iso(120), revoked_at: null, active_devices: 742, revoked_codes: 3 },
      { id: "b2", company_id: "c2", company: "مجموعة الرياض", quantity: 50, months: 3, note: null, issued_at: iso(-80), expires_at: iso(10), revoked_at: null, active_devices: 48, revoked_codes: 0 },
      { id: "b3", company_id: "c2", company: "مجموعة الرياض", quantity: 20, months: 1, note: "تجربة", issued_at: iso(-120), expires_at: iso(-90), revoked_at: null, active_devices: 20, revoked_codes: 0 },
    ],
    code_overview: [
      { code_id: "k1", code_hint: "7KQ2", code_revoked_at: null, batch_id: "b1", expires_at: iso(120), batch_revoked_at: null, company_id: "c1", company: "شركة النور للتقنية", activation_id: "a1", machine_name: "DESK-1361", last_ip: "198.51.100.24", activated_at: iso(-50), last_seen_at: iso(-1), company_android: true, device_android: true },
      // A machine name written to attack the dashboard: it must show as plain text.
      { code_id: "k2", code_hint: "M9XT", code_revoked_at: null, batch_id: "b1", expires_at: iso(120), batch_revoked_at: null, company_id: "c1", company: "شركة النور للتقنية", activation_id: "a2", machine_name: "<img src=x onerror=window.__xss=1>", last_ip: "198.51.100.25", activated_at: iso(-40), last_seen_at: iso(-2), company_android: true, device_android: false },
      { code_id: "k3", code_hint: "P4DN", code_revoked_at: null, batch_id: "b1", expires_at: iso(120), batch_revoked_at: null, company_id: "c1", company: "شركة النور للتقنية", activation_id: null, machine_name: null, last_ip: null, activated_at: null, last_seen_at: null, company_android: true, device_android: false },
    ],
    audit_log: [
      { id: 2, action: "activate", subject: "k1", details: { company: "شركة النور للتقنية", machine: "DESK-1361", ip: "198.51.100.24" }, at: iso(-1) },
      { id: 1, action: "issue_batch", subject: "b1", details: { company: "شركة النور للتقنية", quantity: 1000, months: 6 }, at: iso(-60) },
    ],
    company_admin_overview: [
      { user_id: "u1", username: "alnoor-it", company_id: "c1", company: "شركة النور للتقنية", created_at: iso(-20) },
    ],
  };
  window.__mock = state;
  // Add ?as=company to the preview URL to see what a company admin sees instead of the owner.
  const previewAs = new URLSearchParams(location.search).get("as");

  function query(table) {
    let rows = state[table].slice();
    const builder = {
      select: () => builder,
      order: () => builder,
      limit: () => builder,
      eq: (k, v) => { rows = rows.filter((r) => r[k] === v); return builder; },
      not: (k) => { rows = rows.filter((r) => r[k] !== null); return builder; },
      or: (expr) => {
        const term = expr.match(/%(.*?)%/)[1].toLowerCase();
        rows = rows.filter((r) => [r.code_hint, r.machine_name, r.last_ip].some((v) => (v || "").toLowerCase().includes(term)));
        return builder;
      },
      then: (ok) => Promise.resolve({ data: rows, error: null }).then(ok),
    };
    return builder;
  }

  const rpc = {
    create_company(a) {
      state.companies.push({ id: "c" + (state.companies.length + 1), name: a.p_name, contact: a.p_contact || null, created_at: iso(0) });
    },
    extend_batch(a) {
      const b = state.batch_overview.find((x) => x.id === a.p_batch);
      b.expires_at = new Date(Math.max(new Date(b.expires_at).getTime(), now) + a.p_months * 30 * 86400000).toISOString();
    },
    revoke_batch(a) { state.batch_overview.find((x) => x.id === a.p_batch).revoked_at = iso(0); },
    revoke_code(a) { state.code_overview.find((x) => x.code_id === a.p_code).code_revoked_at = iso(0); },
    release_activation(a) {
      const c = state.code_overview.find((x) => x.activation_id === a.p_activation);
      Object.assign(c, { activation_id: null, machine_name: null, last_ip: null, last_seen_at: null });
    },
    set_company_android(a) {
      const c = state.companies.find((x) => x.id === a.p_company);
      if (c) c.android_control = a.p_on;
      state.code_overview.forEach((r) => { if (r.company_id === a.p_company) r.company_android = a.p_on; });
    },
    set_device_android(a) {
      const r = state.code_overview.find((x) => x.activation_id === a.p_activation);
      if (r) r.device_android = a.p_on;
    },
    my_role() {
      if (previewAs === "company") return { owner: false, company_id: "c1", company: "شركة النور للتقنية" };
      return { owner: true, company_id: null, company: null };
    },
  };

  window.supabase = {
    createClient: () => ({
      from: query,
      rpc: async (fn, args) => ({ data: fn === "is_admin" ? true : (rpc[fn](args) ?? null), error: null }),
      functions: {
        invoke: async (name, { body }) => {
          if (name === "export-batch") return { data: { ok: true, batch: { company: "شركة", quantity: 1 }, csv: "x" }, error: null };
          if (name === "admin-users") {
            if (body.action === "create") {
              const company = state.companies.find((c) => c.id === body.company_id);
              state.company_admin_overview.unshift({ user_id: "u" + (state.company_admin_overview.length + 1), username: body.username, company_id: body.company_id, company: company ? company.name : "", created_at: iso(0) });
              return { data: { ok: true }, error: null };
            }
            if (body.action === "delete") {
              state.company_admin_overview = state.company_admin_overview.filter((a) => a.user_id !== body.user_id);
              return { data: { ok: true }, error: null };
            }
            return { data: { ok: true }, error: null };
          }
          const company = state.companies.find((c) => c.id === body.company_id);
          const batch = { batch_id: "b" + (state.batch_overview.length + 1), company: company.name, quantity: body.quantity, months: body.months, issued_at: iso(0), expires_at: iso(body.months * 30) };
          state.batch_overview.unshift({ id: batch.batch_id, company_id: company.id, company: company.name, quantity: body.quantity, months: body.months, note: body.note, issued_at: batch.issued_at, expires_at: batch.expires_at, revoked_at: null, active_devices: 0, revoked_codes: 0 });
          return { data: { ok: true, batch, csv: "code,company,expires\r\nTKM-AAAAA-BBBBB-CCCCC-DDDDD\r\n" }, error: null };
        },
      },
      auth: {
        getSession: async () => ({ data: { session: state.session } }),
        signInWithPassword: async ({ password }) => {
          if (password !== "demo") return { error: { message: "bad" } };
          state.session = {};
          return { error: null };
        },
        signOut: async () => { state.session = null; state.level = "aal1"; },
        mfa: {
          getAuthenticatorAssuranceLevel: async () => ({ data: { currentLevel: state.level } }),
          listFactors: async () => ({ data: { totp: state.factors, all: state.factors } }),
          unenroll: async () => ({}),
          enroll: async () => ({ data: { id: "f1", totp: { secret: "JBSWY3DPEHPK3PXP", qr_code: "<svg xmlns='http://www.w3.org/2000/svg' width='176' height='176'><rect width='176' height='176' fill='#000'/><rect x='40' y='40' width='96' height='96' fill='#fff'/></svg>" } } }),
          challengeAndVerify: async ({ code }) => {
            if (code !== "123456") return { error: { message: "bad" } };
            state.level = "aal2";
            state.factors = [{ id: "f1", status: "verified" }];
            return { error: null };
          },
        },
      },
    }),
  };
})();
