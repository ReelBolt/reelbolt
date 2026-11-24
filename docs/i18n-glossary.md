# Italian glossary and style (i18n)

Single reference for every Italian string in the dashboard, the marketing site, emails and the docs. English is the source language.
Draft by us, **to be reviewed by the owner** (flagged strings are exported to `docs/i18n-review/it.md`). Change a term here first, then
in the catalogues; `i18n/glossary.json` (generated from the table) feeds the banned-variants test.

## Voice
- Informal **tu** (never "Lei"), short sentences, same register as the English: calm, direct, no hype.
- Natural Italian, not word for word. Keep a sentence's job (button = verb in the imperative: "Crea progetto", "Salva modifiche").
- Established product words stay as used by Italian video editors: timeline, render, upload is "carica", export is "esporta", clip, layer, mask, preset, trailer.
- Sentence case for buttons, headings, labels (only the first word capitalised), like the English UI.
- Numbers, dates, money through `Intl` with the active locale (1.234,56; 28 ott 2026); never typed into a string.
- Plurals through ICU (`{count, plural, one {# file} other {# file}}`); Italian needs only `one`/`other` in our catalogues.
- Never translate: ReelBolt, plan names (Creator, Pro, Team, Enterprise), Remotion-free buyer copy rules still apply (no ffmpeg, Remotion, Docker, PostgreSQL, "workflow engine" on marketing pages), file extensions, code, API/field names, keyboard keys.
- Error text says what happened and what to do next, in the same order as the English.

## Terms
| English | Italian | Notes |
|---|---|---|
| project | progetto | |
| workspace | spazio di lavoro | "Personal" = "Personale"; team workspace = "spazio di lavoro del team" |
| workflow | flusso di lavoro | plural "flussi di lavoro"; beginner surfaces say "app" (what you run), "video" (what it makes), "esecuzione" (one run); "flusso di lavoro", "passaggio", "template" and "render" only in the builder, editor and Advanced pages |
| app (workflow app) | app | |
| step | passaggio | |
| run (noun) | esecuzione | "to run" = "eseguire"; button "Run" = "Avvia" |
| brief | brief | |
| render (noun/verb) | render / "renderizzare" avoided: use "esporta" or "genera il video" | button "Render" = "Render". Beginner surfaces (dashboard, project page, Files, gallery, run page, tour) say "video" (the "Renders" tab and filter = "Video"); "render" stays only in the editor and Advanced pages |
| output / result | risultato | finished video = "video finale" |
| credits | crediti | never "punti" |
| plan | piano | |
| billing | fatturazione | "Plan & billing" = "Piano e fatturazione" |
| subscription | abbonamento | |
| invoice | fattura | |
| top-up | ricarica | |
| assistant | assistente | |
| agent | agente | |
| skill | competenza | in agent context |
| provider | provider | AI connection = "connessione AI" |
| publish (to a social platform) | pubblica | "Publishing" page = "Pubblicazione"; the connected profile is an "account" |
| schedule (a post) | programma | "Scheduled" = "Programmato"; the list of waiting posts is the "coda" (queue) |
| model | modello | |
| style | stile | |
| design system | design system | brand library = "libreria del brand" |
| file | file | folder = "cartella"; upload = "carica"; download = "scarica" |
| source / generated | origine / generato | Files tab categories: Sources = "Sorgenti", Generated = "Generati", Renders = "Video" |
| editor | editor | |
| timeline | timeline | |
| clip | clip | |
| track | traccia | |
| picture / thumbnail | immagine / miniatura | "Pictures" = "Immagini" |
| layer | livello | |
| mask | maschera | |
| caption / subtitle | sottotitolo | |
| voiceover | voce narrante | |
| avatar | avatar | invariable plural ("gli avatar"); "New avatar" = "Nuovo avatar" |
| object (an avatar of a thing, not a person) | oggetto | "New object" = "Nuovo oggetto"; "people and objects" = "persone e oggetti"; "object statement" = "dichiarazione" |
| variant (of an avatar or object) | variante | "Add a variant" = "Aggiungi una variante"; "variant label" = "etichetta della variante" |
| avatar library / workspace library | libreria degli avatar / libreria dello spazio di lavoro | the page is "Libreria degli avatar"; the badge and the picker say "libreria dello spazio di lavoro" |
| consent / withdraw consent | consenso / revoca il consenso | "consent statement" = "dichiarazione di consenso"; "written permission" = "permesso scritto" |
| keep for / keep-until date | conserva per / data di conservazione | retention of an avatar or a cloned voice |
| music / sound effect | musica / effetto sonoro | |
| runner | runner | desktop app: "app desktop" |
| member / role | membro / ruolo | Owner = "Proprietario", Admin = "Amministratore", Member = "Membro", Billing = "Fatturazione" |
| invite | invito | |
| settings | impostazioni | |
| sign in / sign up / sign out | accedi / registrati / esci | |
| password | password | |
| save / cancel / delete / edit | salva / annulla / elimina / modifica | |
| draft | bozza | |
| early preview | anteprima anticipata | never "beta" |
| alpha | alpha | "public alpha" = "alpha pubblica" |
| free first video | primo video gratis | |
| trial | prova gratuita | |
| docs | documentazione | |
| notification | notifica | |
| shortcut | scorciatoia | |
| undo / redo | annulla / ripristina | |

## Decisions
One term per concept across the Italian catalogues; the banned variants are not used.
| Concept | Use | Not |
|---|---|---|
| features | Funzionalità | Funzioni |
| credit pool | pool di crediti | gruppo di crediti |
| email | email | e-mail |
| editing rooms | Stanze | Sale |
| connected machine | collegato/a | abbinato/a |
| brand library | libreria del brand | libreria di brand |
| apostrophe | straight ' | curly ’ |
| founder price | Prezzo di lancio | Prezzo fondatore |
| lower thirds | sottopancia | terzi inferiori |
| pickup (re-record a line) | correggere una battuta registrandola di nuovo | ripescaggio |
| workflow template | template | modello |
| voiceover step | Voce narrante | Voce fuori campo |
| imperative "say" | Di' (with apostrophe) | Dì |
| take (one generated attempt of a clip, audio or shot) | editor/Advanced: take (loanword, invariable plural: "i take"); beginner surfaces (video player, project page, run inputs): "versione" | tentativo, ripresa ("riprese" stays for the user's source footage) |
| dynamics (the style axis for effects on the music's beat) | Dinamica | the style choice is "Dinamica" (options "Effetti calmi/medi/veloci"); never "Intensità" |
| pace (the run control Off/Calm/Medium/Fast) | Ritmo degli effetti | "Ritmo" alone is the Pacing style axis (how fast the cut moves between shots); "Intensità" is reserved for strength; never label the pace control "Ritmo" or "Intensità" |

### Workflow step type labels (one label per type, used by the builder, the run page, the assistant UI and the docs)
| Step type | Italian label |
|---|---|
| Agent | Agente |
| ReviewLoop | Ciclo di revisione |
| Conditional | Ramo condizionale |
| ForEach | Per ogni elemento |
| Parallel | In parallelo |
| Extract | Estrai |
| VideoAnalyze | Analizza video |
| VideoCompile | Monta video |
| EditRoom | Stanza di montaggio |
| GraphicsRoom | Stanza grafica |
| ColorGradeRoom | Stanza del colore |
| DailiesRoom | Stanza dailies |
| VideoGenerate | Genera video |
| Voiceover | Voce narrante |
| ThumbnailGenerate | Miniatura |
| TimelineRender | Esporta la timeline |
| ExtractFrame | Cattura un fotogramma |
| AudioGenerate | Genera musica o suoni |
| PinInsert | Metti sugli schermi |
| Revise | Modifiche |
| DesignGraphic | Progetta una grafica |
| DesignSystem | Profilo di brand e prodotto |

"Render" is never turned into "renderizza" in UI text (use "genera il video", "esporta"). The Italian docs still use "Room …" and "Renderizza timeline" in a few places; they are aligned to this table in the docs label reconciliation pass.

### Editor decisions (integration pass)
- **clip** is feminine everywhere: "la clip", "le clip", "una clip", "questa clip", "clip selezionate".
- **lift vs extract**: Lift (removes the clip and leaves a gap) is "Solleva"; Extract (removes it and closes the gap, ripple) is "Estrai". Never use "Estrai" for lift or "Rimuovi" for either.
- **template** stays "template" in the editor (title and graphic templates, the inspector field). "Modello" is only used for an AI model.
- **lower third** is "sottopancia"; **redo** is "ripristina" ("Ripeti" is only for retaking a recording or looping playback); **edit point** is "taglio" ("Taglio precedente / successivo").
- **fade in / fade out** are kept as "Fade in" / "Fade out" on the phone and in the inspector (the Italian words do not fit a 44 px control); transitions keep "Dissolvenza ...".
- **Names stored in a timeline or sent to the API** (Adjustment, Adjustments, Group N, Correction N, Shot match, Compound clip, Voiceover, "Untitled ..." picture names, "... (new)" copies) stay English: they are shared data. Only their display is translated.
- Apostrophes in Italian strings are straight (').
