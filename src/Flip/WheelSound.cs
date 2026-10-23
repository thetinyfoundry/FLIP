using System.IO;
using System.Runtime.InteropServices;
using System.Text;
namespace Flip;
// Original procedural sound. No recordings or assets from other applications.
internal sealed class WheelSound : IDisposable
{
    readonly GCHandle wave;
    [DllImport("winmm.dll", EntryPoint = "PlaySoundW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool PlaySound(nint data, nint module, uint flags);
    bool unavailable;
    bool disposed;
    int attempts;
    internal bool Enabled { get; set; }
    internal WheelSound(bool enabled)
    {
        Enabled = enabled;
        wave = GCHandle.Alloc(CreateWave(), GCHandleType.Pinned);
        Evidence.Log("sound.initialized", new { enabled, backend = "winmm-memory" });
    }
    internal void Play()
    {
        if (disposed) return;
        if (!Enabled) { Evidence.Log("sound.skipped", "disabled"); return; }
        if (unavailable) { Evidence.Log("sound.skipped", "unavailable"); return; }
        try
        {
            // SND_ASYNC | SND_MEMORY | SND_NODEFAULT. Restart the short tick;
            // never queue stale hover feedback or suppress valid sector changes.
            bool accepted = PlaySound(wave.AddrOfPinnedObject(), 0, 0x7); attempts++;
            if (!accepted) Evidence.Log("sound.rejected", new { attempt = attempts });
            else if (attempts == 1) Evidence.Log("sound.requested", new { attempt = attempts, backend = "winmm-memory" });
        }
        catch (Exception ex) { unavailable = true; Evidence.Log("sound.unavailable", ex.Message); }
    }
    internal static byte[] CreateWave()
    {
        const int rate = 44100, count = 1764; // 40 ms original dry tick, mono PCM16
        using var output = new MemoryStream(); using var writer = new BinaryWriter(output, Encoding.ASCII, true);
        writer.Write(Encoding.ASCII.GetBytes("RIFF")); writer.Write(36 + count * 2); writer.Write(Encoding.ASCII.GetBytes("WAVEfmt "));
        writer.Write(16); writer.Write((short)1); writer.Write((short)1); writer.Write(rate); writer.Write(rate * 2); writer.Write((short)2); writer.Write((short)16);
        writer.Write(Encoding.ASCII.GetBytes("data")); writer.Write(count * 2);
        uint noise = 0x464C4950;
        for (int i = 0; i < count; i++)
        {
            double t = (double)i / rate, u = (double)i / (count - 1);
            noise = unchecked(noise * 1664525 + 1013904223);
            double transient = (noise / (double)uint.MaxValue * 2 - 1) * Math.Exp(-t / 0.0035);
            double attack = 1 - Math.Exp(-t / 0.00035);
            double tail = Math.Pow(Math.Cos(u * Math.PI / 2), 2);
            double sample = (0.50 * transient + 0.45 * Math.Sin(2 * Math.PI * 2300 * t) * Math.Exp(-t / 0.0065) + 0.25 * Math.Sin(2 * Math.PI * 950 * t) * Math.Exp(-t / 0.009)) * attack * tail * 0.65;
            writer.Write((short)Math.Round(Math.Clamp(sample, -1, 1) * short.MaxValue));
        }
        writer.Flush(); return output.ToArray();
    }
    public void Dispose() { if (disposed) return; disposed = true; PlaySound(0, 0, 0); wave.Free(); }
}
