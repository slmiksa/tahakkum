// The license dashboard. Every value is put on the page with textContent, never as HTML:
// machine names and addresses come from the other side and must not be able to run here.
"use strict";

const config = window.TAHAKKUM_CONFIG;
const app = document.getElementById("app");
const db = config.url ? window.supabase.createClient(config.url, config.anonKey) : null;

const MONTH_CHOICES = [1, 3, 6, 12, 24, 36];
const PAGE_SIZE = 200;
// Supabase accounts are email addresses, so a plain username is stored under this domain.
const USERNAME_DOMAIN = "tahakkum.local";

const ERRORS = {
  not_admin: "هذا الحساب لا يملك صلاحية المدير، أو أن التحقق بخطوتين لم يكتمل.",
  bad_quantity: "العدد يجب أن يكون بين 1 و 10000.",
  bad_months: "المدة يجب أن تكون بين 1 و 120 شهراً.",
  bad_company: "اختر شركة أولاً.",
  bad_batch: "تعذر تحديد الدفعة.",
};

function el(tag, attrs, ...children) {
  const node = document.createElement(tag);
  for (const [key, value] of Object.entries(attrs || {})) {
    if (value === undefined || value === null || value === false) continue;
    if (key === "class") node.className = value;
    else if (key.startsWith("on")) node.addEventListener(key.slice(2), value);
    else if (key in node && key !== "list") node[key] = value;
    else node.setAttribute(key, value);
  }
  for (const child of children.flat()) {
    if (child === null || child === undefined || child === false) continue;
    node.append(child instanceof Node ? child : document.createTextNode(String(child)));
  }
  return node;
}

function show(...nodes) {
  app.replaceChildren(...nodes);
}

function errorText(error) {
  const message = (error && (error.message || error.error)) || "";
  for (const key of Object.keys(ERRORS)) if (message.includes(key)) return ERRORS[key];
  if (error && error.code === "23505") return "هذا الاسم مستخدم من قبل.";
  if (error && error.code === "42501") return ERRORS.not_admin;
  return "تعذر إتمام العملية. حاول مرة أخرى.";
}

function day(value) {
  return value ? new Date(value).toISOString().slice(0, 10) : "—";
}

function dateTime(value) {
  return value ? new Date(value).toISOString().slice(0, 16).replace("T", " ") : "—";
}

function daysUntil(value) {
  return Math.ceil((new Date(value).getTime() - Date.now()) / 86400000);
}

function addMonths(months) {
  const d = new Date();
  const dayOfMonth = d.getDate();
  d.setMonth(d.getMonth() + months);
  // Jan 31 plus a month is the end of February, as the database counts it, not early March.
  if (d.getDate() !== dayOfMonth) d.setDate(0);
  return d;
}

function ltr(value, extra) {
  return el("span", { class: "ltr " + (extra || "") }, value);
}

function must({ data, error }) {
  if (error) throw error;
  return data;
}

function saveFile(name, text) {
  const url = URL.createObjectURL(new Blob([text], { type: "text/csv;charset=utf-8" }));
  const link = el("a", { href: url, download: name });
  link.click();
  setTimeout(() => URL.revokeObjectURL(url), 10000);
}

function confirmDialog(title, body, actionText, onConfirm, field) {
  const error = el("p", { class: "danger small" });
  const action = el("button", { class: "primary" }, actionText);
  const dialog = el("dialog", {},
    el("h2", {}, title),
    el("p", { class: "muted", style: "margin-top:8px" }, body),
    field || null,
    error,
    el("div", { class: "dialog-actions" }, action, el("button", { onclick: () => dialog.close() }, "إلغاء")));
  action.addEventListener("click", async () => {
    action.disabled = true;
    error.textContent = "";
    try {
      await onConfirm();
      dialog.close();
    } catch (e) {
      error.textContent = errorText(e);
      action.disabled = false;
    }
  });
  dialog.addEventListener("close", () => dialog.remove());
  document.body.append(dialog);
  dialog.showModal();
}

// ---- signing in ----

function screenNotConfigured() {
  show(el("div", { class: "center" }, el("div", { class: "card auth" },
    el("h1", {}, "لوحة الرخص غير مضبوطة"),
    el("p", { class: "muted" }, "شغّل supabase-backend/tools/setup.mjs ليُكتب رابط مشروع Supabase في config.js."))));
}

function loginEmail(input) {
  const name = input.trim();
  return name.includes("@") ? name : name + "@" + USERNAME_DOMAIN;
}

function screenLogin(message) {
  const email = el("input", { type: "text", autocomplete: "username", required: true, dir: "ltr" });
  const password = el("input", { type: "password", autocomplete: "current-password", required: true, dir: "ltr" });
  const error = el("p", { class: "danger small" }, message || "");
  const submit = el("button", { class: "primary" }, "دخول");

  async function signIn() {
    submit.disabled = true;
    error.textContent = "";
    const { error: failed } = await db.auth.signInWithPassword({ email: loginEmail(email.value), password: password.value });
    submit.disabled = false;
    if (failed) {
      error.textContent = "اسم المستخدم أو كلمة المرور غير صحيحة.";
      return;
    }
    await route();
  }
  submit.addEventListener("click", signIn);
  password.addEventListener("keydown", (e) => { if (e.key === "Enter") signIn(); });

  show(el("div", { class: "center" }, el("div", { class: "card auth" },
    el("div", { class: "auth-brand" },
      el("img", { class: "brand-mark", src: "logo.svg", alt: "" }),
      el("img", { class: "brand-word", src: "wordmark.png", alt: "تحكّم" }),
      el("p", { class: "muted small" }, "لوحة الرخص، للمدير فقط")),
    el("label", {}, "اسم المستخدم", email),
    el("label", {}, "كلمة المرور", password),
    error, submit)));
  email.focus();
}

function codeField() {
  return el("input", { inputMode: "numeric", autocomplete: "one-time-code", maxLength: 6, dir: "ltr", class: "mono" });
}

function screenMfaVerify(factorId) {
  const code = codeField();
  const error = el("p", { class: "danger small" });
  const submit = el("button", { class: "primary" }, "تحقق");
  async function verify() {
    submit.disabled = true;
    const { error: failed } = await db.auth.mfa.challengeAndVerify({ factorId, code: code.value.trim() });
    submit.disabled = false;
    if (failed) {
      error.textContent = "الرمز غير صحيح. حاول مرة أخرى.";
      return;
    }
    await route();
  }
  submit.addEventListener("click", verify);
  code.addEventListener("keydown", (e) => { if (e.key === "Enter") verify(); });
  show(el("div", { class: "center" }, el("div", { class: "card auth" },
    el("h1", {}, "التحقق بخطوتين"),
    el("p", { class: "muted small" }, "أدخل الرمز من تطبيق المصادقة."),
    code, error, submit, el("button", { onclick: signOut }, "خروج"))));
  code.focus();
}

async function screenMfaEnroll() {
  const { data, error: failed } = await db.auth.mfa.enroll({ factorType: "totp" });
  if (failed) {
    screenLogin("تعذر بدء إعداد التحقق بخطوتين.");
    return;
  }
  const qr = data.totp.qr_code;
  const code = codeField();
  const error = el("p", { class: "danger small" });
  const submit = el("button", { class: "primary" }, "تأكيد");
  submit.addEventListener("click", async () => {
    submit.disabled = true;
    const { error: bad } = await db.auth.mfa.challengeAndVerify({ factorId: data.id, code: code.value.trim() });
    submit.disabled = false;
    if (bad) {
      error.textContent = "الرمز غير صحيح. حاول مرة أخرى.";
      return;
    }
    await route();
  });
  show(el("div", { class: "center" }, el("div", { class: "card auth" },
    el("h1", {}, "إعداد التحقق بخطوتين"),
    el("p", { class: "muted small" }, "إلزامي لهذه اللوحة. امسح الرمز بتطبيق مصادقة مثل Google Authenticator، ثم أدخل الرمز الذي يظهر فيه."),
    el("img", { class: "qr", alt: "رمز الإعداد", src: qr.startsWith("data:") ? qr : "data:image/svg+xml;utf-8," + encodeURIComponent(qr) }),
    el("p", { class: "muted small" }, "أو أدخل المفتاح يدوياً: ", ltr(data.totp.secret, "mono")),
    code, error, submit, el("button", { onclick: signOut }, "خروج"))));
}

function screenNotAdmin() {
  show(el("div", { class: "center" }, el("div", { class: "card auth" },
    el("h1", {}, "هذا الحساب ليس مديراً"),
    el("p", { class: "muted small" }, "دخلت بنجاح، لكن الحساب غير مضاف إلى جدول المدراء في قاعدة البيانات. أضفه من محرر SQL في Supabase ثم أعد تحميل الصفحة."),
    el("button", { onclick: signOut }, "خروج"))));
}

async function signOut() {
  await db.auth.signOut();
  screenLogin();
}

async function route() {
  const { data: { session } } = await db.auth.getSession();
  if (!session) return screenLogin();
  const { data: level } = await db.auth.mfa.getAuthenticatorAssuranceLevel();
  if (level.currentLevel !== "aal2") {
    const { data: factors } = await db.auth.mfa.listFactors();
    const verified = factors.totp.find((f) => f.status === "verified");
    if (verified) return screenMfaVerify(verified.id);
    // A setup abandoned halfway would otherwise block starting again.
    for (const f of factors.all) if (f.status !== "verified") await db.auth.mfa.unenroll({ factorId: f.id });
    return screenMfaEnroll();
  }
  // Without this check a non-admin would just see empty pages, which looks like a working dashboard.
  const { data: isAdmin } = await db.rpc("is_admin");
  if (!isAdmin) return screenNotAdmin();
  return showShell();
}

// ---- pages ----

const content = el("main");
const tabs = [
  { id: "batches", title: "الدفعات", render: pageBatches },
  { id: "companies", title: "الشركات", render: pageCompanies },
  { id: "devices", title: "الأكواد والأجهزة", render: pageDevices },
  { id: "audit", title: "السجل", render: pageAudit },
];
let activeTab = "batches";

function showShell() {
  const nav = el("nav", {}, tabs.map((t) => el("button", {
    class: t.id === activeTab ? "active" : "",
    onclick: () => { activeTab = t.id; showShell(); },
  }, t.title)));
  show(
    el("header", { class: "top" },
      el("div", { class: "brand" }, el("img", { class: "brand-mark", src: "logo.svg", alt: "" }), el("h1", {}, "رخص تحكّم")),
      nav, el("button", { onclick: signOut }, "خروج")),
    content);
  renderTab();
}

async function renderTab() {
  const tab = tabs.find((t) => t.id === activeTab);
  content.replaceChildren(el("p", { class: "muted" }, "جاري التحميل…"));
  try {
    await tab.render();
  } catch (e) {
    content.replaceChildren(el("div", { class: "alert" }, errorText(e)));
  }
}

function table(headers, rows, emptyText) {
  if (rows.length === 0) return el("p", { class: "empty" }, emptyText);
  return el("div", { class: "table-wrap" }, el("table", {},
    el("thead", {}, el("tr", {}, headers.map((h) => el("th", {}, h)))),
    el("tbody", {}, rows.map((cells) => el("tr", {}, cells.map((c) => c instanceof HTMLElement && c.tagName === "TD" ? c : el("td", {}, c)))))));
}

function pill(kind, text) {
  return el("span", { class: "pill " + kind }, text);
}

// -- batches --

function batchState(b) {
  if (b.revoked_at) return pill("off", "ملغاة");
  const left = daysUntil(b.expires_at);
  if (left <= 0) return pill("off", "منتهية");
  if (left <= 14) return pill("warn", "متبقٍ " + left + " يوماً");
  return pill("ok", "سارية");
}

async function pageBatches() {
  const [companies, batches] = await Promise.all([
    db.from("companies").select("id, name").order("name").then(must),
    db.from("batch_overview").select("*").order("issued_at", { ascending: false }).limit(PAGE_SIZE).then(must),
  ]);
  content.replaceChildren(issueForm(companies), el("section", { class: "card" }, ...batchList(batches)));
}

function batchList(batches) {
  return [
    el("div", { class: "head-row" }, el("h2", {}, "الدفعات الصادرة")),
    table(["الشركة", "الأكواد", "الأجهزة المفعّلة", "المدة", "الإصدار", "الانتهاء", "الحالة", "ملاحظة", ""],
      batches.map((b) => [
        b.company, b.quantity, b.active_devices + " / " + b.quantity, b.months + " شهر",
        ltr(day(b.issued_at)), ltr(day(b.expires_at)), batchState(b), b.note || "—",
        el("td", { class: "actions" },
          el("button", { class: "tiny", onclick: () => exportBatch(b) }, "تصدير"),
          el("button", { class: "tiny", onclick: () => extendBatch(b) }, "تمديد"),
          !b.revoked_at && el("button", { class: "tiny bad", onclick: () => revokeBatch(b) }, "إلغاء")),
      ]), "لم تصدر أي دفعة بعد."),
  ];
}

function issueForm(companies) {
  if (companies.length === 0)
    return el("section", { class: "card" }, el("h2", {}, "إصدار أكواد"), el("p", { class: "muted", style: "margin-top:8px" }, "أضف شركة أولاً من صفحة الشركات."));

  const company = el("select", {}, companies.map((c) => el("option", { value: c.id }, c.name)));
  const quantity = el("input", { type: "number", min: 1, max: 10000, value: 100 });
  const months = el("select", {}, MONTH_CHOICES.map((m) => el("option", { value: m, selected: m === 6 }, m + " شهر")));
  const note = el("input", { type: "text", maxLength: 500 });
  const preview = el("p", { class: "muted small" });
  const error = el("p", { class: "danger small" });
  const result = el("div");
  const submit = el("button", { class: "primary" }, "إصدار وتنزيل الملف");

  function updatePreview() {
    preview.replaceChildren("كل الأكواد تنتهي في ", ltr(day(addMonths(Number(months.value)))), "، أي من اليوم وليس من يوم تفعيلها.");
  }
  months.addEventListener("change", updatePreview);
  updatePreview();

  submit.addEventListener("click", async () => {
    submit.disabled = true;
    error.textContent = "";
    try {
      const { data, error: failed } = await db.functions.invoke("issue-batch", {
        body: { company_id: company.value, quantity: Number(quantity.value), months: Number(months.value), note: note.value },
      });
      if (failed) throw await functionError(failed);
      if (!data.ok) throw new Error(data.error);
      const name = fileName(data.batch);
      saveFile(name, data.csv);
      result.replaceChildren(el("div", { class: "notice stack" },
        el("div", {}, el("strong", {}, "صدرت " + data.batch.quantity + " كود لشركة " + data.batch.company + ". "),
          "تنتهي في ", ltr(day(data.batch.expires_at)), "."),
        el("p", { class: "small" }, "يمكنك تصدير الملف مرة أخرى في أي وقت من قائمة الدفعات، مع حالة كل كود."),
        el("div", {}, el("button", { onclick: () => saveFile(name, data.csv) }, "تنزيل الملف مرة أخرى"))));
      await refreshBatchList();
    } catch (e) {
      error.textContent = errorText(e);
    }
    submit.disabled = false;
  });

  return el("section", { class: "card stack" },
    el("h2", {}, "إصدار أكواد"),
    el("div", { class: "form-grid" },
      el("label", {}, "الشركة", company),
      el("label", {}, "عدد الأكواد", quantity),
      el("label", {}, "المدة", months),
      el("label", {}, "ملاحظة (اختياري)", note),
      submit),
    preview, error, result);
}

async function functionError(failed) {
  try {
    const body = await failed.context.json();
    return new Error(body.error || "");
  } catch (e) {
    return failed;
  }
}

function fileName(batch) {
  const safe = batch.company.replace(/[\\/:*?"<>|\s]+/g, "-");
  return "tahakkum-" + safe + "-" + batch.quantity + "-" + day(batch.issued_at) + ".csv";
}

// Keeps the issue form on screen, with the one-time download, while the list updates.
async function refreshBatchList() {
  const batches = await db.from("batch_overview").select("*").order("issued_at", { ascending: false }).limit(PAGE_SIZE).then(must);
  content.lastElementChild.replaceChildren(...batchList(batches));
}

// The file holds usable codes, so this asks first, and the database records every export.
function exportBatch(b) {
  confirmDialog("تصدير الأكواد",
    "ملف " + b.company + " يحتوي على الأكواد الصالحة، وحالة كل كود، وملخص المستخدم والمتبقي. لا ترسله إلا لجهة مخولة في الشركة. وتُسجَّل كل عملية تصدير.",
    "تصدير الملف",
    async () => {
      const { data, error: failed } = await db.functions.invoke("export-batch", { body: { batch_id: b.id } });
      if (failed) throw await functionError(failed);
      if (!data.ok) throw new Error(data.error);
      saveFile(fileName({ company: data.batch.company, quantity: data.batch.quantity, issued_at: new Date().toISOString() }), data.csv);
    });
}

function extendBatch(b) {
  const months = el("select", {}, MONTH_CHOICES.map((m) => el("option", { value: m, selected: m === 6 }, m + " شهر")));
  confirmDialog("تمديد الدفعة",
    b.company + ": تنتهي حالياً في " + day(b.expires_at) + ". يصل التمديد للأجهزة تلقائياً دون إدخال أكواد جديدة.",
    "تمديد",
    async () => {
      must(await db.rpc("extend_batch", { p_batch: b.id, p_months: Number(months.value) }));
      renderTab();
    },
    el("label", { style: "margin-top:14px" }, "مدة التمديد", months));
}

function revokeBatch(b) {
  confirmDialog("إلغاء الدفعة",
    "سيتوقف البرنامج على كل أجهزة " + b.company + " في هذه الدفعة عند اتصالها بالخادم، ولا يمكن التراجع عن ذلك.",
    "إلغاء الدفعة",
    async () => {
      must(await db.rpc("revoke_batch", { p_batch: b.id }));
      renderTab();
    });
}

// -- companies --

async function pageCompanies() {
  const companies = await db.from("companies").select("*").order("name").then(must);
  const name = el("input", { type: "text", maxLength: 200 });
  const contact = el("input", { type: "text", maxLength: 200 });
  const error = el("p", { class: "danger small" });
  const add = el("button", { class: "primary" }, "إضافة");
  add.addEventListener("click", async () => {
    if (!name.value.trim()) {
      error.textContent = "اكتب اسم الشركة.";
      return;
    }
    add.disabled = true;
    try {
      must(await db.rpc("create_company", { p_name: name.value, p_contact: contact.value }));
      renderTab();
    } catch (e) {
      error.textContent = errorText(e);
      add.disabled = false;
    }
  });
  content.replaceChildren(
    el("section", { class: "card stack" },
      el("h2", {}, "إضافة شركة"),
      el("div", { class: "form-grid" },
        el("label", {}, "اسم الشركة", name),
        el("label", {}, "جهة التواصل (اختياري)", contact),
        add),
      error),
    el("section", { class: "card" },
      el("div", { class: "head-row" }, el("h2", {}, "الشركات")),
      table(["الاسم", "جهة التواصل", "أضيفت في"],
        companies.map((c) => [c.name, c.contact || "—", ltr(day(c.created_at))]), "لا توجد شركات بعد.")));
}

// -- codes and devices --

function codeState(c) {
  if (c.code_revoked_at || c.batch_revoked_at) return pill("off", "ملغى");
  if (daysUntil(c.expires_at) <= 0) return pill("off", "منتهٍ");
  return c.activation_id ? pill("ok", "مفعّل") : pill("idle", "لم يُفعّل");
}

async function pageDevices() {
  const companies = await db.from("companies").select("id, name").order("name").then(must);
  const company = el("select", {}, el("option", { value: "" }, "كل الشركات"), companies.map((c) => el("option", { value: c.id }, c.name)));
  const search = el("input", { type: "text", maxLength: 64 });
  const onlyActive = el("select", {}, el("option", { value: "active" }, "المفعّلة فقط"), el("option", { value: "all" }, "كل الأكواد"));
  const list = el("div");

  async function load() {
    list.replaceChildren(el("p", { class: "muted" }, "جاري التحميل…"));
    let query = db.from("code_overview").select("*");
    if (company.value) query = query.eq("company_id", company.value);
    if (onlyActive.value === "active") query = query.not("activation_id", "is", null);
    const term = search.value.trim().replace(/[%_,()]/g, "");
    if (term) query = query.or("code_hint.ilike.%" + term + "%,machine_name.ilike.%" + term + "%,last_ip.ilike.%" + term + "%");
    try {
      const rows = must(await query.order("last_seen_at", { ascending: false, nullsFirst: false }).limit(PAGE_SIZE));
      list.replaceChildren(table(["الكود", "الشركة", "اسم الجهاز", "آخر IP", "آخر اتصال", "ينتهي", "الحالة", ""],
        rows.map((c) => [
          ltr("…" + c.code_hint, "mono"), c.company, c.machine_name ? ltr(c.machine_name) : "—", c.last_ip ? ltr(c.last_ip) : "—",
          ltr(dateTime(c.last_seen_at)), ltr(day(c.expires_at)), codeState(c),
          el("td", { class: "actions" },
            c.activation_id && el("button", { class: "tiny", onclick: () => releaseDevice(c, load) }, "فك الارتباط"),
            !c.code_revoked_at && el("button", { class: "tiny bad", onclick: () => revokeCode(c, load) }, "إلغاء الكود")),
        ]), "لا توجد نتائج."),
        rows.length === PAGE_SIZE ? el("p", { class: "muted small" }, "تظهر أول " + PAGE_SIZE + " نتيجة. ضيّق البحث لرؤية البقية.") : "");
    } catch (e) {
      list.replaceChildren(el("div", { class: "alert" }, errorText(e)));
    }
  }

  company.addEventListener("change", load);
  onlyActive.addEventListener("change", load);
  search.addEventListener("keydown", (e) => { if (e.key === "Enter") load(); });

  content.replaceChildren(el("section", { class: "card stack" },
    el("h2", {}, "الأكواد والأجهزة"),
    el("div", { class: "form-grid" },
      el("label", {}, "الشركة", company),
      el("label", {}, "عرض", onlyActive),
      el("label", {}, "بحث: آخر 4 أحرف من الكود، أو اسم الجهاز، أو IP", search),
      el("button", { onclick: load }, "بحث")),
    list));
  await load();
}

function releaseDevice(c, reload) {
  confirmDialog("فك ارتباط الجهاز",
    "لنقل الكود …" + c.code_hint + " إلى جهاز جديد. يفقد الجهاز " + (c.machine_name || "الحالي") + " الرخصة عند اتصاله التالي بالخادم.",
    "فك الارتباط",
    async () => {
      must(await db.rpc("release_activation", { p_activation: c.activation_id }));
      reload();
    });
}

function revokeCode(c, reload) {
  confirmDialog("إلغاء الكود",
    "لن يعمل الكود …" + c.code_hint + " بعد الآن على أي جهاز، ولا يمكن التراجع عن ذلك. ويتوقف الجهاز المفعّل عند اتصاله التالي بالخادم.",
    "إلغاء الكود",
    async () => {
      must(await db.rpc("revoke_code", { p_code: c.code_id }));
      reload();
    });
}

// -- audit --

const ACTIONS = {
  create_company: "إضافة شركة",
  issue_batch: "إصدار دفعة",
  export_batch: "تصدير دفعة",
  extend_batch: "تمديد دفعة",
  revoke_batch: "إلغاء دفعة",
  revoke_code: "إلغاء كود",
  release_activation: "فك ارتباط جهاز",
  activate: "تفعيل جهاز",
  activation_refused_other_device: "محاولة تفعيل من جهاز آخر",
};

async function pageAudit() {
  const rows = await db.from("audit_log").select("*").order("at", { ascending: false }).limit(PAGE_SIZE).then(must);
  content.replaceChildren(el("section", { class: "card" },
    el("div", { class: "head-row" }, el("h2", {}, "السجل")),
    table(["الوقت (UTC)", "العملية", "التفاصيل"],
      rows.map((r) => [ltr(dateTime(r.at)), ACTIONS[r.action] || r.action, details(r)]), "السجل فارغ.")));
}

function details(row) {
  const d = row.details || {};
  const parts = [];
  if (d.company) parts.push(d.company);
  if (d.quantity) parts.push(d.quantity + " كود");
  if (d.months) parts.push(d.months + " شهر");
  if (d.machine) parts.push(d.machine);
  if (d.ip) parts.push(d.ip);
  if (parts.length === 0 && row.action === "create_company") parts.push(row.subject);
  return parts.join(" • ") || "—";
}

// ---- start ----

if (!db) {
  screenNotConfigured();
} else {
  route();
}
