-- Two additions.
--
-- 1) Automatic file transfer, gated per company and per device exactly like Android TV control.
--    When a device is allowed, it accepts incoming files without the person at the machine having
--    to approve each transfer. The grant rides in the license token as a second flag ('f'), so the
--    program checks it offline. The owner turns it on per company and per device; a company admin
--    may toggle it for its own company's devices (never the company-level switch).
--
-- 2) A permanent "delete company" for the owner, so the list can be cleaned of dead companies.
--    It removes the company and everything under it (batches, codes, activations, admin rows) and
--    returns the admin login ids, which the admin-users Edge Function then deletes from GoTrue.

-- ---- 1) auto file transfer ----

alter table public.companies add column auto_file_transfer boolean not null default false;
alter table public.activations add column auto_file_transfer boolean not null default false;

-- Let the dashboard read the per-device flag (the activations grant lists columns explicitly).
grant select (auto_file_transfer) on public.activations to authenticated;

-- Show both feature flags next to each device. create-or-replace keeps the existing columns in
-- order; the two new flags are appended after the Android ones.
create or replace view public.code_overview with (security_invoker = true) as
  select k.id as code_id, k.code_hint, k.revoked_at as code_revoked_at, k.batch_id,
    b.expires_at, b.revoked_at as batch_revoked_at, c.id as company_id, c.name as company,
    a.id as activation_id, a.machine_name, a.last_ip, a.activated_at, a.last_seen_at,
    c.android_control as company_android, a.android_control as device_android,
    c.auto_file_transfer as company_autofiles, a.auto_file_transfer as device_autofiles
  from public.license_codes k
  join public.license_batches b on b.id = k.batch_id
  join public.companies c on c.id = b.company_id
  left join public.activations a on a.code_id = k.id and a.released_at is null;

-- Owner only: the company-level switch.
create function public.set_company_autofiles(p_company uuid, p_on boolean) returns void
language plpgsql security definer set search_path = '' as $$
begin
  perform public.require_admin();
  update public.companies set auto_file_transfer = p_on where id = p_company;
  if not found then
    raise exception 'unknown_company';
  end if;
  insert into public.audit_log (actor, action, subject, details)
    values (auth.uid(), 'set_company_autofiles', p_company::text, jsonb_build_object('on', p_on));
end;
$$;

-- Owner for any device, a company admin only for a device of its own company. The effective grant
-- still needs the company feature on, so this never overrides the owner's company-level switch.
create function public.set_device_autofiles(p_activation uuid, p_on boolean) returns void
language plpgsql security definer set search_path = '' as $$
declare v_company uuid; v_found boolean;
begin
  select b.company_id into v_company
    from public.activations a
    join public.license_codes k on k.id = a.code_id
    join public.license_batches b on b.id = k.batch_id
   where a.id = p_activation and a.released_at is null;
  v_found := found;
  -- Authorise before saying whether the device exists; coalesce turns a null company (a caller
  -- without aal2) into a plain refusal rather than a null comparison.
  if not (public.is_admin() or (v_found and coalesce(public.admin_company() = v_company, false))) then
    raise exception 'not_admin' using errcode = '42501';
  end if;
  if not v_found then
    raise exception 'unknown_activation';
  end if;
  update public.activations set auto_file_transfer = p_on where id = p_activation and released_at is null;
  insert into public.audit_log (actor, action, subject, details)
    values (auth.uid(), 'set_device_autofiles', p_activation::text, jsonb_build_object('on', p_on));
end;
$$;

revoke all on function public.set_company_autofiles(uuid, boolean), public.set_device_autofiles(uuid, boolean)
  from public, anon, authenticated;
grant execute on function public.set_company_autofiles(uuid, boolean), public.set_device_autofiles(uuid, boolean)
  to authenticated;

-- ---- the two program-facing functions now report both effective grants ----

create or replace function public.activate_code(p_code_hash text, p_device text, p_machine text, p_ip text, p_secret_hash text)
returns jsonb
language plpgsql security definer set search_path = '' as $$
declare
  v_code public.license_codes;
  v_batch public.license_batches;
  v_company text;
  v_company_android boolean;
  v_company_autofiles boolean;
  v_active public.activations;
  v_android boolean := false;
  v_autofiles boolean := false;
begin
  delete from public.activation_failures where at < now() - interval '1 day';
  if (select count(*) from public.activation_failures where ip = p_ip and at > now() - interval '10 minutes') >= 20 then
    return jsonb_build_object('ok', false, 'error', 'rate_limited');
  end if;

  select * into v_code from public.license_codes where code_hash = p_code_hash for update;
  if not found then
    insert into public.activation_failures (ip) values (p_ip);
    return jsonb_build_object('ok', false, 'error', 'invalid_code');
  end if;

  select * into v_batch from public.license_batches where id = v_code.batch_id;
  select name, android_control, auto_file_transfer into v_company, v_company_android, v_company_autofiles
    from public.companies where id = v_batch.company_id;
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
    update public.activations
      set secret_hash = p_secret_hash, machine_name = p_machine, last_ip = p_ip, last_seen_at = now()
      where id = v_active.id;
    v_android := v_active.android_control;
    v_autofiles := v_active.auto_file_transfer;
  else
    insert into public.activations (code_id, device_hash, machine_name, last_ip, secret_hash)
      values (v_code.id, p_device, p_machine, p_ip, p_secret_hash);
    insert into public.audit_log (action, subject, details)
      values ('activate', v_code.id::text, jsonb_build_object('company', v_company, 'machine', p_machine, 'ip', p_ip));
  end if;

  return jsonb_build_object('ok', true, 'code_id', v_code.id, 'company', v_company,
    'expires_at', v_batch.expires_at, 'server_time', now(),
    'android_control', coalesce(v_company_android, false) and coalesce(v_android, false),
    'auto_file_transfer', coalesce(v_company_autofiles, false) and coalesce(v_autofiles, false));
end;
$$;

create or replace function public.refresh_license(p_code uuid, p_device text, p_secret_hash text, p_machine text, p_ip text)
returns jsonb
language plpgsql security definer set search_path = '' as $$
declare
  v_active public.activations;
  v_code public.license_codes;
  v_batch public.license_batches;
  v_company text;
  v_company_android boolean;
  v_company_autofiles boolean;
begin
  select * into v_active from public.activations
    where code_id = p_code and device_hash = p_device and secret_hash = p_secret_hash and released_at is null;
  if not found then
    return jsonb_build_object('ok', false, 'error', 'released');
  end if;

  select * into v_code from public.license_codes where id = p_code;
  select * into v_batch from public.license_batches where id = v_code.batch_id;
  select name, android_control, auto_file_transfer into v_company, v_company_android, v_company_autofiles
    from public.companies where id = v_batch.company_id;
  update public.activations set last_seen_at = now(), last_ip = p_ip, machine_name = p_machine where id = v_active.id;

  if v_code.revoked_at is not null or v_batch.revoked_at is not null then
    return jsonb_build_object('ok', false, 'error', 'revoked');
  end if;
  return jsonb_build_object('ok', v_batch.expires_at > now(), 'error', case when v_batch.expires_at > now() then null else 'expired' end,
    'code_id', v_code.id, 'company', v_company, 'expires_at', v_batch.expires_at, 'server_time', now(),
    'android_control', coalesce(v_company_android, false) and coalesce(v_active.android_control, false),
    'auto_file_transfer', coalesce(v_company_autofiles, false) and coalesce(v_active.auto_file_transfer, false));
end;
$$;

-- ---- 2) permanent company delete (owner only) ----

-- Removes a company and everything under it, in FK order (the references are not ON DELETE CASCADE).
-- Returns the company name and the user ids of its admin logins, so the Edge Function can delete
-- those GoTrue users; the company_admins rows themselves go here (and would also cascade).
create function public.delete_company(p_company uuid) returns jsonb
language plpgsql security definer set search_path = '' as $$
declare v_name text; v_admins uuid[];
begin
  perform public.require_admin();
  select name into v_name from public.companies where id = p_company;
  if not found then
    raise exception 'unknown_company';
  end if;

  select coalesce(array_agg(user_id), '{}') into v_admins
    from public.company_admins where company_id = p_company;

  delete from public.activations a
    using public.license_codes k, public.license_batches b
    where a.code_id = k.id and k.batch_id = b.id and b.company_id = p_company;
  delete from public.license_codes k
    using public.license_batches b
    where k.batch_id = b.id and b.company_id = p_company;
  delete from public.license_batches where company_id = p_company;
  delete from public.company_admins where company_id = p_company;
  delete from public.companies where id = p_company;

  insert into public.audit_log (actor, action, subject, details)
    values (auth.uid(), 'delete_company', p_company::text,
      jsonb_build_object('company', v_name, 'admins', coalesce(array_length(v_admins, 1), 0)));
  return jsonb_build_object('company', v_name, 'admin_user_ids', to_jsonb(v_admins));
end;
$$;

revoke all on function public.delete_company(uuid) from public, anon, authenticated;
grant execute on function public.delete_company(uuid) to authenticated;
