using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Media;

namespace Flip;

// Original procedural sound from v0.4.0-beta.3.
internal sealed class WheelSound : IDisposable
{
    readonly string? soundPath;
    readonly GCHandle wave;
    readonly MediaPlayer mediaPlayer = new();
    [DllImport("winmm.dll", EntryPoint = "PlaySoundW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool PlaySound(nint data, nint module, uint flags);
    bool mediaReady;
    bool disposed;

    internal bool Enabled { get; set; }

    internal WheelSound(bool enabled)
    {
        Enabled = enabled;
        byte[] raw = CreateWave();
        wave = GCHandle.Alloc(raw, GCHandleType.Pinned);
        try
        {
            string appData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Flip");
            Directory.CreateDirectory(appData);
            soundPath = Path.Combine(appData, "flip-tick.wav");
            File.WriteAllBytes(soundPath, raw);

            mediaPlayer.Volume = 1.0;
            mediaPlayer.MediaOpened += (_, _) => mediaReady = true;
            mediaPlayer.Open(new Uri(soundPath, UriKind.Absolute));
            Evidence.Log("sound.initialized", new { enabled, backend = "WPF-MediaPlayer-original" });
        }
        catch (Exception ex)
        {
            Evidence.Log("sound.init.error", ex.Message);
        }
    }

    internal void Play()
    {
        if (disposed || !Enabled) return;
        try
        {
            if (mediaReady)
            {
                mediaPlayer.Position = TimeSpan.Zero;
                mediaPlayer.Play();
            }
            else
            {
                PlaySound(wave.AddrOfPinnedObject(), 0, 0x7);
            }
        }
        catch
        {
            try { PlaySound(wave.AddrOfPinnedObject(), 0, 0x7); } catch { }
        }
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

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        try { mediaPlayer.Close(); } catch { }
        try { PlaySound(0, 0, 0); } catch { }
        if (wave.IsAllocated) wave.Free();
    }
}
