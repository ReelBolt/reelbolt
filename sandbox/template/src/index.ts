// Built with Remotion (https://www.remotion.dev). Using this code is subject to the Remotion license: https://www.remotion.dev/docs/license

import {registerRoot} from 'remotion';
// Explicitly specify the .tsx extension to avoid resolution attempts against
// `root.js` which can occur when the project workspace is mutated by agents.
import { Root } from './root.tsx';

registerRoot(Root);
