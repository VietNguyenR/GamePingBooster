//go:build linux

// Package tun opens a Linux TUN device using raw ioctls (no cgo, no external
// dependencies). The relay writes IP packets into it and lets the kernel handle
// forwarding and NAT.
package tun

import (
	"fmt"
	"os"
	"os/exec"
	"strconv"
	"syscall"
	"unsafe"
)

const (
	cloneDevice = "/dev/net/tun"

	iffTun    = 0x0001
	iffNoPI   = 0x1000 // do not prepend the 4-byte packet-info field
	tunSetIff = 0x400454ca
)

// Device is an open TUN interface.
type Device struct {
	file *os.File
	name string
}

// ifReq mirrors the Linux ifreq struct: 16 bytes of name, 2 bytes of flags, padding.
type ifReq struct {
	Name  [16]byte
	Flags uint16
	_     [22]byte
}

// Open creates (or attaches to) the TUN interface called name. Requires root or
// CAP_NET_ADMIN.
func Open(name string) (*Device, error) {
	f, err := os.OpenFile(cloneDevice, os.O_RDWR, 0)
	if err != nil {
		return nil, fmt.Errorf("open %s: %w (must run as root, and the tun module must be loaded)", cloneDevice, err)
	}

	var req ifReq
	if len(name) >= len(req.Name) {
		f.Close()
		return nil, fmt.Errorf("interface name %q is too long", name)
	}
	copy(req.Name[:], name)
	req.Flags = iffTun | iffNoPI

	if _, _, errno := syscall.Syscall(
		syscall.SYS_IOCTL,
		f.Fd(),
		uintptr(tunSetIff),
		uintptr(unsafe.Pointer(&req)),
	); errno != 0 {
		f.Close()
		return nil, fmt.Errorf("ioctl TUNSETIFF %q: %w", name, errno)
	}

	// The kernel may hand back a different name if we passed something like "tun%d".
	actual := string(req.Name[:])
	if i := indexZero(req.Name[:]); i >= 0 {
		actual = string(req.Name[:i])
	}
	return &Device{file: f, name: actual}, nil
}

func indexZero(b []byte) int {
	for i, c := range b {
		if c == 0 {
			return i
		}
	}
	return -1
}

func (d *Device) Name() string                { return d.name }
func (d *Device) Read(p []byte) (int, error)  { return d.file.Read(p) }
func (d *Device) Write(p []byte) (int, error) { return d.file.Write(p) }
func (d *Device) Close() error                { return d.file.Close() }

// Configure assigns the address and MTU and brings the interface up via the `ip`
// command. It is separate from Open so it can be skipped when the interface is
// managed elsewhere (systemd, netplan).
func (d *Device) Configure(cidr string, mtu int) error {
	steps := [][]string{
		{"ip", "addr", "replace", cidr, "dev", d.name},
		{"ip", "link", "set", "dev", d.name, "mtu", strconv.Itoa(mtu)},
		{"ip", "link", "set", "dev", d.name, "up"},
	}
	for _, s := range steps {
		if out, err := exec.Command(s[0], s[1:]...).CombinedOutput(); err != nil {
			return fmt.Errorf("running %v: %w: %s", s, err, out)
		}
	}
	return nil
}
