-- Issue #93: confere as tentativas de selfie-restore-seed.sql, em valores escritos à mão: se o seed mudar, esta lista
-- muda junto, de propósito. Termina com erro ao achar diferença; rode com ON_ERROR_STOP. Não altera nada.
do $check$
declare
  differences text;
  healthy_constraints integer;
  healthy_indexes integer;
begin
  with expected(id, user_id, email, state, policy_version, provider_reference, feedback_code, requested_at,
                expires_at, completed_at, version) as (
    values
      ('c2000000-0000-4000-8000-00000000b001'::uuid, 'c2000000-0000-4000-8000-00000000a001'::uuid,
       'selfie-restore-1@c2.invalid'::text, 'RecaptureRequired'::text, 'c2-restore-fixture'::text,
       'c2-synthetic-session-b001'::text, 'c2-synthetic-feedback'::text, '2099-01-01 10:00:00.000001+00'::timestamptz,
       '2099-01-01 10:10:00.000001+00'::timestamptz, '2099-01-01 10:05:00.000001+00'::timestamptz, 2::bigint),
      ('c2000000-0000-4000-8000-00000000b002', 'c2000000-0000-4000-8000-00000000a001',
       'selfie-restore-1@c2.invalid', 'Passed', 'c2-restore-fixture',
       'c2-synthetic-session-b002', null, '2099-01-02 10:00:00.000002+00',
       '2099-01-02 10:10:00.000002+00', '2099-01-02 10:05:00.000002+00', 3),
      ('c2000000-0000-4000-8000-00000000b003', 'c2000000-0000-4000-8000-00000000a002',
       'selfie-restore-2@c2.invalid', 'Passed', 'c2-restore-fixture',
       null, null, '2099-01-03 10:00:00.000003+00',
       '2099-01-03 10:10:00.000003+00', '2099-01-03 10:05:00.000003+00', 3),
      ('c2000000-0000-4000-8000-00000000b004', 'c2000000-0000-4000-8000-00000000a003',
       'selfie-restore-3@c2.invalid', 'Inconclusive', 'c2-restore-fixture',
       'c2-synthetic-session-b004', 'c2-synthetic-feedback', '2099-01-04 10:00:00.000004+00',
       '2099-01-04 10:10:00.000004+00', '2099-01-04 10:05:00.000004+00', 2),
      ('c2000000-0000-4000-8000-00000000b005', 'c2000000-0000-4000-8000-00000000a004',
       'selfie-restore-4@c2.invalid', 'Expired', 'c2-restore-fixture',
       null, null, '2099-01-05 10:00:00.000005+00',
       '2099-01-05 10:10:00.000005+00', null, 1),
      ('c2000000-0000-4000-8000-00000000b006', 'c2000000-0000-4000-8000-00000000a005',
       'selfie-restore-5@c2.invalid', 'Pending', 'c2-restore-fixture',
       null, null, '2099-01-06 10:00:00.000006+00',
       '2099-01-06 10:10:00.000006+00', null, 0),
      ('c2000000-0000-4000-8000-00000000b007', 'c2000000-0000-4000-8000-00000000a006',
       'selfie-restore-6@c2.invalid', 'Capturing', 'c2-restore-fixture',
       'c2-synthetic-session-b007', null, '2099-01-07 10:00:00.000007+00',
       '2099-01-07 10:10:00.000007+00', null, 1),
      ('c2000000-0000-4000-8000-00000000b008', 'c2000000-0000-4000-8000-00000000a007',
       'selfie-restore-7@c2.invalid', 'Processing', 'c2-restore-fixture',
       'c2-synthetic-session-b008', null, '2099-01-08 10:00:00.000008+00',
       '2099-01-08 10:10:00.000008+00', null, 2)
  ),
  actual as (
    -- left join: tentativa órfã chega aqui com e-mail nulo e aparece como diferença.
    select v.id, v.user_id, u.email::text, v.state::text, v.policy_version::text, v.provider_reference::text,
           v.feedback_code::text, v.requested_at, v.expires_at, v.completed_at, v.version
    from identity.selfie_verifications v
    left join identity.users u on u.id = v.user_id
    where v.policy_version = 'c2-restore-fixture'
       or v.user_id in (select e.user_id from expected e)
  ),
  missing as (
    select * from expected except select * from actual
  ),
  unexpected as (
    select * from actual except select * from expected
  )
  select string_agg(difference.kind || ' ' || difference.id, ', ' order by difference.id, difference.kind)
  into differences
  from (
    select 'missing' as kind, m.id::text as id from missing m
    union all
    select 'unexpected', u.id::text from unexpected u
  ) as difference;

  if differences is not null then
    raise exception 'selfie restore fixture: rows differ: %', differences;
  end if;

  select count(*) into healthy_constraints
  from pg_constraint c
  where c.conrelid = 'identity.selfie_verifications'::regclass
    and c.convalidated
    and (c.conname in ('pk_selfie_verifications', 'ck_selfie_state', 'ck_selfie_version')
         or (c.conname = 'fk_selfie_verifications_users' and c.confrelid = 'identity.users'::regclass
             and c.confdeltype = 'c'));

  if healthy_constraints <> 4 then
    raise exception 'selfie restore fixture: constraints differ: % of 4 healthy', healthy_constraints;
  end if;

  select count(*) into healthy_indexes
  from pg_index i
  join pg_class c on c.oid = i.indexrelid
  where i.indrelid = 'identity.selfie_verifications'::regclass
    and i.indisvalid
    and i.indisready
    and c.relname in ('pk_selfie_verifications', 'ux_selfie_open_user', 'ux_selfie_provider_reference',
                      'ix_selfie_user_requested_at');

  if healthy_indexes <> 4 then
    raise exception 'selfie restore fixture: indexes differ: % of 4 healthy', healthy_indexes;
  end if;
end
$check$;
