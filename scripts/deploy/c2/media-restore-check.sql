-- Issue #69: confere as fotos de media-restore-seed.sql, em valores escritos à mão: se o seed mudar, esta lista
-- muda junto, de propósito. Termina com erro ao achar diferença; rode com ON_ERROR_STOP. Não altera nada.
do $check$
declare
  differences text;
  resurrected text;
  healthy_constraints integer;
  healthy_indexes integer;
begin
  -- Uma linha por foto: upload, asset, tombstone e recibo juntos, para que perder qualquer um apareça como diferença.
  with expected(photo_id, email, upload_state, upload_version, rejection_code, content_sha256, asset_state,
                asset_version, approved_at, asset_deleted_at, operation_key, deleted_version, deleted_at,
                expected_version, result_version) as (
    values
      ('c2000000-0000-4000-8000-00000000d001'::uuid, 'media-restore-1@c2.invalid'::text, 'Approved'::text, 3::bigint,
       null::text, repeat('d1', 32), 'Approved'::text, 1::bigint, '2099-02-01 10:01:00.000001+00'::timestamptz,
       null::timestamptz, null::text, null::bigint, null::timestamptz, null::bigint, null::bigint),
      ('c2000000-0000-4000-8000-00000000d002', 'media-restore-1@c2.invalid', 'Approved', 3,
       null, repeat('d2', 32), 'Approved', 1, '2099-02-02 10:01:00.000002+00',
       null, null, null, null, null, null),
      ('c2000000-0000-4000-8000-00000000d003', 'media-restore-2@c2.invalid', 'Approved', 3,
       null, repeat('d3', 32), 'Approved', 1, '2099-02-03 10:01:00.000003+00',
       null, null, null, null, null, null),
      ('c2000000-0000-4000-8000-00000000d004', 'media-restore-2@c2.invalid', 'Deleted', 4,
       null, repeat('d4', 32), 'Deleted', 2, '2099-02-04 10:01:00.000004+00',
       '2099-03-04 10:00:00.000004+00', repeat('c2', 30) || '0d04', 4, '2099-03-04 10:00:00.000004+00', 3, 4),
      ('c2000000-0000-4000-8000-00000000d005', 'media-restore-3@c2.invalid', 'Deleted', 4,
       'CORRUPT_IMAGE', repeat('d5', 32), null, null, null,
       null, repeat('c2', 30) || '0d05', 4, '2099-03-05 10:00:00.000005+00', 3, 4)
  ),
  actual as (
    -- left joins: upload sem dono, asset, tombstone ou recibo chega aqui com nulos e aparece como diferença.
    select u.id, owner.email::text, u.state::text, u.version, u.rejection_code::text,
           encode(u.content_sha256, 'hex'), a.state::text, a.version, a.approved_at, a.deleted_at,
           t.operation_key::text, t.deleted_version, t.deleted_at, r.expected_version, r.result_version
    from media.uploads u
    left join identity.users owner on owner.id = u.owner_id
    left join media.media_assets a on a.upload_id = u.id
    left join media.photo_tombstones t on t.photo_id = u.id and t.owner_id = u.owner_id
    left join media.photo_deletion_commands r on r.operation_key = t.operation_key and r.photo_id = u.id
    where u.client_key like 'c2-media-%'
       or u.owner_id in (select o.id from identity.users o where o.email like 'media-restore-%@c2.invalid')
  ),
  variant_expected(id, media_asset_id, kind, object_key, edge, size_bytes) as (
    values
      ('c2000000-0000-4000-8000-00000000f101'::uuid, 'c2000000-0000-4000-8000-00000000e001'::uuid, 'thumb'::text,
       'derivatives/c200000000004000800000000000e001/thumb.webp'::text, 160, 1601::bigint),
      ('c2000000-0000-4000-8000-00000000f201', 'c2000000-0000-4000-8000-00000000e001', 'card',
       'derivatives/c200000000004000800000000000e001/card.webp', 640, 6401),
      ('c2000000-0000-4000-8000-00000000f301', 'c2000000-0000-4000-8000-00000000e001', 'full',
       'derivatives/c200000000004000800000000000e001/full.webp', 1080, 10801),
      ('c2000000-0000-4000-8000-00000000f102', 'c2000000-0000-4000-8000-00000000e002', 'thumb',
       'derivatives/c200000000004000800000000000e002/thumb.webp', 160, 1602),
      ('c2000000-0000-4000-8000-00000000f202', 'c2000000-0000-4000-8000-00000000e002', 'card',
       'derivatives/c200000000004000800000000000e002/card.webp', 640, 6402),
      ('c2000000-0000-4000-8000-00000000f302', 'c2000000-0000-4000-8000-00000000e002', 'full',
       'derivatives/c200000000004000800000000000e002/full.webp', 1080, 10802),
      ('c2000000-0000-4000-8000-00000000f103', 'c2000000-0000-4000-8000-00000000e003', 'thumb',
       'derivatives/c200000000004000800000000000e003/thumb.webp', 160, 1603),
      ('c2000000-0000-4000-8000-00000000f203', 'c2000000-0000-4000-8000-00000000e003', 'card',
       'derivatives/c200000000004000800000000000e003/card.webp', 640, 6403),
      ('c2000000-0000-4000-8000-00000000f303', 'c2000000-0000-4000-8000-00000000e003', 'full',
       'derivatives/c200000000004000800000000000e003/full.webp', 1080, 10803)
  ),
  variant_actual as (
    select v.id, v.media_asset_id, v.kind::text, v.object_key::text, v.width, v.size_bytes
    from media.media_variants v
    join media.media_assets a on a.id = v.media_asset_id
    join media.uploads u on u.id = a.upload_id
    where u.client_key like 'c2-media-%' and v.width = v.height
    union all
    -- Variante não quadrada não existe no seed: entra com id próprio para aparecer como diferença.
    select v.id, v.media_asset_id, v.kind::text, v.object_key::text, -1, v.size_bytes
    from media.media_variants v
    join media.media_assets a on a.id = v.media_asset_id
    join media.uploads u on u.id = a.upload_id
    where u.client_key like 'c2-media-%' and v.width <> v.height
  )
  select string_agg(difference.kind || ' ' || difference.id, ', ' order by difference.id, difference.kind)
  into differences
  from (
    select 'missing photo' as kind, m.photo_id::text as id
    from (select * from expected except select * from actual) m
    union all
    select 'unexpected photo', x.id::text from (select * from actual except select * from expected) x
    union all
    select 'missing variant', m.id::text
    from (select * from variant_expected except select * from variant_actual) m
    union all
    select 'unexpected variant', x.id::text
    from (select * from variant_actual except select * from variant_expected) x
  ) as difference;

  if differences is not null then
    raise exception 'media restore fixture: rows differ: %', differences;
  end if;

  -- Invariante de qualquer restore, não só da fixture: foto excluída nunca volta como aprovada.
  select string_agg(t.photo_id::text, ', ' order by t.photo_id) into resurrected
  from media.photo_tombstones t
  join media.uploads u on u.id = t.photo_id
  left join media.media_assets a on a.upload_id = t.photo_id
  where u.state <> 'Deleted' or a.state = 'Approved' or (a.id is not null and a.deleted_at is null);

  if resurrected is not null then
    raise exception 'media restore fixture: tombstoned photos are visible again: %', resurrected;
  end if;

  select count(*) into healthy_constraints
  from pg_constraint c
  where c.conrelid = 'media.photo_tombstones'::regclass
    and c.convalidated
    and (c.conname in ('pk_photo_tombstones', 'ux_photo_tombstones_operation', 'ck_photo_tombstones_operation',
                       'ck_photo_tombstones_intent', 'ck_photo_tombstones_version', 'ck_photo_tombstones_deleted_at')
         or (c.conname = 'fk_photo_tombstones_upload' and c.confrelid = 'media.uploads'::regclass
             and c.confdeltype = 'r')
         or (c.conname = 'fk_photo_tombstones_owner' and c.confrelid = 'identity.users'::regclass
             and c.confdeltype = 'r'));

  if healthy_constraints <> 8 then
    raise exception 'media restore fixture: constraints differ: % of 8 healthy', healthy_constraints;
  end if;

  select count(*) into healthy_indexes
  from pg_index i
  join pg_class c on c.oid = i.indexrelid
  where i.indisvalid
    and i.indisready
    and ((i.indrelid = 'media.photo_tombstones'::regclass
          and c.relname in ('pk_photo_tombstones', 'ux_photo_tombstones_operation', 'ix_photo_tombstones_owner'))
         or (i.indrelid = 'media.media_assets'::regclass and c.relname = 'ux_media_assets_upload_id')
         or (i.indrelid = 'media.media_variants'::regclass
             and c.relname in ('ux_media_variants_media_asset_id_kind', 'ux_media_variants_object_key')));

  if healthy_indexes <> 6 then
    raise exception 'media restore fixture: indexes differ: % of 6 healthy', healthy_indexes;
  end if;
end
$check$;
