package services

import (
	"bufio"
	"context"
	_ "embed"
	"encoding/json"
	"errors"
	"io"
	"net"
	"net/http"
	"net/url"
	"strings"
	"sync"
	"time"
)

// Abuse controls for self-serve signup (docs/design/saas-accounts.md, C5).

//go:embed disposable_domains.txt
var disposableDomainsRaw string

var (
	disposableOnce sync.Once
	disposableSet  map[string]struct{}
)

func disposableDomains() map[string]struct{} {
	disposableOnce.Do(func() {
		disposableSet = map[string]struct{}{}
		sc := bufio.NewScanner(strings.NewReader(disposableDomainsRaw))
		for sc.Scan() {
			line := strings.ToLower(strings.TrimSpace(sc.Text()))
			if line == "" || strings.HasPrefix(line, "#") {
				continue
			}
			disposableSet[line] = struct{}{}
		}
	})
	return disposableSet
}

// emailDomain returns the lower-cased part after the last '@' ("" if none).
func emailDomain(email string) string {
	i := strings.LastIndex(email, "@")
	if i < 0 {
		return ""
	}
	return strings.ToLower(strings.TrimSpace(strings.TrimRight(email[i+1:], ".")))
}

// IsDisposableEmail reports whether the address's domain, or any parent
// domain of it, is on the embedded disposable-email blocklist (so
// "x.mailinator.com" is caught when "mailinator.com" is listed).
func IsDisposableEmail(email string) bool {
	d := emailDomain(email)
	if d == "" {
		return false
	}
	set := disposableDomains()
	for {
		if _, ok := set[d]; ok {
			return true
		}
		i := strings.Index(d, ".")
		if i < 0 || !strings.Contains(d[i+1:], ".") {
			// Never test a bare TLD.
			return false
		}
		d = d[i+1:]
	}
}

// Resolver hooks, replaceable in tests.
var (
	lookupMX = func(ctx context.Context, domain string) ([]*net.MX, error) {
		return net.DefaultResolver.LookupMX(ctx, domain)
	}
	lookupHost = func(ctx context.Context, domain string) ([]string, error) {
		return net.DefaultResolver.LookupHost(ctx, domain)
	}
)

// ErrNoMailServer means the domain definitively cannot receive mail.
var ErrNoMailServer = errors.New("email domain cannot receive mail")

func isNotFound(err error) bool {
	var dnsErr *net.DNSError
	return errors.As(err, &dnsErr) && dnsErr.IsNotFound
}

// CheckMX verifies that the address's domain can receive mail. Only a definite
// "no such domain / no records" answer is a failure; a timeout or other
// resolver trouble allows the signup (an outage must not block everyone).
// A domain with no MX but an address record is accepted (RFC 5321 implicit MX).
func CheckMX(ctx context.Context, email string) error {
	d := emailDomain(email)
	if d == "" {
		return ErrNoMailServer
	}
	ctx, cancel := context.WithTimeout(ctx, 3*time.Second)
	defer cancel()
	mx, err := lookupMX(ctx, d)
	if len(mx) > 0 || (err != nil && !isNotFound(err)) {
		return nil
	}
	addrs, err := lookupHost(ctx, d)
	if len(addrs) > 0 || (err != nil && !isNotFound(err)) {
		return nil
	}
	return ErrNoMailServer
}

// TurnstileVerifyURL is Cloudflare's siteverify endpoint (replaceable in tests).
var TurnstileVerifyURL = "https://challenges.cloudflare.com/turnstile/v0/siteverify"

var turnstileClient = &http.Client{Timeout: 5 * time.Second}

// VerifyTurnstile validates a Cloudflare Turnstile token. With an empty secret
// the challenge is not configured and every request passes. A network failure
// reaching Cloudflare fails the check (the operator opted into a challenge).
func VerifyTurnstile(ctx context.Context, secret, token, remoteIP string) (bool, error) {
	if secret == "" {
		return true, nil
	}
	if strings.TrimSpace(token) == "" {
		return false, nil
	}
	form := url.Values{"secret": {secret}, "response": {token}}
	if remoteIP != "" {
		form.Set("remoteip", remoteIP)
	}
	req, err := http.NewRequestWithContext(ctx, http.MethodPost, TurnstileVerifyURL, strings.NewReader(form.Encode()))
	if err != nil {
		return false, err
	}
	req.Header.Set("Content-Type", "application/x-www-form-urlencoded")
	resp, err := turnstileClient.Do(req)
	if err != nil {
		return false, err
	}
	defer resp.Body.Close()
	if resp.StatusCode != http.StatusOK {
		return false, errors.New("turnstile verification returned " + resp.Status)
	}
	var out struct {
		Success bool `json:"success"`
	}
	if err := json.NewDecoder(io.LimitReader(resp.Body, 64<<10)).Decode(&out); err != nil {
		return false, err
	}
	return out.Success, nil
}
