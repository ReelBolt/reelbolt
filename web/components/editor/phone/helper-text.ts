import { LOOKS } from '@/lib/editing/color/grade';
import { useCallback } from 'react';
import { useT } from '@/i18n';
import { msg, type Msg } from '@/i18n/msg';

/**
 * Display-time translation of text the editor's logic modules still produce in English (refusal sentences,
 * history labels, hints). The English stays the stored/returned value (history labels live in documents and tests
 * assert them); only what is SHOWN is mapped here. A text that is not one of our defaults (a user-typed name)
 * is returned untouched.
 */
const STATIC_TEXT: Readonly<Record<string, string>> = {
  "Add a music track first. A track named Music is picked up by itself.": 'duckNoMusic',
  "Put a voice or dialogue clip on its own audio track so the music has something to duck under.": 'duckNoVoice',
  "Duck music under voice": 'duckMusic',
  "Remove ducking": 'removeDucking',
  "Clean voice": 'cleanVoice',
  "Clean voice off": 'cleanVoiceOff',
  "The captions are already there.": 'capsThere',
  "No spoken words were found on this track.": 'capsNoWords',
  "A caption cannot be empty. Delete it instead.": 'capEmpty',
  "Remove look": 'removeLook',
  "Look strength": 'lookStrength',
  "Reset fixes": 'resetFixes',
  "Pick a look first, then set how strong it is.": 'pickLookStrength',
  "Pick a look or change a setting first, then apply it to all clips.": 'pickLookAll',
  "Type the words for the title first.": 'titleWordsFirst',
  "A title needs some words. The old words are kept.": 'titleNeedsWords',
  "The title already says that.": 'titleSame',
  "Edit title text": 'editTitleText',
  "Title style": 'titleStyle',
  "The title already has that style.": 'titleStyleSame',
  "Cropped. Change it on desktop.": 'noteCropped',
  "Plays backwards. Change it on desktop.": 'noteReverse',
  "A freeze frame.": 'noteFreeze',
  "This is animated, so it can only be changed on desktop.": 'animated',
  "Only a clip on a picture track can be fitted.": 'onlyFit',
  "The picture already fits.": 'alreadyFits',
  "The picture already fills the frame.": 'alreadyFills',
  "Fit picture": 'fitPicture',
  "Fill picture": 'fillPicture',
  "Only a clip on a picture track can be moved.": 'onlyMove',
  "The picture is already there.": 'alreadyThere',
  "Place picture": 'placePicture',
  "Only a clip on a picture track has a place in the frame.": 'onlyPlace',
  "Type a number. The old value is kept.": 'typeNumber',
  "That is already the value.": 'alreadyValue',
  "Picture size": 'pictureSize',
  "Move picture": 'movePicture',
  "Only a clip on a picture track has opacity.": 'onlyOpacity',
  "Opacity": 'opacity',
  "That is already the fade.": 'alreadyFade',
  "Fade in": 'fadeIn',
  "Fade out": 'fadeOut',
  "The timeline is empty": 'timelineEmpty',
  "Move": 'moveLabel',
  "Placed": 'placedLabel',
};

const num = (s: string) => Number(s);

/** Only the default look names read as a look: any other label that happens to end in " look" is shown as it is. */
const LOOK_NAME_TEXT = new RegExp(`^(${[...LOOKS, 'Teal & orange'].map((l) => l.replace(/[.*+?^${}()|[\]\\]/g, '\\$&')).join('|')}) look$`);

/** The Msg for a known English default (static or number-bearing), else null. */
export function helperMsg(text: string): Msg | null {
  const id = STATIC_TEXT[text];
  if (id) return msg(`editor-helpers-2.text.${id}`);
  let m: RegExpExecArray | null;
  if ((m = /^Rotated (-?\d+) degrees( \(animated on desktop\))?\. Change it on desktop\.$/.exec(text))) return msg('editor-helpers-2.fmt.rotated', { degrees: num(m[1]), animated: m[2] ? 'yes' : 'no' });
  if ((m = /^Size is between (\d+)% and (\d+)%\. The old value is kept\.$/.exec(text))) return msg('editor-helpers-2.fmt.sizeRange', { min: num(m[1]), max: num(m[2]) });
  if ((m = /^Position is between -(\d+(?:\.\d+)?)% and \d+(?:\.\d+)?%\. The old value is kept\.$/.exec(text))) return msg('editor-helpers-2.fmt.positionRange', { limit: num(m[1]) });
  if ((m = /^(\d+) captions? added\.$/.exec(text))) return msg('editor-helpers-2.fmt.captionsAdded', { count: num(m[1]) });
  if ((m = /^Delete (\d+) words?$/.exec(text))) return msg('editor-helpers-2.fmt.deleteWords', { count: num(m[1]) });
  if ((m = /^(.+) is between (.+) and (.+)\. The old value is kept\.$/.exec(text))) return msg('editor-helpers-2.fmt.fixRange', { label: m[1], min: m[2], max: m[3] });
  if ((m = LOOK_NAME_TEXT.exec(text))) return msg('editor-helpers-2.fmt.lookName', { name: m[1] });
  return null;
}

/** `const show = useHelperText(); show(problem)`: the translated text for a known default, otherwise the text as given. */
export function useHelperText(): (text: string | null) => string | null {
  const t = useT();
  return useCallback(
    (text: string | null) => {
      if (text === null) return null;
      const m = helperMsg(text);
      return m ? t.msg(m) : text;
    },
    [t],
  );
}
