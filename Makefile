.PHONY: image image-release
IMAGE_TAG ?= dev
# Ex.: make image IMAGE=web. O worker exige SIXLABORS_LICENSE_KEY (secret do BuildKit).
IMAGE ?= web
DOCKERFILE_web = infra/images/dotnet/Dockerfile
DOCKERFILE_worker = infra/images/dotnet/Dockerfile
DOCKERFILE_migrator = infra/images/dotnet/Dockerfile
DOCKERFILE_edge = infra/images/edge/Dockerfile
DOCKERFILE_postgres = infra/images/postgres/Dockerfile
TARGET_web = --target web
TARGET_worker = --target worker --secret id=sixlabors_license,env=SIXLABORS_LICENSE_KEY
TARGET_migrator = --target migrator

image:
	@test -n "$(DOCKERFILE_$(IMAGE))" || { echo 'IMAGE deve ser web, worker, migrator, edge ou postgres' >&2; exit 2; }
	./scripts/docker-build.sh local -f $(DOCKERFILE_$(IMAGE)) $(TARGET_$(IMAGE)) -t duora-$(IMAGE):$(IMAGE_TAG) .

image-release:
	@test -n "$(DOCKERFILE_$(IMAGE))" || { echo 'IMAGE deve ser web, worker, migrator, edge ou postgres' >&2; exit 2; }
	mkdir -p .docker-artifacts
	./scripts/docker-build.sh oci .docker-artifacts/duora-$(IMAGE).tar -f $(DOCKERFILE_$(IMAGE)) $(TARGET_$(IMAGE)) -t duora-$(IMAGE):$(IMAGE_TAG) .
