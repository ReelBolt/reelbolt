using System.Net;
using System.Text;

namespace ReelBolt.Inference.Api.Services.FirstRunReminder;

/// <summary>The sender identity every mail carries (GDPR 13(1)(a), e-commerce directive art. 6). Blank name or address: no mail.</summary>
public sealed record FirstRunReminderIdentity(string ControllerName, string? VatNumber, string PostalAddress);

/// <summary>One mail to send. <see cref="RcptRef"/> is the only thing about the recipient that may be logged.</summary>
public sealed record FirstRunReminderMessage(
    string ToAddress,
    string RcptRef,
    string Subject,
    string Html,
    string Text,
    string? UnsubscribeUrl);

/// <summary>The EN and IT mail: one line, one link, no tracking pixel, no remote image, identity footer.</summary>
public static class FirstRunReminderMail
{
    public static string NormalizeLocale(string? locale)
        => locale is not null && locale.Trim().StartsWith("it", StringComparison.OrdinalIgnoreCase) ? "it" : "en";

    public static FirstRunReminderMessage Build(
        string toAddress,
        string rcptRef,
        string? locale,
        string runUrl,
        FirstRunReminderIdentity identity,
        string? unsubscribeUrl)
    {
        bool it = NormalizeLocale(locale) == "it";
        string subject = it ? "Il tuo materiale è pronto per il primo montaggio" : "Your footage is ready for its first cut";
        string line = it
            ? "Il tuo materiale è pronto: un clic per fare il primo montaggio."
            : "Your footage is ready: one click to make the first cut.";
        string cta = it ? "Apri il progetto" : "Open your project";
        string why = it
            ? "Ricevi questo messaggio una sola volta perché hai creato un progetto su ReelBolt e non lo hai ancora eseguito. Non ti scriveremo di nuovo per questo."
            : "You get this once because you created a project on ReelBolt and have not run it yet. We will not email you about this again.";
        string operated = it ? "ReelBolt è gestito da" : "ReelBolt is operated by";
        string unsub = it ? "Non voglio più ricevere email da ReelBolt" : "Stop emails from ReelBolt";

        string vat = string.IsNullOrWhiteSpace(identity.VatNumber) ? string.Empty : $", P.IVA {identity.VatNumber.Trim()}";
        string identityLine = $"{operated} {identity.ControllerName.Trim()}{vat}, {identity.PostalAddress.Trim()}";

        StringBuilder html = new();
        html.Append("<!doctype html><html><body style=\"font-family:sans-serif;color:#111\">")
            .Append("<p>").Append(WebUtility.HtmlEncode(line)).Append("</p>")
            .Append("<p><a href=\"").Append(WebUtility.HtmlEncode(runUrl)).Append("\">").Append(WebUtility.HtmlEncode(cta)).Append("</a></p>")
            .Append("<hr><p style=\"font-size:12px;color:#555\">").Append(WebUtility.HtmlEncode(why)).Append("<br>")
            .Append(WebUtility.HtmlEncode(identityLine));
        if (unsubscribeUrl is not null)
        {
            html.Append("<br><a href=\"").Append(WebUtility.HtmlEncode(unsubscribeUrl)).Append("\">").Append(WebUtility.HtmlEncode(unsub)).Append("</a>");
        }

        html.Append("</p></body></html>");

        StringBuilder text = new();
        text.Append(line).Append("\n\n").Append(cta).Append(": ").Append(runUrl).Append("\n\n-- \n").Append(why).Append('\n').Append(identityLine);
        if (unsubscribeUrl is not null)
        {
            text.Append('\n').Append(unsub).Append(": ").Append(unsubscribeUrl);
        }

        return new FirstRunReminderMessage(toAddress, rcptRef, subject, html.ToString(), text.ToString(), unsubscribeUrl);
    }
}
