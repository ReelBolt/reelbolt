package services

import (
	"io/fs"
	"regexp"
	"sort"
	"strings"
	"testing"
	"time"
)

var placeholderRE = regexp.MustCompile(`\{\{-?\s*(?:if|with|range|else if)?\s*(\.[A-Za-z]+)`)

func placeholders(t *testing.T, path string) []string {
	t.Helper()
	b, err := fs.ReadFile(emailTemplateFS, path)
	if err != nil {
		t.Fatalf("read %s: %v", path, err)
	}
	set := map[string]bool{}
	for _, m := range placeholderRE.FindAllStringSubmatch(string(b), -1) {
		set[m[1]] = true
	}
	var out []string
	for k := range set {
		out = append(out, k)
	}
	sort.Strings(out)
	return out
}

func TestEveryLocaleHasEveryTemplateSubjectAndSamePlaceholders(t *testing.T) {
	for _, loc := range SupportedLocales() {
		if _, ok := subjectsData.FooterNote[loc]; !ok && loc != DefaultLocale {
			t.Errorf("%s: no footerNote", loc)
		}
		if _, ok := subjectsData.FallbackName[loc]; !ok && loc != DefaultLocale {
			t.Errorf("%s: no fallbackName", loc)
		}
		for _, name := range AllTemplates {
			if loc != DefaultLocale && name == TemplateOperatorDigest {
				continue // the operator digest is English only: the operator reads it, not a customer
			}
			if mailSubjects[loc][string(name)] == nil {
				t.Errorf("%s: no subject for %s", loc, name)
			}
			for _, ext := range []string{"html", "txt"} {
				p := "email_templates/" + loc + "/" + string(name) + "." + ext
				if _, err := fs.Stat(emailTemplateFS, p); err != nil {
					t.Errorf("missing %s", p)
					continue
				}
				en := placeholders(t, "email_templates/en/"+string(name)+"."+ext)
				got := placeholders(t, p)
				if strings.Join(en, ",") != strings.Join(got, ",") {
					t.Errorf("%s placeholders differ from en: en=%v %s=%v", p, en, loc, got)
				}
			}
		}
	}
}

func renderLoc(t *testing.T, tmpl TemplateName, loc string, mut func(*MailData)) RenderedMail {
	t.Helper()
	r := newRenderer(MailerConfig{PublicBaseURL: "https://reelbolt.ai"})
	d := testData()
	d.Locale = loc
	if mut != nil {
		mut(&d)
	}
	out, err := r.Render("ada@example.com", tmpl, d)
	if err != nil {
		t.Fatalf("render %s/%s: %v", loc, tmpl, err)
	}
	return out
}

func TestMailsAreLocalized(t *testing.T) {
	cases := []struct {
		tmpl           TemplateName
		enSubj, itSubj string
		itText, enText string
		itHTML         string
	}{
		{TemplateResetPassword, "Reset your ReelBolt password", "Reimposta la password di ReelBolt", "scade tra 1 ora", "expires in 1 hour", "Scegli una nuova password"},
		{TemplateInvite, "Grace invited you to Acme Studio on ReelBolt", "Grace ti ha invitato in Acme Studio su ReelBolt", "Accetta l'invito", "Accept the invitation", "Accetta l'invito"},
		{TemplateVerifyEmail, "Confirm your ReelBolt email", "Conferma la tua email di ReelBolt", "Conferma il tuo indirizzo email", "Confirm your email address", "Conferma email"},
	}
	for _, c := range cases {
		mut := func(d *MailData) { d.ExpiresIn = "1 hour" }
		en := renderLoc(t, c.tmpl, "en", mut)
		it := renderLoc(t, c.tmpl, "it", mut)
		if en.Subject != c.enSubj || it.Subject != c.itSubj {
			t.Errorf("%s subjects en=%q it=%q", c.tmpl, en.Subject, it.Subject)
		}
		if !strings.Contains(it.Text, c.itText) || !strings.Contains(en.Text, c.enText) {
			t.Errorf("%s text not localized:\n%s", c.tmpl, it.Text)
		}
		if !strings.Contains(it.HTML, c.itHTML) || !strings.Contains(it.HTML, `lang="it"`) {
			t.Errorf("%s html not localized", c.tmpl)
		}
		if !strings.Contains(it.Text, "0.4.0-alpha") || !strings.Contains(it.Text, "alpha pubblica") {
			t.Errorf("%s italian footer missing", c.tmpl)
		}
		if !strings.Contains(it.Text, "https://reelbolt.ai/app/verify") {
			t.Errorf("%s link lost", c.tmpl)
		}
	}
}

func TestUnknownOrHostileLocaleFallsBackToEnglish(t *testing.T) {
	for _, loc := range []string{"", "xx", "../etc", "..", "it/../../x", "it\x00", "IT/en", "email_templates/it", strings.Repeat("it", 300)} {
		out := renderLoc(t, TemplateWelcome, loc, nil)
		if out.Subject != "Welcome to ReelBolt" || !strings.Contains(out.Text, "Your ReelBolt account has been created.") {
			t.Errorf("locale %q did not fall back to English: %q", loc, out.Subject)
		}
	}
	// Region and case are normalised, not rejected.
	if out := renderLoc(t, TemplateWelcome, "IT-it", nil); out.Subject != "Ti diamo il benvenuto su ReelBolt" {
		t.Errorf("IT-it subject = %q", out.Subject)
	}
}

func TestMissingTemplateInLocaleFallsBackToEnglish(t *testing.T) {
	r := newRenderer(MailerConfig{})
	h, tx := r.pick("it", TemplateName("nope"))
	if h != r.sets["en"].html || tx != r.sets["en"].text {
		t.Error("an unknown template must resolve to the default locale set")
	}
}

func TestDeletionDateAndDurationLocalized(t *testing.T) {
	d := time.Date(2026, 11, 4, 0, 0, 0, 0, time.UTC)
	if got := FormatMailDate(d, "it"); got != "4 novembre 2026" {
		t.Errorf("it date = %q", got)
	}
	if got := FormatMailDate(d, "en"); got != "4 November 2026" {
		t.Errorf("en date = %q", got)
	}
	for in, want := range map[string]string{"24 hours": "24 ore", "1 hour": "1 ora", "7 days": "7 giorni", "15 minutes": "15 minuti", "soon": "soon"} {
		if got := localizeDuration(in, "it"); got != want {
			t.Errorf("localizeDuration(%q) = %q want %q", in, got, want)
		}
	}
}

func TestItalianGreetingWithoutNameAndRole(t *testing.T) {
	noName := renderLoc(t, TemplateVerifyEmail, "it", func(d *MailData) { d.Name = "" })
	if !strings.HasPrefix(noName.Text, "Ciao,\n") || strings.Contains(noName.Text, "utente") {
		t.Errorf("greeting without a name = %q", strings.SplitN(noName.Text, "\n", 2)[0])
	}
	named := renderLoc(t, TemplateVerifyEmail, "it", func(d *MailData) { d.Name = "Giulia" })
	if !strings.HasPrefix(named.Text, "Ciao Giulia,\n") {
		t.Errorf("named greeting = %q", strings.SplitN(named.Text, "\n", 2)[0])
	}
	if en := renderLoc(t, TemplateVerifyEmail, "en", func(d *MailData) { d.Name = "" }); !strings.HasPrefix(en.Text, "Hello there,") {
		t.Errorf("english greeting changed: %q", strings.SplitN(en.Text, "\n", 2)[0])
	}
	inv := renderLoc(t, TemplateInvite, "it", func(d *MailData) { d.Role = "Admin" })
	if !strings.Contains(inv.Text, "come amministratore") {
		t.Errorf("role not localized:\n%s", inv.Text)
	}
	for in, want := range map[string]string{"Owner": "proprietario", "Member": "membro", "Billing": "referente per la fatturazione", "Custom": "Custom"} {
		if got := localizeRole(in, "it"); got != want {
			t.Errorf("localizeRole(%q) = %q want %q", in, got, want)
		}
	}
	if got := localizeRole("Owner", "en"); got != "Owner" {
		t.Errorf("english role changed: %q", got)
	}
}

func TestItalianDeletionNoticeReadsNaturally(t *testing.T) {
	m := renderLoc(t, TemplateDeletionSchedule, "it", func(d *MailData) { d.DeletionDate = "8 novembre 2026" })
	if !strings.Contains(m.Text, "verrà eliminato il giorno 8 novembre 2026") {
		t.Errorf("date wording:\n%s", m.Text)
	}
	if strings.Contains(strings.ToLower(m.Text), "workflow") {
		t.Errorf("untranslated 'workflow' left in the Italian notice")
	}
	if got := renderLoc(t, TemplateWelcome, "it", nil).Subject; got != "Ti diamo il benvenuto su ReelBolt" {
		t.Errorf("welcome subject = %q", got)
	}
}
