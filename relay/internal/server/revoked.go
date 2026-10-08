package server

import (
	"encoding/hex"
	"strings"
	"time"

	"github.com/gamepingbooster/relay/internal/protocol"
)

// Revoked licence tokens: last sign-in wins.
//
// A token is verified offline and is good until it expires, so on its own the relay cannot know
// that the account holding it has since signed in on another machine and this one was signed out.
// The licence server says so in the reply to every status report: `revoked`, the tokens of machines
// signed out that way that have not expired yet. The relay then refuses those tokens at the
// handshake (StatusCredentialRevoked) and cuts any session already open on one.
//
// Deny-only, and exact. An entry is a device key AND the expiry its token carries, so it names one
// token and nothing else: the same machine signing in again is minted a token with a different
// expiry and is not caught, and nothing in the list can let anybody in. The worst a wrong list can
// do is refuse a token the licence server itself chose to name.
//
// The list replaces the previous one wholesale on every report that carries it, and an entry is
// dropped by the relay itself once its expiry passes, so the set never outgrows one token lifetime
// of sign-outs. A report that fails, or a licence server that predates the field, leaves the last
// list in place - it only ever ages out.

// revokedEntry is one row of the licence server's list, as it arrives.
type revokedEntry struct {
	Key string `json:"key"` // 130 hex characters, the uncompressed P-256 device key
	Exp int64  `json:"exp"` // unix seconds, the expiry the token carries
}

type revokedKey struct {
	device [protocol.PublicKeyLen]byte
	exp    int64
}

type revokedSet map[revokedKey]struct{}

// maxRevoked caps the list, so a licence server answering with something absurd cannot grow the
// relay's memory without bound. Far above any real day of sign-outs.
const maxRevoked = 100_000

// isRevoked reports whether the token for this device key with this expiry has been revoked.
func (s *Server) isRevoked(deviceKey []byte, exp int64) bool {
	set := s.revoked.Load()
	if set == nil || len(deviceKey) != protocol.PublicKeyLen {
		return false
	}
	var k revokedKey
	copy(k.device[:], deviceKey)
	k.exp = exp
	_, hit := (*set)[k]
	return hit
}

// applyRevoked installs a new list and cuts every live session holding a token on it. Returns how
// many sessions were cut. Entries that do not parse, or have already expired, are skipped.
func (s *Server) applyRevoked(list []revokedEntry, now time.Time) int {
	set := make(revokedSet, len(list))
	for i, e := range list {
		if i >= maxRevoked {
			break
		}
		if e.Exp <= now.Unix() || len(e.Key) != 2*protocol.PublicKeyLen {
			continue
		}
		raw, err := hex.DecodeString(strings.ToLower(e.Key))
		if err != nil || raw[0] != 0x04 {
			continue
		}
		var k revokedKey
		copy(k.device[:], raw)
		k.exp = e.Exp
		set[k] = struct{}{}
	}
	s.revoked.Store(&set)

	if len(set) == 0 {
		return 0
	}

	var cut []*session
	s.mu.RLock()
	for _, sess := range s.bySession {
		if s.isRevoked(sess.ident.deviceKey, sess.ident.expiry) {
			cut = append(cut, sess)
		}
	}
	s.mu.RUnlock()

	for _, sess := range cut {
		// The reservation goes too: this machine is not coming back on this token, and its address
		// is better spent on whoever connects next.
		s.releaseSession(sess, true)
		s.log.Info("session cut: the account signed in on another machine",
			"inner_ip", sess.innerIP.String(), "user", sess.ident.userID)
	}
	return len(cut)
}

// pruneRevoked drops entries whose token has expired anyway. Called from the janitor, so a relay
// that has lost touch with the licence server still sheds them.
func (s *Server) pruneRevoked(now time.Time) {
	set := s.revoked.Load()
	if set == nil || len(*set) == 0 {
		return
	}
	next := make(revokedSet, len(*set))
	for k := range *set {
		if k.exp > now.Unix() {
			next[k] = struct{}{}
		}
	}
	if len(next) != len(*set) {
		s.revoked.Store(&next)
	}
}
