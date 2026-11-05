-- Company licensing. Nothing here is writable directly: admins go through the functions
-- below (which require MFA), and the desktop program only reaches the two service-role
-- functions through Edge Functions.

create table public.admins (
  user_id uuid primary key references auth.users (id) on delete cascade,
  created_at timestamptz not null default now()
);

create table public.companies (
  id uuid primary key default gen_random_uuid(),
  name text not null unique check (length(trim(name)) between 1 and 200),
  contact text,
  created_at timestamptz not null default now()
);

-- expires_at is fixed when the batch is issued, so every code in it runs out together,
-- whenever it happens to be activated.
create table public.license_batches (
  id uuid primary key default gen_random_uuid(),
  company_id uuid not null references public.companies (id),
  quantity int not null check (quantity between 1 and 10000),
  months int not null check (months between 1 and 120),
  note text,
  issued_at timestamptz not null default now(),
  expires_at timestamptz not null,
  revoked_at timestamptz,
  created_by uuid references auth.users (id)
);

-- Only a hash of each code is kept: a leak of this table reveals no usable code.
create table public.license_codes (
  id uuid primary key default gen_random_uuid(),
  batch_id uuid not null references public.license_batches (id),
  code_hash text not null unique,
  code_hint text not null,
  revoked_at timestamptz
);
create index license_codes_batch on public.license_codes (batch_id);
create index license_codes_hint on public.license_codes (code_hint);

create table public.activations (
  id uuid primary key default gen_random_uuid(),
  code_id uuid not null references public.license_codes (id),
  device_hash text not null,
  machine_name text,
  last_ip text,
  secret_hash text not null,
  activated_at timestamptz not null default now(),
  last_seen_at timestamptz not null default now(),
  released_at timestamptz
);
-- The database itself guarantees one active device per code.
create unique index activations_one_active on public.activations (code_id) where released_at is null;
create index activations_device on public.activations (device_hash);

create table public.activation_failures (
  ip text not null,
  at timestamptz not null default now()
);
create index activation_failures_ip on public.activation_failures (ip, at);

create table public.audit_log (
  id bigint generated always as identity primary key,
  actor uuid,
  action text not null,
  subject text,
  details jsonb,
  at timestamptz not null default now()
);

-- ---- access ----

alter table public.admins enable row level security;
alter table public.companies enable row level security;
alter table public.license_batches enable row level security;
alter table public.license_codes enable row level security;
alter table public.activations enable row level security;
alter table public.activation_failures enable row level security;
alter table public.audit_log enable row level security;

revoke all on all tables in schema public from anon, authenticated;

create function public.is_admin() returns boolean
language sql stable security definer set search_path = '' as $$
  select coalesce(auth.jwt() ->> 'aal' = 'aal2', false)
    and exists (select 1 from public.admins where user_id = auth.uid());
$$;

create function public.require_admin() returns void
language plpgsql stable security definer set search_path = '' as $$
begin
  if not public.is_admin() then
    raise exception 'not_admin' using errcode = '42501';
  end if;
end;
$$;

grant select on public.companies, public.license_batches, public.license_codes, public.activations, public.audit_log to authenticated;

create policy admin_read on public.companies for select to authenticated using (public.is_admin());
create policy admin_read on public.license_batches for select to authenticated using (public.is_admin());
create policy admin_read on public.license_codes for select to authenticated using (public.is_admin());
create policy admin_read on public.activations for select to authenticated using (public.is_admin());
create policy admin_read on public.audit_log for select to authenticated using (public.is_admin());

-- The hashes and device secrets stay out of everything the dashboard can read.
revoke select on public.license_codes from authenticated;
grant select (id, batch_id, code_hint, revoked_at) on public.license_codes to authenticated;
revoke select on public.activations from authenticated;
grant select (id, code_id, device_hash, machine_name, last_ip, activated_at, last_seen_at, released_at) on public.activations to authenticated;

create view public.batch_overview with (security_invoker = true) as
  select b.id, b.company_id, c.name as company, b.quantity, b.months, b.note, b.issued_at, b.expires_at, b.revoked_at,
    (select count(*) from public.license_codes k
       join public.activations a on a.code_id = k.id and a.released_at is null
      where k.batch_id = b.id) as active_devices,
    (select count(*) from public.license_codes k where k.batch_id = b.id and k.revoked_at is not null) as revoked_codes
  from public.license_batches b
  join public.companies c on c.id = b.company_id;

create view public.code_overview with (security_invoker = true) as
  select k.id as code_id, k.code_hint, k.revoked_at as code_revoked_at, k.batch_id,
    b.expires_at, b.revoked_at as batch_revoked_at, c.id as company_id, c.name as company,
    a.id as activation_id, a.machine_name, a.last_ip, a.activated_at, a.last_seen_at
  from public.license_codes k
  join public.license_batches b on b.id = k.batch_id
  join public.companies c on c.id = b.company_id
  left join public.activations a on a.code_id = k.id and a.released_at is null;

revoke all on public.batch_overview, public.code_overview from anon, authenticated;
grant select on public.batch_overview, public.code_overview to authenticated;

-- ---- admin actions ----

create function public.create_company(p_name text, p_contact text) returns uuid
language plpgsql security definer set search_path = '' as $$
declare v_id uuid;
begin
  perform public.require_admin();
  insert into public.companies (name, contact) values (trim(p_name), nullif(trim(p_contact), ''))
    returning id into v_id;
  insert into public.audit_log (actor, action, subject) values (auth.uid(), 'create_company', p_name);
  return v_id;
end;
$$;

-- The codes are generated by the issue-batch Edge Function, which returns them once and
-- passes only their hashes here.
create function public.issue_batch(p_company uuid, p_months int, p_note text, p_hashes text[], p_hints text[])
returns jsonb
language plpgsql security definer set search_path = '' as $$
declare
  v_batch public.license_batches;
  v_company text;
  v_count int := coalesce(array_length(p_hashes, 1), 0);
begin
  perform public.require_admin();
  if v_count <> coalesce(array_length(p_hints, 1), 0) then
    raise exception 'hash_hint_mismatch';
  end if;
  select name into v_company from public.companies where id = p_company;
  if not found then
    raise exception 'unknown_company';
  end if;

  insert into public.license_batches (company_id, quantity, months, note, expires_at, created_by)
    values (p_company, v_count, p_months, nullif(trim(p_note), ''), now() + make_interval(months => p_months), auth.uid())
    returning * into v_batch;
  insert into public.license_codes (batch_id, code_hash, code_hint)
    select v_batch.id, h, i from unnest(p_hashes, p_hints) as t (h, i);
  insert into public.audit_log (actor, action, subject, details)
    values (auth.uid(), 'issue_batch', v_batch.id::text, jsonb_build_object('company', v_company, 'quantity', v_count, 'months', p_months));

  return jsonb_build_object('batch_id', v_batch.id, 'company', v_company, 'quantity', v_count,
    'months', p_months, 'issued_at', v_batch.issued_at, 'expires_at', v_batch.expires_at);
end;
$$;

create function public.extend_batch(p_batch uuid, p_months int) returns timestamptz
language plpgsql security definer set search_path = '' as $$
declare v_expires timestamptz;
begin
  perform public.require_admin();
  if p_months not between 1 and 120 then
    raise exception 'bad_months';
  end if;
  -- An expired batch continues from today, so a late renewal gets the whole period it paid for.
  update public.license_batches
    set expires_at = greatest(expires_at, now()) + make_interval(months => p_months)
    where id = p_batch
    returning expires_at into v_expires;
  if not found then
    raise exception 'unknown_batch';
  end if;
  insert into public.audit_log (actor, action, subject, details)
    values (auth.uid(), 'extend_batch', p_batch::text, jsonb_build_object('months', p_months, 'expires_at', v_expires));
  return v_expires;
end;
$$;

create function public.revoke_batch(p_batch uuid) returns void
language plpgsql security definer set search_path = '' as $$
begin
  perform public.require_admin();
  update public.license_batches set revoked_at = coalesce(revoked_at, now()) where id = p_batch;
  insert into public.audit_log (actor, action, subject) values (auth.uid(), 'revoke_batch', p_batch::text);
end;
$$;

create function public.revoke_code(p_code uuid) returns void
language plpgsql security definer set search_path = '' as $$
begin
  perform public.require_admin();
  update public.license_codes set revoked_at = coalesce(revoked_at, now()) where id = p_code;
  insert into public.audit_log (actor, action, subject) values (auth.uid(), 'revoke_code', p_code::text);
end;
$$;

-- Frees a code for use on a replacement machine. The old machine loses its license at its next refresh.
create function public.release_activation(p_activation uuid) returns void
language plpgsql security definer set search_path = '' as $$
begin
  perform public.require_admin();
  update public.activations set released_at = now() where id = p_activation and released_at is null;
  insert into public.audit_log (actor, action, subject) values (auth.uid(), 'release_activation', p_activation::text);
end;
$$;

-- ---- called by the desktop program, through Edge Functions only ----

create function public.activate_code(p_code_hash text, p_device text, p_machine text, p_ip text, p_secret_hash text)
returns jsonb
language plpgsql security definer set search_path = '' as $$
declare
  v_code public.license_codes;
  v_batch public.license_batches;
  v_company text;
  v_active public.activations;
begin
  delete from public.activation_failures where at < now() - interval '1 day';
  -- Only failures count: one company may legitimately activate a thousand machines from one address.
  if (select count(*) from public.activation_failures where ip = p_ip and at > now() - interval '10 minutes') >= 20 then
    return jsonb_build_object('ok', false, 'error', 'rate_limited');
  end if;

  -- The row lock makes two machines racing for one code take turns.
  select * into v_code from public.license_codes where code_hash = p_code_hash for update;
  if not found then
    insert into public.activation_failures (ip) values (p_ip);
    return jsonb_build_object('ok', false, 'error', 'invalid_code');
  end if;

  select * into v_batch from public.license_batches where id = v_code.batch_id;
  select name into v_company from public.companies where id = v_batch.company_id;
  if v_code.revoked_at is not null or v_batch.revoked_at is not null then
    return jsonb_build_object('ok', false, 'error', 'revoked');
  end if;
  if v_batch.expires_at <= now() then
    return jsonb_build_object('ok', false, 'error', 'expired', 'expires_at', v_batch.expires_at);
  end if;

  select * into v_active from public.activations where code_id = v_code.id and released_at is null;
  if found and v_active.device_hash <> p_device then
    insert into public.audit_log (action, subject, details)
      values ('activation_refused_other_device', v_code.id::text, jsonb_build_object('machine', p_machine, 'ip', p_ip));
    return jsonb_build_object('ok', false, 'error', 'other_device');
  end if;

  if found then
    -- The same machine again, such as after a reinstall.
    update public.activations
      set secret_hash = p_secret_hash, machine_name = p_machine, last_ip = p_ip, last_seen_at = now()
      where id = v_active.id;
  else
    insert into public.activations (code_id, device_hash, machine_name, last_ip, secret_hash)
      values (v_code.id, p_device, p_machine, p_ip, p_secret_hash);
    insert into public.audit_log (action, subject, details)
      values ('activate', v_code.id::text, jsonb_build_object('company', v_company, 'machine', p_machine, 'ip', p_ip));
  end if;

  return jsonb_build_object('ok', true, 'code_id', v_code.id, 'company', v_company,
    'expires_at', v_batch.expires_at, 'server_time', now());
end;
$$;

create function public.refresh_license(p_code uuid, p_device text, p_secret_hash text, p_machine text, p_ip text)
returns jsonb
language plpgsql security definer set search_path = '' as $$
declare
  v_active public.activations;
  v_code public.license_codes;
  v_batch public.license_batches;
  v_company text;
begin
  select * into v_active from public.activations
    where code_id = p_code and device_hash = p_device and secret_hash = p_secret_hash and released_at is null;
  if not found then
    return jsonb_build_object('ok', false, 'error', 'released');
  end if;

  select * into v_code from public.license_codes where id = p_code;
  select * into v_batch from public.license_batches where id = v_code.batch_id;
  select name into v_company from public.companies where id = v_batch.company_id;
  update public.activations set last_seen_at = now(), last_ip = p_ip, machine_name = p_machine where id = v_active.id;

  if v_code.revoked_at is not null or v_batch.revoked_at is not null then
    return jsonb_build_object('ok', false, 'error', 'revoked');
  end if;
  return jsonb_build_object('ok', v_batch.expires_at > now(), 'error', case when v_batch.expires_at > now() then null else 'expired' end,
    'code_id', v_code.id, 'company', v_company, 'expires_at', v_batch.expires_at, 'server_time', now());
end;
$$;

-- ---- who may call what ----

revoke all on function public.is_admin(), public.require_admin(),
  public.create_company(text, text), public.issue_batch(uuid, int, text, text[], text[]),
  public.extend_batch(uuid, int), public.revoke_batch(uuid), public.revoke_code(uuid), public.release_activation(uuid),
  public.activate_code(text, text, text, text, text), public.refresh_license(uuid, text, text, text, text)
  from public, anon, authenticated;

grant execute on function public.is_admin(), public.require_admin(),
  public.create_company(text, text), public.issue_batch(uuid, int, text, text[], text[]),
  public.extend_batch(uuid, int), public.revoke_batch(uuid), public.revoke_code(uuid), public.release_activation(uuid)
  to authenticated;

grant execute on function public.activate_code(text, text, text, text, text), public.refresh_license(uuid, text, text, text, text)
  to service_role;
