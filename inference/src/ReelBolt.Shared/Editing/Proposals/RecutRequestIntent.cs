using System.Text.RegularExpressions;

namespace ReelBolt.Shared.Editing.Proposals;

/// <summary>What kind of edit the editor's words ask the Re-cut for.</summary>
public enum RecutOpClass
{
    Cut,
    Mute,
    Trim,
    Reorder
}

/// <summary>What the editor wants acted on, when the request names content rather than a clip.</summary>
public enum RecutTarget
{
    Speech,
    Silence,
    Fillers,
    KeepOnly
}

/// <summary>
/// Reads the editor's own request (brief) into an operation class and a content target with fixed English and Italian keyword
/// tables. Deterministic and local: the model never decides what was asked, so the compiler can refuse an answer that swaps
/// the asked operation for another one (for example a mute when the editor asked to cut).
/// </summary>
public static class RecutRequestIntent
{
    // Every pattern has a match timeout; a timeout is caught at the public entry points and read as the fail-closed answer.
    private static readonly TimeSpan Timeout = TimeSpan.FromMilliseconds(100);

    private const RegexOptions Opts = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;

    private static readonly Regex Mute = new(
        @"\bmute\b|\bmuted\b|(?<!\b(?:the|a|any|all|of|no)\s)\bsilence\s+(?:the|this|that|my|all|it|them)\b|\bturn\s+off\s+the\s+(?:audio|sound)\b|\bmuta(?:re|ci|lo|la)?\b|\bmuto\b|\bdisattiva(?:re)?\b|\bzittisci\b|\bmetti\s+in\s+muto\b",
        Opts, Timeout);

    private static readonly Regex Trim = new(
        @"\btrim\b|\bshorten\b|\bshorter\b|\baccorcia(?:re)?\b|\babbrevia(?:re)?\b",
        Opts, Timeout);

    private static readonly Regex Reorder = new(
        @"\breorder\b|\bre-order\b|\brearrange\b|\bmove\b|\bswap\b|\bswitch\s+the\b|\briordina(?:re)?\b|\bsposta(?:re)?\b|\bscambia(?:re)?\b|\binverti\b",
        Opts, Timeout);

    private static readonly Regex CutVerb = new(
        @"\bcut\b|\bcutting\b|\bremove\b|\bdelete\b|\bdrop\b|\bget\s+rid\s+of\b|\btake\s+out\b|\bstrip\b|\bcrop\s+out\b|\btaglia(?:re|mi|le|rli)?\b|\belimina(?:re)?\b|\brimuovi\b|\brimuovere\b|\btogli(?:ere)?\b|\bcancella(?:re)?\b",
        Opts, Timeout);

    // "Don't remove anything": a negated cut verb is not a cut.
    private static readonly Regex NegatedCut = new(
        @"\b(?:don'?t|do\s+not|never)\s+(?:remove|cut|delete|drop|take\s+out|get\s+rid\s+of)\b|\bnon\s+(?:togliere|tagliare|eliminare|rimuovere|cancellare)\b",
        Opts, Timeout);

    private static readonly Regex KeepOnly = new(
        @"\bkeep\s+only\b|\bonly\s+keep\b|\bjust\s+keep\b|\bkeep\s+just\b|\bsolo\s+le\s+parti\b|\btieni\s+(?:solo|soltanto)\b|\bmantieni\s+(?:solo|soltanto)\b|\blascia\s+(?:solo|soltanto)\b|\btieni\s+le\s+sole\b",
        Opts, Timeout);

    private static readonly Regex Fillers = new(
        @"\bums?\b|\buhs?\b|\buhm+\b|\bumm+s?\b|\bfillers?\b|\bfiller\s+words?\b|\bhesitations?\b|\bintercalari?\b|\behm+\b|\beh+\b|\bmh+\b",
        Opts, Timeout);

    private static readonly Regex Silence = new(
        @"\bsilences\b|(?<=\b(?:the|a|any|all|of|no)\s)\bsilence\b|\bsilence\b(?!\s+(?:the|this|that|my|all|it|them)\b)|\bpauses?\b|\bdead\s+air\b|\bquiet\s+(?:parts?|bits?|moments?|gaps?|sections?)\b|\bsilenzi\b|\bsilenzio\b|\bpausa\b|\bmomenti\s+morti\b|\btempi\s+morti\b|\bsilent\b|\bgaps?\b|\bsilenzios[ei]\b|\bsilenziose?\b",
        Opts, Timeout);

    private static readonly Regex Speech = new(
        @"\bspoken\b|\bspeech\b|\bwords?\b|\btalking\b|\btalk\b|\bdialogue\b|\bdialog\b|\bvoices?\b|\bspeaking\b|\bparlato\b|\bparole?\b|\bvoce\b|\bvoci\b|\bdialoghi?\b|\bdiscorso\b|\bparlo\b|\bpronunciat[ae]\b|\bparlat[aeio]\b",
        Opts, Timeout);

    private static readonly Regex Negated = new(
        @"\b(?:nobody|no\s*one|no-one)\s+(?:is\s+|are\s+)?(?:talking|speaking|saying|talks?|speaks?|says?)\b|\bno\s+(?:speech|talking|dialogue|dialog|voice)\b|\bwithout\s+(?:any\s+)?(?:talking|speech|speaking|dialogue|voice)\b|\bnessuno\s+(?:parla|dice)\b|\bsenza\s+(?:parlato|parole|voce|dialogo)\b|\bnon\s+si\s+parla\b",
        Opts, Timeout);

    // Words that are on the picture, not in the sound: never speech.
    private static readonly Regex OnScreen = new(
        @"\bon[- ]?screen\s+(?:words?|text|titles?|captions?)\b|\b(?:words?|text)\s+on\s+(?:the\s+)?screen\b|\btitles?\b|\bcaptions?\b|\bscritte\b|\bsottotitoli\b",
        Opts, Timeout);

    // A cut verb whose object is the audio itself ("remove the camera audio") asks to mute it.
    private static readonly Regex AudioObject = new(
        @"\b(?:cut|remove|delete|drop|strip|togli|togliere|elimina|eliminare|rimuovi|rimuovere|cancella|cancellare)\s+(?:out\s+)?(?:(?:the|my|all|this|that|its|il|lo|la|l'|tutto|quest[oa])\s*){0,2}(?:(?:camera|original|background|clip|track)\s+){0,2}(?:audio|sound|suono|sonoro)\b(?!\s+(?:effects?|fx|clips?|files?|effett[io]))",
        Opts, Timeout);

    private const string SpeechNoun = @"(?:spoken|speech|words?|talking|talk|dialogue|dialog|voices?|speaking|parlato|parole?|voce|voci|dialoghi?|discorso|parlat[aeio])";

    // A keep/leave/preserve (or "don't remove") verb governing the speech noun: the speech is what stays.
    private static readonly Regex Keeping = new(
        @"\b(?:keep|leave|preserve|retain|save|(?:don'?t|do\s+not|never)\s+(?:remove|cut|delete|touch|drop)|tieni|lascia|mantieni|conserva|non\s+(?:togliere|tagliare|eliminare|rimuovere|toccare))\b(?:\s+[\w']+){0,5}?\s+" + SpeechNoun + @"\b",
        Opts, Timeout);

    // "Remove the sound effects" / "effetti sonori" name effects, not the audio itself.
    private static readonly Regex SoundEffects = new(@"\beffett[io]\s+sonor[io]\b", Opts, Timeout);

    // A topical, positional or exception qualifier: the request names a part, not everything.
    private static readonly Regex Qualifier = new(
        @"\babout\b|\bwhere\b|\bwhen\b|\bthat\s+(?:follow\w*|come\w*|precede\w*|happen\w*|appear\w*|occur\w*|are|is|was|were|mention\w*|talk\w*|say\w*|speak\w*|include\w*|contain\w*|start\w*|begin\w*|end\w*|last\w*|run\w*|exceed\w*)\b|\bwhich\b|\bmention(?:s|ed|ing)?\b|\bexcept\b|\bbut\s+(?:not|keep|leave|skip|don'?t|do\s+not|never|only|the|in|at|from|before|after)\b|\bapart\s+from\b|\bat\s+the\s+(?:start|beginning|end)\b|\b(?:start|beginning)\s+of\b|\b(?:second|third|other|middle|latter|second[ao]|terz[ao]|altra|met[aà])\s+(?:half|part|clip|scene|section|parte|met[aà])\b|\bhalf\b|\bmet[aà]\b|\b(?:first|last|prim[aoei]|ultim[aoei])\s+(?:\d+|few|half|third|quarter|seconds?|minutes?|clips?|scenes?|parts?|bits?|sections?|sentences?|words?|lines?|silences?|pauses?|gaps?|fillers?|ums?|silenzi[o]?|paus[ae]|parol[ae]|fras[ie]|second[io]|minut[io])\b|\bbefore\b|\bafter\b|\bbetween\b(?!\s+(?:the\s+)?(?:sentences?|words?|lines?|phrases?|takes?)\b)|\bdi\s+cui\b|\bin\s+cui\b|\bdove\b|\bquando\b|\bche\s+parl\w*|\bsul\b|\bsulla\b|\bsui\b|\bsugli\b|\btranne\b|\beccetto\b|\ball'inizio\b|\balla\s+fine\b|\bprima\b|\bdopo\b|\b(?:tra|fra)\s+(?:\d|(?:il|i|la|le)\s+(?:minut\w+|second\w+|scen\w+|clip))",
        Opts, Timeout);

    private static readonly Regex NegatedClause = new(
        @"\b(?:where|when|dove|quando|in\s+cui)\s+(?=" + Negated + ")", Opts, Timeout);

    // A request with no cut verb that is still clearly a removal ("via i silenzi", "no more pauses", "the silences, please").
    private static readonly Regex RemovalPhrase = new(
        @"\bvia\b|\bno\s+more\b|\bbasta\b",
        Opts, Timeout);

    // A keep/leave/don't-touch verb governing its own object: the window stops at a clause break (and, but, then, comma...).
    private const string KeepVerb = @"(?:keep|leave|preserve|retain|save|(?:don'?t|do\s+not|never)\s+(?:remove|cut|delete|touch|drop)|tieni|lascia|mantieni|conserva|non\s+(?:togliere|tagliare|eliminare|rimuovere|toccare))";
    private const string KeepWindow = @"(?:\s+(?!(?:and|e|but|ma|then|poi|or|o)\b)[\w']+){0,5}?\s+";
    private const string SilenceNoun = @"(?:silences?|pauses?|gaps?|silenzi\w*|paus[ae])\b";
    private const string FillerNoun = @"(?:ums?|uhs?|fillers?|intercalari?)\b";

    private static readonly Regex KeepingSilence = new(@"\b" + KeepVerb + @"\b" + KeepWindow + SilenceNoun, Opts, Timeout);
    private static readonly Regex KeepingFillers = new(@"\b" + KeepVerb + @"\b" + KeepWindow + FillerNoun, Opts, Timeout);

    // The whole keep clause of a silence or filler ("but leave the silences"): not a scope on what is cut.
    private static readonly Regex KeepClause = new(@"(?:\b(?:but|ma)\s+)?\b" + KeepVerb + @"\b" + KeepWindow + "(?:" + SilenceNoun + "|" + FillerNoun + ")", Opts, Timeout);

    /// <summary>True when the request says nobody speaks ("where nobody talks about pricing"): what it names is the quiet, not the speech.</summary>
    private static bool NegatesSpeechCore(string? brief) => !string.IsNullOrWhiteSpace(brief) && Negated.IsMatch(brief);

    private const string Apos = "['\u2019]";
    private const string LocatingWords = @"(?:in|from|on|within|inside|during|at|nel|nella|nello|dal|dalla|dello|sul|sulla|dentro|durante)";
    private const string Elided = "(?:nell|dall|dell|sull|all)" + Apos;

    private const string Det = @"(?:(?:the|il|lo|la|my|our|your|this|that|questa|questo|mia|mio)\s+|l" + Apos + ")";
    private const string ClipNoun = @"(?:(?:clip|video|track|file|take|shot|scena)\s+)";

    // A locating phrase: "in the Intro", "in the clip Intro", "nell'intro". Group "obj" starts at the word it points to.
    private static readonly Regex LocatingPhrase = new(
        @"(?:\b" + LocatingWords + @"\s+" + Det + "?" + ClipNoun + @"?|\b" + Elided + ClipNoun + @"?)(?=(?<obj>\w))", Opts, Timeout);

    // The window before a clip name: a locating word, an optional article or demonstrative and an optional clip noun ("in the clip Intro").
    private static readonly Regex LocatingPrefix = new(
        @"(?:\b" + LocatingWords + @"\s+" + Det + "?" + ClipNoun + @"?|\b" + Elided + ClipNoun + @"?)$", Opts, Timeout);

    // A locating phrase whose object is the whole video ("from the video", "in my footage", "in every clip", "on both clips", "in tutte le clip",
    // "nell'intero filmato") is not a scope, nor are "at once" / "in one go" / "in it". A clip followed by a number, a name or a qualifier is a
    // named clip, and a demonstrative clip ("from that clip") is a part: neither is the whole video.
    private const string Head = @"(?:\b(?:in|from|on|within|inside|during|at|da|dal|dallo|dalla|nel|nello|nella|su|sul|sulla|dentro|durante)\s+|\b(?:nell|dall|sull|all)" + Apos + ")";
    private const string AllDet = @"(?>(?:(?:the|my|our|your|entire|whole|full|all\s+of\s+the|all\s+the|all|every|each|both|tutto|tutta|tutte|tutti|il|lo|la|le|i|gli|l" + Apos + @"|intero|intera|mio|mia|entrambe|entrambi|ogni)\b\s*)*)";
    private const string DemoDet = @"(?>(?:(?:the|my|this|that|these|those|our|your|entire|whole|full|all\s+of\s+the|all\s+the|tutto|tutta|il|lo|la|l" + Apos + @"|intero|intera|mio|mia|questo|questa)\b\s*)*)";
    private const string NoNameAfter = @"(?!\s*[-\w]*\d)(?!\s+(?:with|named|called|where|that|which|of|del|della|dello|dei|degli|delle|di|about|con|chiamat\w+|in\s+cui)\b)";

    // A singular noun is the whole video only when nothing follows it but the end, punctuation or a conjunction: a word after it is a name
    // or a description ("in the clip Intro", "in the clip of the beach"), which is a part.
    private const string EndFollow = @"(?=\s*(?:[,.;:!?]|$)|\s+(?:and|e|please|for|but|ma|then|poi|too|also|now|ora|adesso|grazie|thanks|per|so|together|completely|insieme)\b)";

    private static readonly Regex WholeThing = new(
        Head + DemoDet + @"(?:video|film|footage|recording|movie|timeline|project|file|take|thing|filmato|registrazione|progetto|girato|montaggio)\b" + EndFollow
        + "|" + Head + AllDet + @"(?:clip|video|track|file|take)\b" + EndFollow
        + "|" + Head + AllDet + @"(?:clips|videos|tracks|files|takes|films)\b" + NoNameAfter
        + "|" + Head + @"(?:both(?:\s+of\s+them)?|all\s+of\s+them|them\s+all|each\s+of\s+them|entrambi|entrambe|tutti\s+e\s+due|tutte\s+e\s+due|tutti|tutte)\b" + EndFollow
        + @"|\b(?:in|on|at)\s+(?:it|there|them)\b|\b(?:all\s+)?at\s+once\b|\bin\s+one\s+go\b|\b(?:tutto\s+)?in\s+una\s+volta\b|\bin\s+(?:un\s+)?colpo\s+solo\b",
        Opts, Timeout);

    // A manner or amount qualifier ("but not too much", "ma non troppo", "keep it natural", "leave a little breathing room"): not an exception.
    private static readonly Regex Manner = new(
        @"(?:\b(?:but|ma)\s+)?\b(?:(?:not\s+)?too\s+(?:much|aggressive(?:ly)?|short|long|tight|fast|quick(?:ly)?)|(?:keep|leave)\s+it\s+(?:natural|smooth|tight|clean|short|subtle|loose)|non\s+troppo|mantieni(?:lo)?\s+(?:naturale|fluido|pulito))\b", Opts, Timeout);

    // The next few words of a sentence after a position: a capitalised or numbered word there may be a clip name, so the qualifier is not stripped.
    private static int FewWordsEnd(string t, int from)
    {
        int i = from, words = 0;
        while (i < t.Length && words < 4)
        {
            if (t[i] is '.' or '!' or '?' or ';') break;
            if (char.IsWhiteSpace(t[i])) { i++; continue; }
            while (i < t.Length && !char.IsWhiteSpace(t[i]) && t[i] is not ('.' or '!' or '?' or ';')) i++;
            words++;
        }

        return i;
    }

    private static bool NameLikeAfter(string t, int from)
    {
        int end = FewWordsEnd(t, from);
        for (int i = from; i < end; i++)
        {
            if (char.IsUpper(t[i]) || char.IsDigit(t[i])) return true;
        }

        return false;
    }

    private static string StripManner(string text) =>
        Manner.Replace(text, m => NameLikeAfter(text, m.Index + m.Length) ? m.Value : " ");

    private static readonly Regex ExceptionPhrase = new(
        @"\bexcept\b|\bapart\s+from\b|\bbut\s+(?:not|leave|keep|skip|don'?t)\b|\bother\s+than\b|\bexcluding\b|\btranne\b|\beccetto\b|\bescluso\b|\bma\s+(?:non|lascia|tieni|salta)\b|,\s*not\b|\bnot\s+(?:in|on|from|the|inside|during)\b|\bnon\s+(?:nel|nella|nello|nell|dal|dalla|sul|sulla|l|il|lo|la|toccare)\b|\bwithout\s+(?:touching|cutting|removing|deleting|trimming|muting)\b|\bsenza\s+(?:toccare|tagliare|eliminare|rimuovere|togliere|cancellare)\b|\blascia(?:la|le|lo|li)\b|\bas\s+(?:it\s+is|is)\b|\bdon'?t\s+touch\b|\bdo\s+not\s+touch\b|\bleave\s+(?:\w+\s+){0,2}alone\b|\blascia\s+stare\b",
        Opts, Timeout);

    /// <summary>True when the request carries an exception ("except", "apart from", "tranne"): a name in it is never the scope.</summary>
    private static bool HasExceptionCore(string? brief) => !string.IsNullOrWhiteSpace(brief) && ExceptionPhrase.IsMatch(brief);

    private const int PrefixWindow = 24;

    private static readonly Regex ListConnector = new(
        @"(?:\s*[,&]\s*|\s+(?:and|e|or|o)\s+)(?:(?:the|il|lo|la|nel|nello|nella|dal|dalla)\s+|(?:nell|dall|dell|sull|all|l)" + Apos + @")?$", Opts, Timeout);

    /// <summary>The longest brief, and the most clip names and name hits, considered for scoping; beyond them nothing scopes (fail closed).</summary>
    public const int MaxScopeBriefChars = 2000;
    public const int MaxScopeNames = 300;
    private const int MaxScopeHits = 5000;

    private const string ExclCue = @"(?:except(?:\s+for)?|apart\s+from|but\s+not|but|not|other\s+than|excluding|without(?:\s+(?:touching|cutting|removing|deleting|trimming|muting))?|senza\s+(?:toccare|tagliare|eliminare|rimuovere|togliere|cancellare)|leave|leaving|keep|skip|don'?t\s+touch|do\s+not\s+touch|tranne(?:\s+che)?|eccetto|escluso|ma\s+non(?:\s+toccare)?|non(?:\s+toccare)?|ma\s+lascia(?:\s+stare)?|lascia(?:\s+stare)?|ma\s+tieni|senza|salta)";

    // The window before a clip name that ends in an exception cue: "except the intro", "not in the intro", "ma lascia stare l'intro".
    private static readonly Regex ExclPrefix = new(
        @"\b" + ExclCue + @"\b\s*,?\s*(?:" + LocatingWords + @"\s+|" + Elided + @")?(?:" + Det + @")?" + ClipNoun + "?$", Opts, Timeout);

    private static readonly Regex ExclAnywhere = new(@"\b" + ExclCue + @"\b", Opts, Timeout);

    private static readonly Regex UnresolvedCue = new(
        @"\b" + ExclCue + @"\b\s*,?\s*(?:" + LocatingWords + @"\s+|" + Elided + @")?(?:" + Det + @")?" + ClipNoun + @"?(?-i:[A-Z0-9])", Opts, Timeout);

    // After a clip name: "lasciala com'è", "lascialo stare", "leave it as it is": the clip stays as it is.
    private static readonly Regex ExclSuffix = new(
        @"^\s*,?\s*(?:lascia(?:la|le|lo|li)|leave\s+(?:it|them))\b(?:\s+(?:com" + Apos + @"è|come\s+è|stare|così|as\s+(?:it\s+is|is)|alone))?", Opts, Timeout);

    private static readonly Regex ExclTail = new(@"^\s*(?:as\s+(?:it\s+is|is)|alone|com" + Apos + @"è|come\s+è|stare)\b", Opts, Timeout);

    /// <summary>What the brief says about the clips by name: the ones it points at, the ones it excludes, and the brief with the exclusion clauses blanked.</summary>
    public sealed record ClipScope(HashSet<string> Included, HashSet<string> Excluded, string ScopeBrief, bool Failed)
    {
        public static ClipScope None(string brief) => new(new(StringComparer.OrdinalIgnoreCase), new(StringComparer.OrdinalIgnoreCase), brief, false);
    }

    /// <summary>
    /// Reads the clip names in the brief (longest name wins at a position). A name after an exception cue is excluded; after a locating
    /// preposition (and an optional clip noun), or in a list that continues one, or when distinctive on its own, it is pointed at. One
    /// pass over the hits in order of position, each looked up by where the previous name ended: linear in the hits.
    /// </summary>
    private static ClipScope ReadClipScopeCore(string brief, IReadOnlyList<string> allNames)
    {
        ClipScope scope = ClipScope.None(brief);
        if (string.IsNullOrWhiteSpace(brief) || brief.Length > MaxScopeBriefChars) return scope;

        // Names past the cap, or more hits than the cap, cannot be read: with an exclusion or locating cue in the brief that is a failed read (scoped).
        bool Capped() => ExclAnywhere.IsMatch(brief) || LocatingPhrase.IsMatch(brief) || ExceptionPhrase.IsMatch(brief);
        if (allNames.Count(n => n.Length is > 0 and <= 80) > MaxScopeNames && Capped()) return scope with { Failed = true };

        List<(int Start, int End, string Name)> hits = [];
        foreach (string name in allNames.Where(n => n.Length is > 0 and <= 80).Take(MaxScopeNames))
        {
            foreach (int at in WholeWordHits(brief, name))
            {
                if (hits.Count >= MaxScopeHits) return Capped() ? scope with { Failed = true } : scope;
                hits.Add((at, at + name.Length, name));
            }
        }

        hits.Sort((x, y) => x.Start != y.Start ? x.Start.CompareTo(y.Start) : y.End.CompareTo(x.End));
        char[] blank = brief.ToCharArray();
        List<(int Index, int End, int Reach)> manners = Manner.Matches(brief).Select(m => (m.Index, m.Index + m.Length, FewWordsEnd(brief, m.Index + m.Length))).ToList();
        foreach (Match m in Manner.Matches(brief))
        {
            int after = m.Index + m.Length;
            int reach = FewWordsEnd(brief, after);
            if (NameLikeAfter(brief, after) || hits.Any(h => h.Start >= after && h.Start < reach)) continue;
            for (int i = m.Index; i < after; i++) blank[i] = ' ';
        }

        Dictionary<int, (bool Excluded, int ClauseStart)> chains = [];
        int coveredUntil = 0;
        bool sawConflict = false;
        foreach ((int start, int end, string name) in hits)
        {
            if (start < coveredUntil) continue;   // inside a longer name already read
            coveredUntil = end;
            int from = Math.Max(0, start - 48);
            ReadOnlySpan<char> window = brief.AsSpan(from, start - from);

            bool? excluded = null;
            int clauseStart = start;
            foreach (ValueMatch m in ExclPrefix.EnumerateMatches(window))
            {
                excluded = true;
                clauseStart = from + m.Index;
                break;
            }

            if (excluded is null)
            {
                // A manner phrase right before a clip name ("but not too long intro") keeps its cue: the name is excluded, or the read fails if the name is also located.
                foreach ((int mIndex, int mEnd, int reach) in manners)
                {
                    if (mEnd > start || start >= reach) continue;
                    if (LocatingPrefix.IsMatch(window)) { sawConflict = true; break; }
                    excluded = true;
                    clauseStart = mIndex;
                    break;
                }
            }

            int blankEnd = end;
            if (excluded is true)
            {
                // "leave the intro as it is" / "... alone": the tail belongs to the exclusion.
                foreach (ValueMatch m in ExclTail.EnumerateMatches(brief.AsSpan(end, Math.Min(32, brief.Length - end))))
                {
                    blankEnd = end + m.Index + m.Length;
                    break;
                }
            }

            if (excluded is null)
            {
                ReadOnlySpan<char> after = brief.AsSpan(end, Math.Min(48, brief.Length - end));
                foreach (ValueMatch m in ExclSuffix.EnumerateMatches(after))
                {
                    // "in Main, leave it as it is": the pronoun refers to a clip the request points at, not to an exclusion. Fail closed.
                    if (LocatingPrefix.IsMatch(window)) { sawConflict = true; break; }
                    excluded = true;
                    clauseStart = start;
                    blankEnd = end + m.Index + m.Length;
                    break;
                }
            }

            if (excluded is null)
            {
                foreach (ValueMatch c in ListConnector.EnumerateMatches(window))
                {
                    if (from + c.Index + c.Length != start || !chains.TryGetValue(from + c.Index, out (bool Excluded, int ClauseStart) prev)) continue;
                    excluded = prev.Excluded;
                    clauseStart = from + c.Index;
                    break;
                }
            }

            if (excluded is null && LocatingPrefix.IsMatch(window)) excluded = false;
            if (excluded is null && name.Length >= 3 && name.Any(ch => char.IsDigit(ch) || ch is '-' or ' ')) excluded = false;
            if (excluded is not { } isExcluded) continue;

            chains[blankEnd] = (isExcluded, start);
            if (isExcluded)
            {
                scope.Excluded.Add(name);
                for (int i = clauseStart; i < blankEnd; i++) blank[i] = ' ';
            }
            else
            {
                scope.Included.Add(name);
            }
        }

        // An exclusion cue still followed by something that looks like a name (a capital or a digit) that no clip resolved: fail closed.
        string left = Regex.Replace(new string(blank), @"\s+", " ", RegexOptions.CultureInvariant, Timeout);
        return scope with { ScopeBrief = left, Failed = sawConflict || UnresolvedCue.IsMatch(left) };
    }

    public static ClipScope ReadClipScope(string brief, IReadOnlyList<string> allNames)
    {
        try { return ReadClipScopeCore(brief, allNames); }
        catch (RegexMatchTimeoutException) { return ClipScope.None(brief) with { Failed = true }; }
    }

    /// <summary>The clip names the brief points at (see <see cref="ReadClipScope"/>).</summary>
    public static HashSet<string> NamedClipNames(string brief, IReadOnlyList<string> allNames) => ReadClipScope(brief, allNames).Included;

    /// <summary>True when every locating phrase of the brief ("in the Intro") points at one of <paramref name="names"/> (whole word).</summary>
    private static bool LocatingPhrasesAllNamedCore(string brief, IReadOnlyCollection<string> names)
    {
        if (brief.Length > MaxScopeBriefChars) return false;
        string text = WholeThing.Replace(brief, " ");
        foreach (Match m in LocatingPhrase.Matches(text))
        {
            int idx = m.Index + m.Length;
            if (!names.Take(MaxScopeNames).Any(n => text.AsSpan(idx).StartsWith(n, StringComparison.OrdinalIgnoreCase) && IsWordEnd(text, idx + n.Length))) return false;
        }

        return true;
    }

    private static bool IsWord(char c) => char.IsLetterOrDigit(c) || c is '-' or '_';
    private static bool IsWordEnd(string t, int end) => end >= t.Length || !IsWord(t[end]);

    private static IEnumerable<int> WholeWordHits(string text, string name)
    {
        for (int at = text.IndexOf(name, StringComparison.OrdinalIgnoreCase); at >= 0; at = at + 1 < text.Length ? text.IndexOf(name, at + 1, StringComparison.OrdinalIgnoreCase) : -1)
        {
            if ((at == 0 || !IsWord(text[at - 1])) && IsWordEnd(text, at + name.Length)) yield return at;
        }
    }

    private static readonly Regex HeadEdge = new(@"\b(?:start|beginning|head|inizio|all'inizio|iniziale)\b", Opts, Timeout);
    private static readonly Regex TailEdge = new(@"\b(?:end|tail|fine|finale)\b", Opts, Timeout);

    /// <summary>Which edge of a clip the request names: "at the start" is the head, "at the end" the tail. Neither when it names neither.</summary>
    private static (bool Head, bool Tail) EdgesCore(string? brief) =>
        string.IsNullOrWhiteSpace(brief) ? (false, false) : (HeadEdge.IsMatch(brief), TailEdge.IsMatch(brief));

    private static readonly Regex SpeechQuantified = new(
        @"\b(?:all|every|entire|whole)\s+(?:(?:the|of\s+the|of|my|that|this)\s+)?(?:spoken\s+)?(?:words?|speech|talking|talk|dialogue|dialog|voice|lines?|parts?)\b|\ball\s+(?:the\s+|of\s+the\s+|my\s+)?spoken\b|\btutt[aeio]\s+(?:il\s+|la\s+|le\s+|i\s+|gli\s+|l')?\s*(?:parlato|parole|parti\s+parlate|voce|voci|dialoghi?|discorso)\b|\bogni\s+(?:parola|frase|parte\s+parlata)\b",
        Opts, Timeout);

    /// <summary>
    /// True when the request is unscoped for its target, so a deterministic "cut all of it" is what was asked: no topical, positional
    /// or exception qualifier, and for speech a quantifier on the speech noun ("all the spoken words", "tutto il parlato").
    /// Silence and fillers need no quantifier ("remove the silences" means all of them).
    /// </summary>
    private static bool IsUnscopedCore(string? brief, RecutTarget? target, bool allowLocating = false)
    {
        if (string.IsNullOrWhiteSpace(brief) || target is null or RecutTarget.KeepOnly) return false;
        // "where nobody is talking" names what is cut (the silence), it does not pick a part of the video.
        string scopeText = StripManner(KeepClause.Replace(NegatedClause.Replace(brief, " "), " "));
        if (Qualifier.IsMatch(scopeText) || ExceptionPhrase.IsMatch(scopeText)) return false;
        // Any other locating phrase ("in the second act", "nell'intro") picks a part; the caller lifts this only for phrases that name a clip.
        if (!allowLocating && LocatingPhrase.IsMatch(WholeThing.Replace(scopeText, " "))) return false;
        if (target == RecutTarget.Speech && Keeping.IsMatch(brief)) return false;
        return target != RecutTarget.Speech || SpeechQuantified.IsMatch(brief);
    }

    /// <summary>The operation class and content target of a request; (null, null) when it names neither (for example "make it punchier").</summary>
    private static (RecutOpClass? OpClass, RecutTarget? Target) ClassifyCore(string? brief)
    {
        if (string.IsNullOrWhiteSpace(brief)) return (null, null);
        string text = StripManner(brief.Trim());

        RecutTarget? target = null;
        bool keepOnly = KeepOnly.IsMatch(text);
        string speechText = OnScreen.Replace(text, " ");
        if (keepOnly) target = RecutTarget.KeepOnly;
        else if (Fillers.IsMatch(text)) target = RecutTarget.Fillers;
        else if (Negated.IsMatch(text) || Silence.IsMatch(text)) target = RecutTarget.Silence;
        else if (Speech.IsMatch(Keeping.Replace(speechText, " "))) target = RecutTarget.Speech;

        // The verb that comes first in the request wins ("cut the music and mute the intro" is a cut).
        string CutText = NegatedCut.Replace(text, m => new string(' ', m.Length));
        (int Index, RecutOpClass Op)? best = null;
        void Consider(Regex r, RecutOpClass op)
        {
            Match m = r.Match(ReferenceEquals(r, CutVerb) ? CutText : text);
            if (m.Success && (best is null || m.Index < best.Value.Index)) best = (m.Index, op);
        }

        Consider(Mute, RecutOpClass.Mute);
        Consider(Trim, RecutOpClass.Trim);
        Consider(Reorder, RecutOpClass.Reorder);
        Consider(CutVerb, RecutOpClass.Cut);

        RecutOpClass? op = best?.Op;
        if (keepOnly) op = RecutOpClass.Cut;

        // "Make it shorter by cutting out all the spoken words": a cut verb on a content target makes it a cut, whichever verb came first.
        if (op == RecutOpClass.Trim && target is RecutTarget.Speech or RecutTarget.Silence or RecutTarget.Fillers && CutVerb.IsMatch(text)) op = RecutOpClass.Cut;

        // "Leave the silences" / "don't touch the pauses": the quiet part is what stays, so there is nothing to cut.
        if ((target == RecutTarget.Silence && KeepingSilence.IsMatch(text)) || (target == RecutTarget.Fillers && KeepingFillers.IsMatch(text))) return (null, target);

        // No verb at all, but a silence or filler named with a clear removal phrase: a cut. Anything else stays unclassified.
        if (op is null && target is RecutTarget.Silence or RecutTarget.Fillers && RemovalPhrase.IsMatch(text)) op = RecutOpClass.Cut;

        // "Remove the camera audio" asks to take the sound off, which is a mute of the audio clip.
        if (op == RecutOpClass.Cut && target is null && AudioObject.IsMatch(text) && !SoundEffects.IsMatch(text)) op = RecutOpClass.Mute;

        // "Trim the silences" / "shorten the pauses" mean take them out, not shave a clip's ends.
        if (op == RecutOpClass.Trim && target is RecutTarget.Silence or RecutTarget.Fillers) op = RecutOpClass.Cut;

        return (op, target);
    }

    public static bool NegatesSpeech(string? brief)
    {
        try { return NegatesSpeechCore(brief); }
        catch (RegexMatchTimeoutException) { return true; }
    }

    public static bool HasException(string? brief)
    {
        try { return HasExceptionCore(brief); }
        catch (RegexMatchTimeoutException) { return true; }
    }

    public static bool LocatingPhrasesAllNamed(string brief, IReadOnlyCollection<string> names)
    {
        try { return LocatingPhrasesAllNamedCore(brief, names); }
        catch (RegexMatchTimeoutException) { return false; }
    }

    public static (bool Head, bool Tail) Edges(string? brief)
    {
        try { return EdgesCore(brief); }
        catch (RegexMatchTimeoutException) { return (false, false); }
    }

    public static bool IsUnscoped(string? brief, RecutTarget? target, bool allowLocating = false)
    {
        try { return IsUnscopedCore(brief, target, allowLocating); }
        catch (RegexMatchTimeoutException) { return false; }
    }

    public static (RecutOpClass? OpClass, RecutTarget? Target) Classify(string? brief)
    {
        try { return ClassifyCore(brief); }
        catch (RegexMatchTimeoutException) { return (null, null); }
    }

    public static string Word(RecutOpClass op) => op switch
    {
        RecutOpClass.Cut => "cut",
        RecutOpClass.Mute => "mute",
        RecutOpClass.Trim => "trim",
        _ => "reorder"
    };

    public static string Word(RecutTarget target) => target switch
    {
        RecutTarget.Speech => "speech",
        RecutTarget.Silence => "silence",
        RecutTarget.Fillers => "fillers",
        _ => "keepOnly"
    };
}
