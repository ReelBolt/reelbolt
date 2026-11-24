# Event score: synthetic/default (tolerance +/-2 frames)

## untyped

| scope | TP | FP | FN | P | R | F1 |
|---|---|---|---|---|---|---|
| ALL | 18 | 0 | 108 | 1.00 | 0.14 | 0.25 |
| video: animation | 18 | 0 | 38 | 1.00 | 0.32 | 0.49 |
| video: screen | 0 | 0 | 51 | n/a | 0.00 | n/a |
| video: silent | 0 | 0 | 19 | n/a | 0.00 | n/a |
| type: appear | 0 | 0 | 17 | n/a | 0.00 | n/a |
| type: camera_move_start | 0 | 0 | 3 | n/a | 0.00 | n/a |
| type: camera_move_stop | 0 | 0 | 3 | n/a | 0.00 | n/a |
| type: disappear | 0 | 0 | 6 | n/a | 0.00 | n/a |
| type: hit | 0 | 0 | 19 | n/a | 0.00 | n/a |
| type: move_start | 0 | 0 | 21 | n/a | 0.00 | n/a |
| type: move_stop | 0 | 0 | 7 | n/a | 0.00 | n/a |
| type: speech_end | 8 | 0 | 0 | 1.00 | 1.00 | 1.00 |
| type: speech_start | 7 | 0 | 1 | 1.00 | 0.88 | 0.93 |
| type: text_in | 0 | 0 | 19 | n/a | 0.00 | n/a |
| type: text_out | 0 | 0 | 3 | n/a | 0.00 | n/a |
| type: transition | 3 | 0 | 9 | 1.00 | 0.25 | 0.40 |

## typed

| scope | TP | FP | FN | P | R | F1 |
|---|---|---|---|---|---|---|
| ALL | 0 | 18 | 126 | 0.00 | 0.00 | 0.00 |
| video: animation | 0 | 18 | 56 | 0.00 | 0.00 | 0.00 |
| video: screen | 0 | 0 | 51 | n/a | 0.00 | n/a |
| video: silent | 0 | 0 | 19 | n/a | 0.00 | n/a |
| type: (untyped) | 0 | 18 | 0 | 0.00 | n/a | n/a |
| type: appear | 0 | 0 | 17 | n/a | 0.00 | n/a |
| type: camera_move_start | 0 | 0 | 3 | n/a | 0.00 | n/a |
| type: camera_move_stop | 0 | 0 | 3 | n/a | 0.00 | n/a |
| type: disappear | 0 | 0 | 6 | n/a | 0.00 | n/a |
| type: hit | 0 | 0 | 19 | n/a | 0.00 | n/a |
| type: move_start | 0 | 0 | 21 | n/a | 0.00 | n/a |
| type: move_stop | 0 | 0 | 7 | n/a | 0.00 | n/a |
| type: speech_end | 0 | 0 | 8 | n/a | 0.00 | n/a |
| type: speech_start | 0 | 0 | 8 | n/a | 0.00 | n/a |
| type: text_in | 0 | 0 | 19 | n/a | 0.00 | n/a |
| type: text_out | 0 | 0 | 3 | n/a | 0.00 | n/a |
| type: transition | 0 | 0 | 12 | n/a | 0.00 | n/a |

## video type x event type (untyped recall is the meaningful column)

| scope | TP | FP | FN | P | R |
|---|---|---|---|---|---|
| animation/appear | 0 | 0 | 6 | n/a | 0.00 |
| animation/camera_move_start | 0 | 0 | 2 | n/a | 0.00 |
| animation/camera_move_stop | 0 | 0 | 2 | n/a | 0.00 |
| animation/disappear | 0 | 0 | 1 | n/a | 0.00 |
| animation/hit | 0 | 0 | 9 | n/a | 0.00 |
| animation/move_start | 0 | 0 | 6 | n/a | 0.00 |
| animation/move_stop | 0 | 0 | 3 | n/a | 0.00 |
| animation/speech_end | 8 | 0 | 0 | 1.00 | 1.00 |
| animation/speech_start | 7 | 0 | 1 | 1.00 | 0.88 |
| animation/text_in | 0 | 0 | 5 | n/a | 0.00 |
| animation/transition | 3 | 0 | 3 | 1.00 | 0.50 |
| screen/appear | 0 | 0 | 8 | n/a | 0.00 |
| screen/disappear | 0 | 0 | 5 | n/a | 0.00 |
| screen/hit | 0 | 0 | 9 | n/a | 0.00 |
| screen/move_start | 0 | 0 | 12 | n/a | 0.00 |
| screen/move_stop | 0 | 0 | 1 | n/a | 0.00 |
| screen/text_in | 0 | 0 | 11 | n/a | 0.00 |
| screen/text_out | 0 | 0 | 1 | n/a | 0.00 |
| screen/transition | 0 | 0 | 4 | n/a | 0.00 |
| silent/appear | 0 | 0 | 3 | n/a | 0.00 |
| silent/camera_move_start | 0 | 0 | 1 | n/a | 0.00 |
| silent/camera_move_stop | 0 | 0 | 1 | n/a | 0.00 |
| silent/hit | 0 | 0 | 1 | n/a | 0.00 |
| silent/move_start | 0 | 0 | 3 | n/a | 0.00 |
| silent/move_stop | 0 | 0 | 3 | n/a | 0.00 |
| silent/text_in | 0 | 0 | 3 | n/a | 0.00 |
| silent/text_out | 0 | 0 | 2 | n/a | 0.00 |
| silent/transition | 0 | 0 | 2 | n/a | 0.00 |
