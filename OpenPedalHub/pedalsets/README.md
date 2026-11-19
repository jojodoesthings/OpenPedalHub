# Known pedal sets

Each `.json` file in this folder teaches OpenPedalHub to recognise one model of pedal set automatically.

## Adding a set

1. On a PC with the pedals plugged in, open OpenPedalHub and choose **Settings → Set up a pedal set**.
2. Press each pedal when asked.
3. A report file is saved in `Documents\OpenPedalHub\reports`.
4. Check it (see below), then copy it into this folder. That is all: a report file is already in the right format.

## What a file contains

| Field | Meaning |
|---|---|
| `Name` | Brand and model, as people know it. |
| `Vid`, `Pid` | USB vendor and product id, four hex digits each. Together they identify the model. |
| `Axes` | For `clutch`, `brake` and `throttle`: which axis it is on (`Usage`, a HID usage number: 48 = X, 49 = Y, 50 = Z, 51 = Rx, 52 = Ry, 53 = Rz, 54 = Slider, 55 = Dial), whether it runs backwards (`Reversed`), and optionally its measured `Rest` and `Full` positions. Leave a pedal out if the set does not have it. |
| `HideFromGames` | `true` for a standalone USB pedal set. **`false` for pedals that plug into a wheel base**, because hiding that device would hide the wheel as well. |
| `Report` | Extra detail from the set-up screen for whoever reviews the file. The app ignores it. |

## Before accepting a report

- **`Rest` and `Full` are one person's unit.** Sensors vary, so consider removing them from a shared file and letting each user run *Calibrate pedals*.
- **Check `HideFromGames`.** The set-up screen sets it to `true` only when every axis on the device turned out to be a pedal; a person should confirm.
- **One file per model.** If two reports for the same `Vid`/`Pid` disagree, find out why before choosing.

## Privacy

Reports describe hardware **models** only. They must never contain serial numbers, device instance paths,
computer or user names, or file paths. The set-up screen does not write any of these; do not add them by hand.
