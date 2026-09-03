// Package keyfile reads and writes the hex key files that relayd and licence-gen share.
//
// It exists so there is ONE parser rather than two. licence-gen writes the files relayd reads,
// and when a writer and a reader carry their own copy of a format they drift. The failure that
// drift produces here is a nasty one to diagnose: a key that loads on one side and not the
// other surfaces to the user as "the relay rejects my token", which points at the token, at the
// licence server, at anything except the file that was actually mis-parsed.
//
// Hex on a single line, because that survives being pasted through a terminal, an SSH session
// and a JSON file without anybody having to think about encoding.
package keyfile

import (
	"encoding/hex"
	"fmt"
	"os"
	"strings"
)

// ReadHex reads a single-line hex file and returns the bytes.
//
// A trailing newline, embedded spaces and an 0x prefix are all tolerated, because all three
// arrive sooner or later from a copy and paste and none of them is an error the person making
// it can see.
func ReadHex(path string) ([]byte, error) {
	b, err := os.ReadFile(path)
	if err != nil {
		return nil, err
	}
	text := strings.TrimSpace(string(b))
	text = strings.TrimPrefix(text, "0x")
	text = strings.ReplaceAll(text, " ", "")
	raw, err := hex.DecodeString(text)
	if err != nil {
		return nil, fmt.Errorf("%s is not hex: %w", path, err)
	}
	return raw, nil
}

// WriteHex writes bytes as one line of hex, with a trailing newline.
//
// perm matters: 0600 for anything private, 0644 for a public key. The caller decides, because
// the caller is the only one that knows which it is holding.
func WriteHex(path string, raw []byte, perm os.FileMode) error {
	return os.WriteFile(path, []byte(hex.EncodeToString(raw)+"\n"), perm)
}
