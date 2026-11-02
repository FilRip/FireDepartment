using System.Reflection;
using System.Runtime.InteropServices;

namespace FD3.MG;

/// <summary>
/// Échelle d'affichage de l'écran (100 % = 1, 200 % = 2), lue par SDL (utilisé par MonoGame DesktopGL),
/// sinon par Windows, sinon par la variable GDK_SCALE (Linux).
/// </summary>
public static class Display
{
    static Display()
    {
        NativeLibrary.SetDllImportResolver(typeof(Display).Assembly, (name, asm, path) =>
        {
            if (name != "SDL2") return IntPtr.Zero;
            foreach (var n in new[] { "SDL2.dll", "SDL2", "libSDL2-2.0.so.0", "libSDL2.so", "libSDL2-2.0.0.dylib", "libSDL2.dylib" })
                if (NativeLibrary.TryLoad(n, asm, path, out var h)) return h;
            return IntPtr.Zero;
        });
    }

    [DllImport("SDL2", CallingConvention = CallingConvention.Cdecl)] private static extern int SDL_GetWindowDisplayIndex(IntPtr window);
    [DllImport("SDL2", CallingConvention = CallingConvention.Cdecl)] private static extern int SDL_GetDisplayDPI(int index, out float ddpi, out float hdpi, out float vdpi);
    [DllImport("user32.dll")] private static extern uint GetDpiForSystem();

    public static float Scale(IntPtr sdlWindow)
    {
        float s = 0;
        if (OperatingSystem.IsWindows())
        {
            try
            {
                int idx = sdlWindow != IntPtr.Zero ? SDL_GetWindowDisplayIndex(sdlWindow) : 0;
                if (SDL_GetDisplayDPI(Math.Max(0, idx), out _, out var h, out _) == 0 && h > 20) s = h / 96f;
            }
            catch { }
            if (s <= 0) try { s = GetDpiForSystem() / 96f; } catch { }
        }
        else if (float.TryParse(Environment.GetEnvironmentVariable("GDK_SCALE"), System.Globalization.CultureInfo.InvariantCulture, out var g)) s = g;
        if (float.TryParse(Environment.GetEnvironmentVariable("FD3_ECHELLE"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var f)) s = f;
        if (s < 0.75f || s > 6) s = 1;
        return MathF.Round(s * 4) / 4;   // par pas de 25 %
    }
}
