package server

import (
	"testing"
	"time"
)

// Measured on real hardware: a PUBG session ran 73,252 packets in 1,187 seconds, which is about
// 62 packets and 10 KB per second. Every test here is written against those numbers rather than
// against round ones, because the only failure that matters is the limiter touching a real game.
const (
	gamePacketBytes = 160
	gamePacketsPerS = 62
	gameBytesPerS   = gamePacketBytes * gamePacketsPerS // ~10 KB/s

	defaultRate  = 512 * 1024 // the shipped default: 512 KB/s, fifty times what a game uses
	defaultBurst = defaultRate * 4
)

// The whole point of the limiter is that a game never meets it. Twenty minutes of gameplay at the
// measured rate must pass without a single packet being refused.
func TestGameTrafficIsNeverLimited(t *testing.T) {
	b := newBucket(defaultBurst)

	// Simulated rather than slept: twenty real minutes in a unit test is not a test anybody runs.
	// The bucket refills from the clock, so the loop advances it by hand.
	const minutes = 20
	for i := 0; i < minutes*60*gamePacketsPerS; i++ {
		if !b.allow(gamePacketBytes, defaultRate, defaultBurst) {
			t.Fatalf("a game packet was dropped after %d packets (%.1f seconds in)",
				i, float64(i)/gamePacketsPerS)
		}
		// One packet's worth of time at the game's rate.
		advance(b, time.Second/gamePacketsPerS, defaultRate, defaultBurst)
	}
	if got := b.limited.Load(); got != 0 {
		t.Errorf("%d packets were limited during pure game traffic", got)
	}
}

// A burst far beyond anything a game produces must still pass: the round starting, a hundred
// players spawning, whatever the engine does when it gets busy.
func TestLegitimateBurstPasses(t *testing.T) {
	b := newBucket(defaultBurst)

	// Fifty times the measured rate for two solid seconds.
	spike := gamePacketsPerS * 50 * 2
	for i := 0; i < spike; i++ {
		if !b.allow(gamePacketBytes, defaultRate, defaultBurst) {
			t.Fatalf("a burst of %d packets was cut off at %d - the burst allowance is too small",
				spike, i)
		}
	}
}

// And the reason it exists: somebody using the relay as a free VPN gets capped.
//
// Measured over ten seconds rather than one, on purpose. Over a single second the burst allowance
// is most of the budget, so a limiter that had no sustained rate at all would still look like it
// was working - the first version of this test passed while letting 9.2 MB of a 10 MB flood
// through. Ten seconds makes the sustained rate, not the burst, decide the answer.
func TestBulkTransferIsCapped(t *testing.T) {
	b := newBucket(defaultBurst)

	const full = 1400
	const seconds = 10
	const offeredBytesPerSec = 10 * 1024 * 1024 // ten times the limit
	steps := seconds * offeredBytesPerSec / full

	passed := 0
	for i := 0; i < steps; i++ {
		if b.allow(full, defaultRate, defaultBurst) {
			passed++
		}
		advance(b, time.Duration(seconds)*time.Second/time.Duration(steps), defaultRate, defaultBurst)
	}

	got := int64(passed * full)
	offered := int64(seconds * offeredBytesPerSec)

	// Everything the bucket may legitimately release in this window: the burst it started with,
	// plus the sustained rate for the whole time, plus one second of slack for edges.
	allowed := int64(defaultBurst) + int64(defaultRate)*(seconds+1)
	if got > allowed {
		t.Errorf("let through %d KB over %ds, more than burst+rate allows (%d KB)",
			got/1024, seconds, allowed/1024)
	}
	// And it has to actually be a limit, not a rounding error.
	if got > offered/4 {
		t.Errorf("let through %d KB of the %d KB offered - that is not a meaningful cap",
			got/1024, offered/1024)
	}
	if b.limited.Load() == 0 {
		t.Error("a sustained flood was not limited at all")
	}
	t.Logf("offered %d KB over %ds, passed %d KB (%.1f%%), dropped %d packets",
		offered/1024, seconds, got/1024, float64(got)*100/float64(offered), b.limited.Load())
}

// Zero means off, and off has to mean genuinely off - no accounting, no surprises.
func TestZeroRateDisablesTheLimit(t *testing.T) {
	b := newBucket(0)
	for i := 0; i < 100000; i++ {
		if !b.allow(1400, 0, 0) {
			t.Fatal("packets were dropped with the limit disabled")
		}
	}
	if b.limited.Load() != 0 {
		t.Error("the disabled limiter counted drops")
	}
}

// The first refusal has to reach the log exactly once: silence would make this indistinguishable
// from network loss, and repeating it would bury everything else.
func TestWarnsOnceOnly(t *testing.T) {
	b := newBucket(0)
	if !b.shouldWarn() {
		t.Fatal("the first refusal did not warn")
	}
	for i := 0; i < 100; i++ {
		if b.shouldWarn() {
			t.Fatal("warned more than once")
		}
	}
}

// advance moves the bucket's clock forward without sleeping, so the tests run in milliseconds.
func advance(b *bucket, d time.Duration, rate, burst int64) {
	b.lastFill.Add(-int64(d))
}
