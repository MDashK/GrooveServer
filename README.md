<p align="center">
  <img src="banner.png" width="760"
       alt="GrooveServer - a DJMax Online server emulator">
</p>

A **proof of concept** private server emulator for **DJMAX Online** (Chinese client), written in C# on
.NET 8. It lets you log in and play any song you like in free mode, course mode and ranking mode, offline,
in your own computer.

The server was built based on information acquired from the current and only DJMax Online private server available at [djmax.online](https://djmax.online/).

Multiplayer battle mode between players **is not** implemented and is not part of the project.

---

## Introduction

My objective with this proof of concept side-project is the **preservation of the single-player modes of the game**, so that one can enjoy the game in a localhost environment.

**This project was ~90% done by Claude Opus 5**, and it's my first project where full AI usage was used to achieve the goals. 
We are all humans. Between family, work, hobbies and other daily obligations, our time is limited. Most of the time we aren't even able to play all the games or see all the movies we have in our back catalog, let alone have time to also develop everything we want to.
**AI are tools**. It's the way that one uses such tools that defines morals and judgements.
Employing these tools in favor of **gaming preservation and video game history is a clear, justified and worthwhile pursuit**.

With this in mind, and with the advances and potential that AI has been presenting, the idea was to provide Claude AI with full autonomy on the project from the start, leaving it to deal with everything code-wise and at implementation level, and helping only in tasks that he could not accomplish (for instance: gameplay recordings, client bug testing...).

The functionality of the server emulator was heavily tested against the client, however, **expect bugs to happen and to be present**.

If you want to improve this fork and/or the server, feel free to fork the project over and improve it. **Just make sure to mention this source**.
If you also support the history and preservation of videogames, and want to make a difference, check out **[#StopKillingGames](https://stopkillinggames.com)**

---

## What it does

GrooveServer works by replaying real recorded traffic and rewriting the parts that belong to
your account. Everything that is *yours* — level, XP, MAX, collection, inventory, course
scores, chosen song, scroll speed — is computed by the server and written into the recorded
messages before they go out. Everything else is served as captured.

**What's working:**

- Login, lobby, profile. Accounts come from what you type at the login screen
- Two channels at once: **[5KEY] Classic** on port 23505 and **[7KEY] Classic** on 23705, each
  with its own chart library and its own high-score tables
- **Free mode**, any song in the library, at the speed you pick
- **Course mode**, all 48 courses
- Per-course high-score tables, shared between all accounts on the server, kept separately
  per channel
- Collection discs, awarded from your accuracy on each song
- Shop, inventory, buying, deleting and equipping items, and the EXP/MAX bonuses they give
- **Ranking mode**, three stages scored as one run, with its own record per channel
- Level-up, XP and MAX progression
- The **welcome screen** a new account gets: nickname, age and sex, kept on the account and
  shown on the profile panel. The login name is only for logging in — the nickname is the name
  the game displays
- The bonus every effector and speed mode is worth
- **An optional PAK archive that puts the client's text in English**, opens every song, chart
  and course from the start, and restores the client's disabled generic background animation —
  see [The English patch](#the-english-patch)

Chart coverage:

| Channel | EASY | NORMAL | HARD | MX | SC | Total | Courses playable |
|---|---|---|---|---|---|---|---|
| 5KEY | 220 | 251 | 247 | 85 | 22 | **825** | 48 |
| 7KEY | 187 | 190 | 184 | 57 | 19 | **637** | 48 |

A total of 1462 charts is available to play, which is every one the client's catalogue informs exists. 

On the standard DJMax Online client, **the song list a player sees is gated by their level.** Each song carries a required level per
difficulty and per channel (the `UserLevel_EZ_NM_HD_MX_SC` columns of `Song/DiscStock.csv`), and
the client only lists difficulties the player has reached. 

**[The English patch](#the-english-patch) removes the gate**, so a fresh account sees all 1462 charts at level 1, and
the MAX price of the harder difficulties is zeroed with it. 
It also fixes four courses that requested songs that are somehow marked "offair"
in the default client, by dropping the missing song from those four courses instead, so they
can be played one song shorter, and scales down the combo and score thresholds to match.

What's **NOT** working:
- DJ Messenger (no need)
- Multiplayer: everything that is multiplayer **is not implemented**, since it's not the objective of the project.

---

## Running it

You need:

1. [The DJMAX Online client](#client-download)
2. The [.NET 8 runtime](https://dotnet.microsoft.com/download/dotnet/8.0) — the "Runtime",
   not the SDK, unless you plan to build from source

Then:

1. Unzip the release anywhere
2. Use the provided English patch PAK **or** point the client at the server (see below)
3. Double-click `GrooveServer.exe`
4. Start the game

The account is whatever you type at the login screen. If the name is new it is created on the
spot and the password you used is the one it keeps; if it exists, the password has to match or
the login is refused, exactly as the real server did.

Startup is quiet by design — three lines, the two ports and the banner. If you want more
information, starting the server with the `-v` argument gives the whole
inventory: chart list, course table, accounts, recordings, and the response map of each one.

---

## Client Download

You can download the following clients to use with GrooveServer. The clients
have **always been freely available for download** since this was a Free-to-Play game
with micro-transactions:

**DJMAX Online Full ver24120500 2026**
- This is the **recommended and current most up-to-date client**.
- It is also the one that the private online server uses.
- **English PAK** is already present in the ZIP.

| Mirror 1 | Mirror 2 |
|---|---|
| [IceDrive](https://icedrive.net/s/NgT7f5jVhVhxiAFT25zRV6kvi2fT)  | [Google Drive](https://drive.google.com/file/d/1g8IN3tHSsWIt9_Ab5Ne3SgxGUPEa0Ygy/view) | 


**For the older clients below, please also check the [Compatibility with other older client versions](#Compatibility-with-other-older-client-versions) section.**


**DJMAX Online ver15122401 Xmas 2016**
- It has a Christmas Theme applied.
- Has less musics than the above client.
- Has less courses than the above client: You must start GrooveServer with the "--courses 43" argument so that the client doesn't crash.
- **English PAK** is already present in the ZIP.

| Mirror 1 | Mirror 2 |
|---|---|
| [Uploading]()  | [Uploading]() | 

**DJMax Online SNDA v2.50 / v2.60**
- Compared to the above clients, it only has a few musics.
- Fullscreen 800x600 only
- No Course mode
- The custom PAK present in the ZIP only has the IP address redirected. NO ENGLISH TRANSLATION. EVERYTHING STOCK.

| v2.50 | v2.60 |
|---|---|
| [Uploading]()  | [Uploading]() | 

**HOLD "CTRL" KEY AT START-UP TO ACCESS THE SETTINGS WINDOW.**

---

## The English patch

The client's readable text — menus, dialogs, loading tips, song titles, item and disc names,
shop lists — is in Chinese, with Korean left over from the original developers. The provided
custom PAK file replaces all of it with English, **and at the same time**:

- opens all 48 courses from the start, instead of gating them behind player level;
- removes the credit and disc cost of entering a course, and the `Premium` flag that locked 33
  of them behind a shop pass that does not exist in this client's data;
- opens **every song and every chart from level 1**, and zeroes the MAX price of the MX and SC
  difficulties, so nothing is behind a grind.
- Makes every item existent (avatar, gear, notes) accessible in the store, for free, including regional locked ones.

The MAX cost *of a course* is deliberately left alone: MAX is earned by playing, so that gate
still works as a gate.
Graphics are not translated. Text baked into `.png`, `.tga` and `.jpg` is left as it is.

---

### Compatibility with other older client versions

The server is compatible with the following DJMax Online clients:
- DJMAX Online Full ver24120500 2026 (the most recent version, currently online)
- DJMAX Online 15122401 Xmas 2016 
- DJMAX Online SNDA v2.50 / v2.60

however, the server was build primarly using of the most recent client, so if using an older client,
you may experience crashes or bugs that do not happen using the recent one.

The game shipped in several builds and they do not all know the same content. For instance, the server is the
side that announces **which courses exist**, but the side that knows what each course *is* is the
client, in its own `System\courseclub\CourseSection.ini`. Announce an index the client does not
have in there and it **crashes** the moment you open course mode.

The SNDA v2.50 / v2.60 client has Course mode disabled for some reason, but the "15122401 Xmas 2016" client
has only 43 courses, compared to 48 that the most recent client has.
To overcome this situation, and so that the Xmas client does not crash, just run GrooveServer with the
following argument:

```bash
GrooveServer.exe --courses 43
```

The default is no limit. The version the client announces is **not** a way to tell the two
apart — both say `0x00040201`, because that value comes from `DJMax.dll` and the DLL is
byte-for-byte identical in both; the difference lives in the `.pak` archives, which the server
never sees. Hence the flag.

---

### Accounts

`dados/users.json`, plain text, one entry per player.
Example:

```json
{
  "nome": "LoginUsername",
  "password": "Something",
  "nickname": "NicknameInGame",
  "idade": 23,
  "sexo": 1,
  "creditos": 30,
  "nivel": 12,
  "xp": 1500,
  "max": 111381,
  "combo_maximo": 321,
  "precisao_melhor": 99.44,
  "precisao_soma": 883.2,
  "musicas": 10,
  "recorde": 250027,
  "recorde7k": 212570,
  "ranking": 695467,
  "ranking7k": 581128,
  "avatar": 35846,
  "itens": [ { "item": 193793, "instancia": 20260812, "equipado": true } ],
  "courses": { "5k:24": "253631,714,20260812" },
  "itens_base": { "1030": 23, "1031": 20 }
}
```

Passwords are deliberately in the clear. This is a server on your own machine; there is no
secret here worth protecting, and encrypting it would only add work.

- `nome` is the login name; **`nickname` is what the game displays.** They are different
  fields in the protocol and the client sets the nickname on the welcome screen, not at login
- `idade` and `sexo` come from that same welcome screen — `sexo` is `1` female, `2` male, `0`
  not chosen yet, which is also what makes the client ask
- `nivel` is **zero-based** — the game shows this number plus one
- `combo_maximo`, `precisao_melhor`, `precisao_soma` and `musicas` feed the profile panel; the
  average accuracy it shows is derived from the last two and is not stored
- `recorde` and `recorde7k` are the free-mode best scores, one per channel; `ranking` and
  `ranking7k` are the same for ranking mode, where the score is the total of a three-stage run.
  The profile screen has a box for each of the four
- `courses` is `"score,combo,date"`, keyed by **channel and course index**: `"5k:24"`, `"7k:24"`.
  The same course on the two channels is played on different charts, so the tables are
  separate. Entries written before this split carry a bare number and are migrated to `5k:` on
  first load
- `itens_base` is the disc collection, keyed by disc id.

Every account on the server appears in every course's high-score table — that is what makes it
a ranking rather than a personal best.

---

## Building from source

```bash
dotnet publish "src\GrooveServer\GrooveServer.csproj" -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -p:DebugType=none -o "dist" --nologo
```

That builds framework-dependent and single-file for `win-x64`, drops it in `dist\`.
Then, you need the "dados", "gravacoes" and "songs" folders alongside the executable.

or

```bash
dotnet build src/GrooveServer/GrooveServer.csproj
```

---

Run tests — they validate the cipher and the measured protocol layouts against real
captured bytes:

```bash
dotnet test tests/GrooveServer.Tests
```

---

## What is in this repository

| Path | What it is |
|---|---|
| `src/GrooveServer/` | The server. `Protocol/` is the message layouts, `Net/` the session logic, `Crypto/` the cipher, `Pak/` the client's archive format, `Tools/` the analysis commands |
| `src/reXIP/` | The standalone `.pak` packer. Compiles the same `Pak/` sources into its own executable |
| `tests/GrooveServer.Tests/` | Cipher validation and protocol layout tests, all against real captured bytes |
| `docs/` | Protocol notes, size tables, and the open-questions list |
| `gravacoes/` | The recorded sessions the server replays. **Required at runtime** |
| `gravacoes/extra/` | Recordings not used at runtime, kept for analysis |
| `songs/5k`, `songs/7k` | Chart data per song and channel, harvested from the recordings. **Required at runtime** |
| `dados/` | Everything else the server reads. **Required at runtime** |
| `dados/courses.txt`, `dados/itens.txt` | Course and shop-item tables, generated from the client's own data files |
| `dados/DiscStock.csv` and friends | Client data files, extracted from the `.pak` archives |
| `dados/users.json` | Accounts. Created on first run if it is not there |

---

# Technical stuff

The game's protocol is encrypted (TEA + MT19937, keyed per session) and has **no length field**
— every message id has a fixed size that both sides know from a table. Both of those had to be
reverse-engineered before a single packet could be answered.

> **The source comments are in Portuguese, and will stay that way.** They carry most of what was
> learned about the protocol — what was measured, against which capture, and which hypotheses
> died on the way — and they are worth reading even in translation. The server's own output is
> in English.

---

### Pointing the client to GrooveServer

The server's address lives in `DJMax.ini` **inside** the client's `.pak` archives, so the
cleanest way is to override it with a patch archive — no `hosts` file, no proxy, nothing
running in the background.

The provided client links already contain this custom PAK file, so they already point
to the localhost GrooveServer.

An alternative, more temporary way, is to use `--redirect`, which watches for the client process and writes the address
into its memory while the server runs:

```bash
GrooveServer.exe --redirect
```

That one is temporary by nature — close the server and the game goes back to the original
address — which is what you want if you still need to connect to the current online server. It is **off by
default**; nothing is written to another process.

---

### Repacking the client's `.pak` archives

The game keeps its data — the song catalogue, the shop stock, the icons — in `XIP2` archives.
**reXIP** reads *and writes* them, so a data file can be edited and handed back
to the client.

For this purpose, you can use the [reXIP](https://github.com/MDashK/reXIP) tool.

GrooveServer has reXIP integrated, so the same commands are
also reachable through the server as `GrooveServer.exe pak ...`, sharing the same code.

The client counts the `system*.pak` files in the folder and loads them in order, the last one
winning, so a new numbered archive overrides the original without touching a 224 MB file. Its
startup integrity check walks the list inside `crc.pak` and does not notice an extra archive —
`pak crc` is there for the case where you do modify one of the originals.

---

### About the recordings

The server cannot invent the game's messages, so it replays sessions captured against a live
server. Those recordings are in `gravacoes/` and they are **required** — without them the
server has nothing to say.

They carry the recording player's nickname and profile, which is harmless. But the client's
`AuthenticateInACCReq` (`0x0011`) carries the **account password** for that live server, under
a second layer of obfuscation that this project ships a tool to undo (`creds`).

Every recording in this repository has already been sanitised: the body of that message is
zeroed. The server never needed it for replay — and where it *is* read, for deciding which
account is logging in, it is read from the live client, never from a recording.

---

# A Big Special Thanks To
- Alejandro H of ADHSoft - is tool [Xip-Pak-Extractor](https://github.com/ADHSoft/Xip-Pak-Extractor) was very helpful.
- DJ.Metals @Metalsnake27 - Thanks for sharing stuff over the years.

---

## Legal

GrooveServer ships no game data and no keys. This is a clean-room server implementation.
It contains no game code, no game assets and no client binaries.
The client binaries are and always have been freely available for download.
DJMAX Online is © Pentavision / Neowiz. This project is not affiliated with them.

