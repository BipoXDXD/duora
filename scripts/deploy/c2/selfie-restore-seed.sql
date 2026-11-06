-- Issue #93: tentativas de selfie sintéticas do projeto sombra, conferidas por selfie-restore-check.sql no ensaio de
-- restore. Nenhuma imagem existe: o banco só guarda estado, política, datas e referências, aqui todas inventadas.
-- Sete contas sem senha, fora de accounts.json: a carga nunca as usa.
--
-- As datas ficam em 2099 porque a limpeza do worker roda até com a selfie desligada: referência e feedback saem 24 h
-- depois da expiração, tentativa não aprovada sai em 30 dias (SelfieVerificationDispatcher). Datas passadas fariam a
-- linha sumir por retenção, e o ensaio acusaria perda no restore que não houve.
begin;

insert into identity.users(id, status, created_at, user_name, normalized_user_name, email, normalized_email,
                           email_confirmed, security_stamp, concurrency_stamp, phone_number_confirmed,
                           two_factor_enabled, lockout_enabled, access_failed_count)
select account.id, 'Active', '2026-11-05 12:00:00+00', account.email, upper(account.email), account.email,
       upper(account.email), true, account.id::text, account.id::text, false, false, true, 0
from (values
  ('c2000000-0000-4000-8000-00000000a001'::uuid, 'selfie-restore-1@c2.invalid'),
  ('c2000000-0000-4000-8000-00000000a002'::uuid, 'selfie-restore-2@c2.invalid'),
  ('c2000000-0000-4000-8000-00000000a003'::uuid, 'selfie-restore-3@c2.invalid'),
  ('c2000000-0000-4000-8000-00000000a004'::uuid, 'selfie-restore-4@c2.invalid'),
  ('c2000000-0000-4000-8000-00000000a005'::uuid, 'selfie-restore-5@c2.invalid'),
  ('c2000000-0000-4000-8000-00000000a006'::uuid, 'selfie-restore-6@c2.invalid'),
  ('c2000000-0000-4000-8000-00000000a007'::uuid, 'selfie-restore-7@c2.invalid')
) as account(id, email);

insert into identity.selfie_verifications(id, user_id, state, policy_version, provider_reference, feedback_code,
                                          requested_at, expires_at, completed_at, version)
values
  ('c2000000-0000-4000-8000-00000000b001', 'c2000000-0000-4000-8000-00000000a001', 'RecaptureRequired',
   'c2-restore-fixture', 'c2-synthetic-session-b001', 'c2-synthetic-feedback', '2099-01-01 10:00:00.000001+00',
   '2099-01-01 10:10:00.000001+00', '2099-01-01 10:05:00.000001+00', 2),
  ('c2000000-0000-4000-8000-00000000b002', 'c2000000-0000-4000-8000-00000000a001', 'Passed',
   'c2-restore-fixture', 'c2-synthetic-session-b002', null, '2099-01-02 10:00:00.000002+00',
   '2099-01-02 10:10:00.000002+00', '2099-01-02 10:05:00.000002+00', 3),
  ('c2000000-0000-4000-8000-00000000b003', 'c2000000-0000-4000-8000-00000000a002', 'Passed',
   'c2-restore-fixture', null, null, '2099-01-03 10:00:00.000003+00',
   '2099-01-03 10:10:00.000003+00', '2099-01-03 10:05:00.000003+00', 3),
  ('c2000000-0000-4000-8000-00000000b004', 'c2000000-0000-4000-8000-00000000a003', 'Inconclusive',
   'c2-restore-fixture', 'c2-synthetic-session-b004', 'c2-synthetic-feedback', '2099-01-04 10:00:00.000004+00',
   '2099-01-04 10:10:00.000004+00', '2099-01-04 10:05:00.000004+00', 2),
  ('c2000000-0000-4000-8000-00000000b005', 'c2000000-0000-4000-8000-00000000a004', 'Expired',
   'c2-restore-fixture', null, null, '2099-01-05 10:00:00.000005+00',
   '2099-01-05 10:10:00.000005+00', null, 1),
  ('c2000000-0000-4000-8000-00000000b006', 'c2000000-0000-4000-8000-00000000a005', 'Pending',
   'c2-restore-fixture', null, null, '2099-01-06 10:00:00.000006+00',
   '2099-01-06 10:10:00.000006+00', null, 0),
  ('c2000000-0000-4000-8000-00000000b007', 'c2000000-0000-4000-8000-00000000a006', 'Capturing',
   'c2-restore-fixture', 'c2-synthetic-session-b007', null, '2099-01-07 10:00:00.000007+00',
   '2099-01-07 10:10:00.000007+00', null, 1),
  ('c2000000-0000-4000-8000-00000000b008', 'c2000000-0000-4000-8000-00000000a007', 'Processing',
   'c2-restore-fixture', 'c2-synthetic-session-b008', null, '2099-01-08 10:00:00.000008+00',
   '2099-01-08 10:10:00.000008+00', null, 2);

commit;
