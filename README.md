# LeviText 🕴

LeviText is a lightweight, portable, **always-on-top rich text editor** for Windows 10/11.  
It "levitates" above all other windows so your notes are always visible, accessible, and editable.

🕴 Inspired by the levitating man emoji — LeviText floats effortlessly, a nod to *Leviticus*, a ghostly presence that became part of our workplace culture.

---

## ✨ Features

- 🕴 **Levitate Mode** — toggle the window always-on-top
- 🌑 **Dark Mode by default** — with rich text color/size/font support
- 🎨 **Formatting tools** — change font, size, color, style, spacing
- ⚡ **Auto-adjust near-black text** so everything stays readable
- 💾 **Auto-save & recovery** — never lose your work
- ➕ **Spawn multiple instances** for separate floating notes
- 🚀 Portable `.exe` — no install or dependencies required

---

## 📥 Download

Grab the latest **portable executable** from the  
👉 [Releases page](https://github.com/YOUR-USERNAME/LeviText/releases)

Just unzip and run `LeviText.exe`.

---

## 🛠 Build from Source

Requires **.NET 9 SDK** (or latest stable .NET).

```bash
git clone https://github.com/YOUR-USERNAME/LeviText.git
cd LeviText
dotnet publish -c Release -r win-x64 --self-contained true `
  /p:PublishSingleFile=true `
  /p:IncludeAllContentForSelfExtract=true `
  /p:PublishTrimmed=false
