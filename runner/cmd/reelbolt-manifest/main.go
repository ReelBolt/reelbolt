// Command reelbolt-manifest is the release pipeline's half of the signed
// auto-update contract in runner/internal/update (WP E9), and the tool the
// release workflow in .github/workflows/runner-release.yml is built on (WP
// E10b).
//
// A runner refuses to update from anything it cannot verify. It needs three
// facts about a release, and this command is the only place they are produced:
//
//   - the manifest was signed by a release key compiled into the binary,
//   - each artifact is exactly the number of bytes the manifest says, and
//   - each artifact hashes to the digest the manifest says.
//
// Keeping that in one tool rather than in YAML means the pipeline and the
// runner agree on the format, the domain separation and the digest algorithm by
// construction instead of by review. runner/internal/update/README.md is the
// contract both sides implement.
//
// Subcommands:
//
//	gen-key   create an Ed25519 release keypair (WP E10a, run by the operator)
//	ldflags   print the -ldflags that stamp a version and the release keys
//	assemble  hash the built artifacts and write manifest.json
//	sign      write the detached manifest.json.sig
//	verify    verify a manifest, and optionally the artifacts it names
//	latest    write the per-channel latest.json the download page reads
//
// Nothing here holds a key of its own and nothing here ever prints a private
// key. CI does not run gen-key: the release key is generated once, offline, and
// lives in the GitHub environment "release" as RUNNER_MANIFEST_SIGNING_KEY. See
// runner/build/README.md for the operator steps.
//
// The manifest bytes are signed as they are written. Neither "sign" nor
// "verify" ever rewrites manifest.json, because the signature covers the bytes
// and not the parsed document: appending a newline after signing invalidates
// it. Every command that reads a manifest reads the file and never re-encodes
// it.
package main

import (
	"errors"
	"flag"
	"fmt"
	"io"
	"os"
)

const usage = "reelbolt-manifest - build, sign and verify the runner's update manifest\n" +
	"\n" +
	"Usage:\n" +
	"  reelbolt-manifest gen-key  [flags]   create an Ed25519 release keypair (WP E10a)\n" +
	"  reelbolt-manifest ldflags  [flags]   print the -ldflags that stamp a release build\n" +
	"  reelbolt-manifest assemble [flags]   hash the built artifacts and write manifest.json\n" +
	"  reelbolt-manifest sign     [flags]   write the detached manifest.json.sig\n" +
	"  reelbolt-manifest verify   [flags]   verify a manifest against a release public key\n" +
	"\n" +
	"The contract this implements is runner/internal/update/README.md.\n" +
	"\n" +
	"Run \"reelbolt-manifest <command> -h\" for a command's flags.\n"

func main() {
	if err := run(os.Args[1:], os.Stdout, os.Stderr); err != nil {
		fmt.Fprintf(os.Stderr, "reelbolt-manifest: %v\n", err)
		os.Exit(1)
	}
}

// run is main's body with its streams injected, so the whole command surface is
// testable without exec-ing a binary.
func run(args []string, stdout, stderr io.Writer) error {
	if len(args) == 0 {
		fmt.Fprint(stderr, usage)
		return errors.New("no command given")
	}
	cmd, rest := args[0], args[1:]
	switch cmd {
	case "gen-key":
		return cmdGenKey(rest, stdout)
	case "ldflags":
		return cmdLdflags(rest, stdout)
	case "assemble":
		return cmdAssemble(rest, stdout)
	case "sign":
		return cmdSign(rest, stdout)
	case "verify":
		return cmdVerify(rest, stdout)
	case "latest":
		return cmdLatest(rest, stdout)
	case "help", "-h", "--help":
		fmt.Fprint(stdout, usage)
		return nil
	default:
		fmt.Fprint(stderr, usage)
		return fmt.Errorf("unknown command %q", cmd)
	}
}

// newFlagSet builds a flag set that prints usage to out. Errors come back to
// the caller rather than calling os.Exit, so a usage mistake is reported the
// same way a verification failure is.
func newFlagSet(name, purpose string, out io.Writer) *flag.FlagSet {
	fs := flag.NewFlagSet(name, flag.ContinueOnError)
	fs.SetOutput(out)
	fs.Usage = func() {
		fmt.Fprintf(out, "usage: reelbolt-manifest %s [flags]\n\n%s\n\nflags:\n", name, purpose)
		fs.PrintDefaults()
	}
	return fs
}
