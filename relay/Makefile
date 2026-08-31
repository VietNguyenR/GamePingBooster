# Build relayd for a Linux VPS (works from a Windows or Linux dev machine).

BINARY  := relayd
GOFLAGS := -trimpath -ldflags="-s -w"

.PHONY: build build-arm64 test fmt vet clean deploy dist

## build: compile a static linux/amd64 binary (no cgo needed)
build:
	CGO_ENABLED=0 GOOS=linux GOARCH=amd64 go build $(GOFLAGS) -o $(BINARY) ./cmd/relayd

## build-arm64: for ARM VPSes (Oracle Ampere, AWS Graviton)
build-arm64:
	CGO_ENABLED=0 GOOS=linux GOARCH=arm64 go build $(GOFLAGS) -o $(BINARY).arm64 ./cmd/relayd

test:
	go test ./...

fmt:
	gofmt -w ./cmd ./internal

vet:
	CGO_ENABLED=0 GOOS=linux go vet ./...

clean:
	rm -f $(BINARY) $(BINARY).arm64

## deploy: build then push to the VPS. Usage: make deploy HOST=root@1.2.3.4
deploy: build
	@test -n "$(HOST)" || (echo "Missing HOST, e.g. make deploy HOST=root@1.2.3.4"; exit 1)
	ssh $(HOST) "mkdir -p /opt/gpb/deploy"
	scp $(BINARY) $(HOST):/opt/gpb/relayd
	scp deploy/*.sh deploy/relayd.service $(HOST):/opt/gpb/deploy/
	ssh $(HOST) "chmod +x /opt/gpb/deploy/*.sh && cd /opt/gpb/deploy && ./install.sh"

## dist: build a tarball for manual upload (when ssh/scp is unavailable)
dist: build
	rm -rf dist gpb-relay.tar.gz
	mkdir -p dist/deploy
	cp $(BINARY) dist/
	cp deploy/setup-nat.sh deploy/install.sh deploy/relayd.service dist/deploy/
	chmod +x dist/deploy/*.sh
	tar -czf gpb-relay.tar.gz -C dist .
	rm -rf dist
	@echo "Created gpb-relay.tar.gz. On the VPS: tar -xzf gpb-relay.tar.gz && cd deploy && ./install.sh"
