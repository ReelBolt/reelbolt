using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace ReelBolt.Inference.Api.Services.Assistant;

/// <summary>
/// What <see cref="AssistantInputGuard.Screen"/> decided about one user message: either let it reach the
/// model (<see cref="Allow"/>) or answer it with a fixed, friendly reply and never call the model
/// (<see cref="Refuse"/>).
/// </summary>
public sealed class AssistantInputVerdict
{
    private AssistantInputVerdict(bool isRefused, string? reply)
    {
        IsRefused = isRefused;
        Reply = reply;
    }

    /// <summary>The message may go to the model.</summary>
    public static AssistantInputVerdict Allow { get; } = new(false, null);

    /// <summary>The message must not go to the model; answer with <paramref name="reply"/> instead.</summary>
    public static AssistantInputVerdict Refuse(string reply)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reply);
        return new AssistantInputVerdict(true, reply);
    }

    /// <summary>True when the caller should answer with <see cref="Reply"/> and skip the model.</summary>
    public bool IsRefused { get; }

    /// <summary>The canned answer when <see cref="IsRefused"/>; <c>null</c> otherwise.</summary>
    public string? Reply { get; }
}

/// <summary>
/// A deterministic first line of defence in front of the assistant: it recognises the CLEAR-CUT attempts to
/// pull out the assistant's instructions, tool definitions or secrets, or to talk it out of its rules, and
/// answers them with a fixed reply without spending a model call.
/// </summary>
/// <remarks>
/// <para>This is deliberately <b>narrow</b>. It only fires on unmistakable phrasings ("ignore all previous
/// instructions", "print your system prompt", "list your tools with their parameters", "enable developer mode").
/// Everything subtler — and every legitimate question that merely contains a word like "system", "prompt",
/// "instructions" or "ignore" — passes through to the model, whose own prompt carries the same boundaries.
/// A false positive costs a user one friendly sentence; a false negative costs nothing the model's prompt does
/// not already guard, so the patterns err on the side of letting people through.</para>
/// <para>It is not the confidentiality boundary: the prompt and the reply sanitizer are. It only saves the
/// obvious attacks a round trip and keeps them out of the persisted thread's model context.</para>
/// <para>Every pattern carries a hard match timeout; a timeout allows the message (the model still guards
/// it). Input is normalised first (compatibility forms, accents, zero-width characters, curly quotes and
/// runs of whitespace) so the cheapest evasions do not work.</para>
/// </remarks>
public static class AssistantInputGuard
{
    /// <summary>Messages longer than this are only screened over their first and last stretch.</summary>
    internal const int MaxScreenedChars = 8_000;

    private static readonly TimeSpan MatchTimeout = TimeSpan.FromMilliseconds(200);

    private const RegexOptions Options =
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;

    private sealed record Pattern(string Language, Regex Regex);

    /// <summary>
    /// One verb-class-noun-phrase building block, reused across languages' patterns. <c>[^.!?\n]</c> keeps a
    /// match inside one sentence so a verb in one sentence never pairs with a noun two sentences later.
    /// </summary>
    private const string Near = @"[^.!?\n]";

    private static readonly Pattern[] Patterns =
    [
        // ---- English: override ("ignore all previous instructions") ----
        // "my" between the verb and the noun means the user is talking about THEIR OWN earlier instructions
        // ("ignore my previous instructions about captions"), which is an ordinary correction.
        En($@"\b(?:ignore|disregard|forget|override|bypass|discard)\b(?:(?!\bmy\b){Near}){{0,25}}?\b(?:previous|prior|above|earlier|preceding|original|initial|system|your)\s+(?:instructions?|prompts?|rules|directions|guidelines|programming|constraints|commands|restrictions|safeguards|training)\b"),
        En(@"\bforget\s+(?:everything|all)\s+(?:you\s+(?:were|have\s+been)\s+(?:told|taught|given)|above|your\s+(?:instructions|rules|training))\b"),

        // ---- English: personas and modes ----
        En(@"\b(?:enable|enter|activate|switch\s+to|turn\s+on|you\s+are\s+now\s+in)\s+(?:the\s+)?(?:developer|dev|god|jailbreak(?:en)?|sudo|unrestricted|debug)\s+mode\b"),
        En(@"\b(?:you\s+are|you're|act\s+as|acting\s+as|pretend\s+(?:to\s+be|you\s+are)|role-?play\s+as|become|from\s+now\s+on\s+you\s+are|called)\s+(?:now\s+)?(?-i:DAN)\b"),
        En(@"\bdo\s+anything\s+now\b"),
        En(@"\bjailbreak(?:\s+(?:prompt|mode|yourself|you)\b|ing\s+(?:you|yourself|the\s+(?:ai|assistant|bot))\b)"),
        En(@"\b(?:you\s+are|you're)\s+(?:now\s+)?jailbroken\b"),
        En(@"\b(?:unrestricted|uncensored|unfiltered|unaligned)\s+(?:ai|assistant|mode|model|version|chatbot|bot)\b"),
        En($@"\b(?:pretend|imagine|act\s+as\s+if|assume)\b{Near}{{0,30}}\b(?:you\s+have\s+no|you\s+are\s+free\s+of|you\s+don't\s+have\s+any|there\s+are\s+no)\s+(?:rules|restrictions|limits|limitations|filters|guidelines|instructions)\b"),

        // ---- English: reveal / repeat / transform the instructions ----
        // "your" (or "the assistant's") is required for the ordinary nouns so "show me the instructions of my
        // agent" and "write a system prompt for my agent" pass. "What are your rules?" is a question the model
        // answers (by declining), so only the plainest prompt/instruction nouns accept the question verbs.
        En($@"\b(?:print|show|display|reveal|repeat|recite|output|dump|leak|share|tell\s+me|give\s+me|send\s+me|paste|copy|write\s+out|spit\s+out|expose|read\s+back|disclose|translate|summari[sz]e|paraphrase|encode|encrypt|base64|rot13|decode|what\s+(?:is|are|were)|list)\b{Near}{{0,40}}?\b(?:your|the\s+assistant'?s|this\s+(?:ai|assistant|bot|chatbot)'?s)\s+(?:(?:full|complete|entire|exact|original|initial|hidden|secret|internal|underlying|whole|raw|verbatim|system|developer)\s+){{0,3}}(?:prompts?|instructions?|pre-?prompt)\b"),
        En($@"\b(?:print|show|display|reveal|repeat|recite|output|dump|leak|paste|copy|expose|disclose|translate|summari[sz]e|paraphrase|encode|encrypt|base64|rot13|decode)\b{Near}{{0,40}}?\b(?:your|the\s+assistant'?s|this\s+(?:ai|assistant|bot|chatbot)'?s)\s+(?:(?:full|complete|entire|exact|original|initial|hidden|secret|internal|underlying|whole|raw|verbatim|system|developer)\s+){{0,3}}(?:rules|guidelines|programming|directives|configuration)\b"),
        // The unmistakable nouns ("system prompt", "hidden instructions") need no "your", but they must follow the
        // verb directly (an optional "me" and determiner between), so "show me my agent's system prompt" passes.
        En(@"\b(?:print|show|display|reveal|repeat|recite|output|dump|leak|share|tell|give|send|paste|copy|expose|disclose|translate|summari[sz]e|paraphrase|encode|encrypt|base64|rot13|decode|what\s+(?:is|are|were))\s+(?:(?:me|us|out)\s+)?(?:(?:the|your|this|that|all\s+of\s+the|all\s+the)\s+)?(?:(?:full|complete|entire|exact|original|raw|verbatim)\s+)?(?:system\s+(?:prompt|message|instructions?)|hidden\s+(?:prompt|instructions?)|secret\s+instructions?|initial\s+prompt|developer\s+(?:message|prompt)|pre-?prompt)\b"),
        En($@"\b(?:repeat|print|output|show|copy|write|echo|recite)\b{Near}{{0,30}}?\b(?:everything|all|the\s+text|the\s+words|the\s+content|the\s+message)\s+(?:above|before)\s+(?:this|my|the\s+first|the\s+very\s+first)\b"),
        En($@"\b(?:repeat|print|output|show|copy|write|echo|recite)\b{Near}{{0,60}}?\b(?:starting|beginning)\s+with\s+[""'“‘`]?\s*you\s+are\b"),
        En($@"\bwhat\s+(?:were|are)\s+(?:the\s+)?(?:initial|original|first)\s+instructions\s+(?:you\s+were\s+given|given\s+to\s+you)\b"),

        // ---- English: tool definitions ----
        En($@"\b(?:list|show|print|dump|give|reveal|display|output|tell\s+me|what\s+are)\b{Near}{{0,40}}?\b(?:your|the\s+assistant'?s)\s+(?:(?:available|internal|full|complete|exact|raw|hidden)\s+){{0,2}}(?:tools?|functions?|function\s+calls?|tool\s+calls?)\b{Near}{{0,60}}?\b(?:parameters?|arguments?|schemas?|signatures?|json|definitions?|specs?|names?)\b"),
        En(@"\b(?:json|schema|signature|definition|spec)s?\s+(?:of|for)\s+(?:all\s+)?(?:your|the\s+assistant'?s)\s+(?:tools?|functions?)\b"),

        // ---- English: secrets ----
        En($@"\b(?:reveal|show|print|dump|leak|give|tell|share|send|display|output|read|list|what(?:'s|\s+is|\s+are))\b{Near}{{0,40}}?\b(?:your|the\s+(?:system|server|platform|backend|database|master|signing|jwt|encryption|internal|admin|root)(?:'s)?)\s+(?:\w+\s+){{0,2}}?(?:api\s*keys?|secrets?|tokens?|passwords?|credentials|private\s+keys?|signing\s+keys?|keys?)\b"),
        En($@"\b(?:show|print|dump|reveal|read|list|display|output|give)\b{Near}{{0,30}}?\b(?:environment\s+variables?|env\s+vars?|\.env\b|connection\s+strings?|appsettings)"),

        // ---- Italian ----
        It($@"\b(?:ignora|ignorare|dimentica|dimenticare|trascura|scarta)\b(?:(?!\b(?:mie|miei|mio)\b){Near}){{0,25}}?\b(?:istruzioni|regole|direttive)\s+(?:precedenti|passate|iniziali|originali|di\s+sistema)\b"),
        It($@"\b(?:ignora|ignorare|dimentica|dimenticare)\b{Near}{{0,15}}?\b(?:le\s+tue|tutte\s+le\s+tue)\s+(?:istruzioni|regole|direttive)\b"),
        It($@"\b(?:mostra|mostrami|stampa|rivela|rivelami|ripeti|dimmi|scrivi|traduci|elenca|fammi\s+vedere)\b{Near}{{0,40}}?\b(?:il\s+tuo\s+prompt|il\s+prompt\s+di\s+sistema|prompt\s+di\s+sistema|le\s+tue\s+istruzioni|le\s+tue\s+regole|le\s+istruzioni\s+(?:iniziali|nascoste|di\s+sistema))\b"),

        // ---- Spanish ----
        Es($@"\b(?:ignora|olvida|descarta|omite)\b(?:(?!\b(?:mis|mi)\b){Near}){{0,25}}?\b(?:instrucciones|reglas|directrices)\s+(?:anteriores|previas|iniciales|originales|del\s+sistema)\b"),
        Es($@"\b(?:muestra|muestrame|imprime|revela|repite|dime|traduce|enumera)\b{Near}{{0,40}}?\b(?:tu\s+prompt|el\s+prompt\s+del\s+sistema|prompt\s+del\s+sistema|tus\s+instrucciones|tus\s+reglas|las\s+instrucciones\s+(?:iniciales|ocultas|del\s+sistema))\b"),

        // ---- French ----
        Fr($@"\b(?:ignore|ignorez|oublie|oubliez|neglige|negligez)\b(?:(?!\b(?:mes|mon|ma)\b){Near}){{0,25}}?\b(?:instructions|regles|consignes|directives)\s+(?:precedentes|anterieures|initiales|originales|du\s+systeme)\b"),
        Fr($@"\b(?:montre|montrez|affiche|affichez|revele|revelez|repete|repetez|donne|donnez|traduis|traduisez)\b{Near}{{0,40}}?\b(?:ton\s+prompt|le\s+prompt\s+systeme|prompt\s+systeme|tes\s+instructions|tes\s+regles|vos\s+instructions|les\s+instructions\s+(?:initiales|cachees|du\s+systeme))\b"),

        // ---- German ----
        De($@"\b(?:ignorier\w*|vergiss\w*)\b(?:(?!\b(?:meine|meinen|mein)\b){Near}){{0,25}}?\b(?:vorherigen|bisherigen|obigen|fruheren|ursprunglichen|anfanglichen|vorigen)\s+(?:anweisungen|instruktionen|regeln|vorgaben)\b"),
        De($@"\b(?:zeig|zeige|zeigen|nenne|gib|wiederhole|verrate|ubersetze|drucke)\b{Near}{{0,40}}?\b(?:deinen\s+(?:system-?prompt|prompt)|deine\s+(?:anweisungen|instruktionen|regeln)|den\s+system-?prompt)\b"),
    ];

    private static readonly IReadOnlyDictionary<string, string> Replies = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["en"] = "I can't share or change how I work behind the scenes, and I can't give out keys, tokens or other secrets. "
                 + "I'm happy to help with your ReelBolt work, though: I can set up and run a workflow, cut and caption a video, "
                 + "edit a picture or tidy up your projects. What would you like to do? (If you meant to correct something you "
                 + "asked earlier, just tell me what you'd like instead.)",
        ["it"] = "Non posso condividere né cambiare come funziono dietro le quinte, e non posso fornire chiavi, token o altri segreti. "
                 + "Posso però aiutarti con il tuo lavoro su ReelBolt: impostare ed eseguire un workflow, montare e sottotitolare un video, "
                 + "modificare un'immagine o riordinare i tuoi progetti. Cosa vuoi fare? (Se volevi correggere qualcosa che hai chiesto "
                 + "prima, dimmi pure cosa preferisci.)",
        ["es"] = "No puedo compartir ni cambiar cómo funciono entre bastidores, ni puedo dar claves, tokens u otros secretos. "
                 + "Sí puedo ayudarte con tu trabajo en ReelBolt: preparar y ejecutar un workflow, montar y subtitular un vídeo, "
                 + "editar una imagen u ordenar tus proyectos. ¿Qué te gustaría hacer? (Si querías corregir algo que pediste antes, "
                 + "dime qué prefieres.)",
        ["fr"] = "Je ne peux ni partager ni modifier mon fonctionnement interne, ni donner de clés, de jetons ou d'autres secrets. "
                 + "En revanche, je peux vous aider dans votre travail sur ReelBolt : préparer et lancer un workflow, monter et sous-titrer "
                 + "une vidéo, retoucher une image ou ranger vos projets. Que souhaitez-vous faire ? (Si vous vouliez corriger une demande "
                 + "précédente, dites-moi simplement ce que vous préférez.)",
        ["de"] = "Ich kann nicht offenlegen oder ändern, wie ich im Hintergrund arbeite, und ich gebe keine Schlüssel, Tokens oder "
                 + "andere Geheimnisse weiter. Bei deiner Arbeit mit ReelBolt helfe ich aber gern: einen Workflow einrichten und starten, "
                 + "ein Video schneiden und untertiteln, ein Bild bearbeiten oder deine Projekte aufräumen. Was möchtest du tun? "
                 + "(Wenn du eine frühere Anweisung korrigieren wolltest, sag mir einfach, was du stattdessen möchtest.)"
    };

    /// <summary>
    /// Decides whether <paramref name="message"/> is a clear-cut extraction or jailbreak attempt. Never throws;
    /// anything not recognised — including a null or empty message — is <see cref="AssistantInputVerdict.Allow"/>.
    /// </summary>
    public static AssistantInputVerdict Screen(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
            return AssistantInputVerdict.Allow;

        string text;
        try
        {
            text = Normalize(message);
        }
        catch (ArgumentException)
        {
            // Malformed Unicode that cannot be normalised: let the model deal with it.
            return AssistantInputVerdict.Allow;
        }

        foreach (Pattern pattern in Patterns)
        {
            try
            {
                if (pattern.Regex.IsMatch(text))
                    return AssistantInputVerdict.Refuse(Replies[pattern.Language]);
            }
            catch (RegexMatchTimeoutException)
            {
                // A pathological input must never cost the turn; the model's own boundaries still apply.
            }
        }

        return AssistantInputVerdict.Allow;
    }

    /// <summary>
    /// Lower-risk canonical form for matching: compatibility-composed, accent-free, with zero-width and other
    /// invisible format characters removed, curly quotes straightened and whitespace collapsed.
    /// </summary>
    internal static string Normalize(string message)
    {
        string text = message;
        if (text.Length > MaxScreenedChars)
            text = text[..(MaxScreenedChars / 2)] + "\n" + text[^(MaxScreenedChars / 2)..];

        text = text.Normalize(NormalizationForm.FormKD);

        var sb = new StringBuilder(text.Length);
        bool lastWasSpace = false;
        foreach (char c in text)
        {
            UnicodeCategory category = CharUnicodeInfo.GetUnicodeCategory(c);
            if (category is UnicodeCategory.NonSpacingMark or UnicodeCategory.Format)
                continue; // accents (after decomposition) and zero-width joiners / BOM / soft hyphens

            char mapped = c switch
            {
                '‘' or '’' or 'ʼ' => '\'',
                '“' or '”' => '"',
                _ => c
            };

            if (char.IsWhiteSpace(mapped) && mapped != '\n')
            {
                if (!lastWasSpace)
                    sb.Append(' ');
                lastWasSpace = true;
                continue;
            }

            lastWasSpace = false;
            sb.Append(mapped);
        }

        return sb.ToString();
    }

    private static Pattern En(string pattern) => Make("en", pattern);
    private static Pattern It(string pattern) => Make("it", pattern);
    private static Pattern Es(string pattern) => Make("es", pattern);
    private static Pattern Fr(string pattern) => Make("fr", pattern);
    private static Pattern De(string pattern) => Make("de", pattern);

    private static Pattern Make(string language, string pattern) =>
        new(language, new Regex(pattern, Options, MatchTimeout));
}
