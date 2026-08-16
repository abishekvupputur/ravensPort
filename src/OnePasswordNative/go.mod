module onepasswordnative

go 1.26.5

require github.com/1password/onepassword-sdk-go v0.4.1

// Three of these are pins rather than requirements, and `go mod tidy` will delete them.
//
// golang.org/x/net, google.golang.org/grpc and go.opentelemetry.io/otel/sdk reach the module
// graph through observe-sdk and extism, which name old versions in their own go.mod. No package
// in any of the three is imported by this build -- `go list -deps .` shows only the standard
// library's own vendor/golang.org/x/net/* copies -- so `go build -buildmode=c-shared` links none
// of them into onepassword.dll and no vulnerability in them is reachable from here. govulncheck
// agrees, which is why it never flagged them.
//
// They are pinned anyway because the module graph is what GitHub's dependency graph submits, so
// the old versions surface as Dependabot alerts against this repository. Raising the minimum here
// is what closes those; nothing about the shipped DLL changes.
//
// The catch: tidy keeps an indirect requirement only for a module providing a package the build
// imports, and these provide none, so `go mod tidy` silently drops all three back to the graph
// versions. No workflow runs tidy (CI runs `go build` and `go test` only), so they hold -- but a
// local tidy will undo this, and the alerts come back. Re-add with:
//
//	go get golang.org/x/net@v0.55.0 google.golang.org/grpc@v1.82.1 go.opentelemetry.io/otel/sdk@v1.43.0
require (
	github.com/dylibso/observe-sdk/go v0.0.0-20240828172851-9145d8ad07e1 // indirect
	github.com/extism/go-sdk v1.7.1 // indirect
	github.com/gobwas/glob v0.2.3 // indirect
	github.com/ianlancetaylor/demangle v0.0.0-20251118225945-96ee0021ea0f // indirect
	github.com/tetratelabs/wabin v0.0.0-20230304001439-f6f874872834 // indirect
	github.com/tetratelabs/wazero v1.11.0 // indirect
	go.opentelemetry.io/otel/sdk v1.43.0 // indirect
	go.opentelemetry.io/proto/otlp v1.9.0 // indirect
	golang.org/x/net v0.55.0 // indirect
	golang.org/x/sys v0.45.0 // indirect
	golang.org/x/text v0.37.0 // indirect
	google.golang.org/genproto/googleapis/rpc v0.0.0-20260414002931-afd174a4e478 // indirect
	google.golang.org/grpc v1.82.1 // indirect
	google.golang.org/protobuf v1.36.11 // indirect
)
