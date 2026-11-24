# Event score: synthetic/chance-control(same count as best) (tolerance +/-2 frames)

## untyped

| scope | TP | FP | FN | P | R | F1 |
|---|---|---|---|---|---|---|
| ALL | 12 | 125 | 114 | 0.09 | 0.10 | 0.09 |
| video: animation | 5 | 57 | 51 | 0.08 | 0.09 | 0.08 |
| video: screen | 7 | 32 | 44 | 0.18 | 0.14 | 0.16 |
| video: silent | 0 | 36 | 19 | 0.00 | 0.00 | 0.00 |
| type: (untyped) | 0 | 125 | 0 | 0.00 | n/a | n/a |
| type: appear | 2 | 0 | 15 | 1.00 | 0.12 | 0.21 |
| type: camera_move_start | 0 | 0 | 3 | n/a | 0.00 | n/a |
| type: camera_move_stop | 0 | 0 | 3 | n/a | 0.00 | n/a |
| type: disappear | 0 | 0 | 6 | n/a | 0.00 | n/a |
| type: hit | 3 | 0 | 16 | 1.00 | 0.16 | 0.27 |
| type: move_start | 1 | 0 | 20 | 1.00 | 0.05 | 0.09 |
| type: move_stop | 1 | 0 | 6 | 1.00 | 0.14 | 0.25 |
| type: speech_end | 0 | 0 | 8 | n/a | 0.00 | n/a |
| type: speech_start | 1 | 0 | 7 | 1.00 | 0.12 | 0.22 |
| type: text_in | 3 | 0 | 16 | 1.00 | 0.16 | 0.27 |
| type: text_out | 0 | 0 | 3 | n/a | 0.00 | n/a |
| type: transition | 1 | 0 | 11 | 1.00 | 0.08 | 0.15 |

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
| animation/(untyped) | 0 | 57 | 0 | 0.00 | n/a |
| animation/appear | 1 | 0 | 5 | 1.00 | 0.17 |
| animation/camera_move_start | 0 | 0 | 2 | n/a | 0.00 |
| animation/camera_move_stop | 0 | 0 | 2 | n/a | 0.00 |
| animation/disappear | 0 | 0 | 1 | n/a | 0.00 |
| animation/hit | 1 | 0 | 8 | 1.00 | 0.11 |
| animation/move_start | 1 | 0 | 5 | 1.00 | 0.17 |
| animation/move_stop | 1 | 0 | 2 | 1.00 | 0.33 |
| animation/speech_end | 0 | 0 | 8 | n/a | 0.00 |
| animation/speech_start | 1 | 0 | 7 | 1.00 | 0.12 |
| animation/text_in | 0 | 0 | 5 | n/a | 0.00 |
| animation/transition | 0 | 0 | 6 | n/a | 0.00 |
| screen/(untyped) | 0 | 32 | 0 | 0.00 | n/a |
| screen/appear | 1 | 0 | 7 | 1.00 | 0.12 |
| screen/disappear | 0 | 0 | 5 | n/a | 0.00 |
| screen/hit | 2 | 0 | 7 | 1.00 | 0.22 |
| screen/move_start | 0 | 0 | 12 | n/a | 0.00 |
| screen/move_stop | 0 | 0 | 1 | n/a | 0.00 |
| screen/text_in | 3 | 0 | 8 | 1.00 | 0.27 |
| screen/text_out | 0 | 0 | 1 | n/a | 0.00 |
| screen/transition | 1 | 0 | 3 | 1.00 | 0.25 |
| silent/(untyped) | 0 | 36 | 0 | 0.00 | n/a |
| silent/appear | 0 | 0 | 3 | n/a | 0.00 |
| silent/camera_move_start | 0 | 0 | 1 | n/a | 0.00 |
| silent/camera_move_stop | 0 | 0 | 1 | n/a | 0.00 |
| silent/hit | 0 | 0 | 1 | n/a | 0.00 |
| silent/move_start | 0 | 0 | 3 | n/a | 0.00 |
| silent/move_stop | 0 | 0 | 3 | n/a | 0.00 |
| silent/text_in | 0 | 0 | 3 | n/a | 0.00 |
| silent/text_out | 0 | 0 | 2 | n/a | 0.00 |
| silent/transition | 0 | 0 | 2 | n/a | 0.00 |
