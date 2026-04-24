# Medytao — wytyczne dla Claude

## Język komunikacji

**Cała rozmowa z userem idzie po polsku.** Dotyczy to wszystkiego, co Claude
pisze jako odpowiedź w czacie: planów, podsumowań, pytań, raportów po buildach,
opisu tego co zostało zrobione, propozycji kolejnych kroków. Nawet jeśli tekst
zawiera terminy techniczne lub cytaty z kodu — framing i narracja zawsze po
polsku.

Po angielsku piszemy tylko to, co idzie do repo albo do usera końcowego:
commit messages, PR description, UI labele w aplikacji. Komentarze w kodzie
(`//`, `/* */`, `<!-- -->`, `<summary>`) — po polsku.

## Branching strategy

Każda zmiana idzie przez trzy etapy:

1. **Branch roboczy z `origin/main`**
   ```
   git fetch
   git checkout -b claude/<krótka-nazwa> origin/main
   ```
2. **Commity → push brancha roboczego**
   ```
   git add <pliki>
   git commit -m "<krótki message, po angielsku, focus na 'why'>"
   git push -u origin claude/<krótka-nazwa>
   ```
3. **Merge do `stage` i push**
   ```
   git checkout stage
   git merge --no-ff claude/<krótka-nazwa> -m "Merge claude/<krótka-nazwa> into stage"
   git push origin stage
   ```

Na tym **kończy się rola Claude'a**. User testuje na `stage` i sam decyduje kiedy zmergować `stage` → `main`. Claude **nigdy nie dotyka `main` bezpośrednio** — żadnego commita, push'a ani mergu prosto na main.

### Szybkie naprawy feature'a, który jest już na stage ale jeszcze nie na main

Gdy poprawiamy coś, co już siedzi na `stage`, branchujemy z `stage` (nie z `main`) — wtedy baseline ma feature i diff pokazuje tylko samą poprawkę. Reszta flow (push branch → merge stage → push stage) bez zmian.

### Worktree

Claude może pracować w worktree (`.claude/worktrees/...`), ale **wszystkie operacje git i edycje kodu** idą do głównego repo `C:\Users\ja\source\repos\Medytao`. W worktree zachowują się pliki tymczasowe/narzędziowe. Przed edycją/buildem ustawić `cd C:\Users\ja\source\repos\Medytao` dla pewności.

## Konwencje

- **Rozmowa z userem i komentarze w kodzie:** polski
- **Commit messages i UI labele:** angielski
- **Stack:** Blazor WebAssembly (.NET 9.0), backend w `Medytao.Api/Application/Domain`, frontend w `Medytao.Web`, shared DTO w `Medytao.Shared`
