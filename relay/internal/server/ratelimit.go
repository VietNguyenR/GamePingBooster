package server

import (
	"sync/atomic"
	"time"
)

// A token bucket, one per session per direction, sized so that a game never meets it.
//
// This exists because the pre-shared key is the same on every client: anyone who installs the
// software can use the relay as a free VPN, and the traffic leaves under this VPS's address. The
// limit does not stop that - only per-client credentials can - but it caps what one abuser can
// pull through, which is the difference between a nuisance and a terminated server.
//
// Three properties matter more than the numbers:
//
//  1. It DROPS, it never queues. Queuing would add latency to game packets, which is the one
//     thing this whole project exists to avoid. A dropped packet is what a congested network
//     does anyway, and the game's netcode already copes with it.
//
//  2. It is generous by a wide margin. A PUBG session measured on real hardware runs at about
//     62 packets and 10 KB per second; the default sustained rate is a hundred times that, and
//     the burst allowance absorbs anything a legitimate spike can produce. If this limit is ever
//     reached during a match, the settings are wrong, not the player.
//
//  3. It is loud. A session that gets limited says so in the log, once, and the count appears in
//     the stats line. A rate limiter that silently eats packets is indistinguishable from a bad
//     network - and this project has already spent enough time chasing invisible packet loss.
type bucket struct {
	// Both fields are only ever touched by one goroutine per direction (loopUDP for uplink,
	// loopTUN for downlink), so the atomics are not strictly required today. They are here
	// because "only one goroutine touches this" is exactly the kind of assumption that a later
	// change breaks quietly, and the cost on this path is a few nanoseconds.
	tokens   atomic.Int64 // bytes currently available
	lastFill atomic.Int64 // unix nanoseconds of the last refill

	limited atomic.Uint64 // packets dropped by this bucket
	warned  atomic.Bool   // whether the first drop has been logged
}

func newBucket(burst int64) *bucket {
	b := &bucket{}
	b.tokens.Store(burst)
	b.lastFill.Store(time.Now().UnixNano())
	return b
}

// allow reports whether a packet of n bytes may pass, refilling the bucket for the time elapsed
// since the last call. Rate of 0 disables the limit entirely.
func (b *bucket) allow(n int, ratePerSec, burst int64) bool {
	if ratePerSec <= 0 {
		return true
	}

	now := time.Now().UnixNano()
	last := b.lastFill.Swap(now)
	if elapsed := now - last; elapsed > 0 {
		// Integer arithmetic, deliberately: this runs on every packet, and a float conversion
		// here would be the most expensive thing in the data path.
		refill := ratePerSec * elapsed / int64(time.Second)
		if refill > 0 {
			tokens := b.tokens.Add(refill)
			if tokens > burst {
				b.tokens.Store(burst)
			}
		}
	}

	if b.tokens.Load() < int64(n) {
		b.limited.Add(1)
		return false
	}
	b.tokens.Add(-int64(n))
	return true
}

// shouldWarn reports true exactly once, the first time a bucket refuses a packet, so the log
// records that limiting started without repeating it thousands of times.
func (b *bucket) shouldWarn() bool {
	return b.warned.CompareAndSwap(false, true)
}
