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
// session and a JSON file without anybody having to think about encoding.

import (
	"crypto/ecdsa"
	"encoding/hex"
	"fmt"
	"os"
	"strings"

	"github.com/gamepingbooster/relay/internal/protocol"
)

// loadLicenceKey reads the licence server's public key: 65 bytes of hex, uncompressed P-256.
func loadLicenceKey(path string) (*ecdsa.PublicKey, error) {
	raw, err := readHexFile(path)
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
	raw, err := readHexFile(path)
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
		if err := os.WriteFile(path, []byte(hex.EncodeToString(d)+"\n"), 0o600); err != nil {
			return nil, false, fmt.Errorf("could not save the new relay key to %s: %w", path, err)
		}
		return key, true, nil

	default:
		return nil, false, err
	}
}

func readHexFile(path string) ([]byte, error) {
	b, err := os.ReadFile(path)
	if err != nil {
		return nil, err
	}
	// Tolerate a trailing newline, spaces and an 0x prefix, because all three arrive sooner or
	// later from a copy and paste.
	text := strings.TrimSpace(string(b))
	text = strings.TrimPrefix(text, "0x")
	text = strings.ReplaceAll(text, " ", "")
	raw, err := hex.DecodeString(text)
	if err != nil {
		return nil, fmt.Errorf("%s is not hex: %w", path, err)
	}
	return raw, nil
}
