# Event score: synthetic/events (tolerance +/-2 frames)

Beats predicted on speech-only clips (expect.noBeats): syn-speech-01=0, syn-speech-02=0

## untyped

| scope | TP | FP | FN | P | R | F1 |
|---|---|---|---|---|---|---|
| ALL | 210 | 56 | 31 | 0.79 | 0.87 | 0.83 |
| video: animation | 49 | 29 | 7 | 0.63 | 0.88 | 0.73 |
| video: music | 97 | 2 | 0 | 0.98 | 1.00 | 0.99 |
| video: screen | 29 | 25 | 22 | 0.54 | 0.57 | 0.55 |
| video: silent | 18 | 0 | 1 | 1.00 | 0.95 | 0.97 |
| video: speech | 17 | 0 | 1 | 1.00 | 0.94 | 0.97 |
| type: appear | 14 | 7 | 3 | 0.67 | 0.82 | 0.74 |
| type: beat | 97 | 2 | 0 | 0.98 | 1.00 | 0.99 |
| type: camera_move_start | 3 | 0 | 0 | 1.00 | 1.00 | 1.00 |
| type: camera_move_stop | 3 | 1 | 0 | 0.75 | 1.00 | 0.86 |
| type: disappear | 3 | 4 | 3 | 0.43 | 0.50 | 0.46 |
| type: hit | 12 | 8 | 7 | 0.60 | 0.63 | 0.62 |
| type: move_start | 15 | 4 | 6 | 0.79 | 0.71 | 0.75 |
| type: move_stop | 6 | 12 | 1 | 0.33 | 0.86 | 0.48 |
| type: speech_end | 16 | 1 | 1 | 0.94 | 0.94 | 0.94 |
| type: speech_start | 16 | 0 | 1 | 1.00 | 0.94 | 0.97 |
| type: text_in | 12 | 7 | 7 | 0.63 | 0.63 | 0.63 |
| type: text_out | 2 | 9 | 1 | 0.18 | 0.67 | 0.29 |
| type: transition | 11 | 1 | 1 | 0.92 | 0.92 | 0.92 |

## typed

| scope | TP | FP | FN | P | R | F1 |
|---|---|---|---|---|---|---|
| ALL | 205 | 61 | 36 | 0.77 | 0.85 | 0.81 |
| video: animation | 47 | 31 | 9 | 0.60 | 0.84 | 0.70 |
| video: music | 97 | 2 | 0 | 0.98 | 1.00 | 0.99 |
| video: screen | 26 | 28 | 25 | 0.48 | 0.51 | 0.50 |
| video: silent | 18 | 0 | 1 | 1.00 | 0.95 | 0.97 |
| video: speech | 17 | 0 | 1 | 1.00 | 0.94 | 0.97 |
| type: appear | 11 | 7 | 6 | 0.61 | 0.65 | 0.63 |
| type: beat | 97 | 2 | 0 | 0.98 | 1.00 | 0.99 |
| type: camera_move_start | 3 | 0 | 0 | 1.00 | 1.00 | 1.00 |
| type: camera_move_stop | 3 | 0 | 0 | 1.00 | 1.00 | 1.00 |
| type: disappear | 3 | 4 | 3 | 0.43 | 0.50 | 0.46 |
| type: hit | 11 | 9 | 8 | 0.55 | 0.58 | 0.56 |
| type: move_start | 14 | 6 | 7 | 0.70 | 0.67 | 0.68 |
| type: move_stop | 6 | 14 | 1 | 0.30 | 0.86 | 0.44 |
| type: speech_end | 16 | 0 | 1 | 1.00 | 0.94 | 0.97 |
| type: speech_start | 16 | 0 | 1 | 1.00 | 0.94 | 0.97 |
| type: text_in | 12 | 9 | 7 | 0.57 | 0.63 | 0.60 |
| type: text_out | 2 | 9 | 1 | 0.18 | 0.67 | 0.29 |
| type: transition | 11 | 1 | 1 | 0.92 | 0.92 | 0.92 |

## video type x event type (untyped recall is the meaningful column)

| scope | TP | FP | FN | P | R |
|---|---|---|---|---|---|
| animation/appear | 5 | 4 | 1 | 0.56 | 0.83 |
| animation/camera_move_start | 2 | 0 | 0 | 1.00 | 1.00 |
| animation/camera_move_stop | 2 | 1 | 0 | 0.67 | 1.00 |
| animation/disappear | 0 | 2 | 1 | 0.00 | 0.00 |
| animation/hit | 7 | 8 | 2 | 0.47 | 0.78 |
| animation/move_start | 5 | 2 | 1 | 0.71 | 0.83 |
| animation/move_stop | 3 | 3 | 0 | 0.50 | 1.00 |
| animation/speech_end | 8 | 1 | 0 | 0.89 | 1.00 |
| animation/speech_start | 7 | 0 | 1 | 1.00 | 0.88 |
| animation/text_in | 4 | 2 | 1 | 0.67 | 0.80 |
| animation/text_out | 0 | 6 | 0 | 0.00 | n/a |
| animation/transition | 6 | 0 | 0 | 1.00 | 1.00 |
| music/beat | 97 | 2 | 0 | 0.98 | 1.00 |
| screen/appear | 6 | 3 | 2 | 0.67 | 0.75 |
| screen/disappear | 3 | 2 | 2 | 0.60 | 0.60 |
| screen/hit | 4 | 0 | 5 | 1.00 | 0.44 |
| screen/move_start | 7 | 2 | 5 | 0.78 | 0.58 |
| screen/move_stop | 0 | 9 | 1 | 0.00 | 0.00 |
| screen/text_in | 5 | 5 | 6 | 0.50 | 0.45 |
| screen/text_out | 1 | 3 | 0 | 0.25 | 1.00 |
| screen/transition | 3 | 1 | 1 | 0.75 | 0.75 |
| silent/appear | 3 | 0 | 0 | 1.00 | 1.00 |
| silent/camera_move_start | 1 | 0 | 0 | 1.00 | 1.00 |
| silent/camera_move_stop | 1 | 0 | 0 | 1.00 | 1.00 |
| silent/hit | 1 | 0 | 0 | 1.00 | 1.00 |
| silent/move_start | 3 | 0 | 0 | 1.00 | 1.00 |
| silent/move_stop | 3 | 0 | 0 | 1.00 | 1.00 |
| silent/text_in | 3 | 0 | 0 | 1.00 | 1.00 |
| silent/text_out | 1 | 0 | 1 | 1.00 | 0.50 |
| silent/transition | 2 | 0 | 0 | 1.00 | 1.00 |
| speech/speech_end | 8 | 0 | 1 | 1.00 | 0.89 |
| speech/speech_start | 9 | 0 | 0 | 1.00 | 1.00 |
