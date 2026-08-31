//go:build !linux

// Stub so that `go build ./...` works on a Windows or macOS dev machine.
// The relay only supports Linux in production.
package tun

import "errors"

var errUnsupported = errors.New("TUN devices are only supported on Linux")

type Device struct{}

func Open(string) (*Device, error)            { return nil, errUnsupported }
func (d *Device) Name() string                { return "" }
func (d *Device) Read([]byte) (int, error)    { return 0, errUnsupported }
func (d *Device) Write([]byte) (int, error)   { return 0, errUnsupported }
func (d *Device) Close() error                { return nil }
func (d *Device) Configure(string, int) error { return errUnsupported }
