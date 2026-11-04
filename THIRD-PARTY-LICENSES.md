# Third-party licenses

ZoneTitan incorporates code from the following open-source project.

---

## DisplayProfileManager

- **Author:** zac15987
- **Source:** https://github.com/zac15987/DisplayProfileManager
- **License:** MIT

ZoneTitan's Windows CCD (Connected/Configuration Display) interop layer is ported and
adapted from DisplayProfileManager's `src/Helpers/DisplayConfigHelper.cs`. The affected
files in this repository carry the original copyright notice alongside a
"Modified for ZoneTitan" line:

- `src/ZoneTitan.Core/Native/CcdNative.cs`
- `src/ZoneTitan.Core/Display/MonitorEnumerator.cs` (enumeration approach; identity and
  all-paths logic are ZoneTitan's own)

### Full license text

```
MIT License

Copyright (c) 2025 zac15987

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
```

---

## DisplayMagician — inspiration only, no code

- **Author:** Terry MacDonald
- **Source:** https://github.com/terrymacdonald/DisplayMagician
- **License:** GPL-3.0

**No DisplayMagician code is present in this repository.** ZoneTitan's game-aware
automation (apply → launch → watch → revert) is an independent, clean-room
re-implementation of a behavioral pattern, per spec §0 rule 3. DisplayMagician is
credited as inspiration only; because none of its GPL-3.0 code is used, ZoneTitan
carries no GPL obligation.
