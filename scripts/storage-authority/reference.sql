-- Independent reference database ONLY. Never run this against the business/backup database.
-- An operator supplies separately reviewed custody, authentication and recovery. Runtime never bootstraps epochs.
create schema storage_control;
revoke all on schema storage_control from public;
create sequence storage_control.revisions;
create table storage_control.epochs(
  domain uuid not null, epoch bigint not null check(epoch>0), layout integer not null check(layout=1),
  incarnation uuid not null, active boolean not null default false, session uuid null,
  primary key(domain,epoch));
create table storage_control.generations(
  domain uuid not null, epoch bigint not null, area integer not null check(area in(1,2)),
  generation uuid not null, manifest_hash varchar(64) not null check(manifest_hash ~ '^[0-9a-f]{64}$'),
  state text not null check(state in('Open','Sealed','Closing','Closed')),
  revision bigint not null default nextval('storage_control.revisions'),
  primary key(domain,area,generation),
  foreign key(domain,epoch) references storage_control.epochs(domain,epoch));
revoke all on all tables in schema storage_control from public;
revoke all on all sequences in schema storage_control from public;

create function storage_control.attach(p_domain uuid,p_epoch bigint,p_layout integer,p_incarnation uuid,p_session uuid)
returns boolean language plpgsql security definer set search_path=pg_catalog,pg_temp as $$
declare changed integer;
begin
  update storage_control.epochs e set session=p_session
    where e.domain=p_domain and e.epoch=p_epoch and e.layout=p_layout and e.incarnation=p_incarnation
      and e.active and e.session is null
      and not exists(select 1 from storage_control.epochs n where n.domain=e.domain and n.epoch>e.epoch);
  get diagnostics changed=row_count;
  return changed=1;
end $$;

create function storage_control.transition(p_domain uuid,p_epoch bigint,p_layout integer,p_incarnation uuid,
  p_session uuid,p_area integer,p_generation uuid,p_hash text,p_target text)
returns table(state text,revision bigint) language plpgsql security definer set search_path=pg_catalog,pg_temp as $$
declare e storage_control.epochs%rowtype; g storage_control.generations%rowtype;
begin
  select * into e from storage_control.epochs x where x.domain=p_domain and x.session=p_session
    and x.layout=p_layout and x.incarnation=p_incarnation and x.active
    and not exists(select 1 from storage_control.epochs n where n.domain=x.domain and n.epoch>x.epoch) for update;
  if not found or p_epoch>e.epoch or p_target not in('Read','Open','Sealed','Closing','Closed') then
    raise exception 'Unadmitted storage session' using errcode='23514';
  end if;
  if p_target in('Open','Sealed') and p_epoch<>e.epoch then
    raise exception 'Retired epoch cannot write' using errcode='23514';
  end if;
  select * into g from storage_control.generations x
    where x.domain=p_domain and x.area=p_area and x.generation=p_generation for update;
  if not found then
    if p_target not in('Open','Closing') then
      raise exception 'Unknown generation' using errcode='23514';
    end if;
    insert into storage_control.generations(domain,epoch,area,generation,manifest_hash,state)
      values(p_domain,p_epoch,p_area,p_generation,p_hash,p_target) returning * into g;
  elsif g.epoch<>p_epoch or g.manifest_hash<>p_hash then
    raise exception 'Generation identity mismatch' using errcode='23514';
  elsif p_target='Open' and g.state<>'Open' then
    raise exception 'Generation cannot be readmitted' using errcode='23514';
  elsif p_target='Sealed' and g.state not in('Open','Sealed') then
    raise exception 'Generation cannot be sealed' using errcode='23514';
  elsif p_target='Closed' and g.state not in('Closing','Closed') then
    raise exception 'Closure requires drained Closing' using errcode='23514';
  elsif p_target<>'Read' and g.state<>p_target and not (p_target='Closing' and g.state='Closed') then
    update storage_control.generations x set state=p_target,revision=case when p_target='Closed' then x.revision else nextval('storage_control.revisions') end
      where x.domain=p_domain and x.area=p_area and x.generation=p_generation returning * into g;
  end if;
  return query select g.state,g.revision;
end $$;
revoke all on function storage_control.attach(uuid,bigint,integer,uuid,uuid) from public;
revoke all on function storage_control.transition(uuid,bigint,integer,uuid,uuid,integer,uuid,text,text) from public;
do $$ begin
  if exists(select 1 from pg_roles where rolname='duora_app') then
    revoke all on all tables in schema storage_control from duora_app;
    revoke all on all sequences in schema storage_control from duora_app;
    revoke all on function storage_control.attach(uuid,bigint,integer,uuid,uuid),
      storage_control.transition(uuid,bigint,integer,uuid,uuid,integer,uuid,text,text) from duora_app;
  end if;
end $$;
-- Runtime role requires only USAGE on schema and EXECUTE on these two functions. It must not own these objects.
-- Epoch insertion/retirement is a custodian operation after proven old-family/I/O withdrawal, never a runtime retry.
