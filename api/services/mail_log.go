package services

import (
	"crypto/hmac"
	"crypto/rand"
	"crypto/sha256"
	"encoding/hex"
	"errors"
	"fmt"
	"log"
	"net"
	"net/textproto"
	"os"
	"regexp"
	"strings"
	"sync"
)

// Contract C4: how a recipient and a mail error may appear in a log line. Never the address: a
// recipient is "rcpt=" plus the first 12 hex chars of HMAC-SHA256(key, CanonicalMailbox(addr)), the
// key being MAIL_LOG_REF_KEY (shared with the Inference API, so both services' lines correlate). An
// unkeyed hash of a low-entropy address is reversible by dictionary (F-4).

const envMailLogRefKey = "MAIL_LOG_REF_KEY"

var mailLogKey struct {
	once sync.Once
	key  []byte
}

// CanonicalMailbox is shared contract C1 (identical in the C# API): trim and lowercase; split at the
// last '@'; cut the local part at the first '+' unless that leaves it empty; for gmail.com and
// googlemail.com drop every '.' from the local part and use gmail.com. It is used for limit keys and
// log references only; mail still goes to the address the caller gave.
func CanonicalMailbox(raw string) string {
	s := strings.ToLower(strings.TrimSpace(raw))
	at := strings.LastIndex(s, "@")
	if at < 0 {
		return s
	}
	local, domain := s[:at], s[at+1:]
	if i := strings.Index(local, "+"); i > 0 {
		local = local[:i]
	}
	if domain == "gmail.com" || domain == "googlemail.com" {
		local = strings.ReplaceAll(local, ".", "")
		domain = "gmail.com"
	}
	return local + "@" + domain
}

// loadMailLogRefKey returns the configured key, or — when it is unset — a random 32-byte key for this
// process with one warning: references then correlate only within this process, but are never unkeyed.
func loadMailLogRefKey(getenv func(string) string, logf func(string, ...any)) []byte {
	if k := strings.TrimSpace(getenv(envMailLogRefKey)); k != "" {
		return []byte(k)
	}
	key := make([]byte, 32)
	if _, err := rand.Read(key); err != nil {
		// crypto/rand does not fail on supported platforms; a constant key would be unkeyed in effect.
		panic(fmt.Sprintf("mail log reference key: %v", err))
	}
	logf("[mail] WARNING: %s is not set; mail log references use a random per-process key and will not correlate across restarts, replicas or the Inference API", envMailLogRefKey)
	return key
}

// InitMailLogRef loads the log reference key now. main calls it at startup so the warning about an
// unset key appears there and once, not on the first failed mail.
func InitMailLogRef() {
	mailLogKey.once.Do(func() { mailLogKey.key = loadMailLogRefKey(os.Getenv, log.Printf) })
}

// MailLogRef is how a recipient appears in any log line: "rcpt=<12 hex>".
func MailLogRef(addr string) string {
	InitMailLogRef()
	return mailLogRefWithKey(mailLogKey.key, addr)
}

func mailLogRefWithKey(key []byte, addr string) string {
	mac := hmac.New(sha256.New, key)
	mac.Write([]byte(CanonicalMailbox(addr)))
	return "rcpt=" + hex.EncodeToString(mac.Sum(nil))[:12]
}

var (
	addressPattern  = regexp.MustCompile(`\S+@\S+`)
	enhancedPattern = regexp.MustCompile(`^\s*([245]\.\d{1,3}\.\d{1,3})\b`)
)

// SanitizeMailError renders a mail transport error for a log line. SMTP and network errors carry the
// recipient and the server's free text (`550 5.1.1 <victim@example.com>: user unknown`), so an SMTP
// reply is reduced to its status, enhanced code and class and a network error to its class and
// operation; any other error keeps its text with every \S+@\S+ redacted.
func SanitizeMailError(err error) string {
	if err == nil {
		return ""
	}
	var tpe *textproto.Error
	if errors.As(err, &tpe) {
		class := "smtp_transient"
		if tpe.Code >= 500 {
			class = "smtp_permanent"
		}
		out := fmt.Sprintf("class=%s status=%d", class, tpe.Code)
		if m := enhancedPattern.FindStringSubmatch(tpe.Msg); m != nil {
			out += " enhanced=" + m[1]
		}
		return out
	}
	var opErr *net.OpError
	if errors.As(err, &opErr) {
		return fmt.Sprintf("class=net op=%s timeout=%t", opErr.Op, opErr.Timeout())
	}
	var dnsErr *net.DNSError
	if errors.As(err, &dnsErr) {
		return fmt.Sprintf("class=dns timeout=%t not_found=%t", dnsErr.IsTimeout, dnsErr.IsNotFound)
	}
	return fmt.Sprintf("class=%T msg=%q", err, RedactAddresses(err.Error()))
}

// RedactAddresses replaces every \S+@\S+ in s.
func RedactAddresses(s string) string {
	return addressPattern.ReplaceAllString(s, "[redacted]")
}

// LogMailFailure is the one log line for a failed send: template, recipient reference and the
// sanitised error.
func LogMailFailure(what string, to string, err error) {
	log.Printf("mail: %s to %s failed: %s", what, MailLogRef(to), SanitizeMailError(err))
}
