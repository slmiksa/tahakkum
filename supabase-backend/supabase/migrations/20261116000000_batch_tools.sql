-- Two tools for the batches page:
--   1) add more codes to an existing batch (they inherit the batch's expiry), so a company can be
--      given extra codes without issuing a separate batch;
--   2) permanently delete a batch and everything under it, to keep the list clear of test batches.

-- Appends codes to a batch under the administrator's identity. Expiry counts from issuing, so the
-- new codes end together with the rest of the batch. Returns what the Edge Function needs to build
-- the download file (only the added codes are returned to the caller, once).
create function public.add_batch_codes(p_batch uuid, p_hashes text[], p_hints text[], p_encrypted text[])
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
  if v_count = 0 then
    raise exception 'no_codes';
  end if;
  select * into v_batch from public.license_batches where id = p_batch for update;
  if not found then
    raise exception 'unknown_batch';
  end if;
  if v_batch.revoked_at is not null then
    raise exception 'batch_revoked';
  end if;
  select name into v_company from public.companies where id = v_batch.company_id;

  insert into public.license_codes (batch_id, code_hash, code_hint, code_encrypted)
    select p_batch, h, i, e from unnest(p_hashes, p_hints, p_encrypted) as t (h, i, e);
  update public.license_batches set quantity = quantity + v_count where id = p_batch;

  insert into public.audit_log (actor, action, subject, details)
    values (auth.uid(), 'add_batch_codes', p_batch::text, jsonb_build_object('company', v_company, 'added', v_count));

  return jsonb_build_object('batch_id', v_batch.id, 'company', v_company, 'quantity', v_count,
    'months', v_batch.months, 'issued_at', v_batch.issued_at, 'expires_at', v_batch.expires_at);
end;
$$;

-- Permanently removes a batch with its codes and activations. The references are not ON DELETE
-- CASCADE, so delete in order. Owner only, audited.
create function public.delete_batch(p_batch uuid) returns jsonb
language plpgsql security definer set search_path = '' as $$
declare v_company text;
begin
  perform public.require_admin();
  select c.name into v_company
    from public.license_batches b join public.companies c on c.id = b.company_id
   where b.id = p_batch;
  if not found then
    raise exception 'unknown_batch';
  end if;

  delete from public.activations a using public.license_codes k
    where a.code_id = k.id and k.batch_id = p_batch;
  delete from public.license_codes where batch_id = p_batch;
  delete from public.license_batches where id = p_batch;

  insert into public.audit_log (actor, action, subject, details)
    values (auth.uid(), 'delete_batch', p_batch::text, jsonb_build_object('company', v_company));
  return jsonb_build_object('company', v_company);
end;
$$;

revoke all on function public.add_batch_codes(uuid, text[], text[], text[]), public.delete_batch(uuid)
  from public, anon, authenticated;
grant execute on function public.add_batch_codes(uuid, text[], text[], text[]), public.delete_batch(uuid)
  to authenticated;
