-- Issue #69: fotos sintéticas do projeto sombra, conferidas por media-restore-check.sql no ensaio de restore.
-- Nenhum arquivo existe: o banco só guarda estado, versões, hashes e chaves de objeto, aqui todos inventados. Três
-- contas sem senha, fora de accounts.json: a carga nunca as usa.
--
-- Cobre o que o restore do banco precisa devolver intacto: fotos aprovadas (com as três variantes) e exclusões
-- (upload e asset Deleted, tombstone e recibo com a mesma operation_key). Nenhuma fila é semeada (original_cleanup,
-- photo_deletion_cleanup, review_*): o worker não tem obrigação a cumprir sobre estas linhas. As datas ficam em 2099
-- pelo mesmo motivo da selfie: o expurgo de tombstones e recibos (1 ano) não pode apagá-las antes do ensaio.
begin;

insert into identity.users(id, status, created_at, user_name, normalized_user_name, email, normalized_email,
                           email_confirmed, security_stamp, concurrency_stamp, phone_number_confirmed,
                           two_factor_enabled, lockout_enabled, access_failed_count)
select account.id, 'Active', '2026-11-05 12:00:00+00', account.email, upper(account.email), account.email,
       upper(account.email), true, account.id::text, account.id::text, false, false, true, 0
from (values
  ('c2000000-0000-4000-8000-00000000c001'::uuid, 'media-restore-1@c2.invalid'),
  ('c2000000-0000-4000-8000-00000000c002'::uuid, 'media-restore-2@c2.invalid'),
  ('c2000000-0000-4000-8000-00000000c003'::uuid, 'media-restore-3@c2.invalid')
) as account(id, email);

-- d001-d003 aprovadas; d004 aprovada e excluída; d005 rejeitada e excluída (o código de rejeição fica).
insert into media.uploads(id, owner_id, state, client_key, quarantine_key, declared_content_type, size_bytes,
                          content_sha256, created_at, abandon_after, rejection_code, version)
values
  ('c2000000-0000-4000-8000-00000000d001', 'c2000000-0000-4000-8000-00000000c001', 'Approved', 'c2-media-d001',
   'quarantine/c200000000004000800000000000d001', 'image/jpeg', 210001, decode(repeat('d1', 32), 'hex'),
   '2099-02-01 10:00:00.000001+00', '2099-02-02 10:00:00.000001+00', null, 3),
  ('c2000000-0000-4000-8000-00000000d002', 'c2000000-0000-4000-8000-00000000c001', 'Approved', 'c2-media-d002',
   'quarantine/c200000000004000800000000000d002', 'image/png', 320002, decode(repeat('d2', 32), 'hex'),
   '2099-02-02 10:00:00.000002+00', '2099-02-03 10:00:00.000002+00', null, 3),
  ('c2000000-0000-4000-8000-00000000d003', 'c2000000-0000-4000-8000-00000000c002', 'Approved', 'c2-media-d003',
   'quarantine/c200000000004000800000000000d003', 'image/webp', 430003, decode(repeat('d3', 32), 'hex'),
   '2099-02-03 10:00:00.000003+00', '2099-02-04 10:00:00.000003+00', null, 3),
  ('c2000000-0000-4000-8000-00000000d004', 'c2000000-0000-4000-8000-00000000c002', 'Deleted', 'c2-media-d004',
   'quarantine/c200000000004000800000000000d004', 'image/jpeg', 540004, decode(repeat('d4', 32), 'hex'),
   '2099-02-04 10:00:00.000004+00', '2099-02-05 10:00:00.000004+00', null, 4),
  ('c2000000-0000-4000-8000-00000000d005', 'c2000000-0000-4000-8000-00000000c003', 'Deleted', 'c2-media-d005',
   'quarantine/c200000000004000800000000000d005', 'image/png', 650005, decode(repeat('d5', 32), 'hex'),
   '2099-02-05 10:00:00.000005+00', '2099-02-06 10:00:00.000005+00', 'CORRUPT_IMAGE', 4);

insert into media.media_assets(id, upload_id, owner_id, state, detected_type, width, height, sha256, original_key,
                               approved_at, deleted_at, version)
values
  ('c2000000-0000-4000-8000-00000000e001', 'c2000000-0000-4000-8000-00000000d001',
   'c2000000-0000-4000-8000-00000000c001', 'Approved', 'image/jpeg', 1200, 1600, decode(repeat('e1', 32), 'hex'),
   null, '2099-02-01 10:01:00.000001+00', null, 1),
  ('c2000000-0000-4000-8000-00000000e002', 'c2000000-0000-4000-8000-00000000d002',
   'c2000000-0000-4000-8000-00000000c001', 'Approved', 'image/png', 1080, 1080, decode(repeat('e2', 32), 'hex'),
   null, '2099-02-02 10:01:00.000002+00', null, 1),
  ('c2000000-0000-4000-8000-00000000e003', 'c2000000-0000-4000-8000-00000000d003',
   'c2000000-0000-4000-8000-00000000c002', 'Approved', 'image/webp', 1600, 1200, decode(repeat('e3', 32), 'hex'),
   null, '2099-02-03 10:01:00.000003+00', null, 1),
  ('c2000000-0000-4000-8000-00000000e004', 'c2000000-0000-4000-8000-00000000d004',
   'c2000000-0000-4000-8000-00000000c002', 'Deleted', 'image/jpeg', 1200, 1600, decode(repeat('e4', 32), 'hex'),
   null, '2099-02-04 10:01:00.000004+00', '2099-03-04 10:00:00.000004+00', 2);

insert into media.media_variants(id, media_asset_id, kind, object_key, width, height, size_bytes)
select ('c2000000-0000-4000-8000-00000000f' || variant.suffix || '0' || asset.n)::uuid,
       ('c2000000-0000-4000-8000-00000000e00' || asset.n)::uuid, variant.kind,
       'derivatives/c200000000004000800000000000e00' || asset.n || '/' || variant.kind || '.webp',
       variant.edge, variant.edge, variant.edge * 10 + asset.n::integer
from (values ('1'), ('2'), ('3')) as asset(n)
cross join (values ('1', 'thumb', 160), ('2', 'card', 640), ('3', 'full', 1080)) as variant(suffix, kind, edge);

insert into media.photo_deletion_commands(operation_key, owner_id, key_id, photo_id, intent_sha256, snapshot_sha256,
                                          expected_version, result_version, created_at)
values
  (repeat('c2', 30) || '0d04', 'c2000000-0000-4000-8000-00000000c002', 'c2-media-delete-d004',
   'c2000000-0000-4000-8000-00000000d004', decode(repeat('a4', 32), 'hex'), decode(repeat('b4', 32), 'hex'), 3, 4,
   '2099-03-04 10:00:00.000004+00'),
  (repeat('c2', 30) || '0d05', 'c2000000-0000-4000-8000-00000000c003', 'c2-media-delete-d005',
   'c2000000-0000-4000-8000-00000000d005', decode(repeat('a5', 32), 'hex'), decode(repeat('b5', 32), 'hex'), 3, 4,
   '2099-03-05 10:00:00.000005+00');

insert into media.photo_tombstones(photo_id, owner_id, operation_key, intent_sha256, deleted_version, deleted_at)
values
  ('c2000000-0000-4000-8000-00000000d004', 'c2000000-0000-4000-8000-00000000c002', repeat('c2', 30) || '0d04',
   decode(repeat('a4', 32), 'hex'), 4, '2099-03-04 10:00:00.000004+00'),
  ('c2000000-0000-4000-8000-00000000d005', 'c2000000-0000-4000-8000-00000000c003', repeat('c2', 30) || '0d05',
   decode(repeat('a5', 32), 'hex'), 4, '2099-03-05 10:00:00.000005+00');

commit;
