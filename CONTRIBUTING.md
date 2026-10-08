# Contributing

Thanks for wanting to help. Small, focused pull requests are easiest to review.

## Getting started

You need Windows and the [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0).

```powershell
dotnet run --project src/Kilim            # the rug
dotnet run --project tests/SafetyTests    # checks for everything that touches user files
```

The safety tests use your own `%USERPROFILE%\Kilim` folder and ask you to empty the rug first.

## Adding a rug pattern

New patterns are the easiest way to contribute, and Turkish and Anatolian weaving traditions are especially welcome.

1. Make or find a **straight top-down image of one whole rug**, filling the frame, about 3:2, with fringes only on the short sides. It must be yours or under a license that allows MIT use; say where it comes from in the pull request.
2. Turn it into a texture (cuts off the photographed fringes, Kilim draws its own):
   ```powershell
   python tools/prepare_pattern.py path/to/rug.png yourid
   ```
   This writes `src/Kilim/Assets/Patterns/yourid.jpg`.
3. Add a line to `Patterns.All` in [src/Kilim/Patterns.cs](src/Kilim/Patterns.cs) with the fringe wool color, and its English and Turkish names in `Text.PatternName` in [src/Kilim/Text.cs](src/Kilim/Text.cs).

## Translations

Everything the user reads is in [src/Kilim/Text.cs](src/Kilim/Text.cs), in English and Turkish.

## Changes that touch files

Anything that moves, names or tracks user files needs a check in [tests/SafetyTests](tests/SafetyTests). Kilim must never delete or overwrite a file and must keep working if `state.json` is missing or odd.

## Code style

Match the code around your change. Comments explain why, not what.
