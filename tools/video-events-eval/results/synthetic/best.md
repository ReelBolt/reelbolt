# Event score: synthetic/best (tolerance +/-2 frames)

## untyped

| scope | TP | FP | FN | P | R | F1 |
|---|---|---|---|---|---|---|
| ALL | 46 | 91 | 80 | 0.34 | 0.37 | 0.35 |
| video: animation | 29 | 33 | 27 | 0.47 | 0.52 | 0.49 |
| video: screen | 11 | 28 | 40 | 0.28 | 0.22 | 0.24 |
| video: silent | 6 | 30 | 13 | 0.17 | 0.32 | 0.22 |
| type: (untyped) | 0 | 91 | 0 | 0.00 | n/a | n/a |
| type: appear | 3 | 0 | 14 | 1.00 | 0.18 | 0.30 |
| type: camera_move_start | 1 | 0 | 2 | 1.00 | 0.33 | 0.50 |
| type: camera_move_stop | 1 | 0 | 2 | 1.00 | 0.33 | 0.50 |
| type: disappear | 0 | 0 | 6 | n/a | 0.00 | n/a |
| type: hit | 5 | 0 | 14 | 1.00 | 0.26 | 0.42 |
| type: move_start | 4 | 0 | 17 | 1.00 | 0.19 | 0.32 |
| type: move_stop | 4 | 0 | 3 | 1.00 | 0.57 | 0.73 |
| type: speech_end | 8 | 0 | 0 | 1.00 | 1.00 | 1.00 |
| type: speech_start | 7 | 0 | 1 | 1.00 | 0.88 | 0.93 |
| type: text_in | 3 | 0 | 16 | 1.00 | 0.16 | 0.27 |
| type: text_out | 0 | 0 | 3 | n/a | 0.00 | n/a |
| type: transition | 10 | 0 | 2 | 1.00 | 0.83 | 0.91 |

## typed

| scope | TP | FP | FN | P | R | F1 |
|---|---|---|---|---|---|---|
| ALL | 0 | 137 | 126 | 0.00 | 0.00 | 0.00 |
| video: animation | 0 | 62 | 56 | 0.00 | 0.00 | 0.00 |
| video: screen | 0 | 39 | 51 | 0.00 | 0.00 | 0.00 |
| video: silent | 0 | 36 | 19 | 0.00 | 0.00 | 0.00 |
| type: (untyped) | 0 | 137 | 0 | 0.00 | n/a | n/a |
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
| animation/(untyped) | 0 | 33 | 0 | 0.00 | n/a |
| animation/appear | 0 | 0 | 6 | n/a | 0.00 |
| animation/camera_move_start | 0 | 0 | 2 | n/a | 0.00 |
| animation/camera_move_stop | 1 | 0 | 1 | 1.00 | 0.50 |
| animation/disappear | 0 | 0 | 1 | n/a | 0.00 |
| animation/hit | 2 | 0 | 7 | 1.00 | 0.22 |
| animation/move_start | 2 | 0 | 4 | 1.00 | 0.33 |
| animation/move_stop | 2 | 0 | 1 | 1.00 | 0.67 |
| animation/speech_end | 8 | 0 | 0 | 1.00 | 1.00 |
| animation/speech_start | 7 | 0 | 1 | 1.00 | 0.88 |
| animation/text_in | 2 | 0 | 3 | 1.00 | 0.40 |
| animation/transition | 5 | 0 | 1 | 1.00 | 0.83 |
| screen/(untyped) | 0 | 28 | 0 | 0.00 | n/a |
| screen/appear | 2 | 0 | 6 | 1.00 | 0.25 |
| screen/disappear | 0 | 0 | 5 | n/a | 0.00 |
| screen/hit | 3 | 0 | 6 | 1.00 | 0.33 |
| screen/move_start | 2 | 0 | 10 | 1.00 | 0.17 |
| screen/move_stop | 1 | 0 | 0 | 1.00 | 1.00 |
| screen/text_in | 0 | 0 | 11 | n/a | 0.00 |
| screen/text_out | 0 | 0 | 1 | n/a | 0.00 |
| screen/transition | 3 | 0 | 1 | 1.00 | 0.75 |
| silent/(untyped) | 0 | 30 | 0 | 0.00 | n/a |
| silent/appear | 1 | 0 | 2 | 1.00 | 0.33 |
| silent/camera_move_start | 1 | 0 | 0 | 1.00 | 1.00 |
| silent/camera_move_stop | 0 | 0 | 1 | n/a | 0.00 |
| silent/hit | 0 | 0 | 1 | n/a | 0.00 |
| silent/move_start | 0 | 0 | 3 | n/a | 0.00 |
| silent/move_stop | 1 | 0 | 2 | 1.00 | 0.33 |
| silent/text_in | 1 | 0 | 2 | 1.00 | 0.33 |
| silent/text_out | 0 | 0 | 2 | n/a | 0.00 |
| silent/transition | 2 | 0 | 0 | 1.00 | 1.00 |
