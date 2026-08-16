module onepasswordnative

go 1.26.5

require github.com/1password/onepassword-sdk-go v0.4.1

// Five entries below are pins rather than requirements, and `go mod tidy` will delete them:
// golang.org/x/net, google.golang.org/grpc, go.opentelemetry.io/otel/sdk, golang.org/x/crypto
// and go.opentelemetry.io/otel.
//
// All five reach the module graph through observe-sdk and extism, which name old versions in
// their own go.mod. No package in any of them is imported by this build -- `go list -deps .`
// shows only the standard library's own vendor/golang.org/x/net/* copies, which are a different
// thing -- so `go build -buildmode=c-shared` links none of them into onepassword.dll and no
// vulnerability in them is reachable from here. govulncheck agrees, which is why it has never
// flagged one.
//
// They are pinned anyway because the module graph is what the dependency scanners read, so the
// old versions surface as alerts against this repository. Raising the minimum here is what closes
// those; nothing about the shipped DLL changes.
//
// Expect this list to grow rather than shrink. Pinning a graph-only module raises whatever *its*
// go.mod requires into the graph too, and the scanner then reports on that: x/net v0.55.0 brought
// x/crypto v0.51.0 with it (thirteen ssh advisories, CVE-2026-39827 and friends, fixed in v0.52.0)
// and grpc v1.82.1 brought otel v1.43.0 (CVE-2026-41178 in otel/baggage and otel/propagation,
// fixed in v1.44.0). Both are pinned above their fix. The entries carried in alongside them --
// otel/metric, otel/trace, auto/sdk, go-logr and xxhash -- are not pins and are not linked either.
//
// The catch: tidy keeps an indirect requirement only for a module providing a package the build
// imports, and these provide none, so `go mod tidy` silently drops every pin back to the graph
// version. No workflow runs tidy (CI runs `go build` and `go test` only), so they hold -- but a
// local tidy will undo this, and the alerts come back. Re-add with:
//
//	go get golang.org/x/net@v0.55.0 google.golang.org/grpc@v1.82.1 go.opentelemetry.io/otel/sdk@v1.43.0 golang.org/x/crypto@v0.52.0 go.opentelemetry.io/otel@v1.44.0
require (
	github.com/cespare/xxhash/v2 v2.3.0 // indirect
	github.com/dylibso/observe-sdk/go v0.0.0-20240828172851-9145d8ad07e1 // indirect
	github.com/extism/go-sdk v1.7.1 // indirect
	github.com/go-logr/logr v1.4.3 // indirect
	github.com/go-logr/stdr v1.2.2 // indirect
	github.com/gobwas/glob v0.2.3 // indirect
	github.com/ianlancetaylor/demangle v0.0.0-20251118225945-96ee0021ea0f // indirect
	github.com/tetratelabs/wabin v0.0.0-20230304001439-f6f874872834 // indirect
	github.com/tetratelabs/wazero v1.11.0 // indirect
	go.opentelemetry.io/auto/sdk v1.2.1 // indirect
	go.opentelemetry.io/otel v1.44.0 // indirect
	go.opentelemetry.io/otel/metric v1.44.0 // indirect
	go.opentelemetry.io/otel/sdk v1.43.0 // indirect
	go.opentelemetry.io/otel/trace v1.44.0 // indirect
	go.opentelemetry.io/proto/otlp v1.9.0 // indirect
	golang.org/x/crypto v0.52.0 // indirect
	golang.org/x/net v0.55.0 // indirect
	golang.org/x/sys v0.45.0 // indirect
	golang.org/x/text v0.37.0 // indirect
	google.golang.org/genproto/googleapis/rpc v0.0.0-20260414002931-afd174a4e478 // indirect
	google.golang.org/grpc v1.82.1 // indirect
	google.golang.org/protobuf v1.36.11 // indirect
)
