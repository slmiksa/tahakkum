-- Per-company administrators. The owner creates a login for a customer company; that login sees
-- only its own company's devices, may grant or withdraw Android TV control per device, and may
-- release a device so its code moves to a new machine. It never sees another company, issues no
-- codes, and reaches none of the owner-only pages. The same two-step (MFA) bar as the owner applies:
-- without aal2 a company admin sees nothing, exactly like the owner.

create table public.company_admins (
  user_id uuid primary key references auth.users (id) on delete cascade,
  company_id uuid not null references public.companies (id) on delete cascade,
  username text not null,
  created_at timestamptz not null default now(),
  created_by uuid references auth.users (id)
);
create index company_admins_company on public.company_admins (company_id);

alter table public.company_admins enable row level security;
revoke all on public.company_admins from anon, authenticated;
grant select on public.company_admins to authenticated;

-- The company this caller administers, or null. Requires aal2, so a half-finished sign-in grants nothing.
create function public.admin_company() returns uuid
language sql stable security definer set search_path = '' as $$
  select company_id from public.company_admins
   where user_id = auth.uid()
     and coalesce(auth.jwt() ->> 'aal' = 'aal2', false);
$$;

-- What the dashboard needs to pick a view: the owner sees everything, a company admin sees one company.
create function public.my_role() returns jsonb
language sql stable security definer set search_path = '' as $$
  select jsonb_build_object(
    'owner', public.is_admin(),
    'company_id', public.admin_company(),
    'company', (select c.name from public.companies c where c.id = public.admin_company()));
$$;

-- ---- who may read what ----

-- The owner keeps the admin_read policies from the first migration; these add a company admin's own
-- rows. Postgres combines permissive policies with OR, so the owner is unaffected.
create policy company_admin_read on public.companies for select to authenticated
  using (id = public.admin_company());
create policy company_admin_read on public.license_batches for select to authenticated
  using (company_id = public.admin_company());
create policy company_admin_read on public.license_codes for select to authenticated
  using (exists (select 1 from public.license_batches b where b.id = batch_id and b.company_id = public.admin_company()));
create policy company_admin_read on public.activations for select to authenticated
  using (exists (select 1 from public.license_codes k
                 join public.license_batches b on b.id = k.batch_id
                 where k.id = code_id and b.company_id = public.admin_company()));

-- The owner reads every company admin; a company admin reads only their own row (to learn their company).
create policy owner_read on public.company_admins for select to authenticated using (public.is_admin());
create policy self_read on public.company_admins for select to authenticated using (user_id = auth.uid());

-- A flat list for the owner's management page. security_invoker, so a company admin sees only itself.
create view public.company_admin_overview with (security_invoker = true) as
  select ca.user_id, ca.username, ca.company_id, c.name as company, ca.created_at
  from public.company_admins ca
  join public.companies c on c.id = ca.company_id;
revoke all on public.company_admin_overview from anon, authenticated;
grant select on public.company_admin_overview to authenticated;

-- ---- the two device actions a company admin shares with the owner ----

-- Grant or withdraw Android TV control for one device. The owner may do it for any device; a company
-- admin only for a device of its own company. The effective grant still needs the company feature on
-- (activate/refresh AND the two flags), so this never overrides the owner's company-level switch.
create or replace function public.set_device_android(p_activation uuid, p_on boolean) returns void
language plpgsql security definer set search_path = '' as $$
declare v_company uuid; v_found boolean;
begin
  select b.company_id into v_company
    from public.activations a
    join public.license_codes k on k.id = a.code_id
    join public.license_batches b on b.id = k.batch_id
   where a.id = p_activation and a.released_at is null;
  v_found := found;
  -- Authorise before saying whether the device exists, so a stranger cannot probe for ids. coalesce
  -- turns a null company (a caller without aal2) into a plain refusal rather than a null comparison.
  if not (public.is_admin() or (v_found and coalesce(public.admin_company() = v_company, false))) then
    raise exception 'not_admin' using errcode = '42501';
  end if;
  if not v_found then
    raise exception 'unknown_activation';
  end if;
  update public.activations set android_control = p_on where id = p_activation and released_at is null;
  insert into public.audit_log (actor, action, subject, details)
    values (auth.uid(), 'set_device_android', p_activation::text, jsonb_build_object('on', p_on));
end;
$$;

-- Free a code for a replacement machine. Owner for any device, a company admin for its own company's.
create or replace function public.release_activation(p_activation uuid) returns void
language plpgsql security definer set search_path = '' as $$
declare v_company uuid; v_found boolean;
begin
  select b.company_id into v_company
    from public.activations a
    join public.license_codes k on k.id = a.code_id
    join public.license_batches b on b.id = k.batch_id
   where a.id = p_activation and a.released_at is null;
  v_found := found;
  if not (public.is_admin() or (v_found and coalesce(public.admin_company() = v_company, false))) then
    raise exception 'not_admin' using errcode = '42501';
  end if;
  update public.activations set released_at = now() where id = p_activation and released_at is null;
  insert into public.audit_log (actor, action, subject) values (auth.uid(), 'release_activation', p_activation::text);
end;
$$;

-- ---- owner-only: manage the company-admin rows (the login itself is created by the Edge Function) ----

create function public.create_company_admin(p_user uuid, p_company uuid, p_username text) returns void
language plpgsql security definer set search_path = '' as $$
begin
  perform public.require_admin();
  if not exists (select 1 from public.companies where id = p_company) then
    raise exception 'unknown_company';
  end if;
  insert into public.company_admins (user_id, company_id, username, created_by)
    values (p_user, p_company, p_username, auth.uid());
  insert into public.audit_log (actor, action, subject, details)
    values (auth.uid(), 'create_company_admin', p_user::text,
      jsonb_build_object('company', (select name from public.companies where id = p_company), 'username', p_username));
end;
$$;

create function public.delete_company_admin(p_user uuid) returns void
language plpgsql security definer set search_path = '' as $$
begin
  perform public.require_admin();
  delete from public.company_admins where user_id = p_user;
  insert into public.audit_log (actor, action, subject) values (auth.uid(), 'delete_company_admin', p_user::text);
end;
$$;

revoke all on function public.admin_company(), public.my_role(),
  public.create_company_admin(uuid, uuid, text), public.delete_company_admin(uuid)
  from public, anon, authenticated;
grant execute on function public.admin_company(), public.my_role(),
  public.create_company_admin(uuid, uuid, text), public.delete_company_admin(uuid)
  to authenticated;
