# Translations

Every language Lumora speaks is one file in this folder, named after its language code: `en.json`, `de.json`.
English is the base. A key a translation leaves out shows in English, so a partial file is fine to send.

## Adding a language

1. Copy `en.json` to `<code>.json`, using the language's code (`fr`, `pt-BR`, `ja`).
2. Set the header:
   - `localeCode`: the same code as the file name.
   - `nativeName`: the language's name in that language (`Français`). It is what the language picker shows.
   - `authors`: your name, plus anyone who helped.
3. Translate the values under `messages`. Never change the keys.
4. Keep placeholders such as `{0}` and `{1}` as they are. They can move within the sentence, but each one has to stay.
5. Open a pull request with the one file. Nothing else needs to change: every file here ships with the game.

## Trying it before you send it

Put your file in the `Locale` folder inside Lumora's data folder, then pick the language in Settings, Interface.
A file there wins over the built-in one for every key it has, so you can also fix a single line of an existing
language that way.
