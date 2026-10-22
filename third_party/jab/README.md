# Java Access Bridge client DLL

`WindowsAccessBridge-64.dll` is the Windows-side (assistive technology) half of Java Access Bridge, taken unmodified
from the Eclipse Temurin 21 JRE (`bin\WindowsAccessBridge-64.dll`, Temurin-21.0.12.1+1, 2026-08-18). winuse loads it
to read Swing windows. The Java side (`JavaAccessBridge-32/64.dll` with `access-bridge-*.jar`, or the
`jdk.accessibility` module) comes from the JRE the Java app runs on, so this one DLL serves 32-bit and 64-bit JVMs of
any version since 7u6.

It is bundled because the machines that need it most, those with only a 32-bit Java, have no copy of their own: a
32-bit JRE ships only `WindowsAccessBridge-32.dll` and the legacy `WindowsAccessBridge.dll`, which a 64-bit process
cannot load. `src/WinUse/WinUse.csproj` copies the DLL and `legal/` next to `winuse.exe` on build and publish.

License: GNU General Public License v2 with the Classpath Exception (OpenJDK; `legal/LICENSE`,
`legal/ASSEMBLY_EXCEPTION`, `legal/ADDITIONAL_LICENSE_INFO`). Source: https://github.com/adoptium/jdk21u
(`src/jdk.accessibility/windows/native/libwindowsaccessbridge`).

Refresh with `pwsh -File scripts/fetch-jab.ps1`, then update the version above.
