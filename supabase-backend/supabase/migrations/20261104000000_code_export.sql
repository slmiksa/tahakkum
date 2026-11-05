-- Lets an administrator export a batch again. Each code is stored encrypted, with a key held
-- only by the Edge Functions, so a leak of the database alone still reveals no usable code.
-- Batches issued before this have no stored code: they export their status and hint only.

alter table public.license_codes add column code_encrypted text;
-- Deliberately not granted to the dashboard's role: its column grants list what it may read.

drop function public.issue_batch(uuid, int, text, text[], text[]);

create function public.issue_batch(p_company uuid, p_months int, p_note text, p_hashes text[], p_hints text[], p_encrypted text[])
returns jsonb
language plpgsql security definer set search_path = '' as $$
declare
  v_batch public.license_batches;
  v_company text;
  v_count int := coalesce(array_length(p_hashes, 1), 0);
begin
  perform public.require_admin();
  if v_count <> coalesce(array_length(p_hints, 1), 0) or v_count <> coalesce(array_length(p_encrypted, 1), 0) then
    raise exception 'array_length_mismatch';
  end if;
  select name into v_company from public.companies where id = p_company;
  if not found then
    raise exception 'unknown_company';
  end if;

  insert into public.license_batches (company_id, quantity, months, note, expires_at, created_by)
    values (p_company, v_count, p_months, nullif(trim(p_note), ''), now() + make_interval(months => p_months), auth.uid())
    returning * into v_batch;
  insert into public.license_codes (batch_id, code_hash, code_hint, code_encrypted)
    select v_batch.id, h, i, e from unnest(p_hashes, p_hints, p_encrypted) as t (h, i, e);
  insert into public.audit_log (actor, action, subject, details)
    values (auth.uid(), 'issue_batch', v_batch.id::text, jsonb_build_object('company', v_company, 'quantity', v_count, 'months', p_months));

  return jsonb_build_object('batch_id', v_batch.id, 'company', v_company, 'quantity', v_count,
    'months', p_months, 'issued_at', v_batch.issued_at, 'expires_at', v_batch.expires_at);
end;
$$;

-- Every export is recorded, because it hands out usable codes.
create function public.export_batch(p_batch uuid) returns jsonb
language plpgsql security definer set search_path = '' as $$
declare
  v_batch public.license_batches;
  v_company text;
  v_codes jsonb;
begin
  perform public.require_admin();
  select * into v_batch from public.license_batches where id = p_batch;
  if not found then
    raise exception 'unknown_batch';
  end if;
  select name into v_company from public.companies where id = v_batch.company_id;

  select coalesce(jsonb_agg(jsonb_build_object(
      'encrypted', k.code_encrypted,
      'hint', k.code_hint,
      'revoked', k.revoked_at is not null,
      'machine', a.machine_name,
      'last_ip', a.last_ip,
      'activated_at', a.activated_at,
      'last_seen_at', a.last_seen_at
    ) order by a.activated_at nulls first, k.code_hint), '[]'::jsonb)
    into v_codes
    from public.license_codes k
    left join public.activations a on a.code_id = k.id and a.released_at is null
    where k.batch_id = p_batch;

  insert into public.audit_log (actor, action, subject, details)
    values (auth.uid(), 'export_batch', p_batch::text, jsonb_build_object('company', v_company, 'quantity', v_batch.quantity));

  return jsonb_build_object('batch_id', v_batch.id, 'company', v_company, 'quantity', v_batch.quantity,
    'months', v_batch.months, 'issued_at', v_batch.issued_at, 'expires_at', v_batch.expires_at,
    'batch_revoked', v_batch.revoked_at is not null, 'codes', v_codes);
end;
$$;

revoke all on function public.issue_batch(uuid, int, text, text[], text[], text[]), public.export_batch(uuid)
  from public, anon, authenticated;
grant execute on function public.issue_batch(uuid, int, text, text[], text[], text[]), public.export_batch(uuid)
  to authenticated;
