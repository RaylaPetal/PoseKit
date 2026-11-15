<div align="center">

# ✨ PoseKit ✨

</div>

---

PoseKit takes the fiddly parts out of posing and animations. It finds the animation mods you already have, lets you nudge your character into exactly the right spot, and remembers it all so you only have to set up once.

---

## 🌸 Features

### 🎭 Animation Browser
Find the animation mods in your Penumbra library for you.

### 🎯 Live Offset
Drag your character into position via Simple Heels.

### 📚 Presets
Save any pose, including its position, as a named preset and replay it with one click. Organize large libraries with groups, search and filters.

### 📍 Anchors
Presets can remember *where* they happened: a spot in the world, a piece of furniture, or your partner. When you play them again, you land right back in place.

### 💞 Pairing
Pair up with someone to play together. Target them and press Pair the first time; after that you can pair again from anywhere. Both of you need PoseKit 3.1 or newer.

### 🦴 Bone Align
Pick a body part on you and one on your partner, press **Align**, and PoseKit aligns the body parts for you.

### 🧠 Alignment Memory
Once you've aligned an animation, PoseKit remembers it. Next time you play it together, it lines up on its own.

### 🔄 Emote Sync
Restart everyone's emotes nearby at the same moment.

### 🎥 Free Camera
Unlock the camera and fly around freely.

---

## 📦 Installation

1. Add the following repo:
   ```
   https://raw.githubusercontent.com/RaylaPetal/PoseKit/master/repo.json
   ```

---

## 🚀 Getting Started

| Command | What it does |
|---|---|
| `/posekit` | Open or close PoseKit |
| `/posekit tfc` | Turn free camera on or off |
| `/posekit sync` | Resync nearby emotes |

---

## 🔒 Pairing and privacy

Pairing messages travel through PoseKit's own relay on Cloudflare, never as game chat: PoseKit sends no tells.
Messages between partners are end-to-end encrypted, so the relay can't read what you pick, offer or play.
What the relay does see: that a character is online with PoseKit (it routes invites by a hash of the character's
name and world), when messages are sent, and how big they are. Nothing is stored beyond a 2-minute hold for a partner
who briefly lost connection.

A first pairing is verified in the game itself: your partner's PoseKit only trusts an invite from a character
standing next to them and targeting them. Partners you've paired with before are remembered by their device key.

---

## ☕ Support PoseKit

**[Ko-fi](https://ko-fi.com/raylapetal)** · **[Patreon](https://www.patreon.com/RaylaPetal)** · **[Discord](https://discord.gg/87eMbV4FU9)**

---

## 🙏 Credits

PoseKit learned from and adapted techniques in these open-source plugins. Thank you to their authors:

- [SimpleHeels](https://github.com/Caraxi/SimpleHeels): the render offset and pose identification
- [Cammy](https://github.com/UnknownX7/Cammy): freecam input handling
- [Ktisis](https://github.com/ktisis-tools/Ktisis): smooth freecam movement and raw keyboard input

---
