"""Testes das políticas e da configuração dos buckets na AWS (infra/aws). Rodar da raiz:
python3 -m unittest discover -s scripts/deploy -v

Os valores esperados são literais: afrouxar uma política (ação nova, curinga, recurso mais largo, negação removida)
faz um teste falhar, e a correção é decidir de novo no PR, não ajustar o teste.
"""

from __future__ import annotations

import json
import re
import unittest
from pathlib import Path
from tempfile import TemporaryDirectory
from typing import Any

import duora_aws
import duora_backup

REPO = Path(__file__).resolve().parents[2]
AWS = REPO / "infra" / "aws"

BACKUP_BUCKET = "duora-backup-exemplo"
MEDIA_BUCKET = "duora-media-exemplo"
ACCOUNT_ID = "123456789012"
VPS_IPV4 = "8.8.4.4"  # qualquer IPv4 público: o parser recusa os blocos de documentação (não são globais)
VALUES = duora_aws.TemplateValues(backup_bucket=BACKUP_BUCKET, media_bucket=MEDIA_BUCKET, account_id=ACCOUNT_ID,
                                  vps_ipv4=VPS_IPV4)

BACKUP_ARN = f"arn:aws:s3:::{BACKUP_BUCKET}"
MEDIA_ARN = f"arn:aws:s3:::{MEDIA_BUCKET}"
ADMIN_ROLE_ARN = f"arn:aws:iam::{ACCOUNT_ID}:role/duora-backup-admin"
SECURE_TRANSPORT = {"Bool": {"aws:SecureTransport": "true"}}

# Ações que nenhuma credencial da VPS pode ter no bucket de backup (ADR-0023): apagar versão, contornar ou mudar a
# retenção, mexer em versionamento, lock, lifecycle, política, ACL, bloqueio público, cifra, posse ou replicação.
BACKUP_PROTECTED_ACTIONS = {
    "s3:DeleteObjectVersion", "s3:BypassGovernanceRetention", "s3:PutObjectRetention", "s3:PutObjectLegalHold",
    "s3:PutBucketVersioning", "s3:PutBucketObjectLockConfiguration", "s3:PutLifecycleConfiguration",
    "s3:PutBucketPolicy", "s3:DeleteBucketPolicy", "s3:DeleteBucket", "s3:PutBucketAcl", "s3:PutObjectAcl",
    "s3:PutObjectVersionAcl", "s3:PutBucketPublicAccessBlock", "s3:PutEncryptionConfiguration",
    "s3:PutBucketOwnershipControls", "s3:PutReplicationConfiguration",
}


def rendered(relative: str) -> dict[str, Any]:
    return json.loads(duora_aws.render_text((AWS / relative).read_text(encoding="utf-8"), VALUES))


def as_list(value: str | list[str]) -> list[str]:
    return [value] if isinstance(value, str) else value


def statements(policy: dict[str, Any], effect: str) -> list[dict[str, Any]]:
    return [s for s in policy["Statement"] if s["Effect"] == effect]


def allowed_grants(policy: dict[str, Any]) -> set[tuple[str, str]]:
    return {(action, resource) for s in statements(policy, "Allow")
            for action in as_list(s["Action"]) for resource in as_list(s["Resource"])}


def wildcard_problems(policy: dict[str, Any]) -> list[str]:
    """Curinga em ação ou recurso de um Allow, NotAction/NotResource em Allow e Allow sem TLS."""
    problems = []
    for statement in statements(policy, "Allow"):
        if "NotAction" in statement or "NotResource" in statement:
            problems.append(f"{statement.get('Sid')}: NotAction/NotResource em Allow")
            continue
        problems += [f"{statement.get('Sid')}: ação {a}" for a in as_list(statement["Action"]) if "*" in a]
        problems += [f"{statement.get('Sid')}: recurso {r}" for r in as_list(statement["Resource"])
                     if r == "*" or r.endswith(":::*") or r.endswith(":*")]
        if statement.get("Condition", {}).get("Bool", {}).get("aws:SecureTransport") != "true":
            problems.append(f"{statement.get('Sid')}: Allow sem aws:SecureTransport")
    return problems


def denies(policy: dict[str, Any], condition: dict[str, Any]) -> list[dict[str, Any]]:
    return [s for s in statements(policy, "Deny") if s.get("Condition") == condition]


class TemplatesTests(unittest.TestCase):
    """Todo JSON de infra/aws é JSON válido antes e depois de preencher, e nada fica sem preencher."""

    def test_every_template_is_valid_json_and_renders_without_placeholders(self) -> None:
        templates = sorted(AWS.rglob("*.json"))
        self.assertGreaterEqual(len(templates), 17)
        for path in templates:
            with self.subTest(template=str(path.relative_to(AWS))):
                text = path.read_text(encoding="utf-8")
                json.loads(text)
                output = duora_aws.render_text(text, VALUES)
                json.loads(output)
                self.assertNotIn("{{", output)

    def test_unknown_placeholder_is_refused(self) -> None:
        with self.assertRaisesRegex(duora_aws.AwsInputError, "BUCKET_NOVO"):
            duora_aws.render_text('{"Resource": "arn:aws:s3:::{{BUCKET_NOVO}}"}', VALUES)

    def test_iam_policy_variables_pass_through_untouched(self) -> None:
        self.assertEqual('"${aws:username}"', duora_aws.render_text('"${aws:username}"', VALUES))


class BackupUserPolicyTests(unittest.TestCase):
    """duora-backup: a única credencial do bucket de backup na VPS (container backup e job restore)."""

    policy = rendered("iam/duora-backup.json")

    def test_grants_exactly_what_pgbackrest_and_the_verify_need_under_the_repository_prefix(self) -> None:
        self.assertEqual({
            ("s3:ListBucket", BACKUP_ARN),
            ("s3:ListBucketVersions", BACKUP_ARN),
            ("s3:GetBucketVersioning", BACKUP_ARN),
            ("s3:GetBucketObjectLockConfiguration", BACKUP_ARN),
            ("s3:GetObject", f"{BACKUP_ARN}/duora/*"),
            ("s3:GetObjectVersion", f"{BACKUP_ARN}/duora/*"),
            ("s3:PutObject", f"{BACKUP_ARN}/duora/*"),
            ("s3:DeleteObject", f"{BACKUP_ARN}/duora/*"),
        }, allowed_grants(self.policy))

    def test_listing_is_limited_to_the_repository_prefix(self) -> None:
        for statement in statements(self.policy, "Allow"):
            if {"s3:ListBucket", "s3:ListBucketVersions"} & set(as_list(statement["Action"])):
                with self.subTest(sid=statement["Sid"]):
                    self.assertEqual(["duora", "duora/*"],
                                     statement["Condition"]["StringLike"]["s3:prefix"])

    def test_prefix_matches_the_pgbackrest_repository_path(self) -> None:
        conf = (REPO / "infra/images/postgres/pgbackrest-repo.conf").read_text(encoding="utf-8")
        self.assertRegex(conf, r"(?m)^repo1-path=/duora$")

    def test_no_wildcard_and_every_allow_requires_tls(self) -> None:
        self.assertEqual([], wildcard_problems(self.policy))

    def test_denies_every_protected_action(self) -> None:
        protected = [s for s in statements(self.policy, "Deny") if "Condition" not in s]
        self.assertEqual(1, len(protected))
        self.assertEqual("*", protected[0]["Resource"])
        self.assertEqual(BACKUP_PROTECTED_ACTIONS, set(protected[0]["Action"]))

    def test_denies_everything_without_tls_and_from_outside_the_vps(self) -> None:
        assert_denies_insecure_transport_and_other_origins(self, self.policy)


class MediaUserPolicyTests(unittest.TestCase):
    """Credenciais separadas por processo no bucket de mídia (ADR-0025, item 5). Nenhuma lista o bucket."""

    web = rendered("iam/duora-media-web.json")
    worker = rendered("iam/duora-media-worker.json")

    def test_web_writes_and_compensates_in_quarantine_and_only_reads_derivatives(self) -> None:
        self.assertEqual({
            ("s3:PutObject", f"{MEDIA_ARN}/quarantine/*"),
            ("s3:DeleteObject", f"{MEDIA_ARN}/quarantine/*"),
            ("s3:GetObject", f"{MEDIA_ARN}/derivatives/*"),
        }, allowed_grants(self.web))

    def test_worker_consumes_quarantine_and_owns_derivatives(self) -> None:
        self.assertEqual({
            ("s3:GetObject", f"{MEDIA_ARN}/quarantine/*"),
            ("s3:DeleteObject", f"{MEDIA_ARN}/quarantine/*"),
            ("s3:PutObject", f"{MEDIA_ARN}/derivatives/*"),
            ("s3:GetObject", f"{MEDIA_ARN}/derivatives/*"),
            ("s3:DeleteObject", f"{MEDIA_ARN}/derivatives/*"),
        }, allowed_grants(self.worker))

    def test_web_never_reads_the_quarantine(self) -> None:
        self.assertNotIn(("s3:GetObject", f"{MEDIA_ARN}/quarantine/*"), allowed_grants(self.web))

    def test_no_wildcard_and_every_allow_requires_tls(self) -> None:
        for name, policy in (("web", self.web), ("worker", self.worker)):
            with self.subTest(policy=name):
                self.assertEqual([], wildcard_problems(policy))

    def test_denies_everything_without_tls_and_from_outside_the_vps(self) -> None:
        for name, policy in (("web", self.web), ("worker", self.worker)):
            with self.subTest(policy=name):
                assert_denies_insecure_transport_and_other_origins(self, policy)

    def test_prefixes_match_the_object_keys_of_the_backend(self) -> None:
        source = (REPO / "backend/src/Duora.Core/Media/ObjectKey.cs").read_text(encoding="utf-8")
        self.assertIn('QuarantinePrefix = "quarantine/"', source)
        self.assertIn('DerivativesPrefix = "derivatives/"', source)


class BackupAdminRoleTests(unittest.TestCase):
    """Papel administrativo fora da VPS: única exceção às negações da política do bucket de backup."""

    trust = rendered("iam/duora-backup-admin-trust.json")
    policy = rendered("iam/duora-backup-admin.json")

    def test_only_the_account_with_recent_mfa_assumes_the_role(self) -> None:
        (statement,) = self.trust["Statement"]
        self.assertEqual("Allow", statement["Effect"])
        self.assertEqual("sts:AssumeRole", statement["Action"])
        self.assertEqual({"AWS": f"arn:aws:iam::{ACCOUNT_ID}:root"}, statement["Principal"])
        self.assertEqual("true", statement["Condition"]["Bool"]["aws:MultiFactorAuthPresent"])
        self.assertLessEqual(int(statement["Condition"]["NumericLessThan"]["aws:MultiFactorAuthAge"]), 3600)

    def test_no_wildcard_and_only_the_backup_bucket(self) -> None:
        self.assertEqual([], wildcard_problems(self.policy))
        for _, resource in allowed_grants(self.policy):
            with self.subTest(resource=resource):
                self.assertTrue(resource == BACKUP_ARN or resource.startswith(f"{BACKUP_ARN}/"), resource)

    def test_never_bypasses_retention_nor_deletes_the_bucket(self) -> None:
        actions = {action for action, _ in allowed_grants(self.policy)}
        self.assertEqual(set(), actions & {"s3:BypassGovernanceRetention", "s3:DeleteBucket",
                                           "s3:PutBucketObjectLockConfiguration", "s3:PutBucketVersioning"})


class BucketPolicyTests(unittest.TestCase):
    backup = rendered("buckets/backup/policy.json")
    media = rendered("buckets/media/policy.json")

    def test_bucket_policies_only_deny(self) -> None:
        for name, policy in (("backup", self.backup), ("media", self.media)):
            with self.subTest(bucket=name):
                self.assertEqual([], statements(policy, "Allow"))

    def test_both_buckets_refuse_plain_http_and_old_tls_for_everyone(self) -> None:
        for name, policy, arn in (("backup", self.backup, BACKUP_ARN), ("media", self.media, MEDIA_ARN)):
            for condition in ({"Bool": {"aws:SecureTransport": "false"}},
                              {"NumericLessThan": {"s3:TlsVersion": "1.2"}}):
                with self.subTest(bucket=name, condition=condition):
                    (statement,) = denies(policy, condition)
                    self.assertEqual("*", statement["Principal"])
                    self.assertEqual("s3:*", statement["Action"])
                    self.assertEqual({arn, f"{arn}/*"}, set(statement["Resource"]))

    def test_backup_denies_protected_actions_to_every_principal_but_the_admin_role(self) -> None:
        (statement,) = denies(self.backup, {"ArnNotEquals": {"aws:PrincipalArn": ADMIN_ROLE_ARN}})
        self.assertEqual("*", statement["Principal"])
        self.assertEqual({BACKUP_ARN, f"{BACKUP_ARN}/*"}, set(statement["Resource"]))
        self.assertEqual(BACKUP_PROTECTED_ACTIONS, set(statement["Action"]))


class BucketConfigurationTests(unittest.TestCase):
    def test_backup_object_lock_is_compliance_for_at_least_what_the_verify_demands(self) -> None:
        lock = rendered("buckets/backup/object-lock.json")
        self.assertEqual("Enabled", lock["ObjectLockEnabled"])
        self.assertEqual({"Mode": "COMPLIANCE", "Days": 21}, lock["Rule"]["DefaultRetention"])
        self.assertGreaterEqual(lock["Rule"]["DefaultRetention"]["Days"], duora_backup.MIN_OBJECT_LOCK_DAYS)

    def test_backup_is_versioned(self) -> None:
        self.assertEqual({"Status": "Enabled"}, rendered("buckets/backup/versioning.json"))

    def test_backup_lifecycle_expires_noncurrent_versions_after_the_lock_and_cleans_markers(self) -> None:
        (rule,) = rendered("buckets/backup/lifecycle.json")["Rules"]
        self.assertEqual("Enabled", rule["Status"])
        self.assertEqual({}, rule["Filter"])
        self.assertEqual(30, rule["NoncurrentVersionExpiration"]["NoncurrentDays"])
        self.assertGreater(rule["NoncurrentVersionExpiration"]["NoncurrentDays"], duora_backup.MIN_OBJECT_LOCK_DAYS)
        self.assertEqual({"ExpiredObjectDeleteMarker": True}, rule["Expiration"])
        self.assertEqual({"DaysAfterInitiation": 7}, rule["AbortIncompleteMultipartUpload"])

    def test_media_has_neither_versioning_nor_object_lock(self) -> None:
        # ADR-0025: o DELETE de uma foto apaga de fato (spec 12.4 e 16.2).
        names = {path.name for path in (AWS / "buckets/media").iterdir()}
        self.assertEqual(set(), names & {"versioning.json", "object-lock.json"})

    def test_media_lifecycle_drops_forgotten_originals_and_incomplete_uploads(self) -> None:
        rules = {rule["ID"]: rule for rule in rendered("buckets/media/lifecycle.json")["Rules"]}
        self.assertEqual({"Prefix": "quarantine/"}, rules["quarantine-safety-net"]["Filter"])
        self.assertEqual({"Days": 3}, rules["quarantine-safety-net"]["Expiration"])
        self.assertEqual({}, rules["abort-incomplete-uploads"]["Filter"])
        self.assertEqual({"DaysAfterInitiation": 1}, rules["abort-incomplete-uploads"]["AbortIncompleteMultipartUpload"])
        self.assertTrue(all(rule["Status"] == "Enabled" for rule in rules.values()))

    def test_both_buckets_block_public_access_disable_acls_and_encrypt_with_sse_s3(self) -> None:
        for bucket in ("backup", "media"):
            with self.subTest(bucket=bucket):
                self.assertEqual({"BlockPublicAcls": True, "IgnorePublicAcls": True, "BlockPublicPolicy": True,
                                  "RestrictPublicBuckets": True},
                                 rendered(f"buckets/{bucket}/public-access-block.json"))
                self.assertEqual({"Rules": [{"ObjectOwnership": "BucketOwnerEnforced"}]},
                                 rendered(f"buckets/{bucket}/ownership-controls.json"))
                (rule,) = rendered(f"buckets/{bucket}/encryption.json")["Rules"]
                self.assertEqual({"SSEAlgorithm": "AES256"}, rule["ApplyServerSideEncryptionByDefault"])


class WildcardCheckSabotageTests(unittest.TestCase):
    """O verificador acusa cada forma de afrouxar um Allow (prova de que os testes acima falhariam)."""

    def allow(self, **overrides: Any) -> dict[str, Any]:
        statement = {"Sid": "X", "Effect": "Allow", "Action": ["s3:GetObject"],
                     "Resource": [f"{MEDIA_ARN}/derivatives/*"], "Condition": SECURE_TRANSPORT}
        statement.update(overrides)
        return {"Version": "2012-10-17", "Statement": [statement]}

    def test_accepts_the_narrow_statement(self) -> None:
        self.assertEqual([], wildcard_problems(self.allow()))

    def test_flags_each_loosening(self) -> None:
        cases = {
            "s3:*": {"Action": ["s3:*"]},
            "s3:Get*": {"Action": "s3:Get*"},
            "*": {"Action": "*"},
            "recurso *": {"Resource": "*"},
            "bucket inteiro": {"Resource": "arn:aws:s3:::*"},
            "sem TLS": {"Condition": {}},
            "NotAction": {"NotAction": ["s3:DeleteObject"]},
        }
        for name, override in cases.items():
            with self.subTest(case=name):
                self.assertNotEqual([], wildcard_problems(self.allow(**override)))


def assert_denies_insecure_transport_and_other_origins(test: unittest.TestCase, policy: dict[str, Any]) -> None:
    for condition in ({"Bool": {"aws:SecureTransport": "false"}},
                      {"NotIpAddress": {"aws:SourceIp": f"{VPS_IPV4}/32"}}):
        (statement,) = denies(policy, condition)
        test.assertEqual("s3:*", statement["Action"])
        test.assertEqual("*", statement["Resource"])


class InputParsingTests(unittest.TestCase):
    def test_accepts_dns_compatible_bucket_names(self) -> None:
        for name in ("duora-backup-a1b2", "abc", "a" * 63):
            with self.subTest(name=name):
                self.assertEqual(name, duora_aws.parse_bucket_name(name))

    def test_refuses_bucket_names_that_break_virtual_host_tls_or_aws_rules(self) -> None:
        for name in ("", "ab", "a" * 64, "Duora-Backup", "duora.backup", "-duora", "duora-", "duora_backup",
                     "xn--duora", "duora-s3alias", "duora--ol-s3", "192.168.0.1", "duora backup"):
            with self.subTest(name=name):
                with self.assertRaises(duora_aws.AwsInputError):
                    duora_aws.parse_bucket_name(name)

    def test_account_id_has_twelve_digits(self) -> None:
        self.assertEqual("000000000001", duora_aws.parse_account_id("000000000001"))
        for value in ("", "12345678901", "1234567890123", "12345678901a", " 123456789012"):
            with self.subTest(value=value):
                with self.assertRaises(duora_aws.AwsInputError):
                    duora_aws.parse_account_id(value)

    def test_vps_address_is_a_public_ipv4(self) -> None:
        self.assertEqual("8.8.4.4", duora_aws.parse_vps_ipv4("8.8.4.4"))
        for value in ("", "10.0.0.5", "172.30.20.10", "192.168.1.1", "127.0.0.1", "169.254.169.254", "0.0.0.0",
                      "2001:db8::1", "198.51.100.7", "8.8.4.0/24", "1.2.3"):
            with self.subTest(value=value):
                with self.assertRaises(duora_aws.AwsInputError):
                    duora_aws.parse_vps_ipv4(value)

    def test_backup_and_media_buckets_must_differ(self) -> None:
        with self.assertRaisesRegex(duora_aws.AwsInputError, "diferentes"):
            duora_aws.TemplateValues(backup_bucket="duora-x", media_bucket="duora-x", account_id=ACCOUNT_ID,
                                     vps_ipv4=VPS_IPV4)


class RenderCommandTests(unittest.TestCase):
    def setUp(self) -> None:
        self._tmp = TemporaryDirectory()
        self.addCleanup(self._tmp.cleanup)
        self.out = Path(self._tmp.name) / "out"

    def run_render(self, *extra: str) -> int:
        return duora_aws.main(["render", "--backup-bucket", BACKUP_BUCKET, "--media-bucket", MEDIA_BUCKET,
                               "--account-id", ACCOUNT_ID, "--vps-ipv4", VPS_IPV4, "--out", str(self.out), *extra])

    def test_writes_every_template_filled_in_with_the_same_layout(self) -> None:
        self.assertEqual(0, self.run_render())

        expected = {p.relative_to(AWS) for p in AWS.rglob("*.json")}
        self.assertEqual(expected, {p.relative_to(self.out) for p in self.out.rglob("*.json")})
        policy = json.loads((self.out / "iam/duora-backup.json").read_text(encoding="utf-8"))
        self.assertIn((("s3:PutObject", f"{BACKUP_ARN}/duora/*")), allowed_grants(policy))

    def test_refuses_an_output_directory_that_already_has_files(self) -> None:
        self.out.mkdir()
        (self.out / "velho.json").write_text("{}", encoding="utf-8")

        self.assertEqual(1, self.run_render())
        self.assertEqual(["velho.json"], [p.name for p in self.out.iterdir()])

    def test_invalid_input_writes_nothing(self) -> None:
        code = duora_aws.main(["render", "--backup-bucket", "Duora.Backup", "--media-bucket", MEDIA_BUCKET,
                               "--account-id", ACCOUNT_ID, "--vps-ipv4", VPS_IPV4, "--out", str(self.out)])

        self.assertEqual(1, code)
        self.assertFalse(self.out.exists())


class S3CidrsTests(unittest.TestCase):
    """CIDRs do S3 na região, para duora_backup_s3_cidrs (filtro de saída do backup)."""

    RANGES = json.dumps({"prefixes": [
        {"ip_prefix": "52.216.0.0/15", "region": "us-east-1", "service": "S3"},
        {"ip_prefix": "3.5.0.0/19", "region": "us-east-1", "service": "S3"},
        {"ip_prefix": "52.216.0.0/15", "region": "us-east-1", "service": "AMAZON"},
        {"ip_prefix": "52.92.16.0/20", "region": "us-west-2", "service": "S3"},
        {"ip_prefix": "3.5.0.0/19", "region": "us-east-1", "service": "S3"},
    ], "ipv6_prefixes": [{"ipv6_prefix": "2600:1f18::/33", "region": "us-east-1", "service": "S3"}]})

    def test_keeps_only_ipv4_s3_prefixes_of_the_region_sorted_without_duplicates(self) -> None:
        self.assertEqual(["3.5.0.0/19", "52.216.0.0/15"], duora_aws.s3_cidrs(self.RANGES, "us-east-1"))

    def test_region_without_s3_prefixes_is_an_error(self) -> None:
        with self.assertRaisesRegex(duora_aws.AwsInputError, "sa-east-1"):
            duora_aws.s3_cidrs(self.RANGES, "sa-east-1")

    def test_unreadable_document_is_an_error(self) -> None:
        for text in ("", "[]", '{"prefixes": [{"region": "us-east-1"}]}', '{"prefixes": "x"}'):
            with self.subTest(text=text):
                with self.assertRaises(duora_aws.AwsInputError):
                    duora_aws.s3_cidrs(text, "us-east-1")

    def test_refuses_a_prefix_that_is_not_a_network(self) -> None:
        text = json.dumps({"prefixes": [{"ip_prefix": "0.0.0.0/0", "region": "us-east-1", "service": "S3"}]})
        with self.assertRaises(duora_aws.AwsInputError):
            duora_aws.s3_cidrs(text, "us-east-1")


class RunbookTests(unittest.TestCase):
    """O runbook aplica todo arquivo de infra/aws: um JSON novo sem passo no runbook não é aplicado por ninguém."""

    def test_every_rendered_file_is_referenced_by_the_runbook(self) -> None:
        runbook = (REPO / "docs/operations/aws-setup.md").read_text(encoding="utf-8")
        for path in sorted(AWS.rglob("*.json")):
            relative = str(path.relative_to(AWS))
            with self.subTest(file=relative):
                self.assertRegex(runbook, re.escape(relative))


if __name__ == "__main__":
    unittest.main()
