package main

// Key loading for the licensed authentication mode.
//
// Two keys, and they are not the same kind of thing:
//
//   - the LICENCE key is the licence server's PUBLIC key. It is all the relay needs to verify a
//     token offline: no database, no network call, and nothing secret. A relay that is rented,
//     rebuilt or stolen leaks nothing that lets anybody forge access, which is the whole reason
//     the design is a signed blob rather than an API call.
//
//   - the RELAY key is this machine's OWN private key, used to sign handshake answers. In token
//     mode there is no shared secret, so without it a client cannot tell a real relay from a
//     forged reply. Its public half goes into the profile beside the endpoint.
//
// Both are hex on a single line, because that survives being pasted through a terminal, an SSH
// session and a JSON file without anybody having to think about encoding. The reading and
// writing of that format lives in internal/keyfile, shared with licence-gen, which is the tool
// that produces these files.

import (
	"crypto/ecdsa"
	"errors"
	"fmt"
	"os"
	"syscall"

	"github.com/gamepingbooster/relay/internal/keyfile"
	"github.com/gamepingbooster/relay/internal/protocol"
)

// loadLicenceKey reads the licence server's public key: 65 bytes of hex, uncompressed P-256.
func loadLicenceKey(path string) (*ecdsa.PublicKey, error) {
	raw, err := keyfile.ReadHex(path)
	if err != nil {
		return nil, err
	}
	if len(raw) != protocol.PublicKeyLen {
		return nil, fmt.Errorf("the licence key in %s is %d bytes, want %d (uncompressed P-256, 0x04 || X || Y)",
			path, len(raw), protocol.PublicKeyLen)
	}
	pub, err := protocol.ParsePublicKey(raw)
	if err != nil {
		return nil, fmt.Errorf("the licence key in %s is not a valid P-256 point: %w", path, err)
	}
	return pub, nil
}

// loadOrCreateRelayKey reads this relay's own private key, generating one the first time.
//
// Generating it here rather than in install.sh means a relay always has a usable key, and one
// fewer step to forget. It is written before it is used, and a write failure is fatal: a relay
// that minted a fresh key on every restart would invalidate the public key already published in
// the profile, and every client would start refusing its answers - a failure that would look
// like a network problem and be nothing of the sort.
func loadOrCreateRelayKey(path string) (*ecdsa.PrivateKey, bool, error) {
	raw, err := keyfile.ReadHex(path)
	switch {
	case err == nil:
		if len(raw) != protocol.PrivateKeyLen {
			return nil, false, fmt.Errorf("the relay key in %s is %d bytes, want %d",
				path, len(raw), protocol.PrivateKeyLen)
		}
		key, err := protocol.ParsePrivateKey(raw)
		if err != nil {
			return nil, false, fmt.Errorf("the relay key in %s is not a valid P-256 scalar: %w", path, err)
		}
		return key, false, nil

	case os.IsNotExist(err):
		key, err := protocol.GenerateKey()
		if err != nil {
			return nil, false, fmt.Errorf("could not generate a relay key: %w", err)
		}
		d := make([]byte, protocol.PrivateKeyLen)
		key.D.FillBytes(d)
		// 0600: it is a private key, and the directory it lives in is usually /etc/gpb.
		if err := keyfile.WriteHex(path, d, 0o600); err != nil {
			// Name the likely cause. Under systemd the unit sets ProtectSystem=full, which makes
			// /etc READ-ONLY for this process - so a relay that has never had a key cannot make
			// one on its first start, and restarts forever with an error about a filesystem the
			// operator knows perfectly well is writable. install.sh creates the key before the
			// service is ever started, which is why this is rare; it is not impossible.
			if errors.Is(err, syscall.EROFS) {
				return nil, false, fmt.Errorf("could not save the new relay key to %s: %w. "+
					"Under systemd this is expected - ProtectSystem=full makes /etc read-only for "+
					"the service. Create it once by hand with `relayd -print-relay-key`, which runs "+
					"outside the unit, then restart", path, err)
			}
			return nil, false, fmt.Errorf("could not save the new relay key to %s: %w", path, err)
		}
		return key, true, nil

	default:
		return nil, false, err
	}
}
