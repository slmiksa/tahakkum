-- Android TV control over IP, gated per company and per device. A device may control TV screens
-- only when its company has the feature on and that device is allowed. The grant rides in the
-- license token as a flags field, so the program checks it offline like the rest of the license.

alter table public.companies add column android_control boolean not null default false;
alter table public.activations add column android_control boolean not null default false;

-- Let the dashboard read the per-device flag (the activations grant lists columns explicitly).
grant select (android_control) on public.activations to authenticated;

-- Show the flag next to each device in the dashboard's code view. The original columns keep their
-- order (create-or-replace view requires that); the two new flags are appended at the end.
create or replace view public.code_overview with (security_invoker = true) as
  select k.id as code_id, k.code_hint, k.revoked_at as code_revoked_at, k.batch_id,
    b.expires_at, b.revoked_at as batch_revoked_at, c.id as company_id, c.name as company,
    a.id as activation_id, a.machine_name, a.last_ip, a.activated_at, a.last_seen_at,
    c.android_control as company_android, a.android_control as device_android
  from public.license_codes k
  join public.license_batches b on b.id = k.batch_id
  join public.companies c on c.id = b.company_id
  left join public.activations a on a.code_id = k.id and a.released_at is null;

-- ---- admin toggles ----

create function public.set_company_android(p_company uuid, p_on boolean) returns void
language plpgsql security definer set search_path = '' as $$
begin
  perform public.require_admin();
  update public.companies set android_control = p_on where id = p_company;
  if not found then
    raise exception 'unknown_company';
  end if;
  insert into public.audit_log (actor, action, subject, details)
    values (auth.uid(), 'set_company_android', p_company::text, jsonb_build_object('on', p_on));
end;
$$;

create function public.set_device_android(p_activation uuid, p_on boolean) returns void
language plpgsql security definer set search_path = '' as $$
begin
  perform public.require_admin();
  update public.activations set android_control = p_on where id = p_activation and released_at is null;
  if not found then
    raise exception 'unknown_activation';
  end if;
  insert into public.audit_log (actor, action, subject, details)
    values (auth.uid(), 'set_device_android', p_activation::text, jsonb_build_object('on', p_on));
end;
$$;

revoke all on function public.set_company_android(uuid, boolean), public.set_device_android(uuid, boolean)
  from public, anon, authenticated;
grant execute on function public.set_company_android(uuid, boolean), public.set_device_android(uuid, boolean)
  to authenticated;

-- ---- the two program-facing functions now also report the effective Android grant ----

create or replace function public.activate_code(p_code_hash text, p_device text, p_machine text, p_ip text, p_secret_hash text)
returns jsonb
language plpgsql security definer set search_path = '' as $$
declare
  v_code public.license_codes;
  v_batch public.license_batches;
  v_company text;
  v_company_android boolean;
  v_active public.activations;
  v_android boolean := false;
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
  select name, android_control into v_company, v_company_android from public.companies where id = v_batch.company_id;
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
  else
    insert into public.activations (code_id, device_hash, machine_name, last_ip, secret_hash)
      values (v_code.id, p_device, p_machine, p_ip, p_secret_hash);
    insert into public.audit_log (action, subject, details)
      values ('activate', v_code.id::text, jsonb_build_object('company', v_company, 'machine', p_machine, 'ip', p_ip));
  end if;

  return jsonb_build_object('ok', true, 'code_id', v_code.id, 'company', v_company,
    'expires_at', v_batch.expires_at, 'server_time', now(),
    'android_control', coalesce(v_company_android, false) and coalesce(v_android, false));
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
begin
  select * into v_active from public.activations
    where code_id = p_code and device_hash = p_device and secret_hash = p_secret_hash and released_at is null;
  if not found then
    return jsonb_build_object('ok', false, 'error', 'released');
  end if;

  select * into v_code from public.license_codes where id = p_code;
  select * into v_batch from public.license_batches where id = v_code.batch_id;
  select name, android_control into v_company, v_company_android from public.companies where id = v_batch.company_id;
  update public.activations set last_seen_at = now(), last_ip = p_ip, machine_name = p_machine where id = v_active.id;

  if v_code.revoked_at is not null or v_batch.revoked_at is not null then
    return jsonb_build_object('ok', false, 'error', 'revoked');
  end if;
  return jsonb_build_object('ok', v_batch.expires_at > now(), 'error', case when v_batch.expires_at > now() then null else 'expired' end,
    'code_id', v_code.id, 'company', v_company, 'expires_at', v_batch.expires_at, 'server_time', now(),
    'android_control', coalesce(v_company_android, false) and coalesce(v_active.android_control, false));
end;
$$;
