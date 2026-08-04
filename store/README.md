Text for the mod pages. One file per field, because each site takes a different format and a
different length, and keeping one copy per platform is what stops them from drifting apart.

`nexus-description.txt` — Nexus, main description. BBCode. No length limit worth worrying about.

`nexus-summary.txt` — Nexus, the summary under the title.

`sotf-mods-description.md` — sotf-mods.com, main description. Markdown, hard cap 2000 characters. Their short field caps at 200, but every mod there uses a single short phrase, so it is written straight into the site rather than kept here.

The install instructions in these files must match what `package.sh` actually builds: the archive carries its own `Mods/` folder and is unpacked into the game folder.
