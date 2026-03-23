# FootIK

A Dalamud plugin for FFXIV that applies real-time foot IK, keeping your character's feet planted naturally on slopes and uneven terrain.

![Preview](images/footik_preview.gif)

---

## Features

**Foot IK** — Adjusts foot, knee, and thigh bones each frame to match ground height. No more floating feet or legs sinking into the floor on stairs and slopes.

**Skirt correction** — Rotates front skirt bones (`j_sk_f_a/b/c`) along with the leg lift to reduce mesh clipping.

---

## Usage

Open the settings window with `/footik`.

| Setting | Description |
|---|---|
| Enable FootIK | Master on/off |
| Apply to all characters | Also apply to nearby characters |
| Max step | Maximum height difference (metres) the IK will compensate |
| Single foot only | Only correct the foot that needs the larger adjustment — helps preserve emote poses |
| Skirt correction | Enable/disable skirt correction |

Fine-tuning options (blend weights, smoothing speeds) are available in the **Experimental** tab. The **Animation Filter** lets you control which animations IK is active for.

---

## Installation

Add the custom repository URL in Dalamud's Plugin Installer settings, then install FootIK from the list.

```
https://raw.githubusercontent.com/twelvehouse/DalamudPlugins/main/pluginmaster.json
```
